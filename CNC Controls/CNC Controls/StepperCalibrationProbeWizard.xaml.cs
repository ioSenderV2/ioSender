/*
 * StepperCalibrationProbeWizard.xaml.cs - part of CNC Controls library
 *
 * Steps/mm calibration by probing a reference block of ALREADY-KNOWN true size (a precision square,
 * larger is better) against a validated Corner Fence fixture - instead of V-bit scratch marks measured
 * by hand with calipers (StepperCalibrationScratchWizard). Reuses the same pcorner.macro corner-probing
 * StartJobView.BuildProgram uses, including its corner-1 single-probe optimization (Fixture.CornerOffsetX/
 * Y/SpoilboardZ) - see the "double probe of corner 1" backlog item this shares its plumbing with.
 *
 * Only X (corner 1 -> 2, FrontLeft -> FrontRight) and Y (corner 1 -> 3, FrontLeft -> BackLeft) are probed -
 * a single measurement per axis against a block whose true size is already precisely known, not a
 * multi-candidate comparison (see StepperCalibrationScratchWizard's Results grid for that approach).
 *
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
    /// <summary>
    /// Interaction logic for StepperCalibrationProbeWizard.xaml
    /// </summary>
    public partial class StepperCalibrationProbeWizard : ConfigPanel<StepperCalProbeParams>, IGrblConfigTab
    {
        private GrblViewModel model;
        private string program = string.Empty;
        private ProgramView programView;
        private bool subscribed = false;
        private string restoreFixtureName;   // captured from config, applied once RefreshFixtures has a list to match against

        private double? measuredWidth, measuredHeight;
        private GrblSettingDetails settingX, settingY, settingZ;
        private double? newStepsX, newStepsY, newStepsZ;
        // The steps/mm value each axis had right before the last Save - lets Undo put it back if the
        // correction turns out to have made things worse (confirmed possible on real hardware).
        private double? lastSavedFromX, lastSavedFromY, lastSavedFromZ;

        // 1-2-3 gauge block probe results, 1-indexed (index 0 unused) - each is a Z DISTANCE from the
        // spoilboard baseline probe to the block's top surface in that orientation, not an absolute
        // position. GaugeUnits_Checked's own field persists whether the 3 NumericFields below currently
        // display mm or in - Value itself is always canonical mm, same convention as StartJobView.
        private readonly double?[] measuredGauge = new double?[4];
        private bool gaugeIsImperial = true;   // 1-2-3 blocks are inherently imperial parts - default in

        // The machine position the operator jogged to at the "within 10mm of spoilboard" prompt, captured
        // from EVERY run (regardless of chkReuseStart) so there's always something to reuse once the
        // operator opts in. hasStartPos guards against reusing an all-zero default before any run has
        // ever completed that prompt.
        private bool reuseStartPos = false;
        private bool hasStartPos = false;
        private double startPosX, startPosY, startPosZ;

        // True only between Activate(true)/Activate(false) - guards writes to MacroProcessor's Generate-mode
        // statics (shared across all Generate-first tabs) so a stale event firing after this tab was left
        // can't stomp whichever OTHER tab is now focused. See StartJobView.isActiveTab's own comment.
        private bool isActiveTab = false;

        // (PRINT, CAL_X=..) / (PRINT, CAL_Y=..) - same "(PRINT, TAG=value)" idiom StartJobView's own
        // rxResult already parses for LS_X/LS_Y.
        private static readonly Regex rxCalX = new Regex(@"CAL_X\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        private static readonly Regex rxCalY = new Regex(@"CAL_Y\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // (PRINT, GZ1=..) / (PRINT, GZ2=..) / (PRINT, GZ3=..) - BuildProgramZ's three gauge-block measurements.
        private static readonly Regex rxGZ = new Regex(@"GZ(\d)\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // (PRINT, GZSX=..) / (PRINT, GZSY=..) / (PRINT, GZSZ=..) - BuildProgramZ's captured starting position.
        private static readonly Regex rxGZStart = new Regex(@"GZS([XYZ])\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

        // Any of these inputs changing after a Generate makes the sitting 'program' stale (wrong true size/
        // gauge size/travel margin baked into its G-code) - discard it so the Run bar drops back to
        // "Generate" and the next press is guaranteed to rebuild from the CURRENT field values, rather than
        // silently streaming a program built from whatever was entered before the edit.
        private static void OnCalInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var w = (StepperCalibrationProbeWizard)d;
            w.DiscardProgram();
            w.RefreshGenerateReady();
        }

        public static readonly DependencyProperty TrueWidthProperty = DependencyProperty.Register(nameof(TrueWidth), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(400d, OnCalInputChanged));
        public double TrueWidth
        {
            get { return (double)GetValue(TrueWidthProperty); }
            set { SetValue(TrueWidthProperty, value); }
        }

        public static readonly DependencyProperty TrueHeightProperty = DependencyProperty.Register(nameof(TrueHeight), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(400d, OnCalInputChanged));
        public double TrueHeight
        {
            get { return (double)GetValue(TrueHeightProperty); }
            set { SetValue(TrueHeightProperty, value); }
        }

        // Same field/purpose as StartJobView's fldCornerMargin ("Safe Z delta"): corners 2/3 travel at corner 1's
        // own measured stock top plus this delta instead of retracting fully to machine top between corners.
        // Default (15) matches the value this was hardcoded to before the field existed.
        public static readonly DependencyProperty CornerTravelMarginMmProperty = DependencyProperty.Register(nameof(CornerTravelMarginMm), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(15d, OnCalInputChanged));
        public double CornerTravelMarginMm
        {
            get { return (double)GetValue(CornerTravelMarginMmProperty); }
            set { SetValue(CornerTravelMarginMmProperty, value); }
        }

        // 1-2-3 gauge block's own known true dimensions, canonical mm (see GaugeUnits_Checked) - default
        // 25.4/50.8/76.2mm = a real 1-2-3 block's 1/2/3 inch sizes.
        public static readonly DependencyProperty GaugeSize1Property = DependencyProperty.Register(nameof(GaugeSize1), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(25.4d, OnCalInputChanged));
        public double GaugeSize1
        {
            get { return (double)GetValue(GaugeSize1Property); }
            set { SetValue(GaugeSize1Property, value); }
        }

        public static readonly DependencyProperty GaugeSize2Property = DependencyProperty.Register(nameof(GaugeSize2), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(50.8d, OnCalInputChanged));
        public double GaugeSize2
        {
            get { return (double)GetValue(GaugeSize2Property); }
            set { SetValue(GaugeSize2Property, value); }
        }

        public static readonly DependencyProperty GaugeSize3Property = DependencyProperty.Register(nameof(GaugeSize3), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(76.2d, OnCalInputChanged));
        public double GaugeSize3
        {
            get { return (double)GetValue(GaugeSize3Property); }
            set { SetValue(GaugeSize3Property, value); }
        }

        // How-to text for txtHowTo (right panel), swapped by CalAxis_Checked.
        private const string XYHowToText =
            "1. Clamp/register the reference block against the Corner Fence, front-left origin, like a real Start Job.  2. Enter its true (already known) width/height.  3. Generate, then Run - it parks at G30 to confirm the probe, then probes corner 1, then the X and Y neighbours, straight to the tight 5mm-inset anchor (same as Start Job's own corner-1 optimization).  4. Compare Measured to True (left) and check the new-steps/mm calculation, then Save steps/mm to write the correction.";
        private const string ZHowToText =
            "1. Enter the 1-2-3 block's true dimensions, smallest to largest.  2. Generate, then Run - it parks at G30 to confirm the probe, then prompts you to jog within 10mm of the spoilboard and probes it (the baseline) - or, once you've run this at least once, check 'Reuse last saved starting position' to skip the jog and rapid straight there instead.  3. It then prompts you three times to present the block's small/middle/large side up in turn (only one orientation fits at each prompt's retract height) and probes each, retracting to clear the next size before the next prompt.  4. Compare Measured to true size (left) and check the fitted new Z steps/mm, then Save steps/mm to write the correction.";

        public StepperCalibrationProbeWizard()
        {
            InitializeComponent();
            model = DataContext as GrblViewModel;

            // Set here, not via IsChecked="True" in XAML - that would fire the Checked handler mid-BAML-parse,
            // before later-declared sibling fields (pnlZ, txtHowTo, ...) are assigned - see StartJobView's own
            // identical comment on rbUnitsMm for the crash this avoids.
            rbAxisXY.IsChecked = true;
            rbGaugeIn.IsChecked = true;
            chkReuseStart.IsEnabled = false;   // no saved position yet on a fresh install - see hasStartPos
        }

        #region Methods required by IGrblConfigTab

        public GrblConfigType GrblConfigType { get { return GrblConfigType.StepperCalibrationProbe; } }

        private void EnsureProgramView()
        {
            if (programView == null)
                programView = new ProgramView { Title = "Stepper Calibration (probe)" };
        }

        public void Activate(bool activate)
        {
            isActiveTab = activate;
            if (model == null)
                model = DataContext as GrblViewModel;

            if (activate)
            {
                RefreshFixtures();
                if (!subscribed && model != null)
                {
                    model.PropertyChanged += Model_PropertyChanged;
                    subscribed = true;
                }
                if (!string.IsNullOrEmpty(program))
                {
                    EnsureProgramView();
                    programView.SetProgramText(program);
                    programView.Connect();
                }
                MacroProcessor.ActiveRun = Run;   // Cycle Start runs it

                // Generate-mode registration (see MacroProcessor's own comments / StartJobView for the
                // reference implementation): the shared Run bar reads "Generate" until this tab has built its
                // program, then "Run" - no standalone Generate button of this tab's own any more.
                MacroProcessor.SupportsGenerateMode = true;
                MacroProcessor.ActiveGenerate = Generate;
                MacroProcessor.DiscardGenerated = DiscardProgram;
                MacroProcessor.IsProgramGenerated = !string.IsNullOrEmpty(program);
                RefreshGenerateReady();
            }
            else
            {
                MacroProcessor.ActiveRun = null;
                MacroProcessor.SupportsGenerateMode = false;
                MacroProcessor.ActiveGenerate = null;
                MacroProcessor.DiscardGenerated = null;
                // Discard the generated program on tab-leave too (not just after a run finishes - see
                // DiscardProgram's own comment) - so the tab is always back at "Generate" the next time it's
                // focused, regardless of what was generated last visit. Deliberately NOT routed through
                // DiscardProgram() itself: that method's isActiveTab guard (needed elsewhere to stop a stale
                // event from stomping a DIFFERENT tab's readiness) would block this write, since isActiveTab
                // was already set false at the top of this same Activate() call - but THIS is exactly the
                // moment that write is supposed to happen.
                program = string.Empty;
                programView?.Disconnect();
            }

            if (model != null)
                model.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        // The coarse live-readiness gate for the shared Run bar's "Generate" button - mode-dependent: XY
        // needs a fixture selected (Generate()'s own first check); Z needs a 3D probe defined (its own
        // first check - no fixture involved at all, the block is probed at an arbitrary jogged position).
        // Finer preconditions (probe defined for XY too, true sizes set, corner position captured, gauge
        // sizes ascending) still surface via txtWarnings at Generate time, same as before either mode's
        // own standalone Generate button was folded into the shared bar.
        private void RefreshGenerateReady()
        {
            if (!isActiveTab)
                return;
            MacroProcessor.IsGenerateReady = rbAxisZ.IsChecked == true ? ActiveProbe() != null : SelectedFixture != null;
        }

        // Drop the generated program; also registered as MacroProcessor.DiscardGenerated (see Activate) -
        // called right after a clean run finishes so the Run bar reverts to "Generate" for the next job.
        private void DiscardProgram()
        {
            program = string.Empty;
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = false;
        }

        #endregion

        // Only Corner Fence (Implemented, edge-probing) fixtures make sense here - a vise's origin is
        // already known exactly (no face probing), nothing for this tool to measure against.
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

        private void cbxFixture_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            btnSave.IsEnabled = false;
            newStepsX = newStepsY = null;
            Persist();   // not a DependencyProperty change - ConfigPanel.Persist() must be called explicitly
            DiscardProgram();   // a program generated against the PREVIOUS fixture is stale - see OnCalInputChanged
            RefreshGenerateReady();
        }

        // Switches between the XY (fixture + true width/height) and Z (gauge block) calibration UI. Fires
        // from the constructor too (setting the default IsChecked), guarded by pnlXY/pnlZ already being
        // assigned by then - see the constructor's own comment.
        private void CalAxis_Checked(object sender, RoutedEventArgs e)
        {
            bool z = rbAxisZ.IsChecked == true;
            pnlXY.Visibility = z ? Visibility.Collapsed : Visibility.Visible;
            pnlZ.Visibility = z ? Visibility.Visible : Visibility.Collapsed;
            txtHowTo.Text = z ? ZHowToText : XYHowToText;

            // Switching mode invalidates whatever the OTHER mode last measured/computed - a stale X/Y or Z
            // result must not leak into a Save press after the operator moves on to the other axis.
            measuredWidth = measuredHeight = null;
            measuredGauge[1] = measuredGauge[2] = measuredGauge[3] = null;
            newStepsX = newStepsY = newStepsZ = null;
            txtWarnings.Text = string.Empty;
            ShowResult();
            DiscardProgram();   // the sitting program was built for the OTHER axis mode - see OnCalInputChanged
            RefreshGenerateReady();
            Persist();
        }

        // rbGaugeMm/rbGaugeIn toggle for the 3 gauge-size fields - same NumericField.IsImperial attached-
        // property mechanism as StartJobView's own Units_Checked (see its comment): Value stays canonical
        // mm, only the display/entry unit changes.
        private void GaugeUnits_Checked(object sender, RoutedEventArgs e)
        {
            gaugeIsImperial = rbGaugeIn.IsChecked == true;
            NumericField.SetIsImperial(pnlGaugeInputs, gaugeIsImperial);
            Persist();   // no-op until the initial restore completes - see ConfigPanel.Persist()
        }

        // chkReuseStart toggle - see reuseStartPos's own comment. Ignored by BuildProgramZ (falls back to
        // the manual jog prompt) until at least one run has actually captured a position, regardless of
        // when this was checked.
        private void ReuseStartPos_Changed(object sender, RoutedEventArgs e)
        {
            reuseStartPos = chkReuseStart.IsChecked == true;
            Persist();
            DiscardProgram();   // a program already built with/without the reuse-start branch is stale either way
        }

        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GrblViewModel.Message))
                return;

            string msg = model.Message;
            if (string.IsNullOrEmpty(msg))
                return;

            bool hit = false;
            var mx = rxCalX.Match(msg);
            if (mx.Success && double.TryParse(mx.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
            {
                measuredWidth = x;
                hit = true;
            }
            var my = rxCalY.Match(msg);
            if (my.Success && double.TryParse(my.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                measuredHeight = y;
                hit = true;
            }
            var mg = rxGZ.Match(msg);
            if (mg.Success && int.TryParse(mg.Groups[1].Value, out int gi) && gi >= 1 && gi <= 3 &&
                double.TryParse(mg.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gv))
            {
                measuredGauge[gi] = gv;
                hit = true;
            }

            // Captured on EVERY run regardless of chkReuseStart (see its own comment) - doesn't drive
            // ShowResult, just persisted quietly once all three axes have arrived (Z last, per
            // BuildProgramZ's own PRINT order) so the next Generate can offer to reuse it.
            var msp = rxGZStart.Match(msg);
            if (msp.Success && double.TryParse(msp.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sv))
            {
                switch (msp.Groups[1].Value.ToUpperInvariant())
                {
                    case "X": startPosX = sv; break;
                    case "Y": startPosY = sv; break;
                    case "Z":
                        startPosZ = sv;
                        hasStartPos = true;
                        chkReuseStart.IsEnabled = true;   // a valid position now exists - offer the checkbox
                        Persist();
                        break;
                }
            }

            if (hit)
                Dispatcher.BeginInvoke(new System.Action(ShowResult));
        }

        private void ShowResult()
        {
            if (rbAxisZ.IsChecked == true)
                ShowResultZ();
            else
                ShowResultXY();
        }

        private void ShowResultXY()
        {
            double trueW = TrueWidth, trueH = TrueHeight;

            if (!measuredWidth.HasValue && !measuredHeight.HasValue)
            {
                txtResult.Text = "Measured:  X = -   Y = -";
                txtSummary.Text = string.Empty;
                return;
            }

            var sb = new StringBuilder();
            sb.Append("Measured:  ");
            sb.Append(measuredWidth.HasValue ? string.Format(CultureInfo.InvariantCulture, "X = {0:0.###} mm", measuredWidth.Value) : "X = -");
            sb.Append("   ");
            sb.Append(measuredHeight.HasValue ? string.Format(CultureInfo.InvariantCulture, "Y = {0:0.###} mm", measuredHeight.Value) : "Y = -");
            txtResult.Text = sb.ToString();

            var fx = SelectedFixture;
            var summary = new StringBuilder();
            bool canSave = false;

            if (measuredWidth.HasValue && fx != null)
            {
                settingX = GrblSettings.Get(GrblSetting.TravelResolutionBase + 0);   // $100 = X steps/mm
                double curX = settingX != null ? dbl.Parse(settingX.Value) : double.NaN;
                if (settingX != null && !double.IsNaN(curX) && measuredWidth.Value > 0d)
                {
                    // new = current x measured / true - NOT current x true / measured. The probed "measured"
                    // position is itself computed by the firmware from step count / CURRENT steps/mm (it's
                    // not an independent measurement), so it scales WITH the current setting's own error, not
                    // against it - confirmed on real hardware: the inverted formula made the error grow
                    // (~1.5mm -> ~4mm) instead of shrink after applying it once.
                    newStepsX = curX * measuredWidth.Value / trueW;
                    double errX = measuredWidth.Value - trueW;
                    summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "X: true {0:0.###} mm, measured {1:0.###} mm, error {2:+0.###;-0.###;0} mm", trueW, measuredWidth.Value, errX));
                    summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "   new = current x measured / true = {0:0.######} x {1:0.###} / {2:0.###} = {3:0.######}",
                        curX, measuredWidth.Value, trueW, newStepsX.Value));
                    canSave = true;
                }
            }
            if (measuredHeight.HasValue && fx != null)
            {
                settingY = GrblSettings.Get(GrblSetting.TravelResolutionBase + 1);   // $101 = Y steps/mm
                double curY = settingY != null ? dbl.Parse(settingY.Value) : double.NaN;
                if (settingY != null && !double.IsNaN(curY) && measuredHeight.Value > 0d)
                {
                    newStepsY = curY * measuredHeight.Value / trueH;   // see X's own comment on the formula direction
                    double errY = measuredHeight.Value - trueH;
                    summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "Y: true {0:0.###} mm, measured {1:0.###} mm, error {2:+0.###;-0.###;0} mm", trueH, measuredHeight.Value, errY));
                    summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "   new = current x measured / true = {0:0.######} x {1:0.###} / {2:0.###} = {3:0.######}",
                        curY, measuredHeight.Value, trueH, newStepsY.Value));
                    canSave = true;
                }
            }

            txtSummary.Text = summary.ToString();
            btnSave.IsEnabled = canSave;
        }

        private void ShowResultZ()
        {
            if (!measuredGauge[1].HasValue && !measuredGauge[2].HasValue && !measuredGauge[3].HasValue)
            {
                txtResult.Text = "Measured:  1 = -   2 = -   3 = -";
                txtSummary.Text = string.Empty;
                return;
            }

            var sb = new StringBuilder();
            sb.Append("Measured:  ");
            sb.Append(measuredGauge[1].HasValue ? string.Format(CultureInfo.InvariantCulture, "1 = {0:0.###} mm", measuredGauge[1].Value) : "1 = -");
            sb.Append("   ");
            sb.Append(measuredGauge[2].HasValue ? string.Format(CultureInfo.InvariantCulture, "2 = {0:0.###} mm", measuredGauge[2].Value) : "2 = -");
            sb.Append("   ");
            sb.Append(measuredGauge[3].HasValue ? string.Format(CultureInfo.InvariantCulture, "3 = {0:0.###} mm", measuredGauge[3].Value) : "3 = -");
            txtResult.Text = sb.ToString();

            var summary = new StringBuilder();
            bool canSave = false;

            // All three measurements are needed together - the fit below combines them, there's no
            // per-axis-independent partial save the way XY's X/Y can save separately.
            if (measuredGauge[1].HasValue && measuredGauge[2].HasValue && measuredGauge[3].HasValue)
            {
                settingZ = GrblSettings.Get(GrblSetting.TravelResolutionBase + 2);   // $102 = Z steps/mm
                double curZ = settingZ != null ? dbl.Parse(settingZ.Value) : double.NaN;
                if (settingZ != null && !double.IsNaN(curZ))
                {
                    double[] nominal = { GaugeSize1, GaugeSize2, GaugeSize3 };
                    double[] measured = { measuredGauge[1].Value, measuredGauge[2].Value, measuredGauge[3].Value };

                    // Least-squares fit of a proportional model (measured = k * nominal, a line through the
                    // origin - physically correct here because every measurement is already a DELTA from the
                    // shared spoilboard baseline probe, so any constant probe-trigger offset cancels out and
                    // a true size of 0 really does mean a measured distance of 0). Standard "regression
                    // through the origin" slope: k = sum(x*y) / sum(x*x). Same "scales WITH the current
                    // setting's error, not against it" reasoning as the XY tool's own formula (see its
                    // comment) - k already IS an averaged measured/true ratio, fit across all 3 points
                    // instead of trusting any single one.
                    double sumXY = 0d, sumXX = 0d;
                    for (int i = 0; i < 3; i++)
                    {
                        sumXY += nominal[i] * measured[i];
                        sumXX += nominal[i] * nominal[i];
                    }
                    double k = sumXX > 0d ? sumXY / sumXX : double.NaN;

                    if (!double.IsNaN(k))
                    {
                        newStepsZ = curZ * k;
                        for (int i = 0; i < 3; i++)
                        {
                            double err = measured[i] - nominal[i];
                            summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "{0}: true {1:0.###} mm, measured {2:0.###} mm, error {3:+0.###;-0.###;0} mm",
                                i + 1, nominal[i], measured[i], err));
                        }
                        summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "   fit k = sum(true x measured) / sum(true^2) = {0:0.######}", k));
                        summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "   new = current x k = {0:0.######} x {1:0.######} = {2:0.######}", curZ, k, newStepsZ.Value));
                        canSave = true;
                    }
                }
            }

            txtSummary.Text = summary.ToString();
            btnSave.IsEnabled = canSave;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            switch ((string)((Button)sender).Tag)
            {
                case "save":
                    Save();
                    break;
                case "undo":
                    Undo();
                    break;
            }
        }

        private ProbeDefinition ActiveProbe()
        {
            return ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
        }

        private void Generate()
        {
            if (model == null)
                return;
            if (rbAxisZ.IsChecked == true)
                GenerateZ();
            else
                GenerateXY();
        }

        private void GenerateXY()
        {
            var fx = SelectedFixture;
            if (fx == null)
            {
                txtWarnings.Text = "Select a validated Corner Fence fixture first.";
                return;
            }
            // Same "never actually captured under this scheme" guard StartJobView.Generate_Click uses.
            if (fx.CornerOffsetX == 0d || fx.CornerOffsetY == 0d || fx.SpoilboardZ == 0d)
            {
                txtWarnings.Text = "This fixture's corner position hasn't been located yet - run Test position again in Machine Setup > Fixture definitions.";
                return;
            }

            var p = ActiveProbe();
            if (p == null)
            {
                txtWarnings.Text = "Define a 3D probe first (Machine Setup > Probe definitions).";
                return;
            }

            double trueW = TrueWidth, trueH = TrueHeight;
            if (trueW <= 0d || trueH <= 0d)
            {
                txtWarnings.Text = "Enter the reference block's true width and height first.";
                return;
            }

            txtWarnings.Text = string.Empty;
            measuredWidth = measuredHeight = null;
            newStepsX = newStepsY = null;
            btnSave.IsEnabled = false;
            ShowResult();

            program = BuildProgram(fx, p, trueW, trueH, CornerTravelMarginMm);
            MacroProcessor.PublishGenerated("Stepper calibration (probe) XY", program, EnsureProgramView, () => programView);
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = true;   // flips the shared Run bar from "Generate" to "Run"
        }

        private void GenerateZ()
        {
            var p = ActiveProbe();
            if (p == null)
            {
                txtWarnings.Text = "Define a 3D probe first (Machine Setup > Probe definitions).";
                return;
            }

            double g1 = GaugeSize1, g2 = GaugeSize2, g3 = GaugeSize3;
            if (g1 <= 0d || g2 <= 0d || g3 <= 0d)
            {
                txtWarnings.Text = "Enter all three gauge block dimensions first.";
                return;
            }
            // The generated program's retract heights and prompts assume ascending order (see BuildProgramZ) -
            // an operator who entered them out of order would be prompted to present the wrong face at each step.
            if (!(g1 < g2 && g2 < g3))
            {
                txtWarnings.Text = "Gauge sizes must be entered smallest to largest (size 1 < size 2 < size 3).";
                return;
            }

            txtWarnings.Text = string.Empty;
            measuredGauge[1] = measuredGauge[2] = measuredGauge[3] = null;
            newStepsZ = null;
            btnSave.IsEnabled = false;
            ShowResult();

            program = BuildProgramZ(p, g1, g2, g3, reuseStartPos && hasStartPos, startPosX, startPosY, startPosZ);
            MacroProcessor.PublishGenerated("Stepper calibration (probe) Z", program, EnsureProgramView, () => programView);
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = true;   // flips the shared Run bar from "Generate" to "Run"
        }

        private void Run()
        {
            if (model == null)
                return;
            if (string.IsNullOrWhiteSpace(program))
                Generate();
            if (string.IsNullOrWhiteSpace(program))
                return;

            MacroProcessor.Run(model, "Stepper calibration (probe) " + (rbAxisZ.IsChecked == true ? "Z" : "XY"), program, true);
        }

        private void Save()
        {
            bool ok = true;
            double? fromX = null, fromY = null, fromZ = null;
            if (newStepsX.HasValue && settingX != null)
            {
                fromX = dbl.Parse(settingX.Value);   // capture BEFORE overwriting, for Undo
                settingX.Value = newStepsX.Value.ToInvariantString();
                ok &= GrblSettings.Save();
            }
            if (newStepsY.HasValue && settingY != null)
            {
                fromY = dbl.Parse(settingY.Value);
                settingY.Value = newStepsY.Value.ToInvariantString();
                ok &= GrblSettings.Save();
            }
            if (newStepsZ.HasValue && settingZ != null)
            {
                fromZ = dbl.Parse(settingZ.Value);
                settingZ.Value = newStepsZ.Value.ToInvariantString();
                ok &= GrblSettings.Save();
            }
            if (ok)
            {
                lastSavedFromX = fromX;
                lastSavedFromY = fromY;
                lastSavedFromZ = fromZ;

                AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "Steps/mm updated{0}{1}{2}.",
                    newStepsX.HasValue ? " X=" + newStepsX.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty,
                    newStepsY.HasValue ? " Y=" + newStepsY.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty,
                    newStepsZ.HasValue ? " Z=" + newStepsZ.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty),
                    "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                btnSave.IsEnabled = false;
                btnUndo.IsEnabled = fromX.HasValue || fromY.HasValue || fromZ.HasValue;
            }
        }

        // Restore whichever steps/mm value(s) the last Save actually changed, back to what they were
        // right before - for when a correction turns out to have made things worse (confirmed possible on
        // real hardware: an inverted formula made a ~1.5mm error grow to ~4mm after one Save).
        private void Undo()
        {
            bool ok = true;
            if (lastSavedFromX.HasValue && settingX != null)
            {
                settingX.Value = lastSavedFromX.Value.ToInvariantString();
                ok &= GrblSettings.Save();
            }
            if (lastSavedFromY.HasValue && settingY != null)
            {
                settingY.Value = lastSavedFromY.Value.ToInvariantString();
                ok &= GrblSettings.Save();
            }
            if (lastSavedFromZ.HasValue && settingZ != null)
            {
                settingZ.Value = lastSavedFromZ.Value.ToInvariantString();
                ok &= GrblSettings.Save();
            }
            if (ok)
            {
                AppDialogs.Show("Steps/mm reverted to the value before the last Save.",
                    "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                lastSavedFromX = lastSavedFromY = lastSavedFromZ = null;
                btnUndo.IsEnabled = false;
            }
        }

        // Corner 1 (REUSE, cached fixture offset/spoilboard Z - same optimization as StartJobView's own
        // corner-1 probe) then corner 2 (X-neighbour, FrontRight) and corner 3 (Y-neighbour, BackLeft),
        // both tight/"exact" references derived from the ENTERED true size - the whole premise of this
        // tool is that size is already precisely known, so there's no need for a loose locate pass. Same
        // "5mm inset" anchor formula as StartJobView.BuildProgram's own exact-size corners 2/3.
        private static string BuildProgram(Fixture fx, ProbeDefinition p, double trueWidthMm, double trueHeightMm, double cornerTravelMarginMm)
        {
            const double insetMm = 5d;
            double r = p.ProbeDiameter / 2d;
            var fxPos = new Position(fx.Coords);
            string refX = fxPos.X.ToInvariantString("0.0##"), refY = fxPos.Y.ToInvariantString("0.0##");
            string searchF = Math.Max(p.ProbeFeedRate, 200d).ToInvariantString("0.0##");
            string latchF = p.LatchFeedRate.ToInvariantString("0.0##");

            var b = new StringBuilder();
            b.AppendLine("(Stepper calibration - probe a reference block of known true size)");
            b.AppendLine("(PREREQ, connected, homed, EXPR, noalarm)");
            b.AppendLine("G21 G90 G94 G17");
            b.AppendLine("G49");
            b.AppendLine("G10 L2 P1 X0 Y0 Z0");
            if (GrblInfo.HasToolSetter)
                b.AppendLine(string.Format(GrblCommand.ProbeSelect, p.ProbeType == ProbeType.ToolSetter ? 1 : 0));

            b.AppendLine(string.Format("#<_ls_rad> = {0}", r.ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_spacer> = 0");
            b.AppendLine("#<_ls_thickness> = 0");   // unused by pcorner.macro since the fixed-5mm depth change - kept for the global's sake
            b.AppendLine("#<_ls_mode> = 0");
            b.AppendLine("#<_ls_plateoffset> = 0");
            b.AppendLine("#<_ls_spoilx> = 0");
            b.AppendLine("#<_ls_spoily> = 0");
            b.AppendLine(string.Format("#<_ls_searchf> = {0}", searchF));
            b.AppendLine(string.Format("#<_ls_latchf> = {0}", latchF));
            b.AppendLine(string.Format("#<_ls_zfloor> = {0}", (GrblInfo.MaxTravel.Z > 0d ? -(GrblInfo.MaxTravel.Z) + 1.0d : -9999d).ToInvariantString("0.0##")));

            // Park at G30 and confirm the probe before touching anything - same pattern StartJobView.BuildProgram
            // uses (EmitGotoG30 + MBOX). #<_abs_x>/#<_abs_y> are grblHAL's own live current-machine-position
            // named parameters; every G53 move NAMES both axes explicitly (never a bare "G53 G0 Z0") - a firmware
            // bug sign-flips a homing-direction-inverted ($23) axis's parser base after a G53 move that leaves
            // it "unmoved", producing a false Alarm:2.
            b.AppendLine("(park at G30 - install / confirm the probe)");
            b.AppendLine("G53 G0 X[#<_abs_x>] Y[#<_abs_y>] Z0");
            b.AppendLine("G53 G0 X[#5181] Y[#5182]");
            b.AppendLine("G53 G0 X[#5181] Y[#5182] Z[#5183]");
            b.AppendLine("(WAITIDLE)");
            b.AppendLine("(MBOX, OKCANCEL, Install and seat the probe, then click OK. Cancel aborts.)");

            // Corner 1 (origin, FrontLeft) - REUSE mode, cached spoilboard Z + corner offset (no locate pass).
            b.AppendLine("(--- corner 1 (origin) ---)");
            b.AppendLine(string.Format("#<_bottom> = {0}", fx.SpoilboardZ.ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_corner> = 1");
            b.AppendLine(string.Format("#<_ls_refx> = {0}", refX));
            b.AppendLine(string.Format("#<_ls_refy> = {0}", refY));
            b.AppendLine(string.Format("#<_ls_topx> = {0}", (fx.CornerOffsetX + insetMm).ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_ls_topy> = {0}", (fx.CornerOffsetY + insetMm).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_startz> = 0");
            b.AppendLine("#<_ls_maxz> = 0");
            b.AppendLine("#<_ls_appz> = 9999");
            b.AppendLine("O<pcorner> CALL [#<_ls_rad>]");
            b.AppendLine("#<c1x> = #<_corner_x>");
            b.AppendLine("#<c1y> = #<_corner_y>");
            b.AppendLine("#<c1z> = #<_corner_z>");
            b.AppendLine(string.Format("#<c1_maxz> = [#<c1z> + {0}]", cornerTravelMarginMm.ToInvariantString("0.0##")));

            // Corner 2 (X-neighbour, FrontRight, id=2) - tight reference from the ENTERED true width.
            b.AppendLine("(--- corner 2 (X-neighbour) ---)");
            b.AppendLine("#<_ls_topx> = 15");
            b.AppendLine("#<_ls_topy> = 15");
            b.AppendLine("#<_ls_corner> = 2");
            b.AppendLine(string.Format("#<_ls_refx> = [#<c1x> + {0}]", (trueWidthMm + 10d).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_refy> = [#<c1y> - 10]");
            b.AppendLine("#<_ls_startz> = 0");
            b.AppendLine("#<_ls_maxz> = #<c1_maxz>");
            b.AppendLine("#<_ls_appz> = 9999");
            b.AppendLine("O<pcorner> CALL [#<_ls_rad>]");
            b.AppendLine("#<c2x> = #<_corner_x>");
            b.AppendLine("#<size_x> = [#<c2x> - #<c1x>]");
            b.AppendLine("(PRINT, CAL_X=#<size_x>)");

            // Corner 3 (Y-neighbour, BackLeft, id=3) - tight reference from the ENTERED true height.
            b.AppendLine("(--- corner 3 (Y-neighbour) ---)");
            b.AppendLine("#<_ls_corner> = 3");
            b.AppendLine("#<_ls_refx> = [#<c1x> - 10]");
            b.AppendLine(string.Format("#<_ls_refy> = [#<c1y> + {0}]", (trueHeightMm + 10d).ToInvariantString("0.0##")));
            b.AppendLine("#<_ls_startz> = 0");
            b.AppendLine("#<_ls_maxz> = #<c1_maxz>");
            b.AppendLine("#<_ls_appz> = 9999");
            b.AppendLine("O<pcorner> CALL [#<_ls_rad>]");
            b.AppendLine("#<c3y> = #<_corner_y>");
            b.AppendLine("#<size_y> = [#<c3y> - #<c1y>]");
            b.AppendLine("(PRINT, CAL_Y=#<size_y>)");

            b.AppendLine("(--- park at G30 - no origin/WCS is set by this tool, it only measures ---)");
            MacroProcessor.EmitGotoG30(l => b.AppendLine(l));
            b.AppendLine("M2");

            return b.ToString();
        }

        // Straight-down single-point probes only (no lateral search), so unlike BuildProgram above this
        // needs no controller-side O-word macro (pcorner.macro) - the whole sequence is emitted inline
        // using the same G91/G38.2-search/backoff/relatch idiom macros/tc.macro's own toolsetter probe
        // uses. Measures the Z distance from a fresh spoilboard probe (this run's OWN baseline - no
        // fixture or prior-session data needed, unlike the XY tool) to the gauge block's top surface in
        // each of its three orientations (smallest to largest, per GenerateZ's own validation). Retracts
        // between probes are relative (G91) rises computed from the KNOWN entered gauge sizes, landing at
        // (next gauge size + clearance) above the spoilboard each time - the last one repeats gauge size 3
        // since there's nothing further to clear for. See the design note this shipped with (memory:
        // iosender-z-gauge-block-calibration) for the full arithmetic derivation.
        // reuseStartPos (with a known startX/Y/Z from a PRIOR run - see Model_PropertyChanged's GZSX/Y/Z
        // capture) rapids straight to that saved position instead of prompting for a manual jog - the
        // position is re-captured (and re-printed) either way, right after whichever path got there, so it
        // keeps tracking wherever the operator last actually confirmed, automated or not.
        private static string BuildProgramZ(ProbeDefinition p, double g1Mm, double g2Mm, double g3Mm,
            bool reuseStartPos, double startX, double startY, double startZ)
        {
            const double clearanceMm = 10d;
            const double jogSearchMm = 25d;    // step 1: operator jogged "within 10mm" by eye - generous margin
            const double probeSearchMm = 20d;  // steps 2-4: expected descent is exactly clearanceMm if the block matches its true size - generous margin over calibration error
            string searchF = Math.Max(p.ProbeFeedRate, 200d).ToInvariantString("0.0##");
            string latchF = p.LatchFeedRate.ToInvariantString("0.0##");
            string[] sizeLabel = { "small", "middle", "large" };
            double[] g = { g1Mm, g2Mm, g3Mm };

            var b = new StringBuilder();
            b.AppendLine("(Stepper calibration - Z axis via a 1-2-3 gauge block)");
            b.AppendLine("(PREREQ, connected, homed, EXPR, noalarm)");
            b.AppendLine("G21 G90 G94 G17");
            b.AppendLine("G49");
            b.AppendLine("G10 L2 P1 X0 Y0 Z0");
            if (GrblInfo.HasToolSetter)
                b.AppendLine(string.Format(GrblCommand.ProbeSelect, p.ProbeType == ProbeType.ToolSetter ? 1 : 0));

            // Park at G30 and confirm the probe before touching anything - same pattern as BuildProgram
            // above (and StartJobView's).
            b.AppendLine("(park at G30 - install / confirm the probe)");
            MacroProcessor.EmitGotoG30(l => b.AppendLine(l));
            b.AppendLine("(WAITIDLE)");
            b.AppendLine("(MBOX, OKCANCEL, Install and seat the probe, then click OK. Cancel aborts.)");

            b.AppendLine("(--- spoilboard baseline ---)");
            if (reuseStartPos)
            {
                // XY rapid at machine Z0 (top) ONLY - confirm clear BEFORE descending, not after: the saved
                // Z sat within 10mm of the spoilboard, so descending there unconfirmed risks a crash if
                // anything (the block, a clamp, ...) is now in the way. Only descend to the saved Z once the
                // operator has actually looked and clicked OK.
                b.AppendLine("G53 G0 Z0");
                b.AppendLine(string.Format("G53 G0 X{0} Y{1}", startX.ToInvariantString("0.0##"), startY.ToInvariantString("0.0##")));
                b.AppendLine("(MBOX, OKCANCEL, Rapided to the saved starting position (XY only - Z is still at machine top). Confirm it's clear of any obstruction, then click OK to descend and probe. Cancel aborts.)");
                b.AppendLine(string.Format("G53 G0 Z{0}", startZ.ToInvariantString("0.0##")));
                // Unlike the manual-jog branch (where the operator's own click-through absorbs the settle time),
                // this Z descend is immediately followed by an automated relative G38.2 search with NO human
                // gate in between - so the exact "burst's trailing motion" race already found and fixed for the
                // G30 park move (WaitForIdle, MacroProcessor.cs, commit c0d0896 - confirmed on real hardware for
                // THIS wizard among others) applies here too: without this, the relative search could start
                // arming before the descend has genuinely finished, effectively searching from wherever the
                // rapid still was (up near machine top) instead of from the saved Z.
                b.AppendLine("(WAITIDLE)");
            }
            else
                b.AppendLine("(MBOX, OKCANCEL, Manually jog the probe to within 10mm of the spoilboard - clear of any obstructions - then click OK. Cancel aborts.)");
            // Capture the pre-descent position into NGC vars now (this IS the "starting point"), but don't
            // PRINT/persist it yet - only once the probe below actually PROVES it found the spoilboard (a
            // real trigger, #<_gz0> assigned). Saving on mere arrival - before the probe result is known -
            // let a jog prompt clicked through without actually jogging (still sitting at/near machine top)
            // silently bank a near-zero "starting position" as reusable; the next reuse then rapids to that
            // same bogus spot, undershoots its own short relative search, and alarms - confirmed on real
            // hardware 2026-07-21 (twice: -0.044mm then -0.028mm, both times the operator never actually
            // reached the spoilboard first). If the probe below alarms instead, the controller halts before
            // ever reaching the PRINT lines, so a bad run now correctly leaves the LAST GOOD saved position
            // untouched rather than overwriting it with garbage.
            b.AppendLine("#<_gzsx> = #<_abs_x>");
            b.AppendLine("#<_gzsy> = #<_abs_y>");
            b.AppendLine("#<_gzsz> = #<_abs_z>");
            b.AppendLine("G91");
            b.AppendLine(string.Format("G38.2 Z-{0} F[{1}]", jogSearchMm.ToInvariantString("0.0##"), searchF));
            b.AppendLine("G0 Z2");
            b.AppendLine(string.Format("G38.2 Z-5 F[{0}]", latchF));
            b.AppendLine("#<_gz0> = #5063");
            b.AppendLine("(PRINT, GZSX=#<_gzsx>)");
            b.AppendLine("(PRINT, GZSY=#<_gzsy>)");
            b.AppendLine("(PRINT, GZSZ=#<_gzsz>)");
            b.AppendLine(string.Format("G0 Z{0}", (g1Mm + clearanceMm).ToInvariantString("0.0##")));
            b.AppendLine("G90");

            for (int i = 0; i < 3; i++)
            {
                b.AppendLine(string.Format("(--- gauge size {0} ({1}) ---)", i + 1, sizeLabel[i]));
                b.AppendLine(string.Format("(MBOX, OKCANCEL, Position the gauge block under the probe tip - its {0} ({1}mm) side up. Only one orientation fits here. Click OK. Cancel aborts.)",
                    sizeLabel[i], g[i].ToInvariantString("0.0##")));
                b.AppendLine("G91");
                b.AppendLine(string.Format("G38.2 Z-{0} F[{1}]", probeSearchMm.ToInvariantString("0.0##"), searchF));
                b.AppendLine("G0 Z2");
                b.AppendLine(string.Format("G38.2 Z-5 F[{0}]", latchF));
                b.AppendLine(string.Format("#<_gz{0}> = #5063", i + 1));
                // Z increases upward, so the spoilboard (deepest) has a MORE NEGATIVE work-Z than the
                // block's top surface sitting above it - block-top minus spoilboard, not the other way
                // round, or every measurement comes back negative (confirmed on real hardware 2026-07-21).
                b.AppendLine(string.Format("#<_gm{0}> = [#<_gz{0}> - #<_gz0>]", i + 1));
                b.AppendLine(string.Format("(PRINT, GZ{0}=#<_gm{0}>)", i + 1));

                // Retract to (next gauge size + clearance) above the spoilboard - the last iteration (i=2)
                // repeats gauge size 3 (nothing further to clear for).
                double nextG = i < 2 ? g[i + 1] : g[2];
                double relativeRise = (nextG + clearanceMm) - g[i];
                b.AppendLine(string.Format("G0 Z{0}", relativeRise.ToInvariantString("0.0##")));
                b.AppendLine("G90");
            }

            b.AppendLine("(--- park at G30 - no origin/WCS is set by this tool, it only measures ---)");
            MacroProcessor.EmitGotoG30(l => b.AppendLine(l));
            b.AppendLine("M2");

            return b.ToString();
        }

        // Persisted as the "StepperCalProbe" section of App.config.
        public static StepperCalProbeParams SectionConfig;

        #region ConfigPanel<StepperCalProbeParams> overrides

        protected override StepperCalProbeParams Config { get { return SectionConfig; } set { SectionConfig = value; } }

        protected override DependencyProperty[] PersistedProperties => new[]
        {
            TrueWidthProperty, TrueHeightProperty, CornerTravelMarginMmProperty,
            GaugeSize1Property, GaugeSize2Property, GaugeSize3Property
        };

        protected override void ApplyConfig(StepperCalProbeParams p)
        {
            TrueWidth = p.TrueWidth;
            TrueHeight = p.TrueHeight;
            CornerTravelMarginMm = p.CornerTravelMarginMm;
            restoreFixtureName = p.FixtureName;
            GaugeSize1 = p.GaugeSize1;
            GaugeSize2 = p.GaugeSize2;
            GaugeSize3 = p.GaugeSize3;
            gaugeIsImperial = p.GaugeIsImperial;
            rbGaugeIn.IsChecked = gaugeIsImperial;
            rbGaugeMm.IsChecked = !gaugeIsImperial;
            NumericField.SetIsImperial(pnlGaugeInputs, gaugeIsImperial);
            rbAxisZ.IsChecked = p.CalibrateZ;
            rbAxisXY.IsChecked = !p.CalibrateZ;
            reuseStartPos = p.ReuseStartPos;
            hasStartPos = p.HasStartPos;
            startPosX = p.StartPosX;
            startPosY = p.StartPosY;
            startPosZ = p.StartPosZ;
            chkReuseStart.IsEnabled = hasStartPos;
            chkReuseStart.IsChecked = reuseStartPos && hasStartPos;
        }

        protected override StepperCalProbeParams CaptureConfig()
        {
            return new StepperCalProbeParams
            {
                TrueWidth = TrueWidth,
                TrueHeight = TrueHeight,
                CornerTravelMarginMm = CornerTravelMarginMm,
                FixtureName = SelectedFixture?.Name ?? restoreFixtureName,
                CalibrateZ = rbAxisZ.IsChecked == true,
                GaugeSize1 = GaugeSize1,
                GaugeSize2 = GaugeSize2,
                GaugeSize3 = GaugeSize3,
                GaugeIsImperial = gaugeIsImperial,
                ReuseStartPos = reuseStartPos,
                HasStartPos = hasStartPos,
                StartPosX = startPosX,
                StartPosY = startPosY,
                StartPosZ = startPosZ
            };
        }

        protected override void OnConfigReady()
        {
            if (model == null)
                model = DataContext as GrblViewModel;
        }

        #endregion
    }

    // Persisted stepper-calibration-by-probe parameters. Public for XmlSerializer.
    public class StepperCalProbeParams
    {
        public double TrueWidth = 400d, TrueHeight = 400d;
        public double CornerTravelMarginMm = 15d;
        public string FixtureName = string.Empty;
        public bool CalibrateZ = false;
        public double GaugeSize1 = 25.4d, GaugeSize2 = 50.8d, GaugeSize3 = 76.2d;
        public bool GaugeIsImperial = true;
        public bool ReuseStartPos = false;
        public bool HasStartPos = false;
        public double StartPosX = 0d, StartPosY = 0d, StartPosZ = 0d;
    }
}
