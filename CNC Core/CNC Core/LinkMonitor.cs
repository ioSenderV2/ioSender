/*
 * LinkMonitor.cs - part of CNC Core library
 *
 * Detects a controller that has stopped answering while the link still LOOKS open, and hands that fact to
 * the transport's existing Reconnector so the ordinary "connection lost -> retry -> reconnected" path runs.
 *
 * Why this exists. Every transport reports loss from a FAILED read or write - TelnetStream.cs and
 * SerialStream.cs both call reconnector.NotifyLost() from a catch. That covers a socket that errors. It does
 * not cover a HALF-OPEN one: the peer is gone, but the local TCP stack still accepts writes into its send
 * buffer and .NET's Socket.Connected keeps returning true (it reflects the last completed I/O, not live
 * state). Nothing fails, so nothing is ever reported, and the app sits there looking connected.
 *
 * Observed 2026-08-06, and this is the exact shape of it: the controller tore down the telnet session at
 * 07:44:52. The sender sent 5240 status polls over the next 18 minutes and received ZERO bytes. No
 * exception, no error, no state change - the UI just quietly stopped updating. grblHAL only announced
 * "SERIAL STREAM ACTIVE" at 08:03 when lwIP finally reaped the dead PCB, and a fresh session at 08:22
 * brought it back. Eighteen minutes in which the one observable fact - "we are talking and nothing is
 * talking back" - was sitting in plain sight and nobody was looking at it.
 *
 * So look at it. Rx() stamps a timestamp on every reply (one interlocked store, on the read thread), and
 * the poll timer asks Starved() once per poll. Deliberately NOT gated on DebugLog the way PollDiag is -
 * PollDiag is an instrument you switch on when investigating, this is a safety net that is worthless unless
 * it is always armed.
 *
 * ---- What silence does NOT prove ----
 *
 * This class has produced two false positives, both by treating "no reply" as "no link", and both cost a
 * real operation on real hardware. Read them before tightening anything here.
 *
 *   YModem transfer - Rx() only stamps in Comms.PostTo, and a transfer sets EventMode = false and pulls
 *   bytes with ReadByte(), so the clock sees zero RX BY CONSTRUCTION on a link that is busy and healthy.
 *   Guarded in PollGrbl.pollTimer_Elapsed (EventMode) and by suspending the poller outright in
 *   YModem.Upload.
 *
 *   Homing - grblHAL answers '<Home|...>' once when '$H' is accepted and then goes silent for the whole
 *   cycle. A 10s window declared the link lost 10.09s in and tore it down mid-home. Hence
 *   QuietStateTimeoutMs below, and the state check in the poll timer.
 *
 * The pattern in both: the app knew perfectly well it had asked for something that produces silence, and
 * this class was not told. Before shortening a timeout here, ask what legitimately goes quiet for that
 * long - the cost of being wrong is aborting a machine operation, not a late log line.
 *
 * What it does NOT do: decide anything about the connection itself. It calls NotifyLinkLost() and stops.
 * Reconnector owns the retry policy and the ConnectionLost/Reconnected events, and the UI already listens
 * to those - so a starved link now surfaces through exactly the same route as a socket that threw.
 */

using System;
using System.Threading;

namespace CNC.Core
{
    public static class LinkMonitor
    {
        // 30s against a 200ms poll interval = 150 unanswered polls.
        //
        // This was 10s, on the claim that "a busy controller still answers '?' between blocks - status
        // reporting is handled in grblHAL's realtime path, not the protocol loop, so even a long blocking
        // move keeps replying". That claim is WRONG, and it cost a homing cycle on 2026-08-06: '$H' went
        // out at 15:36:44.114, grblHAL answered '<Home|...>' exactly once at .129, and then answered
        // nothing at all - every '?' for the next ten seconds went unanswered until this fired and tore
        // down a link that was working perfectly, mid-home, on a moving machine.
        //
        // So the number is no longer defended by an assumption about firmware internals. The outage this
        // exists to catch ran for EIGHTEEN MINUTES; 30s finds it with seventeen minutes to spare, and the
        // 10s that bought nothing had by then produced two false positives (this and the YModem transfer
        // in PollGrbl.pollTimer_Elapsed). When the cost of a false positive is aborting a machine
        // operation, the watchdog gets to be slow.
        public static int TimeoutMs = 30000;

        // States where silence is normal and prolonged, so the window above does not apply. Homing is the
        // proven one; Sleep is here because a sleeping controller is not talking either, by definition.
        // A backstop rather than a suspension: if the link really does die during a home, this still says
        // so eventually instead of leaving the UI frozen and lying - which was the whole point of the
        // class. Five minutes is longer than any plausible homing cycle, including a slow seek, pull-off
        // and second pass on a long axis.
        public static int QuietStateTimeoutMs = 300000;

        // Environment.TickCount, not DateTime.UtcNow: monotonic-ish, no syscall, and a wall-clock jump
        // (NTP correction, DST) must never be able to fake a dead link. Wraps every ~24.9 days; the
        // unchecked subtraction below is correct across the wrap.
        private static int lastRx;

        // Set when we have already reported this outage, so one dead link produces one NotifyLinkLost()
        // and not one per poll. Cleared by Reset() when traffic is flowing again.
        private static int reported;

        /// <summary>
        /// Called on the read thread for every reply that arrives, before any marshalling.
        /// ⚠ ONLY reached via Comms.PostTo. A caller that sets Comms.com.EventMode = false and pulls
        /// replies with ReadByte() - YModem.Upload does exactly that - bypasses this completely, so the
        /// clock keeps running on a link that is busy and healthy. The poll timer therefore does not
        /// evaluate starvation while EventMode is false; see PollGrbl.pollTimer_Elapsed.
        /// </summary>
        public static void Rx()
        {
            // A plain store is sufficient - int writes are atomic on every CLR target, and a reader that
            // sees a value one poll stale is harmless (the timeout is 50 polls wide).
            lastRx = Environment.TickCount;
            if (reported != 0)
                Interlocked.Exchange(ref reported, 0);
        }

        /// <summary>
        /// Restart the clock. Call whenever a quiet period is legitimate and expected rather than
        /// evidence of a fault - on connect, on reconnect, and when polling resumes after a suspend
        /// (during a suspend no polls go out at all, so that silence proves nothing).
        /// </summary>
        public static void Reset()
        {
            lastRx = Environment.TickCount;
            Interlocked.Exchange(ref reported, 0);
        }

        /// <summary>
        /// True exactly once per outage, when polls have been going out and nothing has come back for
        /// longer than <see cref="TimeoutMs"/>. Cheap enough to call on every poll.
        /// </summary>
        public static bool Starved()
        {
            return Starved(TimeoutMs);
        }

        /// <summary>
        /// As <see cref="Starved()"/>, but against a caller-supplied window - so the poll timer can widen
        /// it for a controller state in which silence is expected. See QuietStateTimeoutMs.
        /// </summary>
        public static bool Starved(int timeoutMs)
        {
            if (unchecked(Environment.TickCount - lastRx) < timeoutMs)
                return false;

            // First caller past the threshold wins and reports; the rest see it is already reported.
            return Interlocked.CompareExchange(ref reported, 1, 0) == 0;
        }

        /// <summary>Milliseconds since the last reply - for the message that reports the outage.</summary>
        public static int SilentMs { get { return unchecked(Environment.TickCount - lastRx); } }
    }
}
