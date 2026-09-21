/*
 * MdiDispatcher.cs - part of CNC Core
 *
 * Ack-paced dispatch for typed and programmatic commands (MDI), on the shared WirePacer.
 *
 */

/*

Replaces JobRunner's private SendMDI pacing (docs/Architecture-MDI-Dispatch-Unification.md). That
mechanism queued onto the program source's own Commands queue, used streamingState==SendMDI as its
busy flag, and wrote the next command from inside ResponseReceived on the UI thread - which meant it
had to learn every grblHAL ack quirk separately from the job pump, and had a failure mode the pump
did not: ResponseReceived returns early while the controller reports Jog, so an ack arriving during a
jog never drained the queue, and a command enqueued during one could sit there indefinitely (observed
live: queue depth climbing 1..5, jogging dead until an app restart).

What is kept, deliberately:

  - ONE outstanding command at a time. Not an efficiency choice: a macro sending several lines in one
    C# loop with no ack pacing put 14 lines / 670 bytes on the wire in 6ms, and the controller threw a
    string of "error:71 - Unknown operation" it should never have seen.
  - Commands are written with StreamComms.WriteCommand, the call MDI has always used (it sets
    CommandState=AwaitAck and encodes UTF8, both of which other code depends on) - not the pump's
    WriteString.
  - The cancel-flushed $J release, with its purge of queued jogs (3eb08c6). See OnStatus.

What changes: dispatch happens on the pacer thread off the classified-reply tap, so it no longer
depends on the UI thread's response handler running - the Jog-state early return cannot starve it,
and the release below fires on its own evidence instead of waiting for the operator to send another
command before anyone notices the queue is stuck.

*/

using System;
using System.Collections.Generic;

namespace CNC.Core
{
    public class MdiDispatcher : WirePacer.IClient
    {
        // How long an unanswered $J= must sit, with the controller REPORTING Idle, before it is declared
        // flushed. A real jog ack lands in ~18ms (wire-measured 2026-08-06); a real jog in progress
        // reports <Jog|, not Idle - so 750ms of Idle silence is far past any legitimate case.
        private const int JogFlushMs = 750;

        private const string Kick = "\0mdi-kick";

        private readonly WirePacer pacer = new WirePacer("mdi");
        private readonly object sync = new object();
        private readonly Queue<string> queue = new Queue<string>();

        /// <summary>Commands waiting to go out. Diagnostics only - the pacer thread owns dispatch.</summary>
        public int Queued { get { lock (sync) return queue.Count; } }

        /// <summary>
        /// Queue a command for dispatch. Called on the UI thread (JobRunner.SendCommand); realtime
        /// bytes never come here - they bypass all pacing by design.
        /// </summary>
        public void Send(string command)
        {
            if (Comms.com == null)
                return;             // no link to write to (mid teardown/reconnect); nothing to queue for

            lock (sync)
            {
                queue.Enqueue(command);

                // Listen again BEFORE anything is written, so no ack can land in the deaf window.
                pacer.Suspended = false;

                // A pacer whose link has been replaced (reconnect, Restart relaunch) is tapped to a dead
                // stream: it would never see another reply, which presents exactly like a wedged
                // controller. Re-Start on the current link instead. Start() aborts the previous run.
                // Aborted-but-not-yet-exited counts as not running: Post would be dropped by a dying
                // pacer and this command would sit in the queue with nothing left to dispatch it.
                if (!pacer.IsActive || pacer.Aborted || !pacer.IsTappedTo(Comms.com))
                {
                    // serialSize is irrelevant here - dispatch is gated on "nothing outstanding", not on
                    // character counting - and blockingWrites stays false: WriteCommand is synchronous
                    // already, and the flag is global, so setting it would fight a streaming job for it.
                    pacer.Start(int.MaxValue, this, blockingWrites: false);
                    return;                     // OnStarted does the first dispatch
                }
            }

            pacer.Post(Kick);                   // dispatch happens on the pacer thread, never here
        }

        /// <summary>Stop dispatching (connection lost / shutdown). Idempotent.</summary>
        public void Abort()
        {
            lock (sync)
            {
                queue.Clear();
                pacer.Abort();
            }
        }

        // ---- WirePacer.IClient (all run on the pacer thread) ----

        void WirePacer.IClient.OnStarted()
        {
            DispatchNext();
        }

        void WirePacer.IClient.OnSignal(string signal)
        {
            DispatchNext();
        }

