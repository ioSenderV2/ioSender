/*
 * JogGate.cs - part of CNC Core
 *
 * Back-pressure for DISCRETE jog commands ($J), shared by every input that can issue one.
 *
 * Why this exists: sending a second $J before the first has been acknowledged can wedge grblHAL
 * completely - confirmed on real hardware 2026-07-15 (console log), where three Y+ jog clicks landing
 * within ~500ms left the controller answering nothing at all, not even a bare $I, until a power cycle.
 * That is a firmware fault; this does not fix it, it stops ioSender from being able to trigger it.
 *
 * This started life as a set of private statics inside JogBaseControl, which left the Xbox controller's
 * D-pad (ControllerMapper.JogStep) completely ungated - it builds its own $J and sends it directly, so
 * rapid D-pad presses reproduced the original wedge unmitigated. Living in Core means both the on-screen
 * jog panels and the gamepad share one gate, per the rule that what talks to the machine belongs here.
 *
 * NOT used by analog stick jogging (ControllerMapper's AnalogJogLoop). That deliberately re-sends a short
 * $J segment every ~67ms and relies on the next segment landing BEFORE the previous one finishes so
 * grblHAL blends them into continuous motion - overlapping sends are the entire mechanism there, and
 * gating it would reintroduce the stutter that loop was written to fix. Only discrete, per-press jogs go
 * through here.
 */

using System;

namespace CNC.Core
{
    public static class JogGate
    {
        // Safety net only, not the normal path: long enough that a genuinely wedged controller stays gated
        // (nothing sent would get through anyway), short enough that one dropped "ok" doesn't lock jogging
        // out for the rest of the session.
        private const int AckTimeoutMs = 2000;

        private static readonly object sync = new object();
        private static bool pending;
        private static DateTime sentAtUtc = DateTime.MinValue;

        // True => the caller may send its $J now (and this call has recorded it as outstanding).
        // False => drop this jog entirely. Dropping a click is strictly better than risking the wedge;
        // there is no queue-and-send-later, because a jog the operator asked for half a second ago is not
        // one they still want by the time the controller frees up.
        //
        // Locked rather than a volatile read/write pair: the on-screen panels fire on the UI thread while
        // the gamepad's button events arrive on ControllerService's own thread, so the test-and-set here
        // genuinely races. The old JogBaseControl version was a bare volatile bool and could let two jogs
        // through together when both inputs were used at once.
        // Every trace line below is built under the lock but WRITTEN outside it: DebugLog.Write does file
        // I/O, and this lock is taken by the UI thread (Ack, for every "ok") as well as by whichever thread
        // the operator's input arrived on. Holding it across a disk write would make the gate itself a
        // source of the stalls it is meant to expose.
        public static bool TryBegin()
        {
            string trace = null;
            bool allowed;

            lock (sync)
            {
                double waiting = pending ? (DateTime.UtcNow - sentAtUtc).TotalMilliseconds : 0d;

                if (pending && waiting < AckTimeoutMs)
                {
                    // The operator pressed and nothing happened. Traced because this is indistinguishable
                    // from a dead app at the machine, and it is the whole explanation for "I can only jog
                    // once every two seconds" - that cadence IS AckTimeoutMs, not a coincidence.
                    if (DebugLog.Enabled)
                        trace = string.Format("DROPPED - previous $J unacked for {0:F0}ms (gate opens at {1}ms)",
                                              waiting, AckTimeoutMs);
                    allowed = false;
                }
                else
                {
                    // Opened by the timeout rather than by an ack: the previous $J was NEVER acknowledged.
                    // That is the fault worth chasing - the gate is doing its job, something upstream is not.
                    if (pending && DebugLog.Enabled)
                        trace = string.Format("TIMEOUT ALLOW - previous $J never acked after {0:F0}ms, sending anyway",
                                              waiting);

                    pending = true;
                    sentAtUtc = DateTime.UtcNow;
                    allowed = true;
                }
            }

            if (trace != null)
                DebugLog.Write("jog", trace);

            return allowed;
        }

        // An "ok"/"error" came back - whatever was outstanding is done. Called from GrblViewModel's response
        // handling for every command response, not just a jog's own: the old gate behaved the same way, and a
        // response to anything at all proves the controller is still answering, which is the property this
        // gate actually cares about.
        public static void Ack()
        {
            string trace = null;

            lock (sync)
            {
                // Only interesting when something was actually outstanding, and only worth a line when it
                // took long enough to have blocked a press - an ordinary few-ms ack is noise.
                if (pending && DebugLog.Enabled)
                {
                    double waited = (DateTime.UtcNow - sentAtUtc).TotalMilliseconds;
                    if (waited > 250d)
                        trace = string.Format("SLOW ACK - $J acknowledged after {0:F0}ms", waited);
                }
                pending = false;
            }

            if (trace != null)
                DebugLog.Write("jog", trace);
        }

        // Jog cancel (0x85) is a realtime byte - it is never gated, and it clears whatever it is cancelling,
        // so nothing stays outstanding behind it.
        public static void Clear()
        {
            lock (sync)
                pending = false;
        }
    }
}
