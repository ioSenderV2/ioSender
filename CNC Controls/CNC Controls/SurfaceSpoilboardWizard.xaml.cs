/*
 * SurfaceSpoilboardWizard.xaml.cs - part of CNC Controls library
 *
 * Spoilboard surfacing generator: gather bit/area/feed parameters, build a boustrophedon
 * facing program and load it into the program view (run from the main view).
 *
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for SurfaceSpoilboardWizard.xaml
    /// </summary>
    public partial class SurfaceSpoilboardWizard : UserControl, IGrblConfigTab
    {
        private GrblViewModel model = null;
        private string program = string.Empty;   // last generated program (previewed in the bottom Program View)
        private bool _paramsLoaded = false;

        public SurfaceSpoilboardWizard()
        {
            InitializeComponent();
            model = DataContext as GrblViewModel;
        }

        #region Methods required by IGrblConfigTab

        public GrblConfigType GrblConfigType { get { return GrblConfigType.SurfaceSpoilboard; } }

        public void Activate(bool activate)
        {
            if (activate)
            {
                DefaultArea();   // size the area to the in-bounds travel envelope (less margins)
                UpdateSummary();
                MacroProcessor.SetActiveProgram?.Invoke("Surface spoilboard", program);   // Program View shows our program
            }
            else
                MacroProcessor.ClearActiveProgram?.Invoke();   // leaving: revert to the loaded-job view

            if (model != null)
                model.Poller.SetState(activate ? AppConfig.Settings.Base.PollInterval : 0);
        }

        #endregion

        // Per-axis max travel ($130 = X, $131 = Y), absolute value; 0 if unknown.
        private static double AxisTravel(int axisIndex)
        {
            double t = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + axisIndex);
            return double.IsNaN(t) ? 0d : Math.Abs(t);
        }

        // +1 if the axis travels in the +machine direction away from home, else -1. Mirrors the
        // click-to-jog limiter (Renderer.AxisDir): with force-set-origin ($22 bit3) grbl puts the
        // home corner at machine 0 and the working area on the side given by the $23 homing-dir mask;
        // without it grbl keeps all travel negative (MPos <= 0).
        private static double AxisDir(int axis)
        {
            if (GrblInfo.ForceSetOrigin)
                return GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(axis)) ? 1d : -1d;
            return -1d;
        }

        // Machine-coordinate minimum (lower) corner of the travel envelope on one axis.
        private static double EnvMin(int axis)
        {
            return AxisDir(axis) > 0d ? 0d : -AxisTravel(axis);
        }

        // Safe inset from each machine limit: the larger of the user edge margin and the homing
        // pull-off (+1 mm) so the raster never grazes a limit switch at the home end.
        private double Inset()
        {
            double pulloff = GrblSettings.GetDouble(GrblSetting.HomingPulloff);
            if (double.IsNaN(pulloff)) pulloff = 0d;
            return Math.Max(Margin, pulloff + 1d);
        }

        // Largest in-bounds area on one axis: full travel less the inset at both ends.
        private double MaxArea(int axis)
        {
            return Math.Max(0d, AxisTravel(axis) - 2d * Inset());
        }

        // Size the area to the in-bounds envelope: fill it when unset, and clamp a stale/oversized
        // value (e.g. an old full-travel default) back down so the raster always fits the machine.
        private void DefaultArea()
        {
            double maxW = MaxArea(0), maxH = MaxArea(1);
            if (maxW > 0d && (WidthMM <= 0d || WidthMM > maxW))
                WidthMM = maxW;
            if (maxH > 0d && (HeightMM <= 0d || HeightMM > maxH))
                HeightMM = maxH;
        }

        #region Dependency properties

        private static readonly PropertyChangedCallback OnParam = (d, e) => ((SurfaceSpoilboardWizard)d).UpdateSummary();

        private static DependencyProperty Reg(string name, double def)
        {
            return DependencyProperty.Register(name, typeof(double), typeof(SurfaceSpoilboardWizard), new PropertyMetadata(def, OnParam));
        }

        public static readonly DependencyProperty BitDiameterProperty = Reg(nameof(BitDiameter), 25.0d);
        public double BitDiameter { get { return (double)GetValue(BitDiameterProperty); } set { SetValue(BitDiameterProperty, value); } }

        public static readonly DependencyProperty BitMaxRPMProperty = Reg(nameof(BitMaxRPM), 18000d);
        public double BitMaxRPM { get { return (double)GetValue(BitMaxRPMProperty); } set { SetValue(BitMaxRPMProperty, value); } }

        public static readonly DependencyProperty SpindleRPMProperty = Reg(nameof(SpindleRPM), 15000d);
        public double SpindleRPM { get { return (double)GetValue(SpindleRPMProperty); } set { SetValue(SpindleRPMProperty, value); } }

        public static readonly DependencyProperty OverlapProperty = Reg(nameof(Overlap), 40d);
        public double Overlap { get { return (double)GetValue(OverlapProperty); } set { SetValue(OverlapProperty, value); } }

        public static readonly DependencyProperty FeedProperty = Reg(nameof(Feed), 2000d);
        public double Feed { get { return (double)GetValue(FeedProperty); } set { SetValue(FeedProperty, value); } }

        public static readonly DependencyProperty PlungeFeedProperty = Reg(nameof(PlungeFeed), 500d);
        public double PlungeFeed { get { return (double)GetValue(PlungeFeedProperty); } set { SetValue(PlungeFeedProperty, value); } }

        public static readonly DependencyProperty DepthOfCutProperty = Reg(nameof(DepthOfCut), 0.3d);
        public double DepthOfCut { get { return (double)GetValue(DepthOfCutProperty); } set { SetValue(DepthOfCutProperty, value); } }

        public static readonly DependencyProperty TotalDepthProperty = Reg(nameof(TotalDepth), 0.3d);
        public double TotalDepth { get { return (double)GetValue(TotalDepthProperty); } set { SetValue(TotalDepthProperty, value); } }

        // Optional light final pass: roughing steps by DepthOfCut down to (TotalDepth - FinishDepth), then one
        // pass removes the last FinishDepth for a clean finish. 0 = no separate finish pass (old behaviour).
        public static readonly DependencyProperty FinishDepthProperty = Reg(nameof(FinishDepth), 0d);
        public double FinishDepth { get { return (double)GetValue(FinishDepthProperty); } set { SetValue(FinishDepthProperty, value); } }

        public static readonly DependencyProperty SafeZProperty = Reg(nameof(SafeZ), 20d);
        public double SafeZ { get { return (double)GetValue(SafeZProperty); } set { SetValue(SafeZProperty, value); } }

        // Edge clearance kept between the raster and each machine travel limit (in addition to the homing pull-off).
        public static readonly DependencyProperty MarginProperty = Reg(nameof(Margin), 5d);
        public double Margin { get { return (double)GetValue(MarginProperty); } set { SetValue(MarginProperty, value); } }

        // Area is WidthMM/HeightMM (FrameworkElement already owns Width/Height); the program math uses these directly.
        public static readonly DependencyProperty WidthMMProperty = Reg(nameof(WidthMM), 0d);
        public double WidthMM { get { return (double)GetValue(WidthMMProperty); } set { SetValue(WidthMMProperty, value); } }

        public static readonly DependencyProperty HeightMMProperty = Reg(nameof(HeightMM), 0d);
        public double HeightMM { get { return (double)GetValue(HeightMMProperty); } set { SetValue(HeightMMProperty, value); } }

        public static readonly DependencyProperty ToolNumberProperty = Reg(nameof(ToolNumber), 0d);
        public double ToolNumber { get { return (double)GetValue(ToolNumberProperty); } set { SetValue(ToolNumberProperty, value); } }

        // Transient run modes (not persisted - default off each session so a real job is never accidentally dry/outline).
        public static readonly DependencyProperty OutlineOnlyProperty = DependencyProperty.Register(nameof(OutlineOnly), typeof(bool), typeof(SurfaceSpoilboardWizard), new PropertyMetadata(false, OnParam));
        public bool OutlineOnly { get { return (bool)GetValue(OutlineOnlyProperty); } set { SetValue(OutlineOnlyProperty, value); } }

        public static readonly DependencyProperty DryRunProperty = DependencyProperty.Register(nameof(DryRun), typeof(bool), typeof(SurfaceSpoilboardWizard), new PropertyMetadata(false, OnParam));
        public bool DryRun { get { return (bool)GetValue(DryRunProperty); } set { SetValue(DryRunProperty, value); } }

        // Skip the park + Z touch-off + dust-boot prompts and reuse the existing work Z0 (same bit / setup).
        // Auto-ticked after a run so a dry run can be followed by the real cut at the same Z0; the XY origin is
        // machine-referenced and always re-set, so only the Z touch-off is reused.
        public static readonly DependencyProperty ReuseZ0Property = DependencyProperty.Register(nameof(ReuseZ0), typeof(bool), typeof(SurfaceSpoilboardWizard), new PropertyMetadata(false, OnParam));
        public bool ReuseZ0 { get { return (bool)GetValue(ReuseZ0Property); } set { SetValue(ReuseZ0Property, value); } }

        public static readonly DependencyProperty StepoverProperty = DependencyProperty.Register(nameof(Stepover), typeof(double), typeof(SurfaceSpoilboardWizard), new PropertyMetadata(0d));
        public double Stepover { get { return (double)GetValue(StepoverProperty); } set { SetValue(StepoverProperty, value); } }

        #endregion

        private static string F(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private double StepoverMM()
        {
            return BitDiameter * (1d - Overlap / 100d);
        }

        // The Z depth (negative) of each cutting pass: rough by DepthOfCut down to (TotalDepth - FinishDepth),
        // then a final light pass at TotalDepth when a Finish pass is set. Returns at least one pass.
        private List<double> PassDepths()
        {
            var depths = new List<double>();
            double total = TotalDepth;
            if (total <= 0d)                       // no depth set -> a single skim at the Z0 plane
            {
                depths.Add(0d);
                return depths;
            }
            double doc = DepthOfCut > 0d ? DepthOfCut : total;
            bool hasFinish = FinishDepth > 0d && FinishDepth < total;
            double rough = hasFinish ? total - FinishDepth : total;

            double z = 0d;
            while (z < rough - 1e-6)
            {
                z = Math.Min(z + doc, rough);
                depths.Add(-z);
            }
            if (hasFinish)
                depths.Add(-total);                // light final pass removes the last FinishDepth
            else if (depths.Count == 0)
                depths.Add(-total);                // rough == 0 edge case: a single pass to total
            return depths;
        }

        // Refresh the computed stepover, the run summary and any warning. Called on every parameter change.
        private void UpdateSummary()
        {
            double stepover = StepoverMM();
            Stepover = stepover;

            bool preview = DryRun || OutlineOnly;   // both are spindle-off, no-plunge safe-Z traces
            string warn = string.Empty;

            double maxW = MaxArea(0), maxH = MaxArea(1);

            if (BitDiameter <= 0d)
                warn = "Bit diameter must be greater than 0.";
            else if (stepover <= 0d)
                warn = "Overlap is too high - the stepover is zero or negative. Reduce overlap below 100%.";
            else if (maxW <= 0d || maxH <= 0d)
                warn = "Set max travel ($130-$132) first - the raster is referenced to the homed machine envelope.";
            else if (WidthMM <= 0d || HeightMM <= 0d)
                warn = "Set the area width and height (they default to the machine travel envelope).";
            else if (WidthMM > maxW || HeightMM > maxH)
                warn = string.Format("Area exceeds the in-bounds envelope ({0:0} x {1:0} mm = travel less {2:0.0} mm margins) - it will be clamped.", maxW, maxH, Inset());
            else if (!preview && SpindleRPM > BitMaxRPM && BitMaxRPM > 0d)
                warn = string.Format("Spindle RPM ({0:0}) exceeds the bit's rated max RPM ({1:0}) - reduce RPM.", SpindleRPM, BitMaxRPM);

            if (txtWarnings != null)
                txtWarnings.Text = warn;

            if (txtSummary != null)
            {
                if (stepover > 0d && WidthMM > 0d && HeightMM > 0d)
                {
                    int passes = preview ? 1 : PassDepths().Count;
                    int rows = Math.Max(2, (int)Math.Ceiling(Math.Min(WidthMM, HeightMM) / stepover) + 1);
                    string mode = OutlineOnly ? "outline (leveling check, pauses at corners)"
                                : DryRun ? "perimeter (dry run)"
                                : string.Format("{0} rows", rows);
                    string depth = preview ? "no plunge" : string.Format("{0} depth pass{1}", passes, passes == 1 ? "" : "es");
                    string rpm = preview ? "spindle off" : string.Format("{0:0} rpm", EffectiveRPM());
                    txtSummary.Text = string.Format("Area {0:0} x {1:0} mm · stepover {2:0.0} mm · {3} · {4} · {5}",
                        WidthMM, HeightMM, stepover, mode, depth, rpm);
                }
                else
                    txtSummary.Text = string.Empty;
            }
        }

        // Build the surfacing program: a serpentine (boustrophedon) raster over Width x Height, stepping
        // by the stepover across the shorter axis and cutting along the longer one, repeated per depth pass.
        private List<string> BuildProgram()
        {
            var lines = new List<string>();

            double d = BitDiameter;
            double stepover = StepoverMM();
            double w = Math.Min(WidthMM, MaxArea(0)), h = Math.Min(HeightMM, MaxArea(1));   // clamped in-bounds
            bool preview = DryRun || OutlineOnly;   // no plunge, spindle off - trace at safe Z to verify extents
            double rpm = EffectiveRPM();
            int tool = (int)Math.Round(ToolNumber);

            // Machine-coordinate origin of the raster: the min-world corner of the travel envelope,
            // inset by the safe margin. The operator touches off Z only (the surface top); ioSender
            // references XY to the homed envelope, so the raster can never overrun a soft limit no
            // matter where the bit was when Z was zeroed.
            double inset = Inset();
            double ox = EnvMin(0) + inset, oy = EnvMin(1) + inset;

            // The XY path to follow: the area perimeter (outline) or the serpentine raster.
            var path = OutlineOnly ? OutlinePath(w, h) : RasterPath(w, h, stepover);
            var depths = PassDepths();
            int nPasses = depths.Count;

            lines.Add(string.Format("(ioSender spoilboard surfacing - {0} x {1} mm area, {2} mm bit, {3:0}% overlap{4}{5})",
                F(w), F(h), F(d), Overlap, OutlineOnly ? ", OUTLINE" : "", DryRun ? ", DRY RUN" : ""));
            lines.Add(string.Format("(stepover {0} mm, {1} depth passes, DOC {2} mm to {3} mm total{4}, spindle {5})",
                F(stepover), nPasses, F(DepthOfCut), F(TotalDepth), FinishDepth > 0d && FinishDepth < TotalDepth ? ", finish " + F(FinishDepth) + " mm" : "",
                preview ? "off" : ((int)Math.Round(rpm)).ToString(CultureInfo.InvariantCulture) + " rpm"));

            // Prerequisites: the machine must be homed (XY is referenced to the homed envelope).
            lines.Add("(PREREQ, connected, homed, noalarm)");

            double zTop = EnvMin(2) + AxisTravel(2) - inset;             // just below the upper Z limit
            lines.Add("G90 G94 G17 G21");

            if (!ReuseZ0)
            {
                // Park at the front-left corner (machine coords) before the touch-off so Z can be zeroed
                // somewhere reachable - the back-left home corner is awkward to reach precisely. Z is parked
                // near the top (max clearance) first, then we move XY over the corner.
                lines.Add("G53 G0 Z" + F(zTop));                             // lift to a safe machine height
                lines.Add(string.Format("G53 G0 X{0} Y{1}", F(ox), F(oy)));  // move to the front-left corner
                lines.Add("(WAITIDLE)");                                      // arrive before prompting

                // 1) Capture Z0 at the surface. Hold (nothing moves) while the operator lowers the bit onto the
                // surface; on OK, zero work Z there. WAITIDLE after OK so the jog has finished before we set Z0.
                lines.Add("(MBOX, OKCANCEL, Jog to the HIGHEST point of the board [keyboard/jog moves X, Y and Z] and lower the bit until it just touches, then click OK to set work Z0 to that height. Where you touch off does not matter - XY is referenced to the machine corner automatically. Click Cancel to abort.)");
                lines.Add("(WAITIDLE)");
                lines.Add("G10 L20 P1 Z0");                                   // work Z0 = surface top (locked here)

                // 2) Auto-raise Z to the top so the dust boot can be fitted hands-free, then prompt. Z0 is already
                // locked, so on OK we just drop back to safe Z above it and start. WAITIDLE so the raise finishes
                // before the prompt, and again so any jog finishes before cutting.
                lines.Add("G53 G0 Z" + F(zTop));                              // raise to the top for boot fitting
                lines.Add("(WAITIDLE)");
                lines.Add("(MBOX, OKCANCEL, Z0 is set and Z is raised to the top. Fit the dust boot / do any final prep, then click OK to start. Z0 is locked. Click Cancel to abort.)");
                lines.Add("(WAITIDLE)");
            }
            else
            {
                // Reuse the work Z0 from the last run: skip the park + Z touch-off, but STILL raise Z to the
                // top and gate on a readiness prompt - the previous run may have been an Outline/Dry run with a
                // dial indicator (not the cutter) fitted and no dust boot, so never start cutting unprompted.
                lines.Add("G53 G0 Z" + F(zTop));
                lines.Add("(WAITIDLE)");
                lines.Add("(MBOX, OKCANCEL, Reusing the existing work Z0. Z is raised to the top - fit the CUTTER and the dust boot, clear the area, then click OK to start. Click Cancel to abort.)");
                lines.Add("(WAITIDLE)");
            }

            // XY origin is machine-referenced and computed (no touch-off needed), so always (re)set it - this
            // picks up any area / margin change and lets Reuse Z0 reuse just the Z touch-off.
            lines.Add(string.Format("G10 L2 P1 X{0} Y{1}", F(ox), F(oy))); // work XY origin = inset machine corner
            lines.Add("G54");
            lines.Add("G0 Z" + F(SafeZ));                                 // drop back to safe Z above the locked Z0
            if (!preview && tool > 0)
                lines.Add("M6 T" + tool.ToString(CultureInfo.InvariantCulture));
            if (!preview && rpm > 0d)
                lines.Add("S" + ((int)Math.Round(rpm)).ToString(CultureInfo.InvariantCulture) + " M3");

            if (OutlineOnly)
            {
                // Leveling check: visit each corner, drop to the Z0 reference plane and hold for a dial-gauge
                // reading, then lift. Spindle off - fit a dial indicator in place of the cutter, zero it at
                // the front-left (reference) corner, then read how far each other corner sits above/below.
                lines.Add("(outline - leveling check: pausing at each corner at the Z0 plane, spindle off)");
                string[] corner = { "front-left (reference)", "front-right", "back-right", "back-left" };
                for (int i = 0; i < 4; i++)
                {
                    lines.Add("G0 " + XY(path[i]));                         // rapid to the corner at safe Z
                    lines.Add(string.Format("G1 Z0 F{0}", F(PlungeFeed)));  // down to the Z0 reference plane
                    lines.Add(string.Format("(MBOX, OKCANCEL, Corner {0} of 4 - {1}. Read the gauge to see how far this corner sits above/below the front-left reference, then click OK to continue. Cancel to stop.)", i + 1, corner[i]));
                    lines.Add("G0 Z" + F(SafeZ));                           // lift before moving on
                }
                lines.Add("G0 " + XY(path[0]));                             // park back at the start corner
            }
            else if (DryRun)
            {
                // Abbreviated dry run: trace just the area PERIMETER at safe Z (spindle off, no plunge). It
                // streams (G1 moves, so Feed Hold / Stop work) and finishes in a few moves, so you can let it
                // COMPLETE - which preserves home and Z0 - then untick Dry run and Run for real reusing Z0,
                // with no need to abort mid-run. The raster always fits inside this perimeter.
                var rect = OutlinePath(w, h);
                lines.Add("(dry run - tracing the area perimeter at safe Z, no plunge, spindle off)");
                lines.Add("G0 " + XY(rect[0]));
                for (int i = 1; i < rect.Count; i++)
                    lines.Add("G1 " + XY(rect[i]) + " F" + F(Feed));
            }
            else for (int p = 0; p < nPasses; p++)
            {
                double z = depths[p];
                bool isFinish = FinishDepth > 0d && FinishDepth < TotalDepth && p == nPasses - 1;
                lines.Add(string.Format("(depth pass {0} of {1} at Z{2}{3})", p + 1, nPasses, F(z), isFinish ? " - finish" : ""));
                lines.Add("G0 " + XY(path[0]));                       // rapid to the start corner at safe Z
                lines.Add(string.Format("G1 Z{0} F{1}", F(z), F(PlungeFeed)));
                for (int i = 1; i < path.Count; i++)
                    lines.Add("G1 " + XY(path[i]) + " F" + F(Feed));
                lines.Add("G0 Z" + F(SafeZ));
            }

            if (!preview && rpm > 0d)
                lines.Add("M5");
            lines.Add("G0 Z" + F(SafeZ));
            lines.Add("M30");

            return lines;
        }

        // Spindle RPM to command: the entered value, or 70% of the bit's rated max when left at 0.
        private double EffectiveRPM()
        {
            return SpindleRPM > 0d ? SpindleRPM : 0.70d * BitMaxRPM;
        }

        // Serpentine (boustrophedon) raster: step across the shorter axis by the stepover, cut along the
        // longer one, alternating direction. Returns the XY polyline (first point is the plunge corner).
        private static List<double[]> RasterPath(double w, double h, double stepover)
        {
            var pts = new List<double[]>();
            bool longIsX = w >= h;
            double longLen = longIsX ? w : h;
            double crossLen = longIsX ? h : w;
            int nRows = Math.Max(2, (int)Math.Ceiling(crossLen / stepover) + 1);
            double step = crossLen / (nRows - 1);   // even spacing, <= requested stepover, last row exactly at the far edge

            Add(pts, longIsX, 0d, 0d);
            double curLong = 0d;
            for (int i = 0; i < nRows; i++)
            {
                double cross = Math.Min(i * step, crossLen);
                if (i > 0)
                    Add(pts, longIsX, curLong, cross);          // step across to this row
                double endLong = curLong == 0d ? longLen : 0d;  // cut along the long axis, alternating direction
                Add(pts, longIsX, endLong, cross);
                curLong = endLong;
            }
            return pts;
        }

        // Closed rectangle perimeter of the area, starting at the front-left corner.
        private static List<double[]> OutlinePath(double w, double h)
        {
            return new List<double[]> {
                new[] { 0d, 0d }, new[] { w, 0d }, new[] { w, h }, new[] { 0d, h }, new[] { 0d, 0d }
            };
        }

        private static void Add(List<double[]> pts, bool longIsX, double longVal, double crossVal)
        {
            pts.Add(longIsX ? new[] { longVal, crossVal } : new[] { crossVal, longVal });
        }

        private static string XY(double[] p)
        {
            return string.Format("X{0} Y{1}", F(p[0]), F(p[1]));
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            switch ((string)((Button)sender).Tag)
            {
                case "generate": Generate(); break;
                case "run": Run(); break;
            }
        }

        // Run the buffered program via the macro path (like Load Stock): the run-control panel floats, then the
        // program streams - its (PREREQ)/(MBOX)/(WAITIDLE) directives confirm state and prompt to set work zero.
        private void Run()
        {
            if (model == null)
                return;
            if (string.IsNullOrWhiteSpace(program))
                Generate();
            if (string.IsNullOrWhiteSpace(program))
                return;

            bool ok = MacroProcessor.Run(model, "Surface spoilboard", program, true);

            // Touch-off completed (or was already reused) -> a follow-up run can reuse this Z0. So after a dry run
            // or outline check you can untick Dry run / Outline only and Run again to cut at the same Z0.
            if (ok)
                ReuseZ0 = true;
        }

        private void Generate()
        {
            if (model == null)
                return;

            if (StepoverMM() <= 0d)
            {
                MessageBox.Show("Overlap is too high - the stepover is zero or negative. Reduce overlap below 100%.",
                                "Surface spoilboard", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (MaxArea(0) <= 0d || MaxArea(1) <= 0d)
            {
                MessageBox.Show("Max travel ($130-$132) is not set. The raster is referenced to the homed machine envelope, so travel must be known first.",
                                "Surface spoilboard", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            DefaultArea();   // fill / clamp the area to the in-bounds envelope
            if (WidthMM <= 0d || HeightMM <= 0d)
            {
                MessageBox.Show("Set the area width and height first (they default to the machine travel envelope).",
                                "Surface spoilboard", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (!(DryRun || OutlineOnly) && SpindleRPM > BitMaxRPM && BitMaxRPM > 0d &&
                MessageBox.Show(string.Format("Spindle RPM ({0:0}) exceeds the bit's rated max RPM ({1:0}).\n\nGenerate anyway?", SpindleRPM, BitMaxRPM),
                                "Surface spoilboard", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                return;

            // Build the program and preview it in the bottom Program View (pops it open); Run streams it.
            program = string.Join("\r\n", BuildProgram());
            btnRun.IsEnabled = true;
            MacroProcessor.ProgramPreview?.Invoke("Surface spoilboard", program);
        }

        private void cbxTool_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Loaded (index 0) = no tool change; Prompt (index 1) = issue M6 to load the bit (ToolNumber > 0).
            ToolNumber = cbxTool.SelectedIndex == 1 ? 1d : 0d;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (model == null)
                model = DataContext as GrblViewModel;

            if (!_paramsLoaded)   // restore the saved parameters once, then persist on every change
            {
                _paramsLoaded = true;
                LoadParams();
                cbxTool.SelectedIndex = ToolNumber > 0d ? 1 : 0;   // Loaded / Prompt
                foreach (var dp in new[] {
                    BitDiameterProperty, BitMaxRPMProperty, SpindleRPMProperty, OverlapProperty, FeedProperty,
                    PlungeFeedProperty, DepthOfCutProperty, TotalDepthProperty, FinishDepthProperty, SafeZProperty, MarginProperty,
                    WidthMMProperty, HeightMMProperty, ToolNumberProperty })
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(dp, typeof(SurfaceSpoilboardWizard))
                        .AddValueChanged(this, (s, ev) => SaveParams());
            }

            // Default / clamp the area to the in-bounds travel envelope.
            DefaultArea();

            txtWarnings.Text = string.Empty;

            UpdateSummary();
        }

        private static string ParamsFile
        {
            get { return System.IO.Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, "SurfaceSpoilboard.xml"); }
        }

        private void LoadParams()
        {
            try
            {
                if (!System.IO.File.Exists(ParamsFile))
                    return;
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(SurfaceParams));
                using (var fs = System.IO.File.OpenRead(ParamsFile))
                {
                    var p = (SurfaceParams)xs.Deserialize(fs);
                    BitDiameter = p.BitDiameter; BitMaxRPM = p.BitMaxRPM; SpindleRPM = p.SpindleRPM;
                    Overlap = p.Overlap; Feed = p.Feed; PlungeFeed = p.PlungeFeed; DepthOfCut = p.DepthOfCut;
                    TotalDepth = p.TotalDepth; FinishDepth = p.FinishDepth; SafeZ = p.SafeZ; Margin = p.Margin; WidthMM = p.WidthMM; HeightMM = p.HeightMM;
                    ToolNumber = p.ToolNumber;
                }
            }
            catch { /* ignore - use the defaults */ }
        }

        private void SaveParams()
        {
            try
            {
                var p = new SurfaceParams
                {
                    BitDiameter = BitDiameter, BitMaxRPM = BitMaxRPM, SpindleRPM = SpindleRPM, Overlap = Overlap,
                    Feed = Feed, PlungeFeed = PlungeFeed, DepthOfCut = DepthOfCut, TotalDepth = TotalDepth,
                    FinishDepth = FinishDepth, SafeZ = SafeZ, Margin = Margin, WidthMM = WidthMM, HeightMM = HeightMM, ToolNumber = ToolNumber
                };
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(SurfaceParams));
                using (var fs = System.IO.File.Create(ParamsFile))
                    xs.Serialize(fs, p);
            }
            catch { }
        }
    }

    // Persisted spoilboard-surfacing parameters. Public for XmlSerializer.
    public class SurfaceParams
    {
        public double BitDiameter = 25.0d, BitMaxRPM = 18000d, SpindleRPM = 15000d, Overlap = 40d, Feed = 2000d,
                      PlungeFeed = 500d, DepthOfCut = 0.3d, TotalDepth = 0.3d, FinishDepth = 0d, SafeZ = 20d, Margin = 5d,
                      WidthMM = 0d, HeightMM = 0d, ToolNumber = 0d;
    }
}
