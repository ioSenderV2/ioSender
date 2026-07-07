/*
 * StreamPump.cs - part of CNC Controls library
 *
 * Background G-code send/ack pump - runs the job flow control off the WPF UI thread.
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
    // Lightweight file tracer for diagnosing stay-put/macro streaming (Load Stock). Writes to
    // %TEMP%\iosender-startjob.log. Cleared at the start of each small (<200-block) run so each
    // reproduction is self-contained; large cutting jobs are not traced (Enabled=false) to avoid bloat.
    internal static class PumpLog
    {
        private static readonly object gate = new object();
        public static bool Enabled = false;
        public static readonly string FilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iosender-startjob.log");

        public static void Clear() { try { System.IO.File.WriteAllText(FilePath, string.Empty); } catch { } }

        public static void W(string msg)
        {
            if (!Enabled)
                return;
            try { lock (gate) System.IO.File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n"); }
            catch { }
        }
    }

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
        // Count of modal-reset prolog lines (source.Commands) sent ahead of the first block on a mid-program
        // start; their acks are swallowed in Run so they don't advance job-line accounting.
        private int preambleAcks = 0;

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

            // PumpLog is enabled/cleared by MacroProcessor for stay-put macro runs (Load Stock); normal jobs leave
            // it disabled. Just record the pump's start parameters when tracing is on.
            PumpLog.W(string.Format("PUMP START from={0} pgmEnd={1} blocks={2} serialSize={3} useBuffering={4} sendComments={5}",
                fromBlock, pgmEndLine, source?.Blocks, serialSize, useBuffering, sendComments));

            Comms.com.BlockingWrites = true;
            // Tap acks straight off the read thread. Dropped while Suspended (tool change) so jog/MDI acks
            // are not mistaken for job-line acks - they fall through to the UI path, which ignores them.
            Comms.com.AckSink = ack => { if (!Suspended) acks.Add(ack); };

            // Re-establish modal state for a mid-program start (units / plane / distance mode): "Start from this
            // toolpath" queues a G90 G94 / G17 / G21 prolog on source.Commands. The legacy streamer drained that
            // via SendNextLine, but this pump streams source.Data directly - so send those lines here, FIRST,
            // ahead of the first block. Their acks are swallowed in Run (they are not job lines). Without this the
            // run inherits whatever units the controller was left in; if it was G20, the toolpath's literal mm
            // coordinates are read as inches -> targets off the table -> Alarm:2 soft limit on the first rapid.
            preambleAcks = 0;
            if (source.Commands != null)
                while (source.Commands.Count > 0)
                {
                    Comms.com.WriteCommand(source.Commands.Dequeue());
                    preambleAcks++;
                }

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

        // Sentinel pushed through the ack channel by KickIdle so the nudge is handled on the pump thread (the
        // owner of serialUsed/inflight/sendIdx) - never touch that accounting from the UI thread.
        private const string IdleKick = "\0idlekick";

        // Called from the UI thread when the controller is confirmed idle while the pump still believes a job is
        // streaming - i.e. the pump stalled (it thinks the controller's buffer is full, but an idle controller has
        // drained it, so some acks must have been missed; or all lines were sent but a tail ack never arrived).
        // An O-word/macro program (Load Stock) can hit this. Handled on the pump thread via the ack channel.
        public void KickIdle()
        {
            PumpLog.W(string.Format("KICK requested  aborted={0}", aborted));
            if (!aborted && acks != null)
                try { acks.Add(IdleKick); } catch { }
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
                    if (preambleAcks > 0 && ack != IdleKick)
                    {
                        preambleAcks--;     // swallow a modal-reset prolog ack - not a job line
                        continue;
                    }
                    if (ack == IdleKick)
                        OnIdleKick();
                    else
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
                {
                    PumpLog.W(string.Format("HOLD barrier  sendIdx={0} inflight={1} used={2}", sendIdx, inflight.Count, serialUsed));
                    break;
                }

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
                    PumpLog.W(string.Format("SEND idx={0} len={1} used={2} inflight={3} last={4}  '{5}'", sendIdx, len, serialUsed, inflight.Count, isLast, line));

                    if (block.BreakAt)
                    {
                        const int m0Len = 3;        // "M0\r"
                        serialUsed += m0Len;
                        inflight.Enqueue(new Sent(-1, m0Len));
                        Comms.com.WriteString("M0" + '\r');
                    }

                    // Barrier on a streamed probe (G38) AND on an O-word CALL: an O<...> CALL runs a controller-
                    // side macro that itself moves/probes (e.g. Load Stock's pcorner.macro, whose G38s are in the
                    // macro - not in this streamed line - so the G38 test alone never fired for it). Piling the
                    // lines that follow into the controller's RX while that macro runs breaks grblHAL's O-word
                    // handling and stalls the run right after the CALL (the tail - final G30 park + M2 - never
                    // executes). Hold the stream until the CALL has fully completed (everything outstanding acked).
                    if (line.IndexOf("G38", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("O<", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        probePending = jobHasProbe = true;
                        PumpLog.W(string.Format("BARRIER set @idx={0}", sendIdx));
                    }

                    sendIdx = isLast ? -1 : sendIdx + 1;

                    if (!useBuffering || probePending)
                        break;
                }
                else
                {
                    PumpLog.W(string.Format("HOLD bufferfull sendIdx={0} used={1} need={2} inflight={3}", sendIdx, serialUsed, serialSize - len, inflight.Count));
                    break;                          // buffer full / probe look-ahead cap reached
                }
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

            PumpLog.W(string.Format("ACK  idx={0} used={1} inflight={2} sendIdx={3} barrier={4}  '{5}'", s.Index, serialUsed, inflight.Count, sendIdx, probePending, ack));

            // probe barrier clears once everything outstanding (including the G38, whose ok arrives only after
            // the probe finishes) has been acked.
            if (probePending && inflight.Count == 0)
            {
                probePending = false;
                PumpLog.W("BARRIER clear");
            }

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
                PumpLog.W("JOB FINISHED (all sent+acked)");
                aborted = true;
                dispatcher.BeginInvoke(onJobFinished);
                return;
            }

            SendNext();
        }

        // The controller is confirmed idle but we still think a job is in flight (see KickIdle). An idle
        // controller has drained its buffer, so any serialUsed/inflight left is from acks we never saw.
        private void OnIdleKick()
        {
            PumpLog.W(string.Format("KICK handled  sendIdx={0} inflight={1} used={2} barrier={3} aborted={4}", sendIdx, inflight.Count, serialUsed, probePending, aborted));
            if (aborted)
                return;

            if (sendIdx >= 0)
            {
                // Lines remain but the pump believed the buffer was full (or is holding the O-word/probe barrier):
                // the controller is idle, so its buffer is empty and the in-flight CALL/probe has finished. Drop
                // the stale accounting, release the barrier, and resume sending the remainder (final G30 + M2).
                serialUsed = 0;
                probePending = false;
                inflight.Clear();
                SendNext();
                // If that was the last of it (nothing left to send AND nothing newly queued), finish now.
                if (sendIdx < 0 && inflight.Count == 0)
                {
                    aborted = true;
                    dispatcher.BeginInvoke(onJobFinished);
                }
            }
            else if (inflight.Count > 0)
            {
                // Everything was sent; only a tail ack is missing and the controller is idle - the job is done.
                aborted = true;
                dispatcher.BeginInvoke(onJobFinished);
            }
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
