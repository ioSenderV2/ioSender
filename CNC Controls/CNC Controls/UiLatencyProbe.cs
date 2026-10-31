/*
 * UiLatencyProbe.cs - part of CNC Controls library
 *
 * Samples how late an Input-priority dispatcher callback runs, and feeds it to PollDiag.UiLate.
 *
 * WHY THIS EXISTS. PollDiag's four original numbers all measure the machine talking to us - the poller
 * firing, the write blocking, the reply arriving, the handler running. None of them measures US failing
 * to answer the OPERATOR. Feed Hold is an ordinary UI-thread click handler
 * (JobControl.btnHold_Click -> Comms.com.WriteByte), so "the UI thread is saturated" and "the operator
 * cannot stop the machine" are the same sentence - and Feed Hold has been starved for a whole job
 * before. That is the strongest remaining argument for freezing the carve view during a job, and it was
 * the one thing the instrument could not see.
 *
 * WHY SAMPLED RATHER THAN MEASURED AT THE PRESS. A click handler cannot see when the click physically
 * happened - by the time it runs, the waiting is already over and unrecorded. Worse, an instrument that
 * only reads when the operator remembers to press something is not running during the minutes that
 * matter. So this samples continuously and the press just stamps itself with the current value.
 *
 * WHY Input PRIORITY. It is the priority mouse input is dispatched at, so lateness measured here is the
 * latency a press would actually have seen. Measuring at Normal would read lower (Normal outranks
 * Input's queue position in some orderings) and Background would read catastrophically high because
 * StreamPump's own display marshals sit there deliberately - neither would be the operator's number.
 *
 * WHY A DispatcherTimer AND NOT A STOPWATCH ON A WORKER. The quantity wanted is queue delay on the UI
 * thread specifically. A DispatcherTimer at Input priority is scheduled for a fixed interval and runs
 * when the dispatcher gets to it, so the excess over its own interval IS the delay, measured by the
 * thread that suffers it.
 *
 * Inert unless PollDiag is on (-debuglog / -debuglog=poll). The timer is not even created otherwise.
 */

using System;
using System.Diagnostics;
using System.Windows.Threading;
using CNC.Core;

namespace CNC.Controls
{
    public static class UiLatencyProbe
    {
        // 250 ms: frequent enough to catch a stall inside a one-minute report window, rare enough that
        // the probe itself is not a load on the thread it is measuring. An instrument that changes the
        // reading is not an instrument - the same reason PollDiag aggregates instead of logging per reply.
        private const int SampleIntervalMs = 250;

        private static DispatcherTimer timer;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static double lastTickAt;

        /// <summary>
        /// Start sampling on the calling thread's dispatcher. Safe to call more than once - the second
        /// call does nothing. No-op when PollDiag is off, so a normal run creates no timer at all.
        /// </summary>
        public static void Start()
        {
            if (timer != null || !PollDiag.Enabled)
                return;

            lastTickAt = clock.Elapsed.TotalMilliseconds;

            timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(SampleIntervalMs)
            };
            timer.Tick += (s, e) =>
            {
                double now = clock.Elapsed.TotalMilliseconds;
                double elapsed = now - lastTickAt;
                lastTickAt = now;

                // Lateness, not elapsed time: the interval is what we asked for, so anything beyond it is
                // the dispatcher not getting to us. Floored at zero because a timer may fire a hair early
                // and a negative "lateness" would drag the mean below what any press could experience.
                double late = elapsed - SampleIntervalMs;
                PollDiag.UiLate(late > 0d ? late : 0d);
            };
            timer.Start();

            DebugLog.Write("poll", string.Format("UI latency probe started at Input priority, {0} ms interval", SampleIntervalMs));
        }
    }
}
