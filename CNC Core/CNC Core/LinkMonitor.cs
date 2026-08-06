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
        // 10s against a 200ms poll interval = 50 unanswered polls. Long enough that no legitimate pause
        // reaches it (a busy controller still answers "?" between blocks - status reporting is handled in
        // grblHAL's realtime path, not the protocol loop, so even a long blocking move keeps replying),
        // short enough that the operator learns the truth while it still means something.
        public static int TimeoutMs = 10000;

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
            if (unchecked(Environment.TickCount - lastRx) < TimeoutMs)
                return false;

            // First caller past the threshold wins and reports; the rest see it is already reported.
            return Interlocked.CompareExchange(ref reported, 1, 0) == 0;
        }

        /// <summary>Milliseconds since the last reply - for the message that reports the outage.</summary>
        public static int SilentMs { get { return unchecked(Environment.TickCount - lastRx); } }
    }
}
