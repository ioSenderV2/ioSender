/*
 * ValidateProcessor.cs - part of CNC Controls library
 *
 * "Validate controller" - exercises the connected grblHAL controller's G-code command set
 * to report which features it accepts. The set of features tested is tailored to the
 * controller's own reported capabilities ($I build options, axis count, $32 mode, tool
 * count, aux I/O counts, work coordinate systems) so a feature the firmware was never
 * built with is not tested and cannot be reported as a (false) failure.
 *
 * Mechanism (no external program needed - ioSender already streams and reads responses):
 *   1. Enable check mode ($C) so the controller parses each line but performs NO motion.
 *      Because nothing moves, the work envelope is irrelevant; all test moves are kept tiny
 *      and relative (G91) too. The machine is required to be homed first when homing is
 *      configured, so the few position-dependent commands (G28/G30/G53) are tested accurately.
 *   2. Stream the generated test one line at a time, in lock-step: send a line, wait for its
 *      "ok" / "error:N", record the result, then send the next. One feature per line.
 *   3. THE CATCH: grbl/grblHAL latch on a check-mode error - after one "error:N" every
 *      following line is rejected too, until the parser is reset. So on each error we recover:
 *      toggle check mode off (which soft-resets the parser) and on again, unlock first if the
 *      reset left an alarm, re-establish the modal set-up, then resume after the failed line.
 *      Recovery always re-confirms check mode is active before another line is sent, so a test
 *      line can never reach the controller as real motion.
 *   4. Show a results window grouped by category, each feature flagged pass/fail with the
 *      error code and message for failures, and a Copy-to-clipboard button.
 *
 * Helper lines (mode set-up / restore, marked Helper) are streamed too but only surface in
 * the report if they themselves fail - they keep modal state (units, distance mode, plane,
 * feed mode, ...) from one test leaking into the next.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    public static class ValidateProcessor
    {
        private const int AckTimeout = 4000;    // ms to wait for a single line's ok/error
        private static bool _running = false;

        // Bedrock modal set-up applied after entering check mode and re-applied after every
        // recovery reset. Every line here must be universally supported (so it never itself
        // errors and re-latches the parser): mm, units/min, XY plane, G54, no TLO, relative moves.
        private static readonly string[] ModalPrefix = { "G21", "G94", "G17", "G54", "G49", "G91" };

        // One streamed line. Helper lines (mode set-up/restore) are sent and checked but only
        // shown in the report if they fail; real feature lines are always shown.
        private class Test
        {
            public string Category;
            public string Feature;
            public string Code;
            public bool Helper;

            public string Response;     // "ok", "error:N" or null (timeout), filled in during the run
            public bool Passed { get { return Response == "ok"; } }
        }

        /// <summary>
        /// Run the validation against the connected controller. Returns false if it could not run
        /// (not connected, busy, cancelled); the reason is shown to the user. Must be called on the UI thread.
        /// </summary>
        public static bool Run(GrblViewModel model)
        {
            if (_running)
                return false;

            string reason = NotReady(model);
            if (reason != null)
            {
                MessageBox.Show(reason, "Validate controller", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // When homing is configured, require it to be done first so the position-dependent tests
            // (G28/G30/G53) are accurate - offer to home now (the user's machine, so always ask).
            if (model.IsHomingEnabled && model.HomedState != HomedState.Homed)
            {
                var ans = MessageBox.Show(
                    "The machine is not homed.\r\n\r\nValidation needs it homed so position-dependent commands (G28/G30/G53) are tested accurately. Home the machine now and continue?",
                    "Validate controller", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (ans != MessageBoxResult.Yes)
                    return false;
                if (!HomeMachine(model))
                {
                    MessageBox.Show("Homing did not complete - validation cancelled.", "Validate controller", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            string busyMessage = model.Message;
            _running = true;

            // Snapshot the persistent parameters BEFORE anything is sent. Check mode COMMITS the
            // G10/G92 writes the test makes (verified against the controller), so this snapshot is
            // what lets us put the work offsets / tool table back exactly afterwards.
            var snapshot = TakeSnapshot(model);
            var tests = BuildTests(model, snapshot.G92IsZero);
            bool startedHomed = model.HomedState == HomedState.Homed;
            bool unhomedDuringRun = false, aborted = false, completed = false;

            try
            {
                if (!EnterCheckMode(model))
                    MessageBox.Show("The controller did not enter check mode ($C) - validation aborted.",
                        "Validate controller", MessageBoxButton.OK, MessageBoxImage.Warning);
                else if (!ApplyPrefix(model))
                    MessageBox.Show("The controller rejected a basic set-up command - validation aborted.",
                        "Validate controller", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                {
                    int n = 0;
                    foreach (var test in tests)
                    {
                        model.Message = string.Format("Validating controller... ({0}/{1})", ++n, tests.Count);
                        test.Response = SendAndAwaitAck(model, test.Code, AckTimeout);

                        // A check-mode error latches the parser: recover before the next line, or every
                        // remaining line would falsely report an error. Recovery resets the parser, which
                        // can drop a homed machine into an alarm - track that for the report.
                        if (!test.Passed && !Recover(model, ref unhomedDuringRun))
                        {
                            aborted = true;     // could not get back into check mode - stop safely
                            break;
                        }
                    }
                    completed = true;
                }
            }
            finally
            {
                // Always leave check mode and restore the snapshot - this is what keeps the run
                // non-destructive. Done here (before the modal results window) and in a finally so it
                // still runs if a test threw or the user closed the app mid-run.
                if (model.IsCheckMode)
                    ExitCheckMode(model);
                RestoreNvram(model, snapshot);
                model.Message = busyMessage;
                _running = false;
            }

            if (completed)
                ShowResults(model, tests, snapshot, aborted, startedHomed && unhomedDuringRun);

            return true;
        }

        // Why the validation cannot run, or null if it can. (An un-homed machine is handled by Run
        // with a prompt, not blocked here.)
        private static string NotReady(GrblViewModel model)
        {
            if (Comms.com == null || !Comms.com.IsOpen)
                return "Not connected to a controller.";

            switch (model.GrblState.State)
            {
                case GrblStates.Alarm:
                    return "The controller is in an alarm state - clear it (home or unlock) before validating.";
                case GrblStates.Unknown:
                    return "The controller is not responding.";
                case GrblStates.Idle:
                case GrblStates.Check:
                    break;
                default:
                    return "The controller must be idle to validate (no job running).";
            }

            if (model.IsJobRunning || model.StreamingState != StreamingState.Idle)
                return "A job is running - stop it before validating.";

            if (model.IsCheckMode)
                return "The controller is already in check mode. Disable it ($C) before validating.";

            return null;
        }

        #region Check-mode control / recovery

        // Enable check mode. The "ok" acknowledging $C means the controller is parsing without
        // motion - that is the safety gate every feature line relies on. Returns false on timeout/error.
        private static bool EnterCheckMode(GrblViewModel model)
        {
            return SendAndAwaitAck(model, GrblConstants.CMD_CHECK, AckTimeout) == "ok";
        }

        // Disable check mode (also soft-resets the parser); pump the UI until the controller leaves it.
        private static void ExitCheckMode(GrblViewModel model)
        {
            Comms.com.WriteCommand(GrblConstants.CMD_CHECK);
            WaitForState(model, s => s != GrblStates.Check && s != GrblStates.Unknown, 3000);
        }

        // Apply the bedrock modal set-up. Returns false if any line is rejected (should never happen -
        // these are universally supported - so a rejection means the controller is unusable for the test).
        private static bool ApplyPrefix(GrblViewModel model)
        {
            foreach (var line in ModalPrefix)
                if (SendAndAwaitAck(model, line, AckTimeout) != "ok")
                    return false;
            return true;
        }

        // Recover from a latched check-mode error: toggle check mode off (soft-resets the parser) and
        // on again, unlocking first if the reset left an alarm, then re-apply the modal set-up. Returns
        // false if check mode could not be re-established (then the caller must stop - no more lines sent).
        // Sets unhomed when the recovery reset cleared a homed state.
        private static bool Recover(GrblViewModel model, ref bool unhomed)
        {
            // Disable check mode -> parser soft-reset. Wait for the controller to settle.
            Comms.com.WriteCommand(GrblConstants.CMD_CHECK);
            WaitForState(model, s => s == GrblStates.Idle || s == GrblStates.Alarm, 4000);

            // A soft reset can re-lock a machine that requires homing; clear the alarm (no motion) so
            // check mode can be re-entered. The machine is now un-homed - note it for the report.
            if (model.GrblState.State == GrblStates.Alarm)
            {
                unhomed = true;
                SendAndAwaitAck(model, GrblConstants.CMD_UNLOCK, AckTimeout);
                WaitForState(model, s => s == GrblStates.Idle, 3000);
            }
            else if (model.HomedState != HomedState.Homed && model.IsHomingEnabled)
                unhomed = true;

            // Re-enter check mode (the "ok" re-confirms parse-only mode) and restore the modal set-up.
            if (!EnterCheckMode(model))
                return false;

            return ApplyPrefix(model);
        }

        // Home the machine and wait for the cycle to finish (state Home -> Idle, homed). Pumps the UI.
        private static bool HomeMachine(GrblViewModel model)
        {
            string busy = model.Message;
            model.Message = "Homing...";
            try
            {
                Comms.com.WriteCommand(GrblConstants.CMD_HOMING);
                // Homing can take a while; wait for it to start then complete.
                WaitForState(model, s => s == GrblStates.Home || s == GrblStates.Idle, 5000);
                if (!WaitForState(model, s => s == GrblStates.Idle, 60000))
                    return false;
                return model.HomedState == HomedState.Homed;
            }
            finally
            {
                model.Message = busy;
            }
        }

        #endregion

        #region Streaming / response capture

        // Send one line and pump the UI until the controller acks it with "ok" or "error:N".
        // Returns the ack string, or null on timeout. Status reports and other async messages are
        // ignored. The wait runs on a background thread blocked on a queue while EventUtils.DoEvents
        // keeps responses (delivered on the UI thread) flowing - the established pattern in this
        // codebase (see Grbl.WaitForResponse / MacroProcessor.PumpForReport).
        private static string SendAndAwaitAck(GrblViewModel model, string command, int msTimeout)
        {
            string ack = null;
            bool done = false;
            var token = new CancellationToken();

            new Thread(() =>
            {
                var q = new BlockingCollection<string>();
                Action<string> add = item => q.TryAdd(item);
                model.OnResponseReceived += add;
                try
                {
                    Comms.com.WriteCommand(command);
                    string evt;
                    while (q.TryTake(out evt, msTimeout, token))
                    {
                        if (evt == "ok" || evt.StartsWith("error"))
                        {
                            ack = evt;
                            break;
                        }
                    }
                }
                finally
                {
                    model.OnResponseReceived -= add;
                    q.Dispose();
                    done = true;
                }
            }).Start();

            while (!done)
                EventUtils.DoEvents();

            return ack;
        }

        // Pump the UI until the controller's state satisfies 'predicate' or msTimeout elapses.
        // Returns true if the predicate was met.
        private static bool WaitForState(GrblViewModel model, Func<GrblStates, bool> predicate, int msTimeout)
        {
            var token = new CancellationToken();
            var sw = Stopwatch.StartNew();

            while (!predicate(model.GrblState.State) && sw.ElapsedMilliseconds < msTimeout)
            {
                bool? res = null;
                new Thread(() =>
                {
                    res = WaitFor.SingleEvent<string>(
                        token, null,
                        a => model.OnResponseReceived += a,
                        a => model.OnResponseReceived -= a,
                        Math.Min(500, msTimeout));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();
            }

            return predicate(model.GrblState.State);
        }

        #endregion

        #region NVRAM snapshot / restore

        // The persistent controller parameters the test could change, captured before the run.
        private class Snapshot
        {
            public string G54Axes;      // "X..Y..Z.." for a G10 L2 P1 restore, or null
            public string Tool1Axes;    // "X..Y..Z.." for a G10 L1 P1 restore, or null
            public bool G92IsZero = true;
            public string ParamText;    // human-readable pre-run parameter dump, for the report/log
        }

        // Read the work-offset / tool-table parameters ($#) so they can be put back after the run and
        // recorded in the report. The test only ever writes G54 (via G10 L2/L20) and tool 1 (via
        // G10 L1/L10/L11), so only those need restoring; G28/G30/G92 writes are avoided entirely.
        private static Snapshot TakeSnapshot(GrblViewModel model)
        {
            var snap = new Snapshot();
            try
            {
                GrblWorkParameters.Get(model);      // refresh $# from the controller

                var g54 = GrblWorkParameters.GetCoordinateSystem("G54");
                if (g54 != null)
                    snap.G54Axes = g54.ToString(GrblInfo.AxisFlags);

                var g92 = GrblWorkParameters.GetCoordinateSystem("G92");
                if (g92 != null)
                    for (int i = 0; i < GrblInfo.NumAxes; i++)
                        if (Math.Abs(g92.Values[i]) > 0.0001d)
                            snap.G92IsZero = false;

                var tool1 = GrblWorkParameters.Tools.FirstOrDefault(t => t.Code == "1");
                if (tool1 != null && GrblInfo.NumTools > 0)
                    snap.Tool1Axes = tool1.ToString(GrblInfo.AxisFlags);

                snap.ParamText = FormatParameters();
            }
            catch { /* leave snapshot best-effort; restore simply does less */ }

            return snap;
        }

        // Put the snapshot's values back. The test writes are committed by check mode, so this is what
        // keeps validation non-destructive. Runs in normal mode (after check mode is exited); unlocks
        // first if the exit reset left an alarm so the writes are accepted (no motion involved).
        private static void RestoreNvram(GrblViewModel model, Snapshot snap)
        {
            if (snap == null || Comms.com == null || !Comms.com.IsOpen)
                return;

            if (model.GrblState.State == GrblStates.Alarm)
            {
                SendAndAwaitAck(model, GrblConstants.CMD_UNLOCK, AckTimeout);
                WaitForState(model, s => s == GrblStates.Idle, 3000);
            }

            if (!string.IsNullOrEmpty(snap.G54Axes))
                SendAndAwaitAck(model, "G10 L2 P1 " + snap.G54Axes, AckTimeout);
            if (!string.IsNullOrEmpty(snap.Tool1Axes))
                SendAndAwaitAck(model, "G10 L1 P1 " + snap.Tool1Axes, AckTimeout);

            GrblWorkParameters.Get(model);  // re-sync the app's cached parameters with the controller
        }

        // A readable dump of the work-offset / tool parameters for the report (the pre-run NVRAM state).
        private static string FormatParameters()
        {
            var sb = new StringBuilder();
            foreach (var cs in GrblWorkParameters.CoordinateSystems)
                sb.AppendLine(string.Format("[{0}:{1}]", cs.Code, cs.ToString(GrblInfo.AxisFlags)));
            foreach (var tool in GrblWorkParameters.Tools.Where(t => t.Id > 0))
                sb.AppendLine(string.Format("[T{0}:{1}]", tool.Code, tool.ToString(GrblInfo.AxisFlags)));
            return sb.ToString();
        }

        #endregion

        #region Test generation

        // Build the capability-tailored test. Gated blocks are emitted only when the controller
        // reports the matching capability so unsupported features are not tested (and so cannot be
        // reported as failures). The modal set-up prefix is NOT included here - it is applied
        // separately (and re-applied on recovery) by ApplyPrefix.
        private static List<Test> BuildTests(GrblViewModel model, bool g92IsZero)
        {
            var t = new List<Test>();

            void Add(string cat, string feature, string code) => t.Add(new Test { Category = cat, Feature = feature, Code = code });
            void Helper(string code) => t.Add(new Test { Helper = true, Feature = code, Code = code });

            // --- Motion ---
            Add("Motion", "G0 rapid", "G0 X0.01");
            Add("Motion", "G1 feed", "G1 X0.01 F100");
            Add("Motion", "G2 arc (IJK)", "G2 X0 Y0 I0.5 J0 F100");
            Add("Motion", "G3 arc (R)", "G3 X0.5 Y0 R0.5 F100");

            // --- Rotary axes (one per axis beyond XYZ) ---
            for (int i = 3; i < GrblInfo.NumAxes; i++)
            {
                string letter = GrblInfo.AxisIndexToLetter(i);
                Add("Rotary axes", letter + " axis word", "G0 " + letter + "0.01");
            }

            // --- Predefined positions (tested early, while the machine is still homed - a recovery
            //     reset later in the run can drop a homed machine into an un-homed state).
            //     NOTE: the store variants G28.1/G30.1 are intentionally NOT tested - they overwrite
            //     the controller's stored G28/G30 positions and there is no g-code to restore an
            //     arbitrary value, so testing them could not be made non-destructive. The go-to forms
            //     below write nothing (no motion in check mode). ---
            Add("Predefined positions", "G28 (go to G28)", "G28");
            Add("Predefined positions", "G30 (go to G30)", "G30");
            Add("Predefined positions", "G53 (machine coords)", "G53 G0 X0");

            // --- Planes / arc distance / feed mode / units ---
            Add("Planes", "G18 (ZX plane)", "G18");
            Add("Planes", "G19 (YZ plane)", "G19");
            Helper("G17");
            Add("Arc distance", "G90.1 (absolute IJK)", "G90.1");
            Add("Arc distance", "G91.1 (incremental IJK)", "G91.1");
            Add("Feed mode", "G93 (inverse time)", "G93");
            Helper("G94");
            Add("Units", "G20 (inches)", "G20");
            Helper("G21");

            // --- Compensation / tool length / path ---
            Add("Compensation", "G40 (cutter comp off)", "G40");
            Add("Tool length", "G43.1 (dynamic TLO)", "G43.1 Z0.001");
            Add("Tool length", "G49 (cancel TLO)", "G49");
            Add("Path mode", "G61 (exact path)", "G61");
            Add("Path mode", "G61.1 (exact stop)", "G61.1");
            Add("Path mode", "G64 (continuous)", "G64");

            // --- Work coordinate systems ---
            foreach (string wcs in new[] { "G54", "G55", "G56", "G57", "G58", "G59" })
                Add("Work coordinate systems", wcs, wcs);
            Helper("G54");
            // Extended WCS - only those the controller actually reports ($#).
            foreach (string wcs in new[] { "G59.1", "G59.2", "G59.3" })
                if (GrblWorkParameters.CoordinateSystems.Any(c => c.Code == wcs))
                    Add("Work coordinate systems", wcs, wcs);
            Helper("G54");
            Add("Work coordinate systems", "G10 L2 (set WCS)", "G10 L2 P1 X0");
            Add("Work coordinate systems", "G10 L20 (set WCS to current)", "G10 L20 P1 X0");
            // G92 writes the persistent G92 offset; only test it (and the G92.1 that clears it back to
            // zero) when no G92 offset is currently active, so the original value is never lost.
            if (g92IsZero)
            {
                Add("Work coordinate systems", "G92 (offset)", "G92 X0");
                Add("Work coordinate systems", "G92.1 (clear offset)", "G92.1");
            }

            // --- Dwell ---
            Add("Dwell", "G4 (dwell)", "G4 P0.01");

            // --- Spindle ---
            Add("Spindle", "M3 (CW)", "M3 S1000");
            Add("Spindle", "M4 (CCW)", "M4 S1000");
            Add("Spindle", "M5 (stop)", "M5");

            // --- Coolant ---
            Add("Coolant", "M8 (flood on)", "M8");
            Add("Coolant", "M7 (mist on)", "M7");
            Add("Coolant", "M9 (coolant off)", "M9");

            // --- Program control ---
            Add("Program control", "M0 (program stop)", "M0");
            Add("Program control", "M1 (optional stop)", "M1");

            // --- Overrides ---
            Add("Overrides", "M48 (overrides on)", "M48");
            Add("Overrides", "M49 (overrides off)", "M49");
            Helper("M48");
            Add("Overrides", "M50 (feed override control)", "M50 P1");
            Add("Overrides", "M51 (rapid override control)", "M51 P1");
            Add("Overrides", "M52 (spindle override control)", "M52 P1");
            Add("Overrides", "M53 (parking override control)", "M53 P1");
            Add("Overrides", "M56 (parking)", "M56 P1");

            // --- Tooling ---
            Add("Tooling", "T (tool select)", "T1");
            Add("Tooling", "M61 (set current tool)", "M61 Q1");
            if (GrblInfo.ManualToolChange || GrblInfo.HasATC)
                Add("Tooling", "M6 (tool change)", "M6 T1");
            if (GrblInfo.NumTools > 0)
            {
                Add("Tool table", "G43 H (tool length from table)", "G43 H1");
                Helper("G49");
                Add("Tool table", "G10 L1 (set tool table)", "G10 L1 P1 Z0");
                Add("Tool table", "G10 L10 (set tool offset to current)", "G10 L10 P1 Z0");
                Add("Tool table", "G10 L11 (set tool offset to machine)", "G10 L11 P1 Z0");
            }

            // --- Probing ---
            Add("Probing", "G38.2 (probe toward, error)", "G38.2 Z-0.01 F50");
            if (GrblInfo.HasProbe)
            {
                Add("Probing", "G38.3 (probe toward, no error)", "G38.3 Z-0.01 F50");
                Add("Probing", "G38.4 (probe away, error)", "G38.4 Z-0.01 F50");
                Add("Probing", "G38.5 (probe away, no error)", "G38.5 Z-0.01 F50");
            }

            // --- Auxiliary I/O (M62-M68), gated on reported port counts ---
            if (GrblAuxIO.DigitalOutputs > 0)
            {
                Add("Auxiliary I/O", "M62 (digital out, synced on)", "M62 P0");
                Add("Auxiliary I/O", "M63 (digital out, synced off)", "M63 P0");
                Add("Auxiliary I/O", "M64 (digital out on)", "M64 P0");
                Add("Auxiliary I/O", "M65 (digital out off)", "M65 P0");
            }
            if (GrblAuxIO.DigitalInputs > 0)
                Add("Auxiliary I/O", "M66 (wait on input)", "M66 P0 L0 Q0.1");
            if (GrblAuxIO.AnalogOutputs > 0)
            {
                Add("Auxiliary I/O", "M67 (analog out, synced)", "M67 E0 Q0");
                Add("Auxiliary I/O", "M68 (analog out, immediate)", "M68 E0 Q0");
            }

            // --- Lathe (only when the controller is in lathe mode) ---
            if (GrblInfo.LatheModeEnabled)
            {
                Add("Lathe", "G7 (diameter mode)", "G7");
                Add("Lathe", "G8 (radius mode)", "G8");
                Add("Lathe", "G33 (spindle-synced motion)", "G33 Z-0.01 K0.1");
                Add("Lathe", "G76 (threading cycle)", "G76 P0.1 Z-0.01 I0 J0.05 K0.1 F0.1");
                Add("Lathe", "G96 (constant surface speed)", "G96 S100 D2000");
                Add("Lathe", "G97 (RPM mode)", "G97 S1000");
                Add("Lathe", "G95 (feed per revolution)", "G95");
                Helper("G94");
            }

            // --- Laser (only when $32 laser mode is enabled) ---
            if (IsLaserMode())
            {
                Add("Laser", "M4 dynamic power", "M4 S500");
                Add("Laser", "M5 (laser off)", "M5");
            }

            return t;
        }

        private static bool IsLaserMode()
        {
            return GrblSettings.HasSetting(GrblSetting.Mode) && GrblSettings.GetInteger(GrblSetting.Mode) == (int)GrblMode.Laser;
        }

        #endregion

        #region Results window

        private static void ShowResults(GrblViewModel model, List<Test> tests, Snapshot snapshot, bool aborted, bool unhomed)
        {
            // Helper lines only appear if they failed (which would mean a set-up/restore problem).
            var shown = tests.Where(x => !x.Helper || !x.Passed).ToList();
            int total = tests.Count(x => !x.Helper);
            int passed = tests.Count(x => !x.Helper && x.Passed);
            int failed = total - passed;

            var win = new Window {
                Title = "Validate controller",
                SizeToContent = SizeToContent.Width,
                Height = 560,
                MinWidth = 440,
                ResizeMode = ResizeMode.CanResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                win.Owner = Application.Current.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { Margin = new Thickness(12) };

            // Header: firmware / axes / summary.
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(new TextBlock {
                Text = string.Format("{0}{1}  -  {2} axes",
                    GrblInfo.Firmware,
                    string.IsNullOrEmpty(GrblInfo.Version) ? "" : " " + GrblInfo.Version,
                    GrblInfo.NumAxes),
                FontWeight = FontWeights.Bold
            });
            header.Children.Add(new TextBlock {
                Text = string.Format("{0} of {1} features passed{2}", passed, total,
                    failed > 0 ? string.Format("  -  {0} failed", failed) : ""),
                Foreground = failed > 0 ? Brushes.Firebrick : Brushes.ForestGreen,
                Margin = new Thickness(0, 2, 0, 0)
            });
            if (aborted)
                header.Children.Add(NoteBlock("Validation stopped early: the controller could not return to check mode after an error. Remaining features were not tested."));
            if (unhomed)
                header.Children.Add(NoteBlock("A recovery reset left the machine un-homed; position-dependent results (G28/G30/G53) may be affected. Re-home before running a job."));
            header.Children.Add(new TextBlock {
                Text = "Work offsets and tool table were restored after the run; machine settings ($$) are not modified.",
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            root.Children.Add(header);

            // Buttons (docked bottom so they stay visible as the list scrolls).
            var buttons = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var copy = new Button { Content = "Copy", MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
            var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 75 };
            copy.Click += (s, e) => {
                try { Clipboard.SetText(BuildReportText(tests, snapshot, passed, total, failed)); }
                catch { /* clipboard occasionally busy - ignore */ }
            };
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            root.Children.Add(buttons);

            // Results list grouped by category.
            var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            string lastCat = null;
            foreach (var test in shown)
            {
                string cat = test.Helper ? "Set-up" : test.Category;
                if (cat != lastCat)
                {
                    list.Children.Add(new TextBlock {
                        Text = cat,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, lastCat == null ? 0 : 10, 0, 3)
                    });
                    lastCat = cat;
                }
                list.Children.Add(BuildRow(test));
            }

            root.Children.Add(new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = list
            });

            win.Content = root;
            win.ShowDialog();
        }

        private static TextBlock NoteBlock(string text)
        {
            return new TextBlock {
                Text = text,
                Foreground = Brushes.DarkOrange,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private static UIElement BuildRow(Test test)
        {
            var row = new Grid { Margin = new Thickness(8, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });        // marker
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180, GridUnitType.Pixel) }); // feature
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });        // gcode / detail

            bool timedOut = test.Response == null;

            var marker = new TextBlock {
                Text = test.Passed ? "✓" : "✗",
                Foreground = test.Passed ? Brushes.ForestGreen : Brushes.Firebrick,
                FontWeight = FontWeights.Bold,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(marker, 0);

            var feature = new TextBlock {
                Text = test.Feature,
                Foreground = test.Passed ? Brushes.Black : Brushes.Firebrick,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = test.Code,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0)
            };
            Grid.SetColumn(feature, 1);

            string detailText;
            if (test.Passed)
                detailText = test.Code;
            else if (timedOut)
                detailText = "(no response)";
            else
            {
                string msg = ErrorMessage(test.Response);
                detailText = msg == null ? test.Response : test.Response + " - " + msg;
            }

            var detail = new TextBlock {
                Text = detailText,
                Foreground = test.Passed ? Brushes.Gray : Brushes.Firebrick,
                FontFamily = new FontFamily("Consolas, Courier New"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(detail, 2);

            row.Children.Add(marker);
            row.Children.Add(feature);
            row.Children.Add(detail);

            return row;
        }

        // Look up the human-readable text for an "error:N" response.
        private static string ErrorMessage(string response)
        {
            if (response == null || !response.StartsWith("error:"))
                return null;
            string code = response.Substring(6);
            string msg = GrblErrors.GetMessage(code);
            return string.IsNullOrEmpty(msg) || msg == code ? null : msg;
        }

        private static string BuildReportText(List<Test> tests, Snapshot snapshot, int passed, int total, int failed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Validate controller");
            sb.AppendLine(string.Format("{0}{1} - {2} axes",
                GrblInfo.Firmware, string.IsNullOrEmpty(GrblInfo.Version) ? "" : " " + GrblInfo.Version, GrblInfo.NumAxes));
            sb.AppendLine(string.Format("{0} of {1} features passed{2}", passed, total, failed > 0 ? string.Format(", {0} failed", failed) : ""));
            sb.AppendLine();

            // The pre-run persistent parameter state ($#), for the record - validation restores these
            // and never writes machine settings ($$).
            if (snapshot != null && !string.IsNullOrEmpty(snapshot.ParamText))
            {
                sb.AppendLine("Work parameters before run (restored afterwards):");
                sb.Append(snapshot.ParamText);
                sb.AppendLine();
            }

            string lastCat = null;
            foreach (var test in tests.Where(x => !x.Helper || !x.Passed))
            {
                string cat = test.Helper ? "Set-up" : test.Category;
                if (cat != lastCat)
                {
                    sb.AppendLine(cat);
                    lastCat = cat;
                }
                string status;
                if (test.Passed)
                    status = "ok";
                else if (test.Response == null)
                    status = "no response";
                else
                {
                    string msg = ErrorMessage(test.Response);
                    status = msg == null ? test.Response : test.Response + " - " + msg;
                }
                sb.AppendLine(string.Format("  [{0}] {1,-34} {2,-22} {3}",
                    test.Passed ? "PASS" : "FAIL", test.Feature, test.Code, status));
            }

            return sb.ToString();
        }

        #endregion
    }
}
