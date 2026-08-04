/*
 * ControllerValidator.cs - part of CNC Core library
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
 *
 * Helper lines (mode set-up / restore, marked Helper) are streamed too but only surface in
 * the report if they themselves fail - they keep modal state (units, distance mode, plane,
 * feed mode, ...) from one test leaking into the next.
 *
 * This class does not prompt, display or localize ANYTHING. It reports machine state
 * (NotReady/NeedsHoming), raises Started/Progress, and returns a ValidationOutcome; deciding
 * what to say about that - and in which language - is the host's job. That line is not
 * cosmetic: the three "validation aborted" strings live in CNC.Controls' own LibStrings.xaml,
 * and there are three different LibStrings classes in this solution, so a FindResource call
 * moved in here would compile perfectly and silently resolve nothing at runtime.
 *
 * The results window, the progress panel and the clipboard export stay in
 * CNC.Controls.ValidateProcessor, which drives this.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace CNC.Core
{
    /// <summary>How a validation run ended. Only Completed produces a report.</summary>
    public enum ValidationOutcome
    {
        Completed,      // the run finished (possibly stopped early - see Aborted)
        NoCheckMode,    // the controller would not enter check mode ($C)
        SetupRejected   // the controller rejected a basic modal set-up command
    }

    /// <summary>
    /// One streamed line. Helper lines (mode set-up/restore) are sent and checked but only
    /// shown in the report if they fail; real feature lines are always shown.
    /// </summary>
    public class ValidationTest
    {
        public string Category;
        public string Feature;
        public string Code;
        public bool Helper;

        public string Response;     // "ok", "error:N" or null (timeout), filled in during the run
        public bool Passed { get { return Response == "ok"; } }
        public bool TimedOut { get { return Response == null; } }
    }

    /// <summary>The persistent controller parameters the test could change, captured before the run.</summary>
    public class ValidationSnapshot
    {
        public string G54Axes;      // "X..Y..Z.." for a G10 L2 P1 restore, or null
        public string Tool1Axes;    // "X..Y..Z.." for a G10 L1 P1 restore, or null
        public bool G92IsZero = true;
        public string ParamText;    // human-readable pre-run parameter dump, for the report/log
    }

    public class ControllerValidator
    {
        private const int AckTimeout = 4000;    // ms to wait for a single line's ok/error
        private const int VisualizeFeed = 6000;  // mm/min feed for the loaded "passed moves" visualization job

        // Motion-test geometry, set by ComputeGeometry() before each run. The motion tests use moves
        // large enough to show a real toolpath in the 3D viewer, but bounded to the homed soft-limit
        // envelope so they never trip a soft-limit alarm in check mode (check mode DOES enforce soft
        // limits). _anchor parks the planned position at the envelope centre before each motion test.
        private double _scale = 10.0;    // linear move size, mm
        private int _rotary = 10;        // rotary move size, degrees
        private int _feed = 500;         // feed for G1/G2/G3
        private string _anchor = null;   // "G53 G0 X.. Y.. Z.." to the envelope centre, or null
        private double[] _center = null; // per-axis envelope-centre machine coord (NaN if no travel)

        // Bedrock modal set-up applied after entering check mode and re-applied after every
        // recovery reset. Every line here must be universally supported (so it never itself
        // errors and re-latches the parser): mm, units/min, XY plane, G54, no TLO, relative moves.
        private static readonly string[] ModalPrefix = { "G21", "G94", "G17", "G54", "G49", "G91" };

        /// <summary>Every test line of the completed run, in streamed order (helpers included).</summary>
        public List<ValidationTest> Tests { get; private set; } = new List<ValidationTest>();

        /// <summary>The pre-run persistent parameter state, restored after the run.</summary>
        public ValidationSnapshot Parameters { get; private set; }

        /// <summary>Set when the controller could not be returned to check mode and the run stopped early.</summary>
        public bool Aborted { get; private set; }

        /// <summary>Set when a recovery reset dropped a machine that started homed out of its homed state.</summary>
        public bool Unhomed { get; private set; }

        /// <summary>Number of real feature tests (helpers excluded) - valid once Started has been raised.</summary>
        public int FeatureCount { get; private set; }

        /// <summary>Raised once the capability-tailored test set is built, with the feature count.</summary>
        public System.Action<int> Started;

        /// <summary>Raised after each feature test completes: (testNumber, passCount, failCount).</summary>
        public System.Action<int, int, int> Progress;

        #region Preconditions

        /// <summary>
        /// Why the validation cannot run, or null if it can. An un-homed machine is NOT blocked here -
        /// see NeedsHoming, which the host resolves (by asking) before calling Run.
        /// </summary>
        public string NotReady(GrblViewModel model)
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

        /// <summary>
        /// True when homing is configured but not done. Validation needs a homed machine so the
        /// position-dependent tests (G28/G30/G53) are accurate. It is the host's machine, so the host
        /// asks - this only reports the condition.
        /// </summary>
        public bool NeedsHoming(GrblViewModel model)
        {
            return model.IsHomingEnabled && model.HomedState != HomedState.Homed;
        }

        // Home the machine and wait for the cycle to finish (state Home -> Idle, homed). Pumps the UI.
        public bool HomeMachine(GrblViewModel model)
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

        /// <summary>
        /// Run the validation against the connected controller. Call only when NotReady returned null
        /// and NeedsHoming has been resolved. Must be called on the host's UI thread (it pumps).
        /// </summary>
        public ValidationOutcome Run(GrblViewModel model)
        {
            string busyMessage = model.Message;

            // Snapshot the persistent parameters BEFORE anything is sent. Check mode COMMITS the
            // G10/G92 writes the test makes (verified against the controller), so this snapshot is
            // what lets us put the work offsets / tool table back exactly afterwards.
            Parameters = TakeSnapshot(model);
            ComputeGeometry();
            Tests = BuildTests(model, Parameters.G92IsZero);
            bool startedHomed = model.HomedState == HomedState.Homed;
            bool unhomedDuringRun = false, enteredCheck = false;
            var outcome = ValidationOutcome.Completed;

            Aborted = false;
            Unhomed = false;
            FeatureCount = Tests.Count(x => !x.Helper);

            int passCount = 0, failCount = 0;

            Started?.Invoke(FeatureCount);

            try
            {
                if (!EnterCheckMode(model))
                    outcome = ValidationOutcome.NoCheckMode;
                else
                {
                    enteredCheck = true;
                    if (!ApplyPrefix(model))
                        outcome = ValidationOutcome.SetupRejected;
                    else
                    {
                        int n = 0;
                        foreach (var test in Tests)
                        {
                            test.Response = SendAndAwaitAck(model, test.Code, AckTimeout);

                            // Tally and report each feature test's verdict as it completes (helper set-up/
                            // restore lines stream silently, leaving the last feature result visible).
                            if (!test.Helper)
                            {
                                n++;
                                if (test.Passed) passCount++; else failCount++;
                                model.Message = string.Format("Test {0} of {1} - {2} - {3}",
                                    n, FeatureCount, test.Feature, test.Passed ? "PASS" : "FAIL");
                                Progress?.Invoke(n, passCount, failCount);
                            }

                            // A check-mode error latches the parser: recover before the next line, or every
                            // remaining line would falsely report an error. Recovery resets the parser, which
                            // can drop a homed machine into an alarm - track that for the report.
                            if (!test.Passed && !Recover(model, ref unhomedDuringRun))
                            {
                                Aborted = true;     // could not get back into check mode - stop safely
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                // Leave check mode and restore the snapshot - this is what keeps the run non-destructive.
                // Gate the exit on what the code KNOWS, not on model.IsCheckMode: that flag is driven by
                // status reports and lags, so right after a recovery re-enters check mode it can still read
                // false and skip the exit - which would leave the controller stuck in $C. Whenever we
                // entered check mode and were not aborted mid-recovery, we are definitely still in it.
                // (An aborted recovery already reset the controller out of check mode.)
                if (enteredCheck && !Aborted)
                    ExitCheckMode(model);
                else if (enteredCheck && model.IsCheckMode)
                    ExitCheckMode(model);
                RestoreNvram(model, Parameters);
                model.Message = busyMessage;
            }

            Unhomed = startedHomed && unhomedDuringRun;

            return outcome;
        }

        #region Check-mode control / recovery

        // Enable check mode. The "ok" acknowledging $C means the controller is parsing without
        // motion - that is the safety gate every feature line relies on. Returns false on timeout/error.
        private bool EnterCheckMode(GrblViewModel model)
        {
            return SendAndAwaitAck(model, GrblConstants.CMD_CHECK, AckTimeout) == "ok";
        }

        // Disable check mode (also soft-resets the parser); pump the UI until the controller leaves it.
        // If $C does not clear it within the timeout, force a soft reset as a guaranteed fallback so the
        // controller is never left stuck in check mode.
        private void ExitCheckMode(GrblViewModel model)
        {
            Comms.com.WriteCommand(GrblConstants.CMD_CHECK);
            WaitForState(model, s => s != GrblStates.Check && s != GrblStates.Unknown, 3000);

            if (model.IsCheckMode)
            {
                Comms.com.WriteByte(GrblConstants.CMD_RESET);
                WaitForState(model, s => s != GrblStates.Check && s != GrblStates.Unknown, 3000);
            }
        }

        // Apply the bedrock modal set-up. Returns false if any line is rejected (should never happen -
        // these are universally supported - so a rejection means the controller is unusable for the test).
        private bool ApplyPrefix(GrblViewModel model)
        {
            foreach (var line in ModalPrefix)
                if (SendAndAwaitAck(model, line, AckTimeout) != "ok")
                    return false;
            // Park at the envelope centre so the bounded motion-test moves stay inside soft limits.
            if (_anchor != null && SendAndAwaitAck(model, _anchor, AckTimeout) != "ok")
                return false;
            return true;
        }

        // Recover from a latched check-mode error: toggle check mode off (soft-resets the parser) and
        // on again, unlocking first if the reset left an alarm, then re-apply the modal set-up. Returns
        // false if check mode could not be re-established (then the caller must stop - no more lines sent).
        // Sets unhomed when the recovery reset cleared a homed state.
        private bool Recover(GrblViewModel model, ref bool unhomed)
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

        #endregion

        #region Streaming / response capture

        // Send one line and pump the UI until the controller acks it with "ok" or "error:N".
        // Returns the ack string, or null on timeout. Status reports and other async messages are
        // ignored. The wait runs on a background thread blocked on a queue while EventUtils.DoEvents
        // keeps responses (delivered on the UI thread) flowing - the established pattern in this
        // codebase (see Grbl.WaitForResponse / MacroProcessor.PumpForReport).
        private string SendAndAwaitAck(GrblViewModel model, string command, int msTimeout)
        {
            string ack = null;
            bool done = false;
            var token = new CancellationToken();

            new Thread(() =>
            {
                var q = new BlockingCollection<string>();
                System.Action<string> add = item => q.TryAdd(item);
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

            // Pump the UI for responses, but yield ~1 ms each spin. With the validate program loaded in
            // the 3D viewer a tight DoEvents busy-loop re-renders the live scene continuously and slows
            // the run to minutes; the short sleep lets the render/response work through cheaply.
            while (!done)
            {
                EventUtils.DoEvents();
                Thread.Sleep(1);
            }

            return ack;
        }

        // Pump the UI until the controller's state satisfies 'predicate' or msTimeout elapses.
        // Returns true if the predicate was met.
        private bool WaitForState(GrblViewModel model, Func<GrblStates, bool> predicate, int msTimeout)
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

        // Read the work-offset / tool-table parameters ($#) so they can be put back after the run and
        // recorded in the report. The test only ever writes G54 (via G10 L2/L20) and tool 1 (via
        // G10 L1/L10/L11), so only those need restoring; G28/G30/G92 writes are avoided entirely.
        private ValidationSnapshot TakeSnapshot(GrblViewModel model)
        {
            var snap = new ValidationSnapshot();
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
        private void RestoreNvram(GrblViewModel model, ValidationSnapshot snap)
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

        // Format a coordinate for a g-code line (invariant decimal, trimmed).
        private static string Num(double v) { return System.Math.Round(v, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }

        // The travel for an axis ($13x), as a positive magnitude (0 if not configured).
        private static double AxisTravel(int axis) { return System.Math.Abs(GrblSettings.GetDouble((GrblSetting)((int)GrblSetting.MaxTravelBase + axis))); }

        // Size the motion-test moves to the machine: big enough to see in the 3D view (~100 mm) but
        // bounded to ~a quarter of the smallest travel so a centred excursion always stays in soft
        // limits. Build the G53 anchor that parks the planned position at the envelope centre.
        private void ComputeGeometry()
        {
            double minTravel = 0.0;
            for (int i = 0; i < System.Math.Min(3, GrblInfo.NumAxes); i++)
            {
                double tr = AxisTravel(i);
                if (tr > 0.0)
                    minTravel = minTravel == 0.0 ? tr : System.Math.Min(minTravel, tr);
            }
            _scale = minTravel <= 0.0 ? 10.0 : System.Math.Max(2.0, System.Math.Min(100.0, minTravel * 0.25));
            _feed = (int)System.Math.Max(100.0, _scale * 12.0);

            _center = new double[GrblInfo.NumAxes];
            for (int i = 0; i < GrblInfo.NumAxes; i++)
                _center[i] = double.NaN;

            var sb = new StringBuilder("G53 G0");
            bool any = false;
            for (int i = 0; i < System.Math.Min(3, GrblInfo.NumAxes); i++)
            {
                double tr = AxisTravel(i);
                if (tr > 0.0)
                {
                    _center[i] = -tr / 2.0;
                    sb.Append(' ').Append(GrblInfo.AxisIndexToLetter(i)).Append(Num(_center[i]));
                    any = true;
                }
            }
            _anchor = any ? sb.ToString() : null;
        }

        /// <summary>
        /// Assemble a SAFE, runnable program from the motion features that passed, for the user to Cycle
        /// Start and watch in 3D. Only the bounded "spoke" moves (Motion + Rotary categories) are included,
        /// each kept inside soft limits by the same G53 re-anchor to the work-area centre used during the
        /// test. Predefined-position tests (G28/G30), machine-coord moves and all non-motion ops (spindle,
        /// coolant, tool change, overrides) are excluded - running those as real motion could be unsafe.
        /// Returns an EMPTY list when no motion test passed, so a caller can tell "nothing to show" from a
        /// real program without counting prefix lines.
        /// </summary>
        public List<string> BuildSafeJob()
        {
            var lines = new List<string>();
            // ABSOLUTE (G90) variant of the prefix. The test streams relative (G91) moves re-anchored with
            // G53 machine coords; the 3D emulator renders that offset from the live tool position, so the
            // toolpath and the head don't line up. Re-emitting the same geometry as plain absolute moves in
            // the work system makes the drawn path and the head agree. (Assumes the work offset is ~zero,
            // which it is for a homed machine with G54 at machine origin - the validate norm.)
            lines.Add("G21"); lines.Add("G94"); lines.Add("G17"); lines.Add("G54"); lines.Add("G49"); lines.Add("G90");
            // Run the visualisation fast: drop the per-move F500 (see StripFeed) and set one high modal feed.
            // grbl clamps it to each axis max rate, so it is safe on real hardware and traces quickly on the sim.
            lines.Add("F" + VisualizeFeed);
            int prefixCount = lines.Count;

            string absAnchor = BuildAbsoluteAnchor();   // "G0 X<cx> Y<cy> Z<cz>" to the envelope centre

            for (int i = 0; i < Tests.Count; i++)
            {
                var t = Tests[i];
                if (!t.Passed || (t.Category != "Motion" && t.Category != "Rotary axes"))
                    continue;
                if (absAnchor != null && i > 0 && Tests[i - 1].Helper && Tests[i - 1].Code == _anchor)
                    lines.Add(absAnchor);
                lines.Add(ToAbsolute(StripFeed(t.Code)));   // relative-from-centre -> absolute
            }

            // Nothing but the prefix means no motion test passed - there is no toolpath to show. The old
            // "job.Count > ModalPrefix.Length" test the caller used could never catch this: the prefix
            // written here is SEVEN lines (six modal + the feed) against a six-line ModalPrefix, so it was
            // always true and an empty run still reported "passed moves loaded".
            if (lines.Count == prefixCount)
                return new List<string>();

            if (absAnchor != null)
                lines.Add(absAnchor);                        // park back at centre when the run ends

            return lines;
        }

        // "G0 X<cx> Y<cy> Z<cz>" rapid to the envelope centre in absolute coords, or null if no travel.
        private string BuildAbsoluteAnchor()
        {
            if (_center == null)
                return null;
            var sb = new StringBuilder("G0");
            bool any = false;
            for (int i = 0; i < _center.Length; i++)
                if (!double.IsNaN(_center[i])) { sb.Append(' ').Append(GrblInfo.AxisIndexToLetter(i)).Append(Num(_center[i])); any = true; }
            return any ? sb.ToString() : null;
        }

        // Convert one relative (G91) test move into an absolute (G90) move: each axis word becomes
        // centre + relative; arc-centre offsets (I/J/K), radius (R) and the motion word (Gn) pass through
        // unchanged (they are frame-independent). Axes without a known centre (e.g. rotary, no travel) are
        // emitted as-is, which is correct when the axis starts at zero.
        private static readonly System.Text.RegularExpressions.Regex WordRx =
            new System.Text.RegularExpressions.Regex(@"([A-Za-z])\s*([-+]?[0-9]*\.?[0-9]+)");

        private string ToAbsolute(string relCode)
        {
            var sb = new StringBuilder();
            foreach (System.Text.RegularExpressions.Match m in WordRx.Matches(relCode))
            {
                char letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
                int axis = AxisLetterToIndex(letter);
                if (sb.Length > 0) sb.Append(' ');
                if (axis >= 0 && _center != null && axis < _center.Length && !double.IsNaN(_center[axis]))
                {
                    double rel = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    sb.Append(letter).Append(Num(_center[axis] + rel));
                }
                else
                    sb.Append(letter).Append(m.Groups[2].Value);   // Gn / I / J / K / R / centre-less axis
            }
            return sb.ToString();
        }

        private static int AxisLetterToIndex(char letter)
        {
            switch (letter)
            {
                case 'X': return 0; case 'Y': return 1; case 'Z': return 2;
                case 'A': return 3; case 'B': return 4; case 'C': return 5;
                case 'U': return 6; case 'V': return 7; case 'W': return 8;
                default: return -1;
            }
        }

        // Remove a trailing feed word ("... F500") so the move uses the job's fast modal feed instead.
        private static string StripFeed(string code)
        {
            int f = code.IndexOf(" F");
            return f >= 0 ? code.Substring(0, f) : code;
        }

        /// <summary>
        /// Load a set of g-code lines into a program (and, for the loaded job, the 3D view) as a runnable
        /// program - the same New/Add/End path g-code generators use. Returns the line count loaded.
        /// The program is passed in rather than taken from a global: Core has no "the loaded program"
        /// singleton (see GCodeProgram), so the host supplies the one it wants filled.
        /// </summary>
        public int LoadProgram(GCodeProgram program, List<string> lines, string name)
        {
            if (program == null || lines == null || lines.Count == 0)
                return 0;

            try
            {
                program.AddBlock(name, Action.New);
                foreach (var line in lines)
                    program.AddBlock(line);
                program.AddBlock("", Action.End);
                return lines.Count;
            }
            catch { return 0; }
        }

        // Build the capability-tailored test. Gated blocks are emitted only when the controller
        // reports the matching capability so unsupported features are not tested (and so cannot be
        // reported as failures). The modal set-up prefix is NOT included here - it is applied
        // separately (and re-applied on recovery) by ApplyPrefix.
        private List<ValidationTest> BuildTests(GrblViewModel model, bool g92IsZero)
        {
            var t = new List<ValidationTest>();

            System.Action<string, string, string> Add = (cat, feature, code) => t.Add(new ValidationTest { Category = cat, Feature = feature, Code = code });
            System.Action<string> Helper = code => t.Add(new ValidationTest { Helper = true, Feature = code, Code = code });
            // Motion-producing test: re-anchor to the envelope centre first, so each move is a bounded
            // "spoke" that stays inside soft limits regardless of where the previous test left things.
            System.Action<string, string, string> Motion = (cat, feature, code) => { if (_anchor != null) Helper(_anchor); Add(cat, feature, code); };
            string F = "F" + _feed;
            double s = _scale;

            // --- Motion --- (bounded "spoke" moves from the envelope centre, sized for the 3D view)
            Motion("Motion", "G0 rapid", "G0 X-" + Num(s) + " Y-" + Num(s));
            Motion("Motion", "G1 feed", "G1 X-" + Num(s) + " Y" + Num(s) + " " + F);
            Motion("Motion", "G2 arc (IJK)", "G2 X0 Y0 I-" + Num(s) + " J0 " + F);      // full circle, radius s
            Motion("Motion", "G3 arc (R)", "G3 X-" + Num(s) + " Y-" + Num(s) + " R" + Num(s) + " " + F);

            // --- Rotary axes (one per axis beyond XYZ) ---
            for (int i = 3; i < GrblInfo.NumAxes; i++)
            {
                string letter = GrblInfo.AxisIndexToLetter(i);
                Motion("Rotary axes", letter + " axis word", "G0 " + letter + _rotary);
            }

            // --- Predefined positions (tested early, while the machine is still homed - a recovery
            //     reset later in the run can drop a homed machine into an un-homed state).
            //     NOTE: the store variants G28.1/G30.1 are intentionally NOT tested - they overwrite
            //     the controller's stored G28/G30 positions and there is no g-code to restore an
            //     arbitrary value, so testing them could not be made non-destructive. The go-to forms
            //     below write nothing (no motion in check mode). ---
            Motion("Predefined positions", "G28 (go to G28)", "G28");
            Motion("Predefined positions", "G30 (go to G30)", "G30");
            Motion("Predefined positions", "G53 (machine coords)", "G53 G0 X0");

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

        #region Report

        /// <summary>Look up the human-readable text for an "error:N" response; null when there is none.</summary>
        public static string ErrorMessage(string response)
        {
            if (response == null || !response.StartsWith("error:"))
                return null;
            string code = response.Substring(6);
            string msg = GrblErrors.GetMessage(code);
            return string.IsNullOrEmpty(msg) || msg == code ? null : msg;
        }

        /// <summary>The full plain-text report for the completed run (what the Copy button exports).</summary>
        public string BuildReportText()
        {
            int total = Tests.Count(x => !x.Helper);
            int passed = Tests.Count(x => !x.Helper && x.Passed);
            int failed = total - passed;

            var sb = new StringBuilder();
            sb.AppendLine("Validate controller");
            sb.AppendLine(string.Format("{0}{1} - {2} axes",
                GrblInfo.Firmware, string.IsNullOrEmpty(GrblInfo.Version) ? "" : " " + GrblInfo.Version, GrblInfo.NumAxes));
            sb.AppendLine(string.Format("{0} of {1} features passed{2}", passed, total, failed > 0 ? string.Format(", {0} failed", failed) : ""));
            sb.AppendLine();

            // The pre-run persistent parameter state ($#), for the record - validation restores these
            // and never writes machine settings ($$).
            if (Parameters != null && !string.IsNullOrEmpty(Parameters.ParamText))
            {
                sb.AppendLine("Work parameters before run (restored afterwards):");
                sb.Append(Parameters.ParamText);
                sb.AppendLine();
            }

            string lastCat = null;
            foreach (var test in Tests.Where(x => !x.Helper || !x.Passed))
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
                else if (test.TimedOut)
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
