/*
 * PollDiag.cs - part of CNC Core library
 *
 * Diagnostic instrument for one specific question: when the DRO falls behind the machine, is the POLLER
 * late sending "?", or is the reply sitting in a queue waiting for a saturated UI thread?
 *
 * Written 2026-08-06 against a measured symptom, not a theory: over an ~11 hour session the interval
 * between status reports in the console log drifted from 0.264 s (15 minutes in, matching a healthy
 * baseline) to 0.758 s, against a configured PollInterval of 200 ms - degrading monotonically with
 * UPTIME rather than with workload. The console log alone cannot say which end is at fault, because its
 * timestamps are written when a line is PROCESSED and "?" itself is never logged at all. Guessing between
 * the two, and patching the guess, is exactly the failure this exists to prevent.
 *
 * The four numbers, and what each one decides:
 *
 *   send interval   PollGrbl's timer actually firing and writing. If this is ~200 ms while status reports
 *                   arrive every 1.2 s, the poller is innocent and the delay is downstream.
 *   write duration  how long Comms.WriteByte blocks on the timer thread. A rising number here means send
 *                   -path contention, not UI load.
 *   marshal latency THE decisive one. Measured across Comms.PostTo: stamped just before context.Post and
 *                   read again inside the posted callback, so it is precisely how long a reply waited for
 *                   the UI thread to get to it. Rises with UI-thread saturation and nothing else.
 *   process time    how long DataReceived itself takes once it does run - the cost of the handler fan-out.
 *
 * Plus subscriber counts on the view model's three hot events. If those climb over a session, handlers are
 * being added on tab activation without being removed, every status report fans out to more work than the
 * last, and that is both the cause and the fix. Flat counts kill that theory outright.
 *
 * AGGREGATED, reported once a minute. Deliberately not per-reply: at 5-10 replies/second over an 11 hour
 * session that is a million lines, and - worse - the logging would land on the very UI thread whose load is
 * the thing being measured. An instrument that changes the reading is not an instrument.
 *
 * Off unless DebugLog is on, and everything here is behind one bool test when it is off. Turn it on with:
 *
 *     ioSender.exe -debuglog=poll
 *
 * (bare -debuglog works too and logs every category; "poll" is this one's category name).
 */

using System;
using System.Diagnostics;

namespace CNC.Core
{
    public static class PollDiag
    {
        // One bool test on every hot path when tracing is off. DebugLog's own category filter decides
        // whether the periodic report actually reaches the file; the sampling below is cheap enough that
        // gating it on the coarse flag rather than per-category is the right trade.
        public static bool Enabled { get { return DebugLog.Enabled; } }

        private const int ReportIntervalMs = 60000;

        // Outliers are logged the moment they happen, not just folded into a mean. The fault this exists to
        // catch is a ONE-OFF: a single reply arriving 7.6s late while every other number in the minute stays
        // healthy. Averaging is exactly the wrong lens for that - the first version of this instrument
        // reported "marshal max=259ms" for a minute containing a multi-second stall, because the stall was
        // not on the path being averaged.
        private const int MarshalWarnMs = 250;
        private const int RxGapWarnMs = 1000;

        // ...but a genuinely broken link would then write a line per reply forever. Cap the immediate lines
        // per reporting window and carry the suppressed count into the periodic line, so the information
        // survives without the flood.
        private const int MaxWarningsPerWindow = 20;

        private static readonly object sync = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();

        // Read once a minute off the app-wide main model. Deliberately NOT captured from a constructor:
        // ioSender builds three GrblViewModels (the main one plus throwaways in OffsetView/ToolView), so a
        // ctor-registered probe would report whichever happened to be constructed last - a number that looks
        // authoritative and means nothing. Grbl.GrblViewModel is the one the machine actually reports through.
        private static string ProbeSubscribers()
        {
            var model = Grbl.GrblViewModel;
            return model == null ? "no model" : model.DiagSubscriberCounts();
        }

        private sealed class Stat
        {
            public long Count;
            public double Total, Max;
            public void Add(double v)
            {
                Count++;
                Total += v;
                if (v > Max)
                    Max = v;
            }
            public double Mean { get { return Count == 0 ? 0d : Total / Count; } }
            public void Reset() { Count = 0; Total = 0d; Max = 0d; }
        }

        private static readonly Stat sendGap = new Stat();     // ms between consecutive "?" writes
        private static readonly Stat writeMs = new Stat();     // ms spent inside Comms.WriteByte
        private static readonly Stat marshalMs = new Stat();   // ms a reply waited for the UI thread
        private static readonly Stat processMs = new Stat();   // ms spent inside DataReceived
        private static readonly Stat rxGap = new Stat();       // ms between consecutive replies off the wire

        private static double lastSendAt = -1d;
        private static double lastRxAt = -1d;
        private static double lastReportAt;
        private static int warningsThisWindow, warningsSuppressed;
        private static readonly DateTime startedUtc = DateTime.UtcNow;

        private static double NowMs { get { return clock.Elapsed.TotalMilliseconds; } }

