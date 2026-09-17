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
 * NOT YET BUILT - the reversal test. Measure, flip the square over, measure again: the square's own error
 * changes sign, the machine's does not, so the mean is the machine and half the difference is the square.
 * It is the only way to tell which of the two you are looking at, and it is cheap once probing is the
 * instrument. Deliberately deferred until a single measurement is hardware-verified.
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

        // The three probed corners, machine coordinates. Null until a run has reported them.
        private double? c1x, c1y, c2x, c2y, c3x, c3y;

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

        // (PRINT, SQ_C<n><axis>=..) - the same "(PRINT, TAG=value)" idiom every other generator here uses.
        private static readonly Regex rxCorner = new Regex(@"SQ_C([123])([XY])\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

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
            "4. Generate, then Run. It parks at G30 to confirm the probe, then probes the heel and the far end of each arm.\n\n" +
            "5. Read the measured skew. Apply offset, then Re-home for it to take effect.\n\n" +
            "6. RUN IT AGAIN. This is not optional and it is not a formality - it is how the correction's direction gets established. The skew should collapse toward zero. If it roughly DOUBLED instead, the sign is backwards for your machine: tick 'Invert correction direction', Apply, re-home and re-measure. Leave it ticked from then on.\n\n" +
            "What the number means: the skew is the angle between the square's two arms as the machine sees them, minus 90 degrees. It is the machine's error and the square's error added together, and nothing here can separate them - so a result at or below about 0.01 degrees is the square's accuracy talking, not the gantry's.\n\n" +
            "If the offset needed is more than a couple of mm, the gantry is mechanically out of square (the two rails out of phase) and racking it that hard can bind a rigid frame. Fix that first; the squaring offset is fine-trim.";

        public AutoSquareProbeWizard()
        {
            InitializeComponent();
            model = DataContext as GrblViewModel;
            txtHowTo.Text = HowToText;
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
            get { return c1x.HasValue && c1y.HasValue && c2x.HasValue && c2y.HasValue && c3x.HasValue && c3y.HasValue; }
        }

        /// <summary>
        /// Deviation from 90 degrees between the blade (corner 1 -> 2) and the tongue (corner 1 -> 3), as the
        /// MACHINE sees them. 0 = the machine's right angle and the square's agree.
        /// </summary>
        /// <remarks>
        /// Identical formula to StartJobView.SkewDegrees, which measures the same thing against a rectangle
        /// of stock and needs only corners 1/2/3 for it - the fourth corner it happens to have is used for
        /// the diagonal check, not this. Kept as its own copy because this tool lives in CNC.Controls and
        /// that one in the app assembly.
        /// </remarks>
        private double? SkewDegrees()
        {
            if (!HasAllCorners)
                return null;

            double ax = c2x.Value - c1x.Value, ay = c2y.Value - c1y.Value;   // along the blade
            double bx = c3x.Value - c1x.Value, by = c3y.Value - c1y.Value;   // along the tongue
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6)
                return null;

            double cos = Math.Max(-1d, Math.Min(1d, (ax * bx + ay * by) / (la * lb)));
            return Math.Acos(cos) * 180d / Math.PI - 90d;
        }

        private double? BladeSpan()
        {
            if (!HasAllCorners) return null;
            double ax = c2x.Value - c1x.Value, ay = c2y.Value - c1y.Value;
            return Math.Sqrt(ax * ax + ay * ay);
        }

        private double? TongueSpan()
        {
            if (!HasAllCorners) return null;
            double bx = c3x.Value - c1x.Value, by = c3y.Value - c1y.Value;
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
            double? skew = SkewDegrees();
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
            bool measureOnly = _offset == null;
            double railSpan = RailSpan();

            txtResult.Text = skew.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "Measured skew:  {0:0.0###}°   (blade {1:0.0##} mm, tongue {2:0.0##} mm)",
                                skew.Value, BladeSpan() ?? 0d, TongueSpan() ?? 0d)
                : "Measured skew:  -   (Generate and Run to probe the square)";

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

            if (btnApply != null)
                btnApply.IsEnabled = !measureOnly && skew.HasValue && railSpan > 0d && Math.Abs(NewOffset - CurrentOffset) > 1e-6;

            if (txtSummary != null)
            {
                if (!skew.HasValue)
                    txtSummary.Text = string.Empty;
                else if (measureOnly)
                    txtSummary.Text = string.Format(CultureInfo.InvariantCulture,
                        "skew {0:0.0###}° → the gantry is {1:0.000} mm out of square across the {2:0} mm rail span. Measure-only (no offset setting) - correct it mechanically.",
                        skew.Value, Math.Abs(CorrectionDelta()), railSpan);
                else
                    txtSummary.Text = string.Format(CultureInfo.InvariantCulture,
                        "skew {0:0.0###}° over a {1:0} mm rail span → correction {2:+0.000;-0.000;0} mm, new offset {3:0.000} (from {4:0.000}){5}",
                        skew.Value, railSpan, CorrectionDelta(), NewOffset, CurrentOffset,
                        invertCorrection ? "  ·  inverted" : string.Empty);
            }
        }

        private static bool SpanAdrift(double? measured, double entered)
        {
            return measured.HasValue && entered > 0d && Math.Abs(measured.Value - entered) > 20d;
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

        private static ProbeDefinition TouchPlateProbe()
        {
            return ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.TouchPlate);
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
            switch (m.Groups[1].Value)
            {
                case "1": if (isX) c1x = v; else c1y = v; break;
                case "2": if (isX) c2x = v; else c2y = v; break;
                case "3": if (isX) c3x = v; else c3y = v; break;
            }

            Dispatcher.BeginInvoke(new System.Action(UpdateComputed));
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
        /// The tool length offset currently in force, from the controller's own <c>$#</c> report
        /// (<c>[TLO:...]</c>), so the program can put back what probing takes away. NaN = not knowable.
        /// </summary>
        /// <remarks>
        /// pcorner.macro cancels the tool length offset on every call - deliberately, its absolute G53 moves
        /// need true machine coordinates - and nothing puts it back, so a probing run that does not restore
        /// it hands the machine over in G49. That is not cosmetic: on this machine a TLO is what makes one
        /// work Z0 mean the same thing for every tool, so afterwards Z0 sits a whole tool length too deep.
        /// The failure is silent and waits for the next job that does NOT emit an M6 - "same bit, same
        /// spindle, nothing touched" - which is precisely when nothing re-applies an offset. It cut a
        /// spoilboard on 2026-08-06, and Start Job grew EmitTloRestore in response.
        ///
        /// This tool cannot use EmitTloRestore: that recomputes the offset from #&lt;_probe_z&gt;/#&lt;_tlo_ref&gt;,
        /// which only exist once a run has actually referenced a toolsetter, and this one deliberately does
        /// not (the measurement is XY - a tool length cannot tilt an angle). So it restores the offset it
        /// INHERITED instead, read live rather than reconstructed.
        ///
        /// Read at Generate time, which is the limitation: a tool change between Generate and Run would make
        /// it stale. Any edit discards the program (OnPersistedPropertyChanged), and the realistic window is
        /// the operator answering one MBOX, so this is narrow rather than absent - worth knowing, not worth
        /// inventing a mechanism for. NaN when the report has not been read: the caller then emits NO restore
        /// and says so in the program, because a guessed tool length is far worse than an admitted G49.
        /// </remarks>
        private static double LiveToolLengthOffset()
        {
            if (!GrblWorkParameters.IsLoaded)
                return double.NaN;
            return GrblWorkParameters.ToolLengtOffset.Z;   // sic - the property is spelled this way in Grbl.cs
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
            c1x = c1y = c2x = c2y = c3x = c3y = null;   // a new run measures afresh; never mix two runs' corners
            UpdateComputed();

            program = BuildProgram(fx, p, BladeLength, TongueLength, FaceDepth(), CornerTravelMarginMm, IsTouchPlate, LiveToolLengthOffset());
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
                                           double inheritedTloZ)
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
                                             "(Squareness - probe a reference square's two arms and report the angle between them)");
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
            b.AppendLine(touchPlate
                ? string.Format("(MBOX, OKCANCEL, Using touch plate: {0}. Fit the {1} bit or dowel it is set up for, clip the lead to the SQUARE - steel, so it conducts directly - and place the plate on the heel corner. Click OK. Cancel aborts.)", p.Name, p.TipDescription)
                : string.Format("(MBOX, OKCANCEL, Install probe: {0}, which uses a {1} gauge pin or dowel. Check the square is clamped flat and its spacer is INSET from the edges - the probe must reach the steel and touch nothing else. Click OK. Cancel aborts.)", p.Name, p.TipDescription));

            // Corner 1 - the heel. #<_bottom> is a SEEK-DEPTH CAP and nothing more; it is the machine's own Z
            // floor, not a cached spoilboard reading.
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
            b.AppendLine(string.Format("#<c1_maxz> = [#<c1z> + {0}]", cornerTravelMarginMm.ToInvariantString("0.0##")));
            b.AppendLine("(PRINT, SQ_C1X=#<c1x>)");
            b.AppendLine("(PRINT, SQ_C1Y=#<c1y>)");
            // Abort the whole run on an alarmed probe rather than letting a stale/undefined value fall
            // through into the skew - same reason StartJobView waits after every corner of its Measure.
            b.AppendLine("(WAITIDLE)");

            if (touchPlate)
                b.AppendLine("(MBOX, OK, Move the touch plate to the far end of the BLADE - the long arm, along X - then click OK.)");

            // Corner 2 - the blade's far end (X-neighbour). Its Y face is the blade's front edge, which is
            // the reference the skew is measured from; its X face is the blade's end, which only sets the
            // lever length and needs no accuracy at all.
            b.AppendLine("(--- corner 2 = blade far end (X-neighbour) ---)");
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
            b.AppendLine("(PRINT, SQ_C2X=#<c2x>)");
            b.AppendLine("(PRINT, SQ_C2Y=#<c2y>)");
            b.AppendLine("(WAITIDLE)");

            if (touchPlate)
                b.AppendLine("(MBOX, OK, Move the touch plate to the far end of the TONGUE - the short arm, along Y - then click OK.)");

            // Corner 3 - the tongue's far end (Y-neighbour). Mirror of corner 2: its X face is the reference.
            b.AppendLine("(--- corner 3 = tongue far end (Y-neighbour) ---)");
            b.AppendLine("#<_ls_corner> = 3");
            b.AppendLine("#<_ls_refx> = [#<c1x> - 10]");
            b.AppendLine(string.Format("#<_ls_refy> = [#<c1y> + {0}]", (tongueMm + 10d).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_startz> = 0");
            b.AppendLine("#<_ls_maxz> = #<c1_maxz>");
            b.AppendLine("#<_ls_appz> = 9999");
            b.AppendLine("O<pcorner> CALL [#<_ls_rad>]");
            b.AppendLine("#<c3x> = #<_corner_x>");
            b.AppendLine("#<c3y> = #<_corner_y>");
            b.AppendLine("(PRINT, SQ_C3X=#<c3x>)");
            b.AppendLine("(PRINT, SQ_C3Y=#<c3y>)");
            b.AppendLine("(WAITIDLE)");

            // Put back the tool length offset pcorner.macro cancelled on every call - see
            // LiveToolLengthOffset for why a probing run that skips this hands the machine back in G49, and
            // what that cost on 2026-08-06. G43.1 sets the offset absolutely, so re-emitting it is free if
            // it somehow survived.
            //
            // BEFORE the footer, not after, and that ordering is load-bearing in the other direction:
            // EmitProgramFooter's own comment records that a program whose final lines do not MOVE reaches
            // Idle before the controller's "[MSG:Pgm End]" arrives, leaving the Run bar stuck on "Run".
            // So the park stays last. Parking at G30 with a live offset is what every ordinary job already
            // does, so nothing novel is being asked of the G53 moves in it.
            if (double.IsNaN(inheritedTloZ))
                // No invented value. An admitted G49 the operator can see beats a guessed tool length that
                // silently puts work Z0 a tool length into the material.
                b.AppendLine("(NOTE: no tool length offset was readable when this program was generated, so none is restored - the machine is left in G49. Re-reference the tool before the next job.)");
            else if (Math.Abs(inheritedTloZ) > 1e-6)
            {
                b.AppendLine("(--- restore the tool length offset that was in force before this run ---)");
                b.AppendLine(string.Format("G43.1 Z{0}", inheritedTloZ.ToInvariantString("0.0###")));
                b.AppendLine(string.Format("(PRINT, SQ_TLO_RESTORED={0})", inheritedTloZ.ToInvariantString("0.0###")));
            }

            b.AppendLine("(--- park at G30 - no origin/WCS is set by this tool, it only measures ---)");
            MacroProcessor.EmitProgramFooter(l => b.AppendLine(l), stopSpindle: false, parkAtG30: true, endWord: "M2");

            return b.ToString();
        }

        #endregion

        #region Apply / Clear / Re-home

        private void Button_Click(object sender, RoutedEventArgs e)
        {
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
            if (Math.Abs(NewOffset - CurrentOffset) <= 1e-6)
                return;

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
                c1x = c1y = c2x = c2y = c3x = c3y = null;
                DiscardProgram();
                UpdateComputed();
                ReHome();
            }
            else
                AppDialogs.Show(string.Format("Could not write ${0}. Only the actually-ganged axis accepts a write - check the {1} axis is the squared one.",
                                              _offset.Id, "XYZ"[_gangedAxis]),
                                "Squareness", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                c1x = c1y = c2x = c2y = c3x = c3y = null;   // as in ApplyOffset - the gantry has changed
                DiscardProgram();
                UpdateComputed();
                ReHome();
            }
            else
                AppDialogs.Show(string.Format("Could not write ${0}. Only the actually-ganged axis accepts a write - check the {1} axis is the squared one.",
                                              _offset.Id, "XYZ"[_gangedAxis]),
                                "Squareness", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                InvertCorrection = invertCorrection
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
    }
}
