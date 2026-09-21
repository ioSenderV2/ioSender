/*
 * AutoSquareProbeWizard.xaml.cs - part of CNC Controls library
 *
 * Gantry squareness measured by PROBING a reference square, rather than by drilling an L, pinning it and
 * sighting the gap by eye (AutoSquareWizard, the tab next door). Same quantity, same correction, same
 * $170-$172 squaring offset written at the end - only the instrument changes.
 *
 * The inversion is the point. In the pin method the machine is the WRITER (the drilled holes are the
 * machine's own idea of a right angle) and a framing square is the READER. Here the square is the artifact
 * and the machine's probe is the reader, which buys three things:
 *
 *   - Resolution. A sighted gap at a pin is +-0.05..0.1 mm; probe repeatability is ~0.01-0.02 mm. Over a
 *     600 mm blade that is 0.002 deg instead of 0.01 deg.
 *   - A free iteration. After Apply + re-home the gantry angle has CHANGED, so the L that was just drilled
 *     sits at the old angle and cannot verify the new one - every pin iteration costs a fresh L, a bit, a
 *     Z touch-off and more spoilboard. A re-probe costs two minutes and nothing else.
 *   - The reversal test (not built yet - see the note at the end of this comment).
 *
 * It probes corners 1 (heel), 2 (blade far end) and 3 (tongue far end) through the same pcorner.macro
 * StartJobView and the stepper-calibration probe wizard already use. All three are real outside corners on
 * an L: each has a genuine X face and Y face for the macro to find. There is no corner 4, which is exactly
 * why this tool exists separately rather than as an option on a stock Measure - and nothing here needs one,
 * since the skew is the angle at corner 1 and takes three points.
 *
 * Thin material: a framing square is ~4.8 mm of steel, and pcorner's face probes searched at a FIXED 5 mm
 * below the probed top until 2026-09-16 - 0.2 mm below the square's underside, seeking through open air.
 * That is what #<_ls_facedepth> was added for; this is its first and so far only caller.
 *
 * Tool length: the run touches the puck after the probe is fitted, through Setup's own baseline/reference/
 * restore trio. Not for the measurement's sake - it is XY, and a tool length cannot tilt an angle - but
 * because fitting the probe IS a tool change and a bit fitted by hand has no offset of its own. Skipping it
 * leaves the run working against, and handing back, whatever offset the PREVIOUS tool left. The first cut
 * of this file restored that inherited offset instead, which is worse than it sounds: it puts back a
 * confident-looking number measured against a tool that is no longer in the spindle.
 *
 * The REVERSAL, built 2026-09-16 once the single measurement was hardware-verified. A lone reading is
 * (square + machine) and cannot be separated - correcting it to zero squares the gantry TO the square.
 * Flipping the square mirrors it, which reverses the sign of ITS error and not the machine's, so:
 *
 *     normal   = S + eps          reversed = S - eps
 *     square S = (normal + reversed) / 2     machine eps = (normal - reversed) / 2
 *
 * The flip forces the heel to MOVE. A mirrored L cannot lie with both arms in the +X/+Y quadrant - it can
 * only manage +X/-Y or -X/+Y - and no rotation undoes a mirror, so the reversed orientation registers the
 * far end of the BLADE in the fence and puts the heel out to the right. Corner ids become 1/2/4 instead
 * of 1/2/3, with the heel as the vertex rather than the fence corner; storing points by ROLE is what
 * keeps that out of the arithmetic. Both halves must be measured at the same squaring offset and with no
 * homing between them, or they describe different machines.
 *
 * The square's error is then PERSISTED, and that is the real payoff: it is a property of a physical
 * object, so every later single reading yields the machine's error by subtraction and the reversal never
 * has to be repeated.
 */