        /// <summary>
        /// The poll timer fired and wrote. <paramref name="writeMilliseconds"/> is how long the write itself
        /// blocked - separating "the timer is late" from "the write is slow", which have different causes.
        /// </summary>
        public static void PollSent(double writeMilliseconds)
        {
            if (!Enabled)
                return;

            string report;

            lock (sync)
            {
                double now = NowMs;
                if (lastSendAt >= 0d)
                    sendGap.Add(now - lastSendAt);
                lastSendAt = now;
                writeMs.Add(writeMilliseconds);
                report = BuildReport(now);
            }

            if (report != null)
                DebugLog.Write("poll", report);   // outside the lock - see Emit
        }

        /// <summary>
        /// A reply came off the wire. Called on the READ thread, before any marshalling, so the gap measured
        /// here is the link's own silence - the controller not answering, or the connection stalling. Compare
        /// against the marshal latency: a long gap here with a short marshal means the app was fine and
        /// nothing arrived; a short gap with a long marshal means it arrived and then sat in a queue.
        /// </summary>
        public static void RxArrived()
        {
            if (!Enabled)
                return;

            string warning = null;

            lock (sync)
            {
                double now = NowMs;
                if (lastRxAt >= 0d)
                {
                    double gap = now - lastRxAt;
                    rxGap.Add(gap);
                    if (gap > RxGapWarnMs && Admit())
                        warning = string.Format("RX GAP {0:F0}ms - nothing received off the wire (poller still sending)", gap);
                }
                lastRxAt = now;
            }

            Emit(warning);
        }

        /// <summary>
        /// Stamp taken on the receiving thread immediately before a reply is posted to the UI thread.
        /// Pair with <see cref="MarshalArrived"/>, which is called inside the posted callback.
        /// </summary>
        public static double MarshalStamp()
        {
            return Enabled ? NowMs : 0d;
        }

        /// <summary>The posted callback is now running: record how long it waited to get here.</summary>
        public static void MarshalArrived(double stamp)
        {
            if (!Enabled || stamp <= 0d)
                return;

            string warning = null;

            lock (sync)
            {
                double waited = NowMs - stamp;
                marshalMs.Add(waited);
                if (waited > MarshalWarnMs && Admit())
                    warning = string.Format("MARSHAL STALL {0:F0}ms - a reply waited this long for the UI thread", waited);
            }

            Emit(warning);
        }

        // Caller holds the lock: has this window's warning budget got room? Counts the overflow either way.
        private static bool Admit()
        {
            if (warningsThisWindow >= MaxWarningsPerWindow)
            {
                warningsSuppressed++;
                return false;
            }

            warningsThisWindow++;
            return true;
        }

        // Deliberately OUTSIDE the lock. DebugLog.Write does file I/O, and this lock is taken by the comms
        // read thread, the UI thread and the poll timer thread - holding it across a disk write would let the
        // instrument produce the very stalls it is measuring.
        private static void Emit(string warning)
        {
            if (warning != null)
                DebugLog.Write("poll", "!! " + warning);
        }

        /// <summary>How long DataReceived took - the cost of the handler fan-out once it does run.</summary>
        public static void Processed(double milliseconds)
        {
            if (!Enabled)
                return;

            lock (sync)
                processMs.Add(milliseconds);
        }

        // Caller holds the lock. Returns the line to write (the CALLER writes it, outside the lock - see
        // Emit), or null when the reporting interval has not elapsed yet.
        private static string BuildReport(double now)
        {
            if (now - lastReportAt < ReportIntervalMs)
                return null;

            lastReportAt = now;

            string subs;
            try { subs = ProbeSubscribers(); }
            catch (Exception ex) { subs = "probe failed: " + ex.Message; }   // a diagnostic must never take the app down

            string line = string.Format(
                "uptime={0:F1}h  send: n={1} mean={2:F0}ms max={3:F0}ms  write: mean={4:F1}ms max={5:F0}ms  " +
                "rx: n={6} mean={7:F0}ms max={8:F0}ms  marshal: n={9} mean={10:F1}ms max={11:F0}ms  " +
                "process: mean={12:F1}ms max={13:F0}ms  subs: {14}{15}",
                (DateTime.UtcNow - startedUtc).TotalHours,
                sendGap.Count, sendGap.Mean, sendGap.Max,
                writeMs.Mean, writeMs.Max,
                rxGap.Count, rxGap.Mean, rxGap.Max,
                marshalMs.Count, marshalMs.Mean, marshalMs.Max,
                processMs.Mean, processMs.Max,
                subs,
                warningsSuppressed > 0 ? string.Format("  [{0} further warnings suppressed]", warningsSuppressed) : "");

            sendGap.Reset();
            writeMs.Reset();
            marshalMs.Reset();
            processMs.Reset();
            rxGap.Reset();
            warningsThisWindow = warningsSuppressed = 0;

            return line;
        }

        /// <summary>
        /// Count an event's subscribers without caring what its delegate type is. Returns -1 when the field
        /// is null (no subscribers at all), which reads differently from 0 and is worth keeping distinct.
        /// </summary>
        public static int Subscribers(Delegate handler)
        {
            return handler == null ? -1 : handler.GetInvocationList().Length;
        }
    }
}
