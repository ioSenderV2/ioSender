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
        private GrblSettingDetails settingX, settingY;
        private double? newStepsX, newStepsY;
        // The steps/mm value each axis had right before the last Save - lets Undo put it back if the
        // correction turns out to have made things worse (confirmed possible on real hardware).
        private double? lastSavedFromX, lastSavedFromY;

        // True only between Activate(true)/Activate(false) - guards writes to MacroProcessor's Generate-mode
        // statics (shared across all Generate-first tabs) so a stale event firing after this tab was left
        // can't stomp whichever OTHER tab is now focused. See StartJobView.isActiveTab's own comment.
        private bool isActiveTab = false;

        // (PRINT, CAL_X=..) / (PRINT, CAL_Y=..) - same "(PRINT, TAG=value)" idiom StartJobView's own
        // rxResult already parses for LS_X/LS_Y.
        private static readonly Regex rxCalX = new Regex(@"CAL_X\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        private static readonly Regex rxCalY = new Regex(@"CAL_Y\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

        public static readonly DependencyProperty TrueWidthProperty = DependencyProperty.Register(nameof(TrueWidth), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(400d));
        public double TrueWidth
        {
            get { return (double)GetValue(TrueWidthProperty); }
            set { SetValue(TrueWidthProperty, value); }
        }

        public static readonly DependencyProperty TrueHeightProperty = DependencyProperty.Register(nameof(TrueHeight), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(400d));
        public double TrueHeight
        {
            get { return (double)GetValue(TrueHeightProperty); }
            set { SetValue(TrueHeightProperty, value); }
        }

        // Same field/purpose as StartJobView's fldCornerMargin ("Safe Z delta"): corners 2/3 travel at corner 1's
        // own measured stock top plus this delta instead of retracting fully to machine top between corners.
        // Default (15) matches the value this was hardcoded to before the field existed.
        public static readonly DependencyProperty CornerTravelMarginMmProperty = DependencyProperty.Register(nameof(CornerTravelMarginMm), typeof(double), typeof(StepperCalibrationProbeWizard), new PropertyMetadata(15d));
        public double CornerTravelMarginMm
        {
            get { return (double)GetValue(CornerTravelMarginMmProperty); }
            set { SetValue(CornerTravelMarginMmProperty, value); }
        }

        public StepperCalibrationProbeWizard()
        {
            InitializeComponent();
            model = DataContext as GrblViewModel;
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
                programView?.Disconnect();
            }

            if (model != null)
                model.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        // The coarse live-readiness gate for the shared Run bar's "Generate" button: a fixture must be
        // selected (Generate()'s own first check). Finer preconditions (probe defined, true width/height set,
        // corner position captured) still surface via txtWarnings at Generate time, same as before this tab's
        // own standalone Generate button was folded into the shared bar - no behavior change there, just who
        // owns the button.
        private void RefreshGenerateReady()
        {
            if (isActiveTab)
                MacroProcessor.IsGenerateReady = SelectedFixture != null;
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
            RefreshGenerateReady();
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

            if (hit)
                Dispatcher.BeginInvoke(new System.Action(ShowResult));
        }

        private void ShowResult()
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
            MacroProcessor.PublishGenerated("Stepper calibration (probe)", program, EnsureProgramView, () => programView);
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

            MacroProcessor.Run(model, "Stepper calibration (probe)", program, true);
        }

        private void Save()
        {
            bool ok = true;
            double? fromX = null, fromY = null;
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
            if (ok)
            {
                lastSavedFromX = fromX;
                lastSavedFromY = fromY;
                AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "Steps/mm updated{0}{1}.",
                    newStepsX.HasValue ? " X=" + newStepsX.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty,
                    newStepsY.HasValue ? " Y=" + newStepsY.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty),
                    "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                btnSave.IsEnabled = false;
                btnUndo.IsEnabled = fromX.HasValue || fromY.HasValue;
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
            if (ok)
            {
                AppDialogs.Show("Steps/mm reverted to the value before the last Save.",
                    "Stepper calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                lastSavedFromX = lastSavedFromY = null;
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

        // Persisted as the "StepperCalProbe" section of App.config.
        public static StepperCalProbeParams SectionConfig;

        #region ConfigPanel<StepperCalProbeParams> overrides

        protected override StepperCalProbeParams Config { get { return SectionConfig; } set { SectionConfig = value; } }

        protected override DependencyProperty[] PersistedProperties => new[] { TrueWidthProperty, TrueHeightProperty, CornerTravelMarginMmProperty };

        protected override void ApplyConfig(StepperCalProbeParams p)
        {
            TrueWidth = p.TrueWidth;
            TrueHeight = p.TrueHeight;
            CornerTravelMarginMm = p.CornerTravelMarginMm;
            restoreFixtureName = p.FixtureName;
        }

        protected override StepperCalProbeParams CaptureConfig()
        {
            return new StepperCalProbeParams
            {
                TrueWidth = TrueWidth,
                TrueHeight = TrueHeight,
                CornerTravelMarginMm = CornerTravelMarginMm,
                FixtureName = SelectedFixture?.Name ?? restoreFixtureName
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
    }
}
