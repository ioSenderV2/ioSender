/*
 * AutoSquareWizard.xaml.cs - part of CNC Controls library
 *
 * Facilitates Phil Barrett's auto-square OFFSET method for a ganged, auto-squared gantry (typically Y).
 * It peck-drills a 3-hole "L" (a corner hole plus one hole out along each axis, at the framing-square arm
 * lengths). You drop a pin in each hole, register a framing square on the Y-leg pins, and measure the gap
 * at the X-leg pin. The tool converts that to a squaring-offset adjustment, writes the setting and re-homes.
 *
 * The firmware must already be built with the axis ganged + auto-squared - this only tunes the runtime
 * offset setting (grblHAL Setting_AxisAutoSquareOffset: X=170, Y=171, Z=172). The drilling run uses the
 * shared run-control flow (prerequisites + Z touch-off prompt + floating run-control panel).
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class AutoSquareWizard : UserControl, IGrblConfigTab
    {
        private GrblViewModel model = null;
        private string program = string.Empty;   // last generated program (previewed in the bottom Program View)
        private bool _paramsLoaded = false;
        private GrblSettingDetails _offset = null;   // the controller's squaring-offset setting (or null)
        private int _gangedAxis = 1;                 // 0=X, 1=Y, 2=Z (derived from the offset setting id)

        // grblHAL Setting_AxisAutoSquareOffset = AxisSettingsBase(100) + 7*INCREMENT(10): X=170, Y=171, Z=172.
        // Only a ganged + auto-squared axis exposes its setting, so a present one identifies the axis.
        private const int AutoSquareOffsetBase = 170;

        public AutoSquareWizard()
        {
            InitializeComponent();
            model = DataContext as GrblViewModel;
        }

        #region IGrblConfigTab

        public GrblConfigType GrblConfigType { get { return GrblConfigType.AutoSquare; } }

        public void Activate(bool activate)
        {
            if (activate)
            {
                DetectOffsetSetting();
                UpdateComputed();
                MacroProcessor.SetActiveProgram?.Invoke("Auto square", program);   // Program View shows our program
            }
            else
                MacroProcessor.ClearActiveProgram?.Invoke();   // leaving: revert to the loaded-job view

            if (model != null)
                model.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        #endregion

        // True when the controller exposes an auto-square offset setting (the firmware is built with a ganged,
        // auto-squared axis). Used by the Tools container to hide this tab when not applicable.
        public static bool SquaringSettingExists()
        {
            return FindOffsetSetting() != null;
        }

        // Locate the squaring-offset setting by id (170-172). grblHAL only exposes it for the ganged,
        // auto-squared axis, so a present one both confirms support and identifies the axis.
        private static GrblSettingDetails FindOffsetSetting()
        {
            return GrblSettings.Settings.FirstOrDefault(s => s.Id >= AutoSquareOffsetBase && s.Id <= AutoSquareOffsetBase + 2);
        }

        private void DetectOffsetSetting()
        {
            // grblHAL REPORTS the offset for all of $170-$172 whenever any axis is squared (its availability
            // check isn't per-axis), but only the actually-ganged axis accepts a WRITE. We can't read which
            // axis that is, so the operator selects it; we target $170 + that axis.
            int id = AutoSquareOffsetBase + _gangedAxis;
            _offset = GrblSettings.Settings.FirstOrDefault(s => s.Id == id);

            if (!SquaringSettingExists())
            {
                txtAxisInfo.Text = "Measure-only: this firmware has no auto-square offset setting (not built with an auto-squared axis), so there's nothing to write. You can still drill the L and measure the gap to gauge how far out of square the gantry is - then correct it mechanically.";
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

        private static double ParseValue(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0d;
        }

        #region Per-axis travel envelope (mirrors the surfacing tool: front-left machine corner, inset)

        private static double AxisTravel(int axis)
        {
            double t = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + axis);
            return double.IsNaN(t) ? 0d : Math.Abs(t);
        }

        private static double AxisDir(int axis)
        {
            if (GrblInfo.ForceSetOrigin)
                return GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(axis)) ? 1d : -1d;
            return -1d;
        }

        private static double EnvMin(int axis)
        {
            return AxisDir(axis) > 0d ? 0d : -AxisTravel(axis);
        }

        private static double Inset()
        {
            double pulloff = GrblSettings.GetDouble(GrblSetting.HomingPulloff);
            if (double.IsNaN(pulloff)) pulloff = 0d;
            return Math.Max(5d, pulloff + 1d);
        }

        #endregion

        #region Dependency properties

        private static readonly PropertyChangedCallback OnParam = (d, e) => ((AutoSquareWizard)d).UpdateComputed();

        private static DependencyProperty Reg(string name, double def)
        {
            return DependencyProperty.Register(name, typeof(double), typeof(AutoSquareWizard), new PropertyMetadata(def, OnParam));
        }

        // Framing-square arm lengths: the X leg is drilled the blade length, the Y leg the tongue length
        // (clamped to what the travel allows). 24" blade / 16" tongue by default.
        public static readonly DependencyProperty BladeLengthProperty = Reg(nameof(BladeLength), 609.6d);  // actual 24" blade
        public double BladeLength { get { return (double)GetValue(BladeLengthProperty); } set { SetValue(BladeLengthProperty, value); } }

        public static readonly DependencyProperty TongueLengthProperty = Reg(nameof(TongueLength), 406.4d); // actual 16" tongue
        public double TongueLength { get { return (double)GetValue(TongueLengthProperty); } set { SetValue(TongueLengthProperty, value); } }

        // Drill the corner reference as two holes offset this far out along each leg (instead of one hole at the
        // vertex) so a framing square with a stress-relief radius at its inside corner still registers cleanly.
        // 0 = a single corner hole (a sharp square).
        public static readonly DependencyProperty CornerOffsetProperty = Reg(nameof(CornerOffset), 5d);
        public double CornerOffset { get { return (double)GetValue(CornerOffsetProperty); } set { SetValue(CornerOffsetProperty, value); } }

        public static readonly DependencyProperty BitDiameterProperty = Reg(nameof(BitDiameter), 1.5875d);   // 1/16"
        public double BitDiameter { get { return (double)GetValue(BitDiameterProperty); } set { SetValue(BitDiameterProperty, value); } }

        public static readonly DependencyProperty DrillDepthProperty = Reg(nameof(DrillDepth), 10d);
        public double DrillDepth { get { return (double)GetValue(DrillDepthProperty); } set { SetValue(DrillDepthProperty, value); } }

        public static readonly DependencyProperty PeckDepthProperty = Reg(nameof(PeckDepth), 3d);
        public double PeckDepth { get { return (double)GetValue(PeckDepthProperty); } set { SetValue(PeckDepthProperty, value); } }

        public static readonly DependencyProperty PlungeFeedProperty = Reg(nameof(PlungeFeed), 400d);
        public double PlungeFeed { get { return (double)GetValue(PlungeFeedProperty); } set { SetValue(PlungeFeedProperty, value); } }

        public static readonly DependencyProperty SpindleRPMProperty = Reg(nameof(SpindleRPM), 12000d);
        public double SpindleRPM { get { return (double)GetValue(SpindleRPMProperty); } set { SetValue(SpindleRPMProperty, value); } }

        public static readonly DependencyProperty SafeZProperty = Reg(nameof(SafeZ), 20d);
        public double SafeZ { get { return (double)GetValue(SafeZProperty); } set { SetValue(SafeZProperty, value); } }

        // Nudge the (centred) hole pattern to dodge existing dog/insert holes.
        public static readonly DependencyProperty XOffsetProperty = Reg(nameof(XOffset), 0d);
        public double XOffset { get { return (double)GetValue(XOffsetProperty); } set { SetValue(XOffsetProperty, value); } }

        public static readonly DependencyProperty YOffsetProperty = Reg(nameof(YOffset), 0d);
        public double YOffset { get { return (double)GetValue(YOffsetProperty); } set { SetValue(YOffsetProperty, value); } }

        // Transient (off each session): trace the hole positions at safe Z, no plunge, spindle off.
        public static readonly DependencyProperty DryRunProperty =
            DependencyProperty.Register(nameof(DryRun), typeof(bool), typeof(AutoSquareWizard), new PropertyMetadata(false, OnParam));
        public bool DryRun { get { return (bool)GetValue(DryRunProperty); } set { SetValue(DryRunProperty, value); } }

        // Skip the park + Z touch-off and reuse the existing work Z0. Auto-ticked after a run so a dry run can
        // be followed by the real cut at the same Z0 (XY origin is always recomputed - it needs no touch-off).
        public static readonly DependencyProperty ReuseZ0Property =
            DependencyProperty.Register(nameof(ReuseZ0), typeof(bool), typeof(AutoSquareWizard), new PropertyMetadata(false, OnParam));
        public bool ReuseZ0 { get { return (bool)GetValue(ReuseZ0Property); } set { SetValue(ReuseZ0Property, value); } }

        public static readonly DependencyProperty MeasuredGapProperty = Reg(nameof(MeasuredGap), 0d);
        public double MeasuredGap { get { return (double)GetValue(MeasuredGapProperty); } set { SetValue(MeasuredGapProperty, value); } }

        public static readonly DependencyProperty CurrentOffsetProperty =
            DependencyProperty.Register(nameof(CurrentOffset), typeof(double), typeof(AutoSquareWizard), new PropertyMetadata(0d, OnParam));
        public double CurrentOffset { get { return (double)GetValue(CurrentOffsetProperty); } set { SetValue(CurrentOffsetProperty, value); } }

        public static readonly DependencyProperty NewOffsetProperty =
            DependencyProperty.Register(nameof(NewOffset), typeof(double), typeof(AutoSquareWizard), new PropertyMetadata(0d));
        public double NewOffset { get { return (double)GetValue(NewOffsetProperty); } set { SetValue(NewOffsetProperty, value); } }

        #endregion

        private static string F(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        // Inputs are the framing square's ACTUAL arm lengths; the holes are drilled inset by SquareOverhang so
        // the square's edge overhangs each far hole (covers the heel / opposite-arm width plus room to sight
        // the gap). The drilled spacing is then clamped to the in-bounds travel of that axis.
        private const double SquareOverhang = 75d;

        private double XLeg()
        {
            double span = BladeLength - SquareOverhang;
            if (span <= 0d) span = BladeLength;
            double room = AxisTravel(0) - 2d * Inset();
            return room > 0d ? Math.Min(span, room) : span;
        }
        private double YLeg()
        {
            double span = TongueLength - SquareOverhang;
            if (span <= 0d) span = TongueLength;
            double room = AxisTravel(1) - 2d * Inset();
            return room > 0d ? Math.Min(span, room) : span;
        }

        // Machine-coordinate corner of the L: centred on the travel, nudged by the X/Y offset, then clamped
        // so all three holes stay inside the inset envelope.
        private double HoleOrigin(int axis, double leg)
        {
            double o = EnvMin(axis) + AxisTravel(axis) / 2d - leg / 2d + (axis == 0 ? XOffset : YOffset);
            double lo = EnvMin(axis) + Inset();
            double hi = EnvMin(axis) + AxisTravel(axis) - Inset() - leg;
            return hi > lo ? Math.Max(lo, Math.Min(hi, o)) : o;
        }

        // The ganged motors sit at the extremes of the perpendicular axis (Y-ganged -> the X rails), so the
        // squaring offset (a per-rail distance) is the measured skew angle times the full rail span.
        private double RailSpan() { return AxisTravel(_gangedAxis == 0 ? 1 : 0); }

        // The span the gap is measured over: from the near (corner-offset) X pin to the far X pin.
        private double Lever()
        {
            double l = XLeg() - (CornerOffset > 0d ? CornerOffset : 0d);
            return l > 0d ? l : XLeg();
        }

        // Convert the measured gap (between the two X pins) into a squaring-offset adjustment:
        //   skew angle = gap / span ;  offset = railSpan * angle = railSpan * gap / span.
        private double OffsetDelta()
        {
            double lever = Lever();
            if (lever <= 0d)
                return 0d;
            double railSpan = RailSpan();
            return (railSpan > 0d ? railSpan : lever) * MeasuredGap / lever;
        }

        // Above this the offset is racking the gantry a lot - on a rigid frame that binds. It means the
        // gantry is mechanically out of square (Y rails out of phase), which the offset can't substitute for.
        private const double LargeOffsetWarn = 2d;

        private bool HasRange { get { return _offset != null && _offset.Max > _offset.Min; } }

        private void UpdateComputed()
        {
            bool measureOnly = _offset == null;
            double delta = OffsetDelta();
            double raw = CurrentOffset + delta;
            NewOffset = HasRange ? Math.Max(_offset.Min, Math.Min(_offset.Max, raw)) : raw;

            bool travelSet = XLeg() > 0d && YLeg() > 0d;

            string warn = string.Empty;
            if (!travelSet)
                warn = "Set max travel ($130-$132) first - the hole positions are referenced to the homed envelope.";
            else if (!measureOnly && HasRange && raw != NewOffset)
                warn = string.Format("New offset would exceed the setting range {0}..{1} mm and was clamped - check the measurement.", F(_offset.Min), F(_offset.Max));
            else if (!measureOnly && Math.Abs(NewOffset) > LargeOffsetWarn)
                warn = string.Format("Offset {0:0.0} mm is large - that much racking can bind a rigid gantry. If it binds, the gantry is mechanically out of square (Y rails out of phase) - fix that first; the squaring offset is fine-trim only.", NewOffset);

            if (txtWarnings != null)
                txtWarnings.Text = warn;

            if (btnApply != null)
                btnApply.IsEnabled = !measureOnly && Math.Abs(MeasuredGap) > 0d && travelSet;

            if (txtSummary != null)
            {
                if (!travelSet)
                    txtSummary.Text = string.Empty;
                else if (measureOnly)
                    // No offset to write - report the squareness error itself (mm out of square across the rail span).
                    txtSummary.Text = string.Format("L legs +{0:0}/{1:0} mm  ·  gap {2:0.000} mm over {3:0} mm span → gantry ~{4:0.000} mm out of square across the {5:0} mm rail span. Measure-only (no offset setting) - correct mechanically.",
                        XLeg(), YLeg(), MeasuredGap, Lever(), delta, RailSpan());
                else
                    txtSummary.Text = string.Format("L legs +{0:0}/{1:0} mm  ·  gap {2:0.000} mm over {3:0} mm span → offset Δ {4:0.000} mm (rail span {5:0})  ·  new offset {6:0.000} mm",
                        XLeg(), YLeg(), MeasuredGap, Lever(), delta, RailSpan(), NewOffset);
            }
        }

        // Peck-drill one hole at work (x,y): plunge in PeckDepth steps to -DrillDepth, retracting above the
        // surface each peck to clear chips, then rapid back near the bottom for the next peck.
        private void Drill(List<string> lines, double x, double y, string label)
        {
            lines.Add(string.Format("({0} at X{1} Y{2})", label, F(x), F(y)));
            lines.Add(string.Format("G0 X{0} Y{1}", F(x), F(y)));
            double clear = 1d, z = 0d;
            double peck = PeckDepth > 0d ? PeckDepth : DrillDepth;
            while (z > -DrillDepth + 1e-6)
            {
                double zn = Math.Max(z - peck, -DrillDepth);
                lines.Add(string.Format("G1 Z{0} F{1}", F(zn), F(PlungeFeed)));
                lines.Add(string.Format("G0 Z{0}", F(clear)));      // retract above the surface to clear chips
                z = zn;
                if (z > -DrillDepth + 1e-6)
                    lines.Add(string.Format("G0 Z{0}", F(z + 0.5d))); // rapid back near the bottom for the next peck
            }
            lines.Add("G0 Z" + F(SafeZ));
        }

        // Build the drilling program: park front-left, touch off Z, then peck-drill the 3-hole L (corner +
        // X-leg + Y-leg). Referenced to the homed envelope (front-left = work 0,0).
        private List<string> BuildProgram()
        {
            var lines = new List<string>();
            double inset = Inset();
            double zTop = EnvMin(2) + AxisTravel(2) - inset;
            double xleg = XLeg(), yleg = YLeg();
            // Centre the L on the travel (nudged by the X/Y offset, clamped in-bounds) so the holes land on
            // clean board in the middle of the bed - not at the rails/corner.
            double ox = HoleOrigin(0, xleg), oy = HoleOrigin(1, yleg);
            bool preview = DryRun;   // visit each hole at safe Z, no plunge, spindle off
            int rpm = (int)Math.Round(SpindleRPM);

            lines.Add(string.Format("(ioSender auto-square reference holes - centred L: +{0} mm X, +{1} mm Y, {2} mm bit, {3} mm deep{4})",
                F(xleg), F(yleg), F(BitDiameter), F(DrillDepth), preview ? ", DRY RUN" : ""));

            lines.Add("(PREREQ, connected, homed, noalarm)");
            lines.Add("G90 G94 G17 G21");
            if (!ReuseZ0)
            {
                // Park at the first hole and touch off Z there (sets work Z0).
                lines.Add("G53 G0 Z" + F(zTop));
                lines.Add(string.Format("G53 G0 X{0} Y{1}", F(ox), F(oy)));   // first hole, near the bed centre
                lines.Add("(WAITIDLE)");
                lines.Add(string.Format("(MBOX, OKCANCEL, At the first hole [near the bed centre]. Fit the {0} mm drill bit and jog Z down until it just touches the surface, then click OK to set work Z0 here. Click Cancel to abort.)", F(BitDiameter)));
                lines.Add("(WAITIDLE)");
                lines.Add("G10 L20 P1 Z0");
            }
            // XY origin is computed (no touch-off needed), so (re)set it every time - this picks up any offset
            // / leg changes and lets ReuseZ0 reuse just the Z touch-off.
            lines.Add(string.Format("G10 L2 P1 X{0} Y{1}", F(ox), F(oy)));
            lines.Add("G54");
            lines.Add("G0 Z" + F(SafeZ));

            // Build the reference-hole list. Corner offset > 0 -> two corner holes (one out each leg) so a
            // framing square with an inside-corner radius still registers; the vertex itself is left undrilled.
            var holes = new List<Tuple<double, double, string>>();
            if (CornerOffset > 0d)
            {
                holes.Add(Tuple.Create(Math.Min(CornerOffset, xleg), 0d, "X-leg corner"));
                holes.Add(Tuple.Create(0d, Math.Min(CornerOffset, yleg), "Y-leg corner"));
            }
            else
                holes.Add(Tuple.Create(0d, 0d, "corner"));
            holes.Add(Tuple.Create(xleg, 0d, "X-leg far"));
            holes.Add(Tuple.Create(0d, yleg, "Y-leg far"));

            if (preview)
            {
                // Dry run: visit each hole at safe Z, spindle off, no plunge - hold at each to check clearance.
                for (int i = 0; i < holes.Count; i++)
                    DryHole(lines, holes[i].Item1, holes[i].Item2, holes[i].Item3, i + 1, holes.Count);
            }
            else
            {
                if (rpm > 0)
                    lines.Add("S" + rpm.ToString(CultureInfo.InvariantCulture) + " M3");
                foreach (var h in holes)
                    Drill(lines, h.Item1, h.Item2, h.Item3);
                if (rpm > 0)
                    lines.Add("M5");
            }

            lines.Add("G0 Z" + F(SafeZ));
            lines.Add("M30");

            return lines;
        }

        // Dry-run "visit": rapid to the hole at safe Z and hold (spindle off, no plunge) so the operator can
        // jog down and confirm the spot clears existing dog/insert holes, then lift before the next.
        private void DryHole(List<string> lines, double x, double y, string label, int n, int total)
        {
            lines.Add(string.Format("({0} at X{1} Y{2})", label, F(x), F(y)));
            lines.Add(string.Format("G0 X{0} Y{1}", F(x), F(y)));
            lines.Add(string.Format("(MBOX, OKCANCEL, Hole {0} of {1} ({2}) - jog down to confirm it clears your dog/insert holes, then OK to continue. Cancel to stop.)", n, total, label));
            lines.Add("G0 Z" + F(SafeZ));
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            switch ((string)((Button)sender).Tag)
            {
                case "generate": Generate(); break;
                case "run": Run(); break;
                case "apply": ApplyOffset(); break;
                case "home": ReHome(); break;
            }
        }

        private void cbxGangedAxis_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_paramsLoaded)
                return;   // ignore the construction / pre-load selection
            _gangedAxis = Math.Max(0, Math.Min(2, cbxGangedAxis.SelectedIndex));
            DetectOffsetSetting();
            UpdateComputed();
            SaveParams();
        }

        private void Generate()
        {
            if (model == null)
                return;
            if (XLeg() <= 0d || YLeg() <= 0d)
            {
                MessageBox.Show("Max travel ($130-$132) is not set - the hole positions are referenced to the homed envelope.",
                                "Auto square", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            program = string.Join("\r\n", BuildProgram());
            btnRun.IsEnabled = true;
            MacroProcessor.ProgramPreview?.Invoke("Auto square", program);
        }

        private void Run()
        {
            if (model == null)
                return;
            Generate();   // always rebuild from the current settings (Dry run / Reuse Z0 / offsets)
            if (string.IsNullOrWhiteSpace(program))
                return;

            bool ok = MacroProcessor.Run(model, DryRun ? "Auto square dry run" : "Auto square holes", program, true);

            // Touch-off completed (or was already reused) -> a follow-up run can reuse this Z0. So after a dry
            // run you can untick Dry run and Run again to cut at the same Z0 without touching off again.
            if (ok)
                ReuseZ0 = true;
        }

        private void ApplyOffset()
        {
            if (_offset == null)
            {
                MessageBox.Show("No auto-square offset setting on this controller.", "Auto square", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (Math.Abs(MeasuredGap) <= 0d || XLeg() <= 0d)
                return;

            double newVal = NewOffset;   // already clamped to the setting range in UpdateComputed
            string caution = Math.Abs(newVal) > LargeOffsetWarn
                ? string.Format("\n\nCAUTION: {0:0.0} mm is a lot of offset - on a rigid gantry it will rack the frame and may bind. If so, the gantry is mechanically out of square (Y rails out of phase) and must be corrected there first.", newVal)
                : string.Empty;
            if (MessageBox.Show(string.Format(
                    "Change {0} (${1}) from {2} to {3} mm, then re-home to apply?{4}\n\nIf the gantry is LESS square after re-homing, enter the gap with the opposite sign and apply again (the firmware's sign convention is build-specific).",
                    _offset.Name, _offset.Id, F(CurrentOffset), F(newVal), caution),
                    "Auto square", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            _offset.Value = newVal.ToString(CultureInfo.InvariantCulture);
            if (GrblSettings.Save())
            {
                CurrentOffset = ParseValue(_offset.Value);
                MeasuredGap = 0d;
                DetectOffsetSetting();   // refresh the readout
                UpdateComputed();
                ReHome();
            }
            else
                MessageBox.Show(string.Format("Could not write ${0}. If the controller reported \"setting not available\", the {1} axis is not the ganged / auto-squared one - select the correct Ganged axis and try again.", _offset.Id, "XYZ"[_gangedAxis]),
                                "Auto square", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ReHome()
        {
            if (model == null)
                return;
            if (MessageBox.Show("Home the machine now (required to apply the new squaring offset)?",
                    "Auto square", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                model.ExecuteCommand("$H");
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (model == null)
                model = DataContext as GrblViewModel;

            if (!_paramsLoaded)
            {
                _paramsLoaded = true;
                LoadParams();
                foreach (var dp in new[] { BladeLengthProperty, TongueLengthProperty, CornerOffsetProperty, BitDiameterProperty,
                                           DrillDepthProperty, PeckDepthProperty, PlungeFeedProperty, SpindleRPMProperty,
                                           SafeZProperty, XOffsetProperty, YOffsetProperty })
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(dp, typeof(AutoSquareWizard))
                        .AddValueChanged(this, (s, ev) => SaveParams());
            }

            if (cbxGangedAxis != null)
                cbxGangedAxis.SelectedIndex = Math.Max(0, Math.Min(2, _gangedAxis));

            DetectOffsetSetting();

            UpdateComputed();
        }

        private static string ParamsFile
        {
            get { return System.IO.Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, "AutoSquare.xml"); }
        }

        private void LoadParams()
        {
            try
            {
                if (!System.IO.File.Exists(ParamsFile))
                    return;
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(AutoSquareParams));
                using (var fs = System.IO.File.OpenRead(ParamsFile))
                {
                    var p = (AutoSquareParams)xs.Deserialize(fs);
                    BladeLength = p.BladeLength; TongueLength = p.TongueLength; CornerOffset = p.CornerOffset;
                    BitDiameter = p.BitDiameter; DrillDepth = p.DrillDepth; PeckDepth = p.PeckDepth;
                    PlungeFeed = p.PlungeFeed; SpindleRPM = p.SpindleRPM; SafeZ = p.SafeZ;
                    XOffset = p.XOffset; YOffset = p.YOffset;
                    _gangedAxis = Math.Max(0, Math.Min(2, p.GangedAxis));
                }
            }
            catch { }
        }

        private void SaveParams()
        {
            try
            {
                var p = new AutoSquareParams
                {
                    BladeLength = BladeLength, TongueLength = TongueLength, CornerOffset = CornerOffset,
                    BitDiameter = BitDiameter, DrillDepth = DrillDepth, PeckDepth = PeckDepth, PlungeFeed = PlungeFeed,
                    SpindleRPM = SpindleRPM, SafeZ = SafeZ, XOffset = XOffset, YOffset = YOffset, GangedAxis = _gangedAxis
                };
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(AutoSquareParams));
                using (var fs = System.IO.File.Create(ParamsFile))
                    xs.Serialize(fs, p);
            }
            catch { }
        }
    }

    // Persisted auto-square drilling parameters. Public for XmlSerializer.
    public class AutoSquareParams
    {
        public double BladeLength = 609.6d, TongueLength = 406.4d, CornerOffset = 5d, BitDiameter = 1.5875d,
                      DrillDepth = 10d, PeckDepth = 3d, PlungeFeed = 400d, SpindleRPM = 12000d, SafeZ = 20d,
                      XOffset = 0d, YOffset = 0d;
        public int GangedAxis = 1;   // 0=X, 1=Y, 2=Z
    }
}
