/*
 * WirePacer.cs - part of CNC Core
 *
 * The one thing that puts a line on the wire and waits for its acknowledgement.
 *
 */

/*

Extracted from StreamPump 2026-08-11 (docs/Architecture-MDI-Dispatch-Unification.md, step 2). Until
then there were TWO pacing mechanisms - the job pump's, and JobRunner's private SendMDI switch - and
every grblHAL quirk had to be discovered and taught to each of them separately. Three logged defects
came out of that split, so what is shared here is deliberately the part that had to learn the quirks:

  - the classified-reply tap (Comms.StreamComms.ReplyClassified), taken straight off the comms READ
    thread and fed into a BlockingCollection so nothing is ever decided on the read thread itself;
  - a single owner thread for all dispatch accounting - what is outstanding, how many bytes of the
    controller's RX buffer are spoken for, when there is room for the next write;
  - one ordered channel carrying replies, status reports and host-posted signals, so an operator
    answer or an idle nudge is handled in sequence with the acks rather than racing them.

What is NOT here is what to send next. That is the client's business and the two clients differ
completely: StreamPump walks a program (character counting, directives, per-line display marks),
MdiDispatcher drains a queue of typed/programmatic commands one at a time. Both ask this class the
same question - "may I write now, and what is still outstanding" - and get the same answer.

Threading contract, inherited from StreamPump and unchanged: every accounting field below is touched
ONLY by the pacer thread (after Start). Other threads interact through Start/Abort/Post and the
volatile flags. IClient callbacks all run ON THE PACER THREAD, in channel order.

*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace CNC.Core
{
    public class WirePacer
    {
        /// <summary>A write that has gone out and is awaiting its acknowledgement.</summary>
        public struct Sent
        {
            public string Text;         // what was written, without the terminator
            public int Length;          // bytes charged against the controller's RX buffer (includes the terminator)
            public int Tag;             // client's own identity for this write (StreamPump: block index, -1 = synthetic)
            public DateTime AtUtc;      // when it was written - the evidence for "this will never be answered"

            public double AgeMs { get { return (DateTime.UtcNow - AtUtc).TotalMilliseconds; } }
        }

        /// <summary>
        /// What a pacer client must answer. Every method runs on the pacer thread, in channel order.
        /// None of them may block for long: a stalled client stalls dispatch, not just itself.
        /// </summary>
        public interface IClient
        {
            /// <summary>The pacer thread is up. Fill the controller's buffer for the first time.</summary>
            void OnStarted();

            /// <summary>An ack/nak ("ok" / "error:N") arrived from the controller.</summary>
            void OnReply(string reply);

            /// <summary>
            /// A status report arrived. Only delivered while <see cref="ForwardStatus"/> is set, so a
            /// client that does not need them never pays for the ~5/s traffic in its channel.
            /// </summary>
            void OnStatus(string report);

            /// <summary>A signal posted from another thread via <see cref="Post"/> (operator answers, nudges).</summary>
            void OnSignal(string signal);
        }

        private enum Kind { Reply, Status, Signal }

        private struct Item
        {
            public Kind Kind;
            public string Text;
            public Item(Kind kind, string text) { Kind = kind; Text = text; }
        }

        private readonly string name;           // DebugLog channel ("pump" / "mdi")
        private IClient client;
        private int serialSize;

        // The comms instance this pacer is tapped to, captured at Start. Deliberately NOT re-read from
        // the static Comms.com later: a reconnect (or the Restart relaunch) replaces that static, and
        // unsubscribing from whatever happens to be current then would leave the handler attached to the
        // OLD stream forever. Clients that outlive a connection check IsTappedTo and restart.
        private StreamComms link;
        private bool ownsBlockingWrites;

        // ---- pacer-thread-owned accounting (no locking; single-thread access after Start) ----
        private int serialUsed;                 // bytes written but not yet acked
        private readonly Queue<Sent> inflight = new Queue<Sent>();

        private Thread thread;
        private BlockingCollection<Item> channel;
        private CancellationTokenSource cts;
        private volatile bool aborted;
        // Bumped by every Start. A pacer thread tears down ONLY its own generation: an Abort()/Start()
        // pair can return before the previous thread has noticed the cancellation, and that thread's
        // finally would otherwise unsubscribe the NEW run's reply tap (same handler, same instance) and
        // clear its IsActive - leaving a live pacer that never sees another ack. Clients reuse one
        // instance across runs (StreamPump across jobs, MdiDispatcher across reconnects), so this is
        // reachable, not theoretical.
        private int generation;

        // ---- cross-thread flags ----
        /// <summary>Drop incoming replies (a tool change hands the wire to another dispatcher).</summary>
        public volatile bool Suspended;
        /// <summary>The pacer thread is running.</summary>
        public volatile bool IsActive;
        /// <summary>Deliver status reports to the client. Off by default - see IClient.OnStatus.</summary>
        public volatile bool ForwardStatus;
        /// <summary>Trace every status report to the debug log (StreamPump's original behaviour; off for chatty clients).</summary>
        public volatile bool LogStatusReports;

        /// <summary>Teardown has been asked for (or has happened). Clients guard their callbacks on this.</summary>
        public bool Aborted { get { return aborted; } }

        public WirePacer(string name)
        {
            this.name = name;
        }

        /// <summary>
        /// Tap the link, then start dispatching.
        /// </summary>
        /// <param name="prologue">
        /// Run after the reply tap is live but BEFORE the pacer thread starts. Anything written here is
        /// guaranteed to reach the controller ahead of the client's first dispatch, and its acks are
        /// guaranteed to be captured. StreamPump's mid-program modal-reset prolog needs exactly that
        /// ordering; doing it after Start would race the thread's initial fill.
        /// </param>
        public void Start(int serialSize, IClient client, bool blockingWrites, System.Action prologue = null)
        {
            if (IsActive)
                Abort();            // never two live pacer threads on one instance - see 'generation'

            this.client = client;
            this.serialSize = serialSize;

            serialUsed = 0;
            inflight.Clear();
            Suspended = false;
            aborted = false;
            IsActive = true;

            cts = new CancellationTokenSource();
            channel = new BlockingCollection<Item>();

            link = Comms.com;

            // BlockingWrites is a property of the LINK, not of this pacer - two pacers can exist at once
            // (a tool change dispatches MDI while the job pump is suspended), so only the instance that
            // turned it on may turn it off again. A client whose writes are synchronous anyway
            // (WriteCommand) asks for false and leaves the flag alone entirely.
            if ((ownsBlockingWrites = blockingWrites))
                link.BlockingWrites = true;

            // -= before += : ReplyClassified is a real multicast event and clients REUSE a pacer instance
            // across runs - a Start() that ever ran before the previous Run()'s finally unsubscribed would
            // silently double-process every ack. -= on a not-currently-subscribed handler is a no-op.
            link.ReplyClassified -= OnReplyClassified;
            link.ReplyClassified += OnReplyClassified;

            if (prologue != null)
                prologue();

            int gen = ++generation;
            thread = new Thread(() => Run(gen)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "WirePacer:" + name };
            thread.Start();
        }

        /// <summary>Stop dispatching. Idempotent, callable from any thread including the pacer's own.</summary>
        public void Abort()
        {
            aborted = true;
            if (link != null)
                link.ReplyClassified -= OnReplyClassified;   // stop routing replies to a dying pacer
            cts?.Cancel();                                   // unblock the channel Take
        }

        /// <summary>
        /// False once the connection this pacer tapped has been replaced (reconnect / Restart). A
        /// long-lived client must re-Start rather than keep writing into a dead tap - a tap left on a
        /// closed stream is silence, and silence here reads exactly like a wedged controller.
        /// </summary>
        public bool IsTappedTo(StreamComms current)
        {
            return link != null && ReferenceEquals(link, current);
        }

        /// <summary>Hand a signal to the pacer thread (operator answers, idle nudges). Thread-safe.</summary>
        public void Post(string signal)
        {
            if (!aborted && channel != null)
                try { channel.Add(new Item(Kind.Signal, signal)); } catch { }
        }

        // Runs ON THE COMMS READ THREAD - must not block (see StreamComms.ReplyClassified's contract).
        private void OnReplyClassified(Comms.ReplyClass cls, string reply)
        {
            if (Suspended)
                return;

            if (cls == Comms.ReplyClass.Ack || cls == Comms.ReplyClass.Nak)
                Enqueue(Kind.Reply, reply);
            else if (cls == Comms.ReplyClass.Status)
            {
                if (LogStatusReports && DebugLog.Enabled)
                    DebugLog.Write(name, "STATUS " + reply);
                if (ForwardStatus)
                    Enqueue(Kind.Status, reply);
            }
        }

        private void Enqueue(Kind kind, string text)
        {
            if (!aborted && channel != null)
                try { channel.Add(new Item(kind, text)); } catch { }
        }

        private void Run(int gen)
        {
            try
            {
                client.OnStarted();             // initial buffer fill

                while (!aborted)
                {
                    Item item;
                    try { item = channel.Take(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    // gen: a later Start has taken over, and 'aborted' has already been reset to false
                    // for it - so this thread must stop delivering callbacks on that check alone. Both
                    // threads share the client's accounting, and only one of them may drive it.
                    if (aborted || gen != generation)
                        break;

                    switch (item.Kind)
                    {
                        case Kind.Reply:
                            client.OnReply(item.Text);
                            break;

                        case Kind.Status:
                            client.OnStatus(item.Text);
                            break;

                        case Kind.Signal:
                            client.OnSignal(item.Text);
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // never let a pacer-thread exception take down the app; the host's state machine still owns recovery
            }
            finally
            {
                // Only if no later Start has taken over (see 'generation'). Abort() has already dropped
                // the tap for the run being torn down, so a stale thread has nothing left to clean up.
                if (gen == generation)
                {
                    if (link != null)
                    {
                        link.ReplyClassified -= OnReplyClassified;
                        if (ownsBlockingWrites)
                            link.BlockingWrites = false;
                    }
                    IsActive = false;
                }
            }
        }

        // ---- dispatch accounting (pacer thread only) ----

        /// <summary>grbl character counting: is there room in the controller's RX buffer for this write?</summary>
        public bool HasRoom(int length)
        {
            return serialUsed < serialSize - length;
        }

        /// <summary>How many writes are awaiting an acknowledgement.</summary>
        public int Outstanding { get { return inflight.Count; } }

        /// <summary>The oldest unanswered write - the evidence a client needs to decide it never will be.</summary>
        public bool TryPeekOldest(out Sent sent)
        {
            if (inflight.Count == 0)
            {
                sent = default(Sent);
                return false;
            }
            sent = inflight.Peek();
            return true;
        }

        /// <summary>
        /// Write a line and charge it against the buffer.
        /// </summary>
        /// <param name="asCommand">
        /// true = StreamComms.WriteCommand (sets CommandState=AwaitAck, encodes UTF8, appends its own
        /// terminator), false = WriteString with a '\r' appended (Encoding.Default). The two differ in
        /// ways that reach the controller, so each client keeps the one it was verified with rather than
        /// having them reconciled here - the job pump has always used WriteString, MDI WriteCommand.
        /// </param>
        public void Write(string text, int length, int tag, bool asCommand = false)
        {
            inflight.Enqueue(new Sent { Text = text, Length = length, Tag = tag, AtUtc = DateTime.UtcNow });
            serialUsed += length;

            if (asCommand)
                link.WriteCommand(text);
            else
                link.WriteString(text + '\r');
        }

        /// <summary>An acknowledgement arrived: retire the oldest outstanding write. False = nothing was outstanding (a stray reply).</summary>
        public bool TryComplete(out Sent sent)
        {
            if (inflight.Count == 0)
            {
                sent = default(Sent);
                return false;
            }

            sent = inflight.Dequeue();
            serialUsed -= sent.Length;
            if (serialUsed < 0)
                serialUsed = 0;
            return true;
        }

        /// <summary>
        /// Drop all outstanding accounting. For the one case where the controller has PROVED the
        /// accounting wrong - it reports Idle, so its buffer is empty and any ack still owed was missed.
        /// Returns what was dropped so a client can decide whether any of it must not be replayed.
        /// </summary>
        public List<Sent> ResetAccounting()
        {
            var dropped = new List<Sent>(inflight);
            inflight.Clear();
            serialUsed = 0;
            return dropped;
        }
    }
}
