/*
 * UiDiag.cs - part of CNC Core library
 *
 * Diagnostic instrument for one specific question: while a program is streaming, WHAT is the UI thread
 * actually doing, and is any single callback long enough to make the app feel unresponsive?
 *
 * Written 2026-09-21 against a reported symptom ("the UI becomes unresponsive when running a program")
 * whose cause was NOT observed. The reply-marshalling priority fault that produced the same complaint in
 * August is fixed (AppConfig.OpenStreamFor posts at DispatcherPriority.Input), so whatever is left is not
 * priority starvation - and priority cannot explain it either way, because once a dispatcher callback
 * STARTS it runs to completion. A single 300 ms callback freezes the UI for 300 ms whatever priority it
 * was posted at. So the thing to measure is DURATION, not order.
 *
 * The suspect paths, all of which run on the UI thread on every executing-line change:
 *
 *   drain        StreamPump.Drain - the coalesced display callback. The PARENT: setting BlockExecuting
 *                inside it fans out synchronously to everything below, so drain's own time should account
 *                for the sum of the others. Its gap statistic gives the duty cycle - what fraction of
 *                wall-clock the UI thread spends in here is exactly how unresponsive the app is.
 *   runstatus    ProgramView.UpdateRunStatus - scans blocks 0..executing for (TOOL ..) / (TOOLPATH ..).
 *   tooltip      ProgramView.UpdateTitleTooltip - scans the WHOLE program, every time.
 *   execpath     Renderer.RenderExecuting - advances the emulator and appends to the bound Point3DCollection
 *                (only when the "render executed" setting is on).
 *
 * THE ITEMS COUNT IS THE POINT, not the milliseconds. The hypothesis under test is that these are O(program
 * length) per executing line, i.e. O(N^2) over a run. Time alone cannot distinguish "expensive work" from
 * "work that grows with the file"; items/call can, and it does it in one line of the report. If items/call
 * tracks the program's line count, the hypothesis is confirmed and the fix is to stop rescanning. If it is
 * small and the milliseconds are still large, the cost is somewhere else and this instrument has done its
 * job by ruling these out - which is the outcome it must be equally able to produce.
 *
 * AGGREGATED, reported every 5 seconds and flushed at the end of a run. Deliberately not per-call: these
 * are the hottest UI-thread paths there are, and writing a log line from inside the very callback whose
 * duration is being measured would dominate the reading. An instrument that changes the reading is not an
 * instrument. Five seconds rather than PollDiag's minute because a job can be over in less than one.
 *
 * Off unless DebugLog is on, and everything here is behind one bool test when it is off. Turn it on with:
 *
 *     ioSender.exe -debuglog=ui
 *
 * (bare -debuglog works too and logs every category; "ui" is this one's category name). A Debug build logs
 * every category with no flag at all - see DebugLog.Init.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace CNC.Core
{
    public static class UiDiag
    {
        /// <summary>True when tracing is on. One bool test on every hot path when it is off.</summary>
        public static bool Enabled { get { return DebugLog.Enabled; } }

        private const int ReportIntervalMs = 5000;

        // A single callback longer than this is, on its own, a visible freeze - report it the moment it
        // happens rather than only folding it into a mean. The fault may well be one long call among many
        // short ones, and averaging is the wrong lens for that (the lesson PollDiag's first version taught).
        private const double CallWarnMs = 100d;

        // ...but a genuinely pathological run would then write a line per call forever. Cap the immediate
        // lines per window and carry the suppressed count into the periodic report.
        private const int MaxWarningsPerWindow = 10;

        private static readonly object sync = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly double TicksToMs = 1000d / Stopwatch.Frequency;

        private static double NowMs { get { return clock.Elapsed.TotalMilliseconds; } }

        private sealed class Site
        {
            public long Calls;
            public double TotalMs, MaxMs;
            public long Items;          // units of work scanned - the O(N) tell
            public long MaxItems;
            public double GapTotalMs;   // wall-clock spent OUTSIDE this site, between consecutive calls
            public double LastEndMs = -1d;

            public void Add(double ms, long items, double startedAtMs)
            {
                if (LastEndMs >= 0d && startedAtMs >= LastEndMs)
                    GapTotalMs += startedAtMs - LastEndMs;

                Calls++;
                TotalMs += ms;
                if (ms > MaxMs)
                    MaxMs = ms;
                Items += items;
                if (items > MaxItems)
                    MaxItems = items;
                LastEndMs = startedAtMs + ms;
            }

            // Reset the accumulators but NOT LastEndMs - the gap across a report boundary is still a real
            // gap, and zeroing it would make the first call of each window look like it followed instantly.
            public void Reset()
            {
                Calls = 0;
                TotalMs = 0d;
                MaxMs = 0d;
                Items = 0;
                MaxItems = 0;
                GapTotalMs = 0d;
            }

            public string Format(string name)
            {
                if (Calls == 0)
                    return null;

                double busy = TotalMs + GapTotalMs;
                return string.Format(CultureInfo.InvariantCulture,
                    "{0}: n={1} mean={2:F2}ms max={3:F1}ms items/call={4:F0} maxitems={5} duty={6:F0}%",
                    name, Calls, TotalMs / Calls, MaxMs,
                    (double)Items / Calls, MaxItems,
                    busy > 0d ? 100d * TotalMs / busy : 0d);
            }
        }

        // Insertion-ordered so the report always reads parent-first (drain, then what it fans out to),
        // however the sites happen to fire. Small and fixed - a handful of named paths, not user data.
        private static readonly Dictionary<string, Site> sites = new Dictionary<string, Site>(StringComparer.Ordinal);
        private static readonly List<string> order = new List<string>();

        private static double lastReportAt;
        private static int warningsThisWindow, warningsSuppressed;

        /// <summary>
        /// Stamp the start of a measured section. Returns a raw Stopwatch tick count, or 0 when tracing is
        /// off - pass it straight back to <see cref="Stop"/>, which treats 0 as "not measuring".
        /// </summary>
        public static long Start()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        /// <summary>
        /// Close a section opened by <see cref="Start"/>. <paramref name="items"/> is how many units of work
        /// the section actually got through (lines scanned, tokens advanced) - the number that says whether
        /// the cost grows with the program. Pass 0 where there is nothing meaningful to count.
        /// </summary>
        public static void Stop(string site, long stamp, long items = 0L)
        {
            if (!Enabled || stamp == 0L)
                return;

            double ms = (Stopwatch.GetTimestamp() - stamp) * TicksToMs;
            string warning = null, report = null;

            lock (sync)
            {
                Site s;
                if (!sites.TryGetValue(site, out s))
                {
                    s = new Site();
                    sites.Add(site, s);
                    order.Add(site);
                }

                double now = NowMs;
                s.Add(ms, items, now - ms);

                if (ms > CallWarnMs && Admit())
                    warning = string.Format(CultureInfo.InvariantCulture,
                        "UI STALL {0} blocked the UI thread for {1:F0}ms in one call ({2} items)", site, ms, items);

                report = BuildReport(now, null);
            }

            Emit(warning);
            Emit(report);
        }

        /// <summary>
        /// Write the accumulated numbers out now regardless of the interval, tagged with why. Called at the
        /// end of a run: a short job can finish inside one reporting window, and a window that never closes
        /// produces an empty log - which answers "did we instrument this?" with a confident and wrong no.
        /// </summary>
        public static void Flush(string reason)
        {
            if (!Enabled)
                return;

            string report;

            lock (sync)
                report = BuildReport(NowMs, reason ?? "flush");

            Emit(report);
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

        // Deliberately OUTSIDE the lock. DebugLog.Write does file I/O and this lock is taken from the UI
        // thread - holding it across a disk write would let the instrument produce the very stalls it is
        // measuring. Same reasoning as PollDiag.Emit.
        private static void Emit(string line)
        {
            if (line != null)
                DebugLog.Write("ui", line);
        }

        // Caller holds the lock. Returns the line to write (the CALLER writes it, outside the lock), or null
        // when the interval has not elapsed and no reason was forced.
        private static string BuildReport(double now, string reason)
        {
            if (reason == null && now - lastReportAt < ReportIntervalMs)
                return null;

            lastReportAt = now;

            var parts = new List<string>();
            foreach (var name in order)
            {
                string part = sites[name].Format(name);
                if (part != null)
                    parts.Add(part);
            }

            if (parts.Count == 0)
                return reason == null ? null : reason + ": nothing measured";

            foreach (var name in order)
                sites[name].Reset();

            string suppressed = warningsSuppressed > 0
                                 ? string.Format(CultureInfo.InvariantCulture, "  [{0} further warnings suppressed]", warningsSuppressed)
                                 : "";
            warningsThisWindow = warningsSuppressed = 0;

            return (reason == null ? "" : reason + "  ") + string.Join("  |  ", parts.ToArray()) + suppressed;
        }
    }
}