using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class AutoSquareProbeWizard : ConfigPanel<AutoSquareProbeParams>, IGrblConfigTab, IAvailabilityGated
    {
        // Same treatment as the pin tab: kept on every build. With the squaring-offset setting present it
        // tunes that offset; without it this is still a squareness GAUGE - arguably a better one than the
        // pin method ever was - just with the Apply step disabled and the error to be corrected mechanically.
        public string UnavailableReason => SquaringSettingExists()
            ? null
            : "No auto-square offset setting ($170-$172) in this firmware - measure only.";
        public bool HideWhenUnavailable => false;

        private GrblViewModel model = null;
        private string program = string.Empty;
        private bool subscribed = false;
        private string restoreFixtureName;   // captured from config, applied once RefreshFixtures has a list to match against

        private GrblSettingDetails _offset = null;   // the controller's squaring-offset setting (or null)
        private int _gangedAxis = 1;                 // 0=X, 1=Y, 2=Z

        // grblHAL Setting_AxisAutoSquareOffset = AxisSettingsBase(100) + 7*INCREMENT(10): X=170, Y=171, Z=172.
        private const int AutoSquareOffsetBase = 170;

        // The three probed corners by ROLE, machine coordinates. Null until a run has reported them.
        //
        // By role and not by corner id, because the reversal test probes a DIFFERENT set of pcorner corners
        // (1/2/4 rather than 1/2/3) with a different one of them as the vertex. Storing what each point IS
        // rather than which id produced it means the skew arithmetic below never learns about orientation at
        // all - the only code that branches is the program generator that aims the seeks.
        private double? heelX, heelY, bladeX, bladeY, tongueX, tongueY;

        /// <summary>One completed measurement, and the conditions that make it comparable to another.</summary>
        private struct Reading
        {
            public double Skew;         // degrees, blade-to-tongue interior angle minus 90
            public double BladeSpan;    // heel -> blade end, mm
            public double TongueSpan;   // heel -> tongue end, mm
            public double Offset;       // the squaring offset in force when this was measured
        }

        // The reversal pair. Both must have been taken at the SAME squaring offset or they are not
        // comparable - the machine's contribution differs between them and the split is meaningless.
        private Reading? readNormal, readReversed;

        /// <summary>
        /// The reference square's OWN out-of-squareness, in degrees, once a reversal pair has established
        /// it. Persisted: it is a property of a physical object the operator owns, not of a session.
        /// </summary>
        /// <remarks>
        /// This is the real payoff of the reversal, and it outlives the run that measured it. A single
        /// normal-orientation reading is (square + machine) and cannot be separated. Once the square's own
        /// error is known, every later single reading gives the MACHINE's error directly by subtraction -
        /// so the reversal is a one-off calibration of the artifact, not a thing to repeat each time.
        ///
        /// A plain double rather than a nullable "known/unknown", because zero and unknown want EXACTLY the
        /// same behaviour: subtract nothing. Collapsing them removes a state that could only ever disagree
        /// with itself - and it has the side benefit that a genuinely square square, which measures zero, is
        /// not a special case. The flag below exists solely so the status text can tell the operator which
        /// of the two they are looking at; no arithmetic reads it.
        ///
        /// Editable, because the reversal that measured it may have happened in a session this install has
        /// no memory of - as it did on 2026-09-16, when the pair was split by hand across an app restart.
        /// </remarks>
        private bool squareErrorKnown = false;

        // Which way the square is currently clamped. Drives the corner ids and seek references in
        // BuildProgram, the operator prompts, and which half of the reversal pair a result lands in.
        private bool reversed = false;

        // Above this the offset is racking the gantry a lot - on a rigid frame that binds. Same threshold,
        // same reasoning, as the pin tab.
        private const double LargeOffsetWarn = 2d;

        // Whether a positive squaring offset racks the gantry the same way a positive measured skew does is
        // a property of which rail the firmware's ganged motor drives, and that is NOT readable from the
        // controller - $170-$172 all report regardless, and only one accepts a write. So this is NOT derived
        // and NOT asserted: the operator resolves it once, empirically, by watching whether the first Apply
        // made the measured skew smaller or bigger. See HowToText.
        //
        // The alternative was to reason it out from the firmware and ship a sign. That is exactly what was
        // done for the WCS rotation R word, where a plausible derivation defended by a confounded hardware
        // reading stood for weeks while every job came out with DOUBLE the error it was correcting.
        private bool invertCorrection = false;

        // (PRINT, SQ_<role><axis>=..) - the same "(PRINT, TAG=value)" idiom every other generator here uses.
        // Tagged by role rather than by corner number so the same three tags serve both orientations; see
        // the role fields above.
        private static readonly Regex rxCorner = new Regex(@"SQ_(HEEL|BLADE|TONGUE)([XY])\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

        private bool isActiveTab = false;

        // See StartJobView.OwnsRunBar / the stepper probe wizard's own copy: across the Generate handoff to
        // the Job tab the run bar still points here, so writes to the shared MacroProcessor statics have to
        // be gated on this rather than isActiveTab alone.
        private bool OwnsRunBar { get { return isActiveTab || MacroProcessor.HoldsHandoffFrom(ViewType.Calibration); } }

        // An IDENTITY, not a label - the handoff record and its watcher test the loaded job against it.
        private const string ProgramNameSquare = "Squareness (probe)";

        private const string HowToText =
            "1. Clamp the reference square down with its heel into the Corner Fence, blade along X and tongue along Y, sitting on a spacer that is INSET from its edges - the probe has to be able to reach the steel, and nothing else may be flush with it.\n\n" +
            "2. Zero the current offset first (the Clear button), so what you measure is the machine's raw mechanical error rather than the residual of a correction you have forgotten about.\n\n" +
            "3. Enter the square's arm lengths (roughly - they only aim the seek) and its thickness (accurately - it sets the probe depth).\n\n" +
            "4. Generate, then Run. It parks at G30 to confirm the probe, touches the puck to give that probe its own tool length offset, then probes the heel and the far end of each arm - and puts the offset back at the end. Fitting the probe is a tool change, so without that reference the run would work against whatever offset the previous tool left, and hand the machine back the same way.\n\n" +
            "5. Read the measured skew. Apply offset, then Re-home for it to take effect.\n\n" +
            "6. RUN IT AGAIN. This is not optional and it is not a formality - it is how the correction's direction gets established. The skew should collapse toward zero. If it roughly DOUBLED instead, the sign is backwards for your machine: tick 'Invert correction direction', Apply, re-home and re-measure. Leave it ticked from then on.\n\n" +
            "7. THE REVERSAL - do this once and the square stops being the unknown. A single reading is the square's own error and the machine's added together, so correcting it to zero would square the gantry TO the square. To split them: measure in Normal, then FLIP THE SQUARE OVER and re-clamp with the far end of the BLADE registered in the fence and the heel out to the right at blade length, switch the orientation above to Reversed, and run again. Do NOT change the offset and do NOT re-home between the two - both halves must see the same machine, and skipping the homing keeps its own scatter out of the difference.\n\n" +
            "Why flipping works: a mirrored square has its own error reversed in sign while the machine's is unchanged. Normal reads square + machine, reversed reads square - machine, so the mean is the square and half the difference is the gantry. The square's error is then remembered, and every later single reading gives the machine alone by subtraction - you never need to reverse again unless you change squares.\n\n" +
            "What the number means: the skew is the angle between the square's two arms as the machine sees them, minus 90 degrees. It is the machine's error and the square's error added together, and nothing here can separate them - so a result at or below about 0.01 degrees is the square's accuracy talking, not the gantry's.\n\n" +
            "If the offset needed is more than a couple of mm, the gantry is mechanically out of square (the two rails out of phase) and racking it that hard can bind a rigid frame. Fix that first; the squaring offset is fine-trim.";

        public AutoSquareProbeWizard()
        {
            InitializeComponent();
            model = DataContext as GrblViewModel;
            txtHowTo.Text = HowToText;
            // Set here rather than IsChecked="True" in the XAML: that fires the Checked handler mid-BAML
            // parse, before later-declared sibling fields exist. Same reason the stepper probe wizard sets
            // its own radio default in its constructor. ApplyConfig overrides this once the saved state
            // loads; without it a fresh install would start with NEITHER radio selected.
            rbOrientNormal.IsChecked = true;
        }

        #region Methods required by IGrblConfigTab

        public GrblConfigType GrblConfigType { get { return GrblConfigType.AutoSquareProbe; } }

        public void Activate(bool activate)
        {
            isActiveTab = activate;
            if (model == null)
                model = DataContext as GrblViewModel;

            if (activate)
            {
                RefreshFixtures();
                RefreshProbeChoices();
                DetectOffsetSetting();
                if (!subscribed && model != null)
                {
                    model.PropertyChanged += Model_PropertyChanged;
                    subscribed = true;
                }
                MacroProcessor.ActiveRun = Run;

                MacroProcessor.SupportsGenerateMode = true;
                MacroProcessor.ActiveGenerate = Generate;
                MacroProcessor.DiscardGenerated = DiscardProgram;
                MacroProcessor.IsProgramGenerated = !string.IsNullOrEmpty(program);
                RefreshGenerateReady();
                UpdateComputed();
            }
            // Our OWN handoff switching away to the Job tab, not the operator leaving - keep the run bar and
            // the program. See the stepper probe wizard's identical guard for what tearing down here costs.
            else if (!MacroProcessor.IsHandingOffFrom(ViewType.Calibration))
            {
                MacroProcessor.ActiveRun = null;
                MacroProcessor.SupportsGenerateMode = false;
                MacroProcessor.ActiveGenerate = null;
                MacroProcessor.DiscardGenerated = null;
                program = string.Empty;
                MacroProcessor.ReleaseHandoff(model);
            }

            if (model != null)
                model.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        // The coarse live-readiness gate for the shared Run bar's "Generate" button. Finer preconditions
        // (arm lengths, thickness, a located fixture corner) surface via txtWarnings at Generate time.
        private void RefreshGenerateReady()
        {
            if (!isActiveTab)
                return;
            bool haveProbe = ActiveProbe() != null;
            MacroProcessor.IsGenerateReady = haveProbe && SelectedFixture != null;
            if (!MacroProcessor.IsGenerateReady)
                MacroProcessor.GenerateBlockedReason = !haveProbe
                    ? "No " + (IsTouchPlate ? "touch plate" : "3D probe") + " is defined - add one in Machine Setup > Probe definitions."
                    : "Select a validated Corner Fence fixture first (Machine Setup > Fixture definitions).";
        }

        // The handoff is over. On an ABORT the operator is left on the Job tab and the run bar would go on
        // pointing at this off-screen tab - give the bar back. See the sibling's own comment.
        private void EndHandoff()
        {
            if (isActiveTab)
                return;
            MacroProcessor.ActiveRun = null;
            MacroProcessor.SupportsGenerateMode = false;
            MacroProcessor.ActiveGenerate = null;
            MacroProcessor.DiscardGenerated = null;
            MacroProcessor.IsProgramGenerated = false;
        }

        private void DiscardProgram()
        {
            program = string.Empty;
            MacroProcessor.ReleaseHandoff(model);
            if (OwnsRunBar)
                MacroProcessor.IsProgramGenerated = false;
        }

        #endregion

        #region The squaring-offset setting ($170-$172)

        // Duplicated from AutoSquareWizard rather than factored into a shared helper, deliberately and for
        // now: that tab is hardware-proven and this one has never run, so a refactor that touches both is
        // the one change that could break the working method to serve the unproven one. If the probe method
        // wins, the pin tab goes away and there is nothing left to share; if it does not, this file does.
        // Revisit once this has measured a real gantry.
        public static bool SquaringSettingExists()
        {
            return GrblSettings.Settings.Any(s => s.Id >= AutoSquareOffsetBase && s.Id <= AutoSquareOffsetBase + 2);
        }

        private void DetectOffsetSetting()
        {
            // grblHAL REPORTS the offset for all of $170-$172 whenever ANY axis is squared (its availability
            // check is not per-axis), but only the actually-ganged axis accepts a WRITE. Which one that is
            // cannot be read back, so the operator selects it; we target $170 + that axis.
            int id = AutoSquareOffsetBase + _gangedAxis;
            _offset = GrblSettings.Settings.FirstOrDefault(s => s.Id == id);

            if (!SquaringSettingExists())
            {
                txtAxisInfo.Text = "Measure-only: this firmware has no auto-square offset setting (not built with an auto-squared axis), so there is nothing to write. The measurement below is still the machine's squareness error - correct it mechanically.";
                CurrentOffset = 0d;
                _offset = null;
            }
            else if (_offset == null)
            {
                txtAxisInfo.Text = string.Format("No ${0} setting for the {1} axis.", id, "XYZ"[_gangedAxis]);
                CurrentOffset = 0d;
            }
            else
            {
                CurrentOffset = ParseValue(_offset.Value);
                string unit = string.IsNullOrEmpty(_offset.Unit) ? "mm" : _offset.Unit;
                txtAxisInfo.Text = string.Format("{0} ganged auto-square  ·  setting ${1} - {2}\nCurrent squaring offset: {3} {4} (range {5}..{6})",
                    "XYZ"[_gangedAxis], _offset.Id, _offset.Name, _offset.Value, unit, F(_offset.Min), F(_offset.Max));
            }
        }

        private bool HasRange { get { return _offset != null && _offset.Max > _offset.Min; } }

        private static double ParseValue(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0d;
        }

        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static double AxisTravel(int axis)
        {
            double t = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + axis);
            return double.IsNaN(t) ? 0d : Math.Abs(t);
        }

        // The ganged motors sit at the extremes of the PERPENDICULAR axis (Y-ganged -> the X rails), so the
        // squaring offset - a per-rail distance - is the skew angle times the full rail span.
        private double RailSpan() { return AxisTravel(_gangedAxis == 0 ? 1 : 0); }

        #endregion

        #region Dependency properties

        private static DependencyProperty Reg(string name, double def)
        {
            return DependencyProperty.Register(name, typeof(double), typeof(AutoSquareProbeWizard),
                                               new PropertyMetadata(def, (d, e) => ((AutoSquareProbeWizard)d).UpdateComputed()));
        }

        public static readonly DependencyProperty BladeLengthProperty = Reg(nameof(BladeLength), 609.6d);   // actual 24" blade
        public double BladeLength { get { return (double)GetValue(BladeLengthProperty); } set { SetValue(BladeLengthProperty, value); } }

        public static readonly DependencyProperty TongueLengthProperty = Reg(nameof(TongueLength), 406.4d);  // actual 16" tongue
        public double TongueLength { get { return (double)GetValue(TongueLengthProperty); } set { SetValue(TongueLengthProperty, value); } }

        // Measured, not nominal - 4.8 mm is a Milwaukee Red framing square on the bench this was written for.
        public static readonly DependencyProperty SquareThicknessProperty = Reg(nameof(SquareThickness), 4.8d);
        public double SquareThickness { get { return (double)GetValue(SquareThicknessProperty); } set { SetValue(SquareThicknessProperty, value); } }

        public static readonly DependencyProperty CornerTravelMarginMmProperty = Reg(nameof(CornerTravelMarginMm), 15d);
        public double CornerTravelMarginMm { get { return (double)GetValue(CornerTravelMarginMmProperty); } set { SetValue(CornerTravelMarginMmProperty, value); } }

        /// <summary>
        /// The reference square's own error, subtracted from every reading to leave the machine's.
        /// </summary>
        /// <remarks>
        /// NOT in PersistedProperties, deliberately. That list routes through OnPersistedPropertyChanged,
        /// which discards the generated program - correct for anything that changes where the probe goes,
        /// and wrong for this: it changes only how the result is interpreted, and throwing away a program
        /// the operator is about to run would be a surprise with no cause. It is persisted by hand from its
        /// own callback instead.
        /// </remarks>
        public static readonly DependencyProperty SquareErrorProperty =
            DependencyProperty.Register(nameof(SquareError), typeof(double), typeof(AutoSquareProbeWizard),
                                        new PropertyMetadata(0d, (d, e) => ((AutoSquareProbeWizard)d).OnSquareErrorChanged()));
        public double SquareError { get { return (double)GetValue(SquareErrorProperty); } set { SetValue(SquareErrorProperty, value); } }

        private void OnSquareErrorChanged()
        {
            // An operator typing a value is asserting that they know it, exactly as a completed reversal
            // does. Zero means "subtract nothing", which is also what not knowing means - see the field.
            if (Math.Abs(SquareError) > 1e-9)
                squareErrorKnown = true;
            Persist();
            UpdateComputed();
        }

        public static readonly DependencyProperty CurrentOffsetProperty =
            DependencyProperty.Register(nameof(CurrentOffset), typeof(double), typeof(AutoSquareProbeWizard),
                                        new PropertyMetadata(0d, (d, e) => ((AutoSquareProbeWizard)d).UpdateComputed()));
        public double CurrentOffset { get { return (double)GetValue(CurrentOffsetProperty); } set { SetValue(CurrentOffsetProperty, value); } }

        // Editable: normally computed from the measurement, but an operator may type a target directly.
        // Deliberately NOT a persisted property - it is a result, and restoring a stale one across sessions
        // would offer to Apply a correction for a measurement that no longer exists.
        public static readonly DependencyProperty NewOffsetProperty =
            DependencyProperty.Register(nameof(NewOffset), typeof(double), typeof(AutoSquareProbeWizard),
                                        new PropertyMetadata(0d, (d, e) => ((AutoSquareProbeWizard)d).RefreshReadout()));
        public double NewOffset { get { return (double)GetValue(NewOffsetProperty); } set { SetValue(NewOffsetProperty, value); } }

        #endregion

        #region Measurement -> correction

        private bool HasAllCorners
        {
            get { return heelX.HasValue && heelY.HasValue && bladeX.HasValue && bladeY.HasValue && tongueX.HasValue && tongueY.HasValue; }
        }

        private void ClearCorners()
        {
            heelX = heelY = bladeX = bladeY = tongueX = tongueY = null;
        }

        /// <summary>
        /// File a completed measurement into the half of the reversal pair matching the current orientation,
        /// and - when that completes a comparable pair - solve for the square's own error.
        /// </summary>
        /// <remarks>
        /// The pair is only comparable at a single squaring offset. Measure one orientation, change $17x,
        /// then measure the other and the machine's contribution is not the same in both, so the split is
        /// arithmetic on two different machines. Refused rather than averaged: the stored offset is what
        /// makes that detectable, and a silently wrong square calibration would bias every correction
        /// afterwards while looking more authoritative than the raw number it replaced.
        ///
        /// There is also no need to re-home between the two, and you should not - the ganged axis
        /// re-establishes its squareness from two home switches with a spread of its own (about 0.004 deg
        /// measured on this machine), and that spread cancels out of the difference only if no homing
        /// happens between the readings.
        /// </remarks>
        private void CaptureReading()
        {
            double? skew = SkewDegrees();
            if (!skew.HasValue)
                return;

            var r = new Reading
            {
                Skew = skew.Value,
                BladeSpan = BladeSpan() ?? 0d,
                TongueSpan = TongueSpan() ?? 0d,
                Offset = CurrentOffset
            };

            if (reversed)
                readReversed = r;
            else
                readNormal = r;

            // Normal reads (square + machine), reversed reads (square - machine) - so the mean is the
            // square and half the difference is the machine. See the file header for the derivation.
            if (PairIsComparable)
            {
                squareErrorKnown = true;
                SetCurrentValue(SquareErrorProperty, (readNormal.Value.Skew + readReversed.Value.Skew) / 2d);
            }

            // Every half, not just a completed pair. A lone half IS the thing worth surviving a restart -
            // it is twenty minutes of clamping and probing, and the second half is normally measured after
            // the operator has been away from the machine.
            Persist();
        }

        /// <summary>Both halves measured, and at the same squaring offset.</summary>
        private bool PairIsComparable
        {
            get
            {
                // Against each other AND against the offset in force NOW. The first alone was enough while
                // the halves lived only in memory and Apply cleared them; once they survive restarts, a pair
                // taken at 0.166 can be read back on a machine whose $17x has since been changed elsewhere -
                // by the settings editor, by MDI, by the pins tab - and it would then report a machine error
                // for a gantry that no longer exists. Falling back to (reading - square error) is correct
                // there, and is what this makes happen.
                return readNormal.HasValue && readReversed.HasValue
                    && Math.Abs(readNormal.Value.Offset - readReversed.Value.Offset) <= 1e-6
                    && Math.Abs(readNormal.Value.Offset - CurrentOffset) <= 1e-6;
            }
        }

        /// <summary>How the machine's own error was arrived at - shown, because it changes what to trust.</summary>
        private enum SkewBasis
        {
            None,
            Raw,            // one reading, no square calibration: square + machine, inseparable
            Calibrated,     // one reading minus a previously measured square error
            Reversal        // both halves of a reversal pair, this session
        }

        private SkewBasis basis = SkewBasis.None;

        /// <summary>
        /// The MACHINE's own squareness error - the thing the squaring offset can actually correct - and
        /// the basis it rests on.
        /// </summary>
        private double? MachineSkewDegrees(out SkewBasis how)
        {
            if (PairIsComparable)
            {
                how = SkewBasis.Reversal;
                return (readNormal.Value.Skew - readReversed.Value.Skew) / 2d;
            }

            double? skew = SkewDegrees();
            if (!skew.HasValue)
            {
                how = SkewBasis.None;
                return null;
            }

            // Subtract unconditionally. Unknown is stored as zero, so this is the same arithmetic either
            // way - only the label the operator sees differs.
            how = squareErrorKnown ? SkewBasis.Calibrated : SkewBasis.Raw;
            return skew.Value - SquareError;
        }

        /// <summary>
        /// Deviation from 90 degrees between the blade and the tongue, measured from the heel, as the
        /// MACHINE sees them. 0 = the machine's right angle and the square's agree.
        /// </summary>
        /// <remarks>
        /// Identical formula to StartJobView.SkewDegrees, which measures the same thing against a rectangle
        /// of stock and needs only three of its corners for it. Kept as its own copy because this tool lives
        /// in CNC.Controls and that one in the app assembly.
        ///
        /// ORIENTATION-BLIND, and deliberately so. In the reversed orientation the blade runs along -X
        /// rather than +X, so the vectors point into a different quadrant - but the unsigned angle between
        /// them is still the interior angle of the L, and that is all this computes. The reversal's sign
        /// flip comes from the GEOMETRY, not from any branch here: normal reads (square + machine) and
        /// reversed reads (square - machine) because a mirrored square in a skewed frame genuinely measures
        /// differently. See the file header.
        /// </remarks>
        private double? SkewDegrees()
        {
            if (!HasAllCorners)
                return null;

            double ax = bladeX.Value - heelX.Value, ay = bladeY.Value - heelY.Value;      // along the blade
            double bx = tongueX.Value - heelX.Value, by = tongueY.Value - heelY.Value;    // along the tongue
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6)
                return null;

            double cos = Math.Max(-1d, Math.Min(1d, (ax * bx + ay * by) / (la * lb)));
            return Math.Acos(cos) * 180d / Math.PI - 90d;
        }

        private double? BladeSpan()
        {
            if (!HasAllCorners) return null;
            double ax = bladeX.Value - heelX.Value, ay = bladeY.Value - heelY.Value;
            return Math.Sqrt(ax * ax + ay * ay);
        }

        private double? TongueSpan()
        {
            if (!HasAllCorners) return null;
            double bx = tongueX.Value - heelX.Value, by = tongueY.Value - heelY.Value;
            return Math.Sqrt(bx * bx + by * by);
        }

        /// <summary>
        /// The change to the squaring offset that the measured skew implies: the two ganged motors are
        /// RailSpan apart, so an angular error of theta is a per-rail distance of RailSpan * tan(theta).
        /// </summary>
        /// <remarks>
        /// A DELTA on the current offset, not an absolute value - the skew was measured with whatever offset
        /// is presently in force, so it is the RESIDUAL error, and correcting it means moving the offset by
        /// this much from where it is. (The pin tab next door maps its measured gap to an absolute offset
        /// instead. That is only equivalent when the holes were drilled at offset zero - which is why this
        /// tool's how-to tells you to Clear first, and why the two tabs will not agree on a machine already
        /// carrying an offset. Not reconciled here; changing the proven tab is not this change's business.)
        ///
        /// The SIGN is not derived - see invertCorrection.
        /// </remarks>
        private double CorrectionDelta()
        {
            SkewBasis how;
            double? skew = MachineSkewDegrees(out how);
            if (!skew.HasValue)
                return 0d;
            double rs = RailSpan();
            if (rs <= 0d)
                return 0d;
            double d = rs * Math.Tan(skew.Value * Math.PI / 180d);
            return invertCorrection ? -d : d;
        }

        // Set by UpdateComputed when the setting's own range actually bit, and read by RefreshReadout to
        // decide whether to say so. It cannot be re-derived there by comparing NewOffset against the computed
        // value, because NewOffset is editable - any hand-typed number differs from the computed one, and
        // that test reported "clamped - check the measurement" at every keystroke.
        private bool computedWasClamped = false;

        private void UpdateComputed()
        {
            double raw = CurrentOffset + CorrectionDelta();
            double clamped = HasRange ? Math.Max(_offset.Min, Math.Min(_offset.Max, raw)) : raw;
            computedWasClamped = Math.Abs(clamped - raw) > 1e-6;
            SetCurrentValue(NewOffsetProperty, clamped);
            RefreshReadout();
        }

        private void RefreshReadout()
        {
            if (txtResult == null)
                return;

            double? skew = SkewDegrees();
            double? machine = MachineSkewDegrees(out basis);
            bool measureOnly = _offset == null;
            double railSpan = RailSpan();

            txtResult.Text = skew.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "Measured skew:  {0:0.0###}°   ({1}, blade {2:0.0##} mm, tongue {3:0.0##} mm)",
                                skew.Value, reversed ? "reversed" : "normal", BladeSpan() ?? 0d, TongueSpan() ?? 0d)
                : "Measured skew:  -   (Generate and Run to probe the square)";

            if (txtReversal != null)
                txtReversal.Text = ReversalStatus(machine);

            string warn = string.Empty;
            if (railSpan <= 0d)
                warn = "Set max travel ($130-$132) first - the rail span the offset is computed over comes from the homed envelope.";
            else if (skew.HasValue && !measureOnly && computedWasClamped)
                warn = string.Format("The offset this skew implies is outside the setting range {0}..{1} mm and was clamped - check the measurement, and check the square is clamped flat.", F(_offset.Min), F(_offset.Max));
            else if (skew.HasValue && !measureOnly && Math.Abs(NewOffset) > LargeOffsetWarn)
                warn = string.Format("Offset {0:0.0} mm is large - that much racking can bind a rigid gantry. If it binds, the gantry is mechanically out of square (the rails out of phase); fix that first, the squaring offset is fine-trim only.", NewOffset);
            // A span wildly adrift from the entered arm length means the probe found something other than the
            // edge it was aimed at - a clamp, the spacer, the fence. Worth saying before the number is trusted.
            else if (skew.HasValue && (SpanAdrift(BladeSpan(), BladeLength) || SpanAdrift(TongueSpan(), TongueLength)))
                warn = "A measured arm span is more than 20 mm from the length entered above - check the probe touched the square's own edges and not a clamp, the spacer or the fence before trusting the skew.";

            if (txtWarnings != null)
                txtWarnings.Text = warn;

            // Enabled whenever there is a setting to write. Gated on NOTHING else, and both of the
            // conditions that used to be here were wrong:
            //
            //   skew.HasValue - made a hand-typed offset impossible after a relaunch, because the live
            //   corners are empty until a run reports them. Typing a value IS a legitimate way to use this
            //   panel; it is what the operator was told to do while the square calibration was being worked
            //   out by hand.
            //
            //   NewOffset != CurrentOffset - deadlocks against the commit-on-click fix. A typed value does
            //   not reach the DependencyProperty until focus leaves the field, so the button compares the
            //   OLD value, sees no difference, and disables itself - and a disabled button never receives
            //   the click that would have committed the edit. The gate depended on the very state it
            //   prevented from being updated.
            //
            // The handler decides instead, after CommitPendingEdits has run, where the real value is known.
            if (btnApply != null)
                btnApply.IsEnabled = !measureOnly;

            if (txtSummary != null)
            {
                if (!skew.HasValue)
                    txtSummary.Text = string.Empty;
                else if (measureOnly)
                    txtSummary.Text = string.Format(CultureInfo.InvariantCulture,
                        "machine error {0:0.0###}° → the gantry is {1:0.000} mm out of square across the {2:0} mm rail span. Measure-only (no offset setting) - correct it mechanically.",
                        machine ?? 0d, Math.Abs(CorrectionDelta()), railSpan);
                else
                    txtSummary.Text = string.Format(CultureInfo.InvariantCulture,
                        "machine error {0:0.0###}° over a {1:0} mm rail span → correction {2:+0.000;-0.000;0} mm, new offset {3:0.000} (from {4:0.000}){5}",
                        machine ?? 0d, railSpan, CorrectionDelta(), NewOffset, CurrentOffset,
                        invertCorrection ? "  ·  inverted" : string.Empty);
            }
        }

        private static bool SpanAdrift(double? measured, double entered)
        {
            return measured.HasValue && entered > 0d && Math.Abs(measured.Value - entered) > 20d;
        }

        /// <summary>
        /// The reversal line: what is known about the square itself, and therefore how much of the measured
        /// skew is actually the machine.
        /// </summary>
        /// <remarks>
        /// Stated at every stage rather than only when complete, because the whole point of this panel is
        /// that a raw reading CANNOT distinguish a crooked gantry from a crooked square, and an operator who
        /// forgets that will square the machine to the square and read it as success.
        /// </remarks>
        private string ReversalStatus(double? machine)
        {
            switch (basis)
            {
                case SkewBasis.Reversal:
                    return string.Format(CultureInfo.InvariantCulture,
                        "Reversal complete (both halves at offset {0:0.000}):  square {1:+0.0###;-0.0###;0}°  ·  machine {2:+0.0###;-0.0###;0}°\nnormal {3:+0.0###;-0.0###;0}° = square + machine,  reversed {4:+0.0###;-0.0###;0}° = square - machine. The square's error is now remembered.",
                        readNormal.Value.Offset, SquareError, machine ?? 0d,
                        readNormal.Value.Skew, readReversed.Value.Skew);

                case SkewBasis.Calibrated:
                    return string.Format(CultureInfo.InvariantCulture,
                        "Square error {0:+0.0###;-0.0###;0}° known from an earlier reversal - subtracted, so the machine error above is the gantry alone.",
                        SquareError);

                case SkewBasis.Raw:
                    string half = readNormal.HasValue || readReversed.HasValue
                        ? (readNormal.HasValue ? "Normal half measured" : "Reversed half measured")
                          + (readNormal.HasValue && readReversed.HasValue
                             ? string.Format(CultureInfo.InvariantCulture, " - but the two halves are at DIFFERENT offsets ({0:0.000} and {1:0.000}), so they cannot be split. Re-run one of them at the other's offset.",
                                             readNormal.Value.Offset, readReversed.Value.Offset)
                             : " - now flip the square, switch the orientation above, and run the other half WITHOUT re-homing or changing the offset.")
                        : string.Empty;
                    return "This reading is the square's error and the machine's added together, and nothing here can separate them - correcting it to zero would square the gantry TO the square. Run the reversal to split them.\n" + half;

                default:
                    return squareErrorKnown
                        ? string.Format(CultureInfo.InvariantCulture, "Square error {0:+0.0###;-0.0###;0}° known; it will be subtracted from the next reading.", SquareError)
                        : "No reversal done yet - a single reading cannot tell a crooked gantry from a crooked square.";
            }
        }

        #endregion

        #region Fixture / probe pickers

        private void RefreshFixtures()
        {
            string current = SelectedFixture?.Name ?? restoreFixtureName;
            cbxFixture.ItemsSource = Fixtures.Items
                .Where(f => f.PositionValidated && f.Kind == FixtureKind.CornerFence && f.Implemented)
                .ToList();
            txtNoFixture.Visibility = (cbxFixture.ItemsSource as System.Collections.Generic.List<Fixture>)?.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(current))
                cbxFixture.SelectedItem = (cbxFixture.ItemsSource as System.Collections.Generic.List<Fixture>)?.FirstOrDefault(f => f.Name == current);
        }

        private Fixture SelectedFixture { get { return cbxFixture.SelectedItem as Fixture; } }

        private bool IsTouchPlate
        {
            get { return cbxProbeType != null && cbxProbeType.SelectedIndex == 1; }
            set { if (cbxProbeType != null) cbxProbeType.SelectedIndex = value ? 1 : 0; }
        }

        private static ProbeDefinition ThreeDProbe()
        {
            return ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
        }

        // Corner-capable only: this wizard probes the reference square's corners through pcorner.macro, so a
        // flat Z-only plate has nothing to register against. See ProbeDefinition.CanProbeCorner.
        private static ProbeDefinition TouchPlateProbe()
        {
            return ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.TouchPlate && p.CanProbeCorner);
        }

        private ProbeDefinition ActiveProbe()
        {
            return IsTouchPlate ? TouchPlateProbe() : ThreeDProbe();
        }

        private void RefreshProbeChoices()
        {
            if (cbxProbeType == null)
                return;

            bool has3d = ThreeDProbe() != null, hasTouch = TouchPlateProbe() != null;
            cbiProbe3d.IsEnabled = has3d;
            cbiProbeTouch.IsEnabled = hasTouch;

            if (IsTouchPlate && !hasTouch && has3d)
                IsTouchPlate = false;
            else if (!IsTouchPlate && !has3d && hasTouch)
                IsTouchPlate = true;

            txtNoProbe.Visibility = ActiveProbe() == null ? Visibility.Visible : Visibility.Collapsed;

            // Say so rather than silently doing nothing: the box stays tickable but a run cannot reference
            // anything without a puck defined, and the consequence (ending in G49) is the one worth seeing
            // BEFORE the run rather than discovering on the next job.
            if (txtNoToolSetter != null)
                txtNoToolSetter.Visibility = chkReferenceTlo != null && chkReferenceTlo.IsChecked == true && !WillReferenceTlo
                                           ? Visibility.Visible : Visibility.Collapsed;
        }

        private void cbxProbeType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Fires DURING InitializeComponent (the ComboBox carries SelectedIndex="0" and WPF raises
            // SelectionChanged from ItemsControl.EndInit as the BAML loads), when every other x:Name field
            // is still null. The sibling wizard crashed the whole Calibration view this way. Nothing is lost
            // by skipping it - Activate(true) calls RefreshProbeChoices itself.
            if (!IsInitialized)
                return;

            RefreshProbeChoices();
            Persist();
            DiscardProgram();
            RefreshGenerateReady();
        }

        private void cbxFixture_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Persist();          // not a DependencyProperty change - ConfigPanel.Persist() must be called explicitly
            DiscardProgram();   // a program generated against the PREVIOUS fixture is stale
            RefreshGenerateReady();
        }

        private void cbxGangedAxis_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized)
                return;
            _gangedAxis = Math.Max(0, Math.Min(2, cbxGangedAxis.SelectedIndex));
            Persist();
            DetectOffsetSetting();
            UpdateComputed();
        }

        /// <summary>
        /// Orientation pick. Only the live corners are dropped - the stored reversal halves must survive,
        /// since switching orientation is precisely the act of going to measure the other one.
        /// </summary>
        private void Orientation_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized)
                return;
            reversed = rbOrientReversed.IsChecked == true;
            ClearCorners();     // the reading on screen belongs to the orientation being left
            Persist();
            DiscardProgram();   // the sitting program aims its seeks for the OTHER orientation
            RefreshGenerateReady();
            UpdateComputed();
        }

        private void ReferenceTlo_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized)
                return;
            Persist();
            DiscardProgram();   // the sitting program was built with/without the puck touch - stale either way
            RefreshProbeChoices();
        }

        private void Invert_Changed(object sender, RoutedEventArgs e)
        {
            invertCorrection = chkInvert.IsChecked == true;
            Persist();
            UpdateComputed();   // flips the sign of the pending correction immediately, no re-probe needed
        }

        #endregion

        #region Result capture

        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GrblViewModel.Message))
                return;

            string msg = model.Message;
            if (string.IsNullOrEmpty(msg))
                return;

            var m = rxCorner.Match(msg);
            if (!m.Success || !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return;

            bool isX = m.Groups[2].Value.ToUpperInvariant() == "X";
            switch (m.Groups[1].Value.ToUpperInvariant())
            {
                case "HEEL": if (isX) heelX = v; else heelY = v; break;
                case "BLADE": if (isX) bladeX = v; else bladeY = v; break;
                case "TONGUE": if (isX) tongueX = v; else tongueY = v; break;
            }

            // Filing the reading happens on the UI thread with the recompute, not here - CaptureReading
            // writes the square-error property and calls Persist(), and this runs on a comms thread.
            Dispatcher.BeginInvoke(new System.Action(() => { CaptureReading(); UpdateComputed(); }));
        }

        #endregion

        #region Generate / Run

        /// <summary>
        /// How far below the square's probed top the side faces are probed: half the thickness, so the
        /// contact point sits mid-material with equal margin above and below.
        /// </summary>
        /// <remarks>
        /// Floored at 0.8 so a mistyped thickness cannot ask for a contact right on the top arris, and
        /// capped at 5 - pcorner's own default, which is what anything thick enough should use anyway
        /// (deeper buys nothing and only risks a clamp). With a ball tip the contact lands on the ball's
        /// equator at this height, so the radius compensation the macro already applies stays exact.
        /// </remarks>
        private double FaceDepth()
        {
            return Math.Max(0.8d, Math.Min(5d, SquareThickness / 2d));
        }

        /// <summary>
        /// The deepest machine Z a seek may be aimed at: the REACHABLE floor (travel less the homing
        /// pull-off), backed off 1 mm so pcorner's own "#&lt;_bottom&gt; + 1" target lands inside the envelope
        /// rather than exactly on its edge. NaN when the envelope is unknown - the caller must REFUSE.
        /// </summary>
        /// <remarks>
        /// Same formula, for the same reason, as the stepper probe wizard's SeekFloorZ: ignoring the pull-off
        /// once aimed a top seek 4 mm outside a real envelope, and grblHAL refused it at PLANNING with
        /// Alarm:2 - the machine never moved, which looks exactly like a dead probe rather than a bad target.
        /// </remarks>
        private static double SeekFloorZ()
        {
            double floor = GrblInfo.ReachableLimit(2, false);
            return double.IsNaN(floor) ? double.NaN : floor + 1.0d;
        }

        /// <summary>
        /// Whether this run will reference the fitted probe at the puck: the operator asked for it AND a
        /// Tool Setter probe is actually defined.
        /// </summary>
        /// <remarks>
        /// Gated on a toolsetter being DEFINED, not on its feeds - the puck probe's own feeds live in
        /// tlo.macro now, where the puck is. Same gate, same wording, as the scratch wizard's.
        /// </remarks>
        private bool WillReferenceTlo
        {
            get
            {
                return chkReferenceTlo != null && chkReferenceTlo.IsChecked == true
                    && ProbeDefinitions.Items.Any(d => d.ProbeType == ProbeType.ToolSetter);
            }
        }

        private void Generate()
        {
            if (model == null)
                return;

            var fx = SelectedFixture;
            if (fx == null)
            {
                txtWarnings.Text = "Select a validated Corner Fence fixture first.";
                return;
            }
            // The explicit flag, not "either offset is 0" - 0 is a legitimate measurement (see
            // Fixture.CornerOffsetX). Same guard StartJobView.Generate_Click uses.
            if (!fx.CornerLocated)
            {
                txtWarnings.Text = "This fixture's corner position hasn't been located yet - run Test position again in Machine Setup > Fixture definitions.";
                return;
            }

            var p = ActiveProbe();
            if (p == null)
            {
                txtWarnings.Text = "Define a " + (IsTouchPlate ? "touch plate" : "3D probe") + " first (Machine Setup > Probe definitions).";
                return;
            }

            if (BladeLength <= 0d || TongueLength <= 0d)
            {
                txtWarnings.Text = "Enter the square's blade and tongue lengths first.";
                return;
            }
            if (SquareThickness <= 0d)
            {
                txtWarnings.Text = "Enter the square's thickness - it sets how far below the top the side faces are probed, and there is no safe default for it.";
                return;
            }
            if (double.IsNaN(SeekFloorZ()))
            {
                txtWarnings.Text = "The machine's Z travel or homing pull-off is not known yet, so a safe probe depth cannot be worked out. Connect and let the settings load, then try again.";
                return;
            }

            txtWarnings.Text = string.Empty;
            // Clear the live corners only. The stored reversal halves must SURVIVE a Generate - running
            // the second orientation is exactly how the pair gets completed.
            ClearCorners();
            UpdateComputed();

            // The WCS to come back to is whichever one is ACTIVE - tlo.macro ends standing on the puck in
            // G59.3, and a caller that continues from there in the wrong frame drives into the toolsetter.
            // Falls back to G54 only when the model cannot say, which is the frame this program assumes
            // anyway. Same reasoning, same fallback, as the scratch wizard's.
            string returnWcs = model != null && !string.IsNullOrEmpty(model.WorkCoordinateSystem)
                             ? model.WorkCoordinateSystem : "G54";

            program = BuildProgram(fx, p, BladeLength, TongueLength, FaceDepth(), CornerTravelMarginMm, IsTouchPlate,
                                   WillReferenceTlo, model != null && model.IsTloReferenceSet,
                                   AppConfig.Settings.Base.TloRefBaseline, returnWcs, reversed);
            MacroProcessor.HandOffToJobTab(model, ProgramNameSquare, program, ViewType.Calibration, onHandoffEnd: EndHandoff);
            // OwnsRunBar, not isActiveTab: the handoff's tab switch has already run Activate(false)
            // synchronously by now, so isActiveTab is false and this write would simply be skipped.
            if (OwnsRunBar)
                MacroProcessor.IsProgramGenerated = true;
        }

        private void Run()
        {
            if (model == null)
                return;
            if (string.IsNullOrWhiteSpace(program))
                Generate();
            if (string.IsNullOrWhiteSpace(program))
                return;

            MacroProcessor.Run(model, ProgramNameSquare, program, true);
        }

        /// <summary>
        /// Probe corner 1 (the heel), corner 2 (the blade's far end) and corner 3 (the tongue's far end),
        /// and print all six coordinates. Every corner is a real outside corner with both an X and a Y face,
        /// so pcorner.macro handles all three unchanged - the only thing this program does that no previous
        /// caller did is set #&lt;_ls_facedepth&gt; for the thin material.
        /// </summary>
        private static string BuildProgram(Fixture fx, ProbeDefinition p, double bladeMm, double tongueMm,
                                           double faceDepthMm, double cornerTravelMarginMm, bool touchPlate,
                                           bool referenceTlo, bool tloAlreadyReferenced, double tloBaseline,
                                           string returnWcs, bool reversedOrientation)
        {
            const double insetMm = 5d;
            double r = p.ProbeDiameter / 2d;
            var fxPos = new Position(fx.Coords);
            string refX = fxPos.X.ToInvariantString("0.0##"), refY = fxPos.Y.ToInvariantString("0.0##");
            string searchF = Math.Max(p.ProbeFeedRate, 200d).ToInvariantString("0.0##");
            string latchF = p.LatchFeedRate.ToInvariantString("0.0##");
            string floorZ = SeekFloorZ().ToInvariantString("0.0##");

            var b = new StringBuilder();
            MacroProcessor.EmitProgramHeader(l => b.AppendLine(l), "connected, homed, EXPR, noalarm",
                                             string.Format("(Squareness {0} - probe a reference square's two arms and report the angle between them)",
                                                           reversedOrientation ? "REVERSED - square flipped, heel at the front-right" : "normal - heel in the fence"));
            // NOT cancelToolOffset: true. That was copied in from the stepper probe wizard, and
            // EmitModalDefaults' own parameter documentation warns against it in as many words - on this
            // machine the tool length offset is what makes one work Z0 mean the same thing for every tool.
            // This program has no use for G49 anyway: it works entirely in machine coordinates, and
            // pcorner.macro cancels the offset internally for its own G53 moves regardless. All emitting it
            // here achieved was to discard the operator's offset a few lines earlier than the macro would.
            // See the restore before the footer, which is what actually puts it back.
            MacroProcessor.EmitModalDefaults(l => b.AppendLine(l));
            // Through EmitWcsWrite, never bare: this exact line, against a G54 carrying a 0.10 deg
            // rotation, corrupted the parser position and turned the G30 park's "G53 G0 Z0" lift into a
            // 662 mm rapid across the table on 2026-09-16. See EmitWcsWrite for the mechanism.
            MacroProcessor.EmitWcsWrite(l => b.AppendLine(l), "G10 L2 P1 X0 Y0 Z0");
            if (GrblInfo.HasToolSetter)
                b.AppendLine(string.Format(GrblCommand.ProbeSelect, p.ProbeType == ProbeType.ToolSetter ? 1 : 0));

            b.AppendLine(string.Format("#<_ls_rad> = {0}", r.ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_spacer> = 0");
            // Only sizes the pre-probe APPROACH height, and only on the branch where the caller has no
            // trusted safe height - which is not this one (see #<_ls_maxz> below). Stay conservative rather
            // than passing the square's real ~5 mm: over-stating it can only make an approach HIGHER.
            b.AppendLine("#<_ls_thickness> = 25.4");
            // The whole reason this tool needed a macro change. A framing square is ~4.8 mm of steel and the
            // face probes searched at a fixed 5 mm below the probed top - 0.2 mm UNDER the square, seeking
            // through open air. Half the thickness puts the contact mid-material. See pcorner.macro's header.
            b.AppendLine(string.Format("#<_ls_facedepth> = {0}", faceDepthMm.ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_ls_mode> = {0}", touchPlate ? 1 : 0));
            b.AppendLine(string.Format("#<_ls_plateoffset> = {0}", (touchPlate ? p.PlateThickness : 0d).ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_ls_lipoffset> = {0}", (touchPlate ? p.LipWidth : 0d).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_edgemargin> = 10");   // floored to 20 by the macro - see its own comment
            b.AppendLine(string.Format("#<_ls_searchf> = {0}", searchF));
            b.AppendLine(string.Format("#<_ls_latchf> = {0}", latchF));
            b.AppendLine(string.Format("#<_ls_zfloor> = {0}", floorZ));

            b.AppendLine("(park at G30 - install / confirm the probe)");
            MacroProcessor.EmitGotoG30(l => b.AppendLine(l));
            b.AppendLine("(WAITIDLE)");
            // Says which way round the square goes, in the prompt the operator is actually looking at when
            // they set it up. Getting this wrong does not fail loudly - it silently measures the same
            // orientation twice, and two identical halves average to "the machine is perfect".
            string clamping = reversedOrientation
                ? "REVERSED RUN: the square must be FLIPPED OVER, with the far end of the BLADE registered in the fence and the heel out to the RIGHT at blade length, tongue running back from the heel."
                : "NORMAL RUN: the square sits face-up with its HEEL registered in the fence, blade along X and tongue along Y.";

            b.AppendLine(touchPlate
                ? string.Format("(MBOX, OKCANCEL, Using touch plate: {0}. Fit the {1} bit or dowel it is set up for, clip the lead to the SQUARE - steel, so it conducts directly - and place the plate on the corner in the fence. {2} Click OK. Cancel aborts.)", p.Name, p.TipDescription, clamping)
                : string.Format("(MBOX, OKCANCEL, Install probe: {0}, which uses a {1} gauge pin or dowel. {2} Check it is clamped flat and its spacer is INSET from the edges - the probe must reach the steel and touch nothing else. Click OK. Cancel aborts.)", p.Name, p.TipDescription, clamping));

            // Give the tool that was just fitted its OWN tool length offset, by touching the puck. Fitting a
            // probe IS a tool change, and a bit fitted by hand has no offset - tc.macro applies one on every
            // M6, and no M6 runs here. Without this the run works against whatever offset the PREVIOUS tool
            // left behind and hands the machine back the same way, which is the shape of the failure that
            // cut a spoilboard on 2026-08-06: "the offset was never stale, just discarded".
            //
            // Setup's sequence, not a copy of it - the baseline/reference/restore trio was lifted into
            // MacroProcessor for exactly this, and they are a SET used in order.
            //
            // Placed after the install prompt, because it must reference the probe that is NOW fitted, not
            // whatever was in the spindle when the program was generated. No lift afterwards: tlo.macro
            // guarantees it ends parked at G30 in returnWcs, which is where corner 1 expects to start from.
            if (referenceTlo)
            {
                MacroProcessor.EmitTloBaseline(l => b.AppendLine(l), tloAlreadyReferenced, tloBaseline);
                // 8 = the 3D probe stylus, which probes the MAIN input because it must not bear down on the
                // puck. A touch-plate run has a rigid gauge pin or dowel in the spindle instead, so any id
                // but 8 - tlo.macro reads that as "pushes the puck's own switch, use the toolsetter input".
                // Do not pre-decide which input: the macro branches, and it cannot be done in a streamed
                // program because o-word flow control cannot be streamed to grblHAL.
                MacroProcessor.EmitTloReference(l => b.AppendLine(l), touchPlate ? 1 : 8, returnWcs);
            }
            else
                b.AppendLine("(NOTE: no TLO reference taken, so the fitted probe has no tool length offset of its own and this run ends in G49. Re-reference the tool before the next job.)");

            // Which physical part of the square each probe lands on. The first two probes are IDENTICAL in
            // both orientations - same pcorner ids, same references - because reversing the square swaps
            // which END of the blade sits in the fence, not where the two points are. Only their ROLES swap,
            // and only the third probe genuinely differs.
            //
            //   normal   : fence = heel,           second = blade end,  third = tongue end, id 3 (back-LEFT)
            //   reversed : fence = blade far end,  second = heel,       third = tongue end, id 4 (back-RIGHT)
            //
            // In the reversed orientation the blade runs from the fence rightwards to the heel and the
            // tongue runs back from the heel, so the tongue's far end is the back-RIGHT corner and its
            // reference sits outside in +X as well as +Y. See the file header for why the heel has to move
            // at all: a mirrored L cannot be laid with both arms in the +X/+Y quadrant, and no rotation
            // undoes a mirror.
            string roleAtFence = reversedOrientation ? "BLADE" : "HEEL";
            string roleSecond = reversedOrientation ? "HEEL" : "BLADE";

            // Corner 1 - the corner registered in the fence. #<_bottom> is a SEEK-DEPTH CAP and nothing
            // more; it is the machine's own Z floor, not a cached spoilboard reading.
            b.AppendLine(string.Format("#<_bottom> = {0}", floorZ));
            b.AppendLine("#<_ls_corner> = 1");
            b.AppendLine(string.Format("#<_ls_refx> = {0}", refX));
            b.AppendLine(string.Format("#<_ls_refy> = {0}", refY));
            b.AppendLine(string.Format("#<_ls_topx> = {0}", (fx.CornerOffsetX + insetMm).ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_ls_topy> = {0}", (fx.CornerOffsetY + insetMm).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_startz> = 0");
            // NOT "0": that is pcorner's sentinel for "the caller has no trusted safe height", and its
            // fallback rapids to #<_bottom> + thickness + plateoffset + 10 - a formula that treats #<_bottom>
            // as the surface the stock sits on, when what we pass is the Z travel limit. Spelling it 0 here
            // drove a tool into a touch plate at 4570 mm/min on 2026-09-14. Machine top IS trustworthy at
            // this point: the operator has just confirmed the probe at G30 and nothing is above Z0.
            b.AppendLine("#<_ls_maxz> = -0.01");
            b.AppendLine("#<_ls_appz> = 9999");
            b.AppendLine("O<pcorner> CALL [#<_ls_rad>]");
            b.AppendLine("#<c1x> = #<_corner_x>");
            b.AppendLine("#<c1y> = #<_corner_y>");
            b.AppendLine("#<c1z> = #<_corner_z>");
            b.AppendLine("#<c1_maxz> = " + GrblInfo.ClampToZTop(
                  string.Format("[#<c1z> + {0}]", cornerTravelMarginMm.ToInvariantString("0.0##"))));
            b.AppendLine(string.Format("(PRINT, SQ_{0}X=#<c1x>)", roleAtFence));
            b.AppendLine(string.Format("(PRINT, SQ_{0}Y=#<c1y>)", roleAtFence));
            // Abort the whole run on an alarmed probe rather than letting a stale/undefined value fall
            // through into the skew - same reason StartJobView waits after every corner of its Measure.
            b.AppendLine("(WAITIDLE)");

            if (touchPlate)
                // Names the corner THIS orientation probes second, not the one the normal run does. In the
                // reversed run the second probe is the heel, so the original wording would have sent the
                // plate to the wrong end of the blade - and a touch plate in the wrong place does not fail,
                // it returns a confident coordinate for the wrong corner.
                b.AppendLine(reversedOrientation
                    ? "(MBOX, OK, Move the touch plate to the HEEL - the outside corner where the two arms meet, at the RIGHT-hand end of the blade - then click OK.)"
                    : "(MBOX, OK, Move the touch plate to the far end of the BLADE - the long arm, along X - then click OK.)");

            // Corner 2 - the blade's far end (X-neighbour). Its Y face is the blade's front edge, which is
            // the reference the skew is measured from; its X face is the blade's end, which only sets the
            // lever length and needs no accuracy at all.
            b.AppendLine(string.Format("(--- corner 2 = {0} (X-neighbour) ---)", reversedOrientation ? "the heel" : "blade far end"));
            b.AppendLine("#<_ls_topx> = 15");
            b.AppendLine("#<_ls_topy> = 15");
            b.AppendLine("#<_ls_corner> = 2");
            b.AppendLine(string.Format("#<_ls_refx> = [#<c1x> + {0}]", (bladeMm + 10d).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_refy> = [#<c1y> - 10]");
            b.AppendLine("#<_ls_startz> = 0");
            b.AppendLine("#<_ls_maxz> = #<c1_maxz>");
            b.AppendLine("#<_ls_appz> = 9999");
            b.AppendLine("O<pcorner> CALL [#<_ls_rad>]");
            b.AppendLine("#<c2x> = #<_corner_x>");
            b.AppendLine("#<c2y> = #<_corner_y>");
            b.AppendLine(string.Format("(PRINT, SQ_{0}X=#<c2x>)", roleSecond));
            b.AppendLine(string.Format("(PRINT, SQ_{0}Y=#<c2y>)", roleSecond));
            b.AppendLine("(WAITIDLE)");

            if (touchPlate)
                // The tongue's far end in both orientations - but at the BACK-RIGHT when reversed and the
                // BACK-LEFT when not, so the corner is named rather than left to be inferred.
                b.AppendLine(reversedOrientation
                    ? "(MBOX, OK, Move the touch plate to the far end of the TONGUE - the short arm running BACK from the heel, so the BACK-RIGHT corner - then click OK.)"
                    : "(MBOX, OK, Move the touch plate to the far end of the TONGUE - the short arm, along Y, so the BACK-LEFT corner - then click OK.)");

            // Corner 3 - the tongue's far end, always. Which pcorner corner that IS depends on the
            // orientation: with the heel at the front-left the tongue runs back from it and its end is the
            // back-LEFT corner (id 3, reference outside in -X/+Y); with the heel at the front-right the
            // tongue runs back from THERE and its end is the back-RIGHT corner (id 4, outside in +X/+Y).
            b.AppendLine("(--- corner 3 = tongue far end ---)");
            b.AppendLine(string.Format("#<_ls_corner> = {0}", reversedOrientation ? 4 : 3));
            b.AppendLine(reversedOrientation
                ? string.Format("#<_ls_refx> = [#<c1x> + {0}]", (bladeMm + 10d).ToInvariantString("0.0##"))
                : "#<_ls_refx> = [#<c1x> - 10]");
            b.AppendLine(string.Format("#<_ls_refy> = [#<c1y> + {0}]", (tongueMm + 10d).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_startz> = 0");
            b.AppendLine("#<_ls_maxz> = #<c1_maxz>");
            b.AppendLine("#<_ls_appz> = 9999");
            b.AppendLine("O<pcorner> CALL [#<_ls_rad>]");
            b.AppendLine("#<c3x> = #<_corner_x>");
            b.AppendLine("#<c3y> = #<_corner_y>");
            b.AppendLine("(PRINT, SQ_TONGUEX=#<c3x>)");
            b.AppendLine("(PRINT, SQ_TONGUEY=#<c3y>)");
            b.AppendLine("(WAITIDLE)");

            // Put back the offset the reference above measured. pcorner.macro cancels it on every call -
            // deliberately, its absolute G53 moves need true machine coordinates - and nothing else puts it
            // back, so without this the run ends in G49 and work Z0 then sits a whole tool length too deep.
            // G43.1 sets the offset absolutely, so re-emitting it costs nothing if it somehow survived.
            // Guarded on referenceTlo because the trio is a SET: with no reference taken, #<_probe_z> and
            // #<_tlo_ref> hold whatever a previous run left, and restoring from those would apply a
            // confident-looking offset measured against a tool that is not in the spindle.
            //
            // BEFORE the footer, not after, and that ordering is load-bearing in the other direction:
            // EmitProgramFooter's own comment records that a program whose final lines do not MOVE reaches
            // Idle before the controller's "[MSG:Pgm End]" arrives, leaving the Run bar stuck on "Run".
            // So the park stays last. Parking at G30 with a live offset is what every ordinary job already
            // does, so nothing novel is being asked of the G53 moves in it.
            if (referenceTlo)
                MacroProcessor.EmitTloRestore(l => b.AppendLine(l));

            b.AppendLine("(--- park at G30 - no origin/WCS is set by this tool, it only measures ---)");
            MacroProcessor.EmitProgramFooter(l => b.AppendLine(l), stopSpindle: false, parkAtG30: true, endWord: "M2");

            return b.ToString();
        }

        #endregion

        #region Apply / Clear / Re-home

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            // FIRST, before reading any field. These buttons are Focusable="False" so they cannot steal
            // the jog keys, and a non-focusable button takes no focus - so a value being typed when the
            // operator clicks has never raised LostFocus, and a length-unit field commits only then.
            // Without this the handler reads the PREVIOUS value and acts on it silently: on 2026-09-16
            // a typed 0.37 was on screen while 0.450 went to the controller. See
            // NumericField.CommitPendingEdits.
            NumericField.CommitPendingEdits(this);

            switch ((string)((Button)sender).Tag)
            {
                case "apply": ApplyOffset(); break;
                case "clear": ClearOffset(); break;
                case "home": ReHome(); break;
            }
        }

        private void ApplyOffset()
        {
            if (_offset == null)
            {
                AppDialogs.Show("This firmware has no auto-square offset setting to write.",
                                "Squareness", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            // Reached only after Button_Click has committed any half-typed field, so this is the real
            // value. Said out loud rather than returning silently: the button is always live now, so a
            // press that does nothing would otherwise look like the failure it used to be.
            if (Math.Abs(NewOffset - CurrentOffset) <= 1e-6)
            {
                AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "New offset is already {0:0.000###} - the same as the current one, so there is nothing to write.", NewOffset),
                    "Squareness", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            double newVal = NewOffset;   // already clamped to the setting range in UpdateComputed
            string caution = Math.Abs(newVal) > LargeOffsetWarn
                ? string.Format(CultureInfo.InvariantCulture, "\n\nCaution: {0:0.0} mm is a lot of racking for a rigid gantry. If it binds, the rails are mechanically out of phase - fix that rather than trimming further.", newVal)
                : string.Empty;

            if (AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "Change {0} (${1}) from {2} to {3}?\n\nThen re-home, and RUN THE MEASUREMENT AGAIN - if the skew grows instead of shrinking, tick 'Invert correction direction' and apply again.{4}",
                    _offset.Name, _offset.Id, F(CurrentOffset), F(newVal), caution),
                    "Squareness", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            _offset.Value = newVal.ToString(CultureInfo.InvariantCulture);
            if (GrblSettings.Save())
            {
                DetectOffsetSetting();   // re-read: CurrentOffset -> newVal
                // The corners on screen were measured against the OLD offset. Once it is written they
                // describe a gantry that no longer exists, and leaving them would let UpdateComputed offer
                // the same correction a second time on top of itself.
                ClearCorners();
                // Both reversal halves were measured against the OLD offset, so they no longer describe
                // this gantry and cannot be split against it. The square's own error survives - that is a
                // property of the steel, not of the machine - so the next single reading still yields a
                // true machine error by subtraction.
                readNormal = readReversed = null;
                Persist();
                DiscardProgram();
                UpdateComputed();
                ReHome();
            }
            else
                AppDialogs.Show(WriteFailedMessage(), "Squareness", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>
        /// What to say when <see cref="GrblSettings.Save"/> reports failure - which is NOT the same as
        /// "the controller refused it".
        /// </summary>
        /// <remarks>
        /// This used to assert a cause: "Only the actually-ganged axis accepts a write - check the Y axis is
        /// the squared one." On 2026-09-16 it said exactly that while the wire log showed $171=0.319 acked
        /// in 9 ms and the controller holding the new value. Save had timed out waiting for an ok it could
        /// not see past the status poller (fixed in GrblSettings.Save), and the dialog turned a timeout into
        /// a confident, wrong diagnosis of the operator's machine configuration.
        ///
        /// So it names the possibilities instead of picking one, and points at the only thing that settles
        /// it - the value the controller actually holds. A wrong-axis write IS one real cause; it is just
        /// not a fact this code has established.
        /// </remarks>
        private string WriteFailedMessage()
        {
            return string.Format(
                "${0} was not confirmed.\n\nThe controller did not acknowledge the write, so the value may or may not have been stored - check ${0} in Settings before relying on it, and re-home if it did land.\n\nIf it was genuinely refused, the usual cause is that {1} is not the ganged axis - only that one accepts a write, even though $170-$172 all report.",
                _offset.Id, "XYZ"[_gangedAxis]);
        }

        private void ClearOffset()
        {
            if (_offset == null)
            {
                AppDialogs.Show("This firmware has no auto-square offset setting to clear.",
                                "Squareness", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (Math.Abs(CurrentOffset) <= 1e-6)
                return;   // already zero
            if (AppDialogs.Show(string.Format("Reset {0} (${1}) to 0 and re-home?", _offset.Name, _offset.Id),
                    "Squareness", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            _offset.Value = "0";
            if (GrblSettings.Save())
            {
                DetectOffsetSetting();
                ClearCorners();
                readNormal = readReversed = null;   // as in ApplyOffset - the gantry has changed, the square has not
                Persist();
                DiscardProgram();
                UpdateComputed();
                ReHome();
            }
            else
                AppDialogs.Show(WriteFailedMessage(), "Squareness", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ReHome()
        {
            if (model == null)
                return;
            if (AppDialogs.Show("Re-home now so the squaring offset takes effect?",
                    "Squareness", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                model.ExecuteCommand("$H");
        }

        #endregion

        // Persisted as the "AutoSquareProbe" section of App.config.
        public static AutoSquareProbeParams SectionConfig;

        #region ConfigPanel<AutoSquareProbeParams> overrides

        protected override AutoSquareProbeParams Config { get { return SectionConfig; } set { SectionConfig = value; } }

        // Without this, editing any input leaves the previously generated program held and the Run bar still
        // reading "Run" - so pressing it probes to the OLD seek references with the OLD face depth. The pin
        // tab shipped with exactly that bug for a while.
        protected override void OnPersistedPropertyChanged()
        {
            Persist();
            DiscardProgram();
            RefreshGenerateReady();
        }

        protected override DependencyProperty[] PersistedProperties => new[] {
            BladeLengthProperty, TongueLengthProperty, SquareThicknessProperty, CornerTravelMarginMmProperty };

        protected override void ApplyConfig(AutoSquareProbeParams p)
        {
            BladeLength = p.BladeLength;
            TongueLength = p.TongueLength;
            SquareThickness = p.SquareThickness;
            CornerTravelMarginMm = p.CornerTravelMarginMm;
            restoreFixtureName = p.FixtureName;
            IsTouchPlate = p.Probe == "TouchPlate";
            _gangedAxis = Math.Max(0, Math.Min(2, p.GangedAxis));
            invertCorrection = p.InvertCorrection;
            chkInvert.IsChecked = invertCorrection;
            reversed = p.Reversed;
            rbOrientReversed.IsChecked = reversed;
            rbOrientNormal.IsChecked = !reversed;
            // A property of the steel, not of the session.
            squareErrorKnown = p.HasSquareError;
            SquareError = p.SquareErrorDeg;
            // The halves outlive the app too. Losing one to a restart used to mean re-clamping and
            // re-running a half that had already been measured perfectly well - which happened on
            // 2026-09-16 and had to be split by hand outside the app.
            readNormal = p.HasNormal
                ? (Reading?)new Reading { Skew = p.NormalSkew, BladeSpan = p.NormalBlade, TongueSpan = p.NormalTongue, Offset = p.NormalOffset }
                : null;
            readReversed = p.HasReversed
                ? (Reading?)new Reading { Skew = p.ReversedSkew, BladeSpan = p.ReversedBlade, TongueSpan = p.ReversedTongue, Offset = p.ReversedOffset }
                : null;
            chkReferenceTlo.IsChecked = p.ReferenceTlo;
        }

        protected override AutoSquareProbeParams CaptureConfig()
        {
            return new AutoSquareProbeParams
            {
                BladeLength = BladeLength,
                TongueLength = TongueLength,
                SquareThickness = SquareThickness,
                CornerTravelMarginMm = CornerTravelMarginMm,
                FixtureName = SelectedFixture?.Name ?? restoreFixtureName,
                Probe = IsTouchPlate ? "TouchPlate" : "ThreeDProbe",
                GangedAxis = _gangedAxis,
                InvertCorrection = invertCorrection,
                Reversed = reversed,
                HasSquareError = squareErrorKnown,
                SquareErrorDeg = SquareError,
                HasNormal = readNormal.HasValue,
                NormalSkew = readNormal.HasValue ? readNormal.Value.Skew : 0d,
                NormalOffset = readNormal.HasValue ? readNormal.Value.Offset : 0d,
                NormalBlade = readNormal.HasValue ? readNormal.Value.BladeSpan : 0d,
                NormalTongue = readNormal.HasValue ? readNormal.Value.TongueSpan : 0d,
                HasReversed = readReversed.HasValue,
                ReversedSkew = readReversed.HasValue ? readReversed.Value.Skew : 0d,
                ReversedOffset = readReversed.HasValue ? readReversed.Value.Offset : 0d,
                ReversedBlade = readReversed.HasValue ? readReversed.Value.BladeSpan : 0d,
                ReversedTongue = readReversed.HasValue ? readReversed.Value.TongueSpan : 0d,
                ReferenceTlo = chkReferenceTlo != null && chkReferenceTlo.IsChecked == true
            };
        }

        protected override void OnConfigReady()
        {
            if (model == null)
                model = DataContext as GrblViewModel;

            if (cbxGangedAxis != null)
                cbxGangedAxis.SelectedIndex = Math.Max(0, Math.Min(2, _gangedAxis));

            DetectOffsetSetting();
            UpdateComputed();
        }

        #endregion
    }

    // Persisted squareness-by-probe parameters. Public for XmlSerializer.
    public class AutoSquareProbeParams
    {
        public double BladeLength = 609.6d;      // actual 24" blade
        public double TongueLength = 406.4d;     // actual 16" tongue
        public double SquareThickness = 4.8d;    // measured, not nominal - a Milwaukee Red framing square
        public double CornerTravelMarginMm = 15d;
        public string FixtureName = string.Empty;
        // "ThreeDProbe" or "TouchPlate" - the same two spellings StartJobSettings.Probe uses, so every tool
        // describes the same choice the same way.
        public string Probe = "ThreeDProbe";
        public int GangedAxis = 1;               // 0=X, 1=Y, 2=Z
        // Resolved once by the operator against their own machine - see AutoSquareProbeWizard.invertCorrection.
        public bool InvertCorrection = false;
        // Which way the square was last clamped.
        public bool Reversed = false;
        // The reference square's own out-of-squareness, once a reversal pair has measured it. Kept
        // because it describes a physical object the operator owns: with it known, a single reading
        // gives the MACHINE's error by subtraction and the reversal never has to be repeated.
        public bool HasSquareError = false;
        public double SquareErrorDeg = 0d;
        // The two reversal halves, kept across restarts so a measured half is never thrown away by
        // an app relaunch. Each carries the offset it was taken at - the pair is only splittable
        // when those match, and storing it is what makes a mismatch detectable rather than silent.
        public bool HasNormal = false;
        public double NormalSkew = 0d, NormalOffset = 0d, NormalBlade = 0d, NormalTongue = 0d;
        public bool HasReversed = false;
        public double ReversedSkew = 0d, ReversedOffset = 0d, ReversedBlade = 0d, ReversedTongue = 0d;
        // Default ON: fitting the probe is a tool change, and the run is wrong-referenced without it.
        public bool ReferenceTlo = true;
    }
}
