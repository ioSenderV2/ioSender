/*
 * StreamPump.cs - part of CNC Controls library
 *
 * Background G-code send/ack pump (experimental, gated by AppConfig.Settings.Base.UseStreamerThread).
 *
 */

/*

The job line-pump traditionally runs on the WPF UI thread: controller acks are marshalled to the UI
dispatcher, and ResponseReceived -> SendNextLine sends the next line there. Heavy UI work (3D view,
grid scroll) then delays the dispatcher, the next line goes out late, the controller's planner buffer
drains, and motion stutters.

StreamPump moves ONLY the latency-critical send/ack flow control onto a dedicated background thread:

  - Acks are tapped straight off the comms read thread via Comms.com.AckSink (no UI dispatcher), fed
    into a BlockingCollection the pump thread consumes.
  - The pump does standard grbl character-counting flow control (keep <= serialSize bytes in flight)
    and writes job lines directly via Comms (BlockingWrites = synchronous, so back-to-back lines can't
    overlap; blocks only this thread - desired backpressure - never the UI).
  - Display only (the grid "Sent" marks, BlockExecuting, ScrollPosition) is marshalled back to the UI,
    coalesced at Background priority so a fast job can't flood the dispatcher. The pump's progress never
    waits on the display drain.
  - The state machine stays on the UI thread (JobControl). The pump just signals job-finished / error.

Threading contract: every accounting field below is touched ONLY by the pump thread (after Start). The
UI thread interacts only through Start/Abort and the volatile PendingLine/Suspended/IsActive flags.

*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using CNC.Core;

namespace CNC.Controls
{
    public class StreamPump
    {
        // one in-flight line awaiting its ack; Index = -1 for the synthetic M0 a breakpoint appends
        private struct Sent
        {
            public int Index;
            public int Length;
            public Sent(int index, int length) { Index = index; Length = length; }
        }

        private readonly GrblViewModel model;
        private readonly Dispatcher dispatcher;

        private IProgramSource source;
        private int serialSize;
        private bool useBuffering, sendComments, startSimulator;
        private System.Action onJobFinished;
        private System.Action<string> onError;

        // ---- pump-thread-owned accounting (no locking; single-thread access after Start) ----
        private int sendIdx;            // next block index to send (-1 = nothing left)
        private int pgmEndLine;         // last block to send (program-end or RunToBlock bound)
        private int serialUsed;         // bytes sent but not yet acked
        private bool started;           // toggled by the "%" demarcation
        private bool probePending, jobHasProbe;
        private readonly Queue<Sent> inflight = new Queue<Sent>();

        private Thread thread;
        private BlockingCollection<string> acks;
        private CancellationTokenSource cts;
        private volatile bool aborted;

        // ---- cross-thread state ----
        public volatile int PendingLine;    // last acked real line - read by JobControl for the tool-change boundary
        public volatile bool Suspended;     // UI sets this during a tool change so jog/MDI acks aren't consumed as job acks
        public volatile bool IsActive;      // a job is streaming through the pump

        // ---- coalesced display marshaling (UI thread drains) ----
        private readonly ConcurrentQueue<KeyValuePair<int, string>> sentMarks = new ConcurrentQueue<KeyValuePair<int, string>>();
        private volatile int latestBlock = -1, latestScroll = -1;
        private int drainPending = 0;

        public StreamPump(GrblViewModel model, Dispatcher dispatcher)
        {
            this.model = model;
            this.dispatcher = dispatcher;
        }

        public void Start(IProgramSource source, int fromBlock, int pgmEndLine, int serialSize, bool useBuffering,
                          bool sendComments, bool startSimulator, System.Action onJobFinished, System.Action<string> onError)
        {
            this.source = source;
            this.serialSize = serialSize;
            this.useBuffering = useBuffering;
            this.sendComments = sendComments;
            this.startSimulator = startSimulator;
            this.onJobFinished = onJobFinished;
            this.onError = onError;

            sendIdx = fromBlock;
            this.pgmEndLine = pgmEndLine;
            serialUsed = 0;
            started = probePending = jobHasProbe = false;
            inflight.Clear();
            while (sentMarks.TryDequeue(out _)) { }
            latestBlock = latestScroll = -1;
            drainPending = 0;

            PendingLine = fromBlock;
            Suspended = false;
            aborted = false;
            IsActive = true;

            cts = new CancellationTokenSource();
            acks = new BlockingCollection<string>();

            Comms.com.BlockingWrites = true;
            // Tap acks straight off the read thread. Dropped while Suspended (tool change) so jog/MDI acks
            // are not mistaken for job-line acks - they fall through to the UI path, which ignores them.
            Comms.com.AckSink = ack => { if (!Suspended) acks.Add(ack); };

            thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "StreamPump" };
            thread.Start();
        }

        // Called on the UI thread to stop the pump (Stop/Reset/Alarm/connection-lost). Idempotent.
        public void Abort()
        {
            aborted = true;
            if (Comms.com != null)
                Comms.com.AckSink = null;   // stop routing acks to a dying pump
            cts?.Cancel();                  // unblock the ack Take
        }

        private void Run()
        {
            try
            {
                SendNext();                 // initial buffer fill
                while (!aborted)
                {
                    string ack;
                    try { ack = acks.Take(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    if (aborted)
                        break;
                    OnAck(ack);
                }
            }
            catch (Exception)
            {
                // never let a pump-thread exception take down the app; the UI state machine still owns recovery
            }
            finally
            {
                if (Comms.com != null)
                {
                    Comms.com.AckSink = null;
                    Comms.com.BlockingWrites = false;
                }
                IsActive = false;
            }
        }

        // Send lines while there is RX-buffer room (grbl character counting), honouring the probe barrier.
        private void SendNext()
        {
            while (sendIdx >= 0 && !aborted)
            {
                if (probePending)               // hold everything while a streamed probe is in flight
                    break;

                GCodeBlock block = source.Data[sendIdx];
                string line = block.Data;
                int len = block.Length;

                // Comments are sent as an empty comment when "Send comments" is off - except to the simulator,
                // which parses (TOOL ...) comments. Use a local length; never mutate the shared block.
                if (block.IsComment && !sendComments && !startSimulator)
                {
                    line = "()";
                    len = line.Length + 1;
                }

                if (serialUsed < serialSize - len && (!jobHasProbe || inflight.Count < JobControl.ProbeLookahead))
                {
                    // program-end markers (mirror the legacy SendNextLine bookkeeping)
                    if (line == "%")
                    {
                        if (!(started = !started))
                            pgmEndLine = sendIdx;
                    }
                    else if (block.ProgramEnd)
                        pgmEndLine = sendIdx;

                    bool isLast = pgmEndLine == sendIdx;

                    MarkSent(sendIdx, "*");
                    serialUsed += len;
                    inflight.Enqueue(new Sent(sendIdx, len));
                    Comms.com.WriteString(line + '\r');

                    if (block.BreakAt)
                    {
                        const int m0Len = 3;        // "M0\r"
                        serialUsed += m0Len;
                        inflight.Enqueue(new Sent(-1, m0Len));
                        Comms.com.WriteString("M0" + '\r');
                    }

                    if (line.IndexOf("G38", StringComparison.OrdinalIgnoreCase) >= 0)
                        probePending = jobHasProbe = true;

                    sendIdx = isLast ? -1 : sendIdx + 1;

                    if (!useBuffering || probePending)
                        break;
                }
                else
                    break;                          // buffer full / probe look-ahead cap reached
            }
        }

        private void OnAck(string ack)
        {
            if (inflight.Count == 0)                 // stray ack (e.g. a late jog/MDI reply) - ignore
                return;

            Sent s = inflight.Dequeue();
            serialUsed -= s.Length;
            if (serialUsed < 0)
                serialUsed = 0;

            // probe barrier clears once everything outstanding (including the G38, whose ok arrives only after
            // the probe finishes) has been acked.
            if (probePending && inflight.Count == 0)
                probePending = false;

            if (s.Index >= 0)                        // a real program line (not the synthetic M0)
            {
                PendingLine = s.Index;
                MarkSent(s.Index, ack);
                latestBlock = s.Index;
                if (s.Index > 5)
                    latestScroll = s.Index - 5;
                ScheduleDrain();
            }

            if (ack.StartsWith("error"))
            {
                aborted = true;
                dispatcher.BeginInvoke(onError, ack);
                return;
            }

            if (sendIdx < 0 && inflight.Count == 0)  // everything sent and acked
            {
                aborted = true;
                dispatcher.BeginInvoke(onJobFinished);
                return;
            }

            SendNext();
        }

        // ---- display marshaling (coalesced, Background priority) ----

        private void MarkSent(int index, string mark)
        {
            sentMarks.Enqueue(new KeyValuePair<int, string>(index, mark));
            ScheduleDrain();
        }

        private void ScheduleDrain()
        {
            if (Interlocked.Exchange(ref drainPending, 1) == 0)
                dispatcher.BeginInvoke((System.Action)Drain, DispatcherPriority.Background);
        }

        private void Drain()
        {
            Interlocked.Exchange(ref drainPending, 0);

            KeyValuePair<int, string> mark;
            var data = source.Data;
            while (sentMarks.TryDequeue(out mark))
            {
                if (mark.Key >= 0 && mark.Key < data.Count)
                    data[mark.Key].Sent = mark.Value;
            }

            int block = latestBlock;
            if (block >= 0)
                model.BlockExecuting = block;

            int scroll = latestScroll;
            if (scroll >= 0)
                model.ScrollPosition = scroll;
        }
    }
}
