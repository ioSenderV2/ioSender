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

        private static double lastSendAt = -1d;
        private static double lastReportAt;
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

            lock (sync)
            {
                double now = NowMs;
                if (lastSendAt >= 0d)
                    sendGap.Add(now - lastSendAt);
                lastSendAt = now;
                writeMs.Add(writeMilliseconds);
                MaybeReport(now);
            }
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

            lock (sync)
                marshalMs.Add(NowMs - stamp);
        }

        /// <summary>How long DataReceived took - the cost of the handler fan-out once it does run.</summary>
        public static void Processed(double milliseconds)
        {
            if (!Enabled)
                return;

            lock (sync)
                processMs.Add(milliseconds);
        }

        // Caller holds the lock.
        private static void MaybeReport(double now)
        {
            if (now - lastReportAt < ReportIntervalMs)
                return;

            lastReportAt = now;

            string subs;
            try { subs = ProbeSubscribers(); }
            catch (Exception ex) { subs = "probe failed: " + ex.Message; }   // a diagnostic must never take the app down

            DebugLog.Write("poll", string.Format(
                "uptime={0:F1}h  send: n={1} mean={2:F0}ms max={3:F0}ms  write: mean={4:F1}ms max={5:F0}ms  " +
                "marshal: n={6} mean={7:F1}ms max={8:F0}ms  process: mean={9:F1}ms max={10:F0}ms  subs: {11}",
                (DateTime.UtcNow - startedUtc).TotalHours,
                sendGap.Count, sendGap.Mean, sendGap.Max,
                writeMs.Mean, writeMs.Max,
                marshalMs.Count, marshalMs.Mean, marshalMs.Max,
                processMs.Mean, processMs.Max,
                subs));

            sendGap.Reset();
            writeMs.Reset();
            marshalMs.Reset();
            processMs.Reset();
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