        void WirePacer.IClient.OnReply(string reply)
        {
            WirePacer.Sent done;
            if (!pacer.TryComplete(out done))    // stray reply (something else on this link asked a question)
                return;

            if (DebugLog.Enabled)
                DebugLog.Write("jobrunner", string.Format("MDI ACKED \"{0}\" -> {1} after {2:F0}ms", done.Text, reply, done.AgeMs));

            // A "$n=value" typed at the MDI changes the machine without going anywhere near the settings UI,
            // so nothing refreshed the cached copy - this is the one place that sees the command and its
            // reply together. No-op for everything that is not an accepted setting write.
            GrblSettings.NoteExternalWrite(done.Text, reply);

            DispatchNext();
        }

        // Status reports are forwarded only while a $J is outstanding (see DispatchNext), so this is the
        // cancel-flushed jog case and nothing else.
        //
        // grblHAL FLUSHES a $J= whose 0x85 jog-cancel lands before it is parsed: no ok, no error,
        // nothing, machine stays Idle - proven on the wire 2026-08-08 12:01 ($J= out, 0x85 3ms behind
        // it, zero replies ever). Ack pacing waits for exactly that reply, so without this release the
        // queue never drains again. JogGate learned this firmware contract long ago (its Alive()/
        // Clear()); this is the same cure, now in the one place that does the pacing.
        //
        // Queued $J rows are DISCARDED, not replayed - a jog the operator asked for a second ago is not
        // one they still want (JogGate's own freshness rule), and replaying a stale continuous jog whose
        // cancel byte went out long ago is genuinely dangerous (the incident had $J=G91G21Z152 queued
        // with nothing left armed to cancel it). Non-jog rows keep their order and resume draining.
        //
        // Scoped strictly to $J, deliberately. A generic "unanswered too long" release would mask real
        // link faults (LinkMonitor's job to call), and it would be WRONG on its face: grblHAL acks a
        // G4 dwell only when the dwell completes and reports <Idle| throughout it, so a timed release
        // would dispatch the next command in the middle of a G4 P30.
        void WirePacer.IClient.OnStatus(string report)
        {
            WirePacer.Sent outstanding;
            if (!report.StartsWith("<Idle") || !pacer.TryPeekOldest(out outstanding)
                 || !IsJog(outstanding.Text) || outstanding.AgeMs < JogFlushMs)
                return;

            WirePacer.Sent flushed;
            pacer.TryComplete(out flushed);

            int purged = 0, kept;
            lock (sync)
            {
                var survivors = new Queue<string>();
                while (queue.Count > 0)
                {
                    string queued = queue.Dequeue();
                    if (IsJog(queued))
                        purged++;
                    else
                        survivors.Enqueue(queued);
                }
                while (survivors.Count > 0)
                    queue.Enqueue(survivors.Dequeue());
                kept = queue.Count;
            }

            if (DebugLog.Enabled)
                DebugLog.Write("jobrunner", string.Format(
                    "MDI RELEASED - outstanding \"{0}\" unanswered for {1:F0}ms with the controller Idle (jog-cancel flush); purged {2} stale queued $J, kept {3}",
                    flushed.Text, flushed.AgeMs, purged, kept));

            DispatchNext();
        }

        private static bool IsJog(string command)
        {
            return command != null && command.StartsWith("$J", StringComparison.OrdinalIgnoreCase);
        }

        // Pacer thread only. Write the next queued command, if the previous one has been answered.
        private void DispatchNext()
        {
            if (pacer.Outstanding > 0 || pacer.Aborted)
                return;                          // still awaiting an ack - one command at a time

            string command;
            int left;
            lock (sync)
            {
                if (queue.Count == 0)
                {
                    // Nothing owed and nothing waiting: stop taking replies until the next Send. Every
                    // reply on this link would otherwise wake this thread - including every ack of a
                    // streaming job, which is the one path that must not carry avoidable noise. Send()
                    // clears this before it writes anything, so no ack can be missed by going deaf here.
                    pacer.Suspended = true;
                    pacer.ForwardStatus = false;
                    return;
                }
                command = queue.Dequeue();
                left = queue.Count;
            }

            // Status reports are the release signal for a jog that may never be acked (OnStatus) - and
            // only for that, so nothing else pays for the ~5/s traffic.
            pacer.ForwardStatus = IsJog(command);

            if (DebugLog.Enabled)
                DebugLog.Write("jobrunner", string.Format("MDI WROTE \"{0}\" - {1} left queued", command, left));

            pacer.Write(command, command.Length + 1, -1, asCommand: true);
        }
    }
}
