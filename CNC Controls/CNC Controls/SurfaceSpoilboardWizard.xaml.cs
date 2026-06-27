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
            }

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
                    int passes = (!preview && TotalDepth > 0d && DepthOfCut > 0d) ? (int)Math.Ceiling(TotalDepth / DepthOfCut) : 1;
                    int rows = Math.Max(2, (int)Math.Ceiling(Math.Min(WidthMM, HeightMM) / stepover) + 1);
                    string mode = OutlineOnly ? "outline (leveling check, pauses at corners)" : string.Format("{0} rows", rows);
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
            int nPasses = (!preview && TotalDepth > 0d && DepthOfCut > 0d) ? (int)Math.Ceiling(TotalDepth / DepthOfCut) : 1;

            lines.Add(string.Format("(ioSender spoilboard surfacing - {0} x {1} mm area, {2} mm bit, {3:0}% overlap{4}{5})",
                F(w), F(h), F(d), Overlap, OutlineOnly ? ", OUTLINE" : "", DryRun ? ", DRY RUN" : ""));
            lines.Add(string.Format("(stepover {0} mm, {1} depth passes, DOC {2} mm to {3} mm total, spindle {4})",
                F(stepover), nPasses, F(DepthOfCut), F(TotalDepth), preview ? "off" : ((int)Math.Round(rpm)).ToString(CultureInfo.InvariantCulture) + " rpm"));

            // Prerequisites: the machine must be homed (XY is referenced to the homed envelope).
            lines.Add("(PREREQ, connected, homed, noalarm)");

            // Park at the front-left corner (machine coords) before the touch-off so Z can be zeroed
            // somewhere reachable - the back-left home corner is awkward to reach precisely. Z is parked
            // near the top (max clearance) first, then we move XY over the corner.
            double zTop = EnvMin(2) + AxisTravel(2) - inset;             // just below the upper Z limit
            lines.Add("G90 G94 G17 G21");
            lines.Add("G53 G0 Z" + F(zTop));                             // lift to a safe machine height
            lines.Add(string.Format("G53 G0 X{0} Y{1}", F(ox), F(oy)));  // move to the front-left corner
            lines.Add("(WAITIDLE)");                                      // arrive before prompting

            // 1) Capture Z0 at the surface. Hold (nothing moves) while the operator lowers the bit onto the
            // surface; on OK, zero work Z there. WAITIDLE after OK so the jog has finished before we set Z0.
            lines.Add("(MBOX, OKCANCEL, At the FRONT-LEFT corner. Jog Z down until the bit just touches the surface, then click OK to set work Z0 here. Click Cancel to abort.)");
            lines.Add("(WAITIDLE)");
            lines.Add("G10 L20 P1 Z0");                                   // work Z0 = surface top (locked here)
            lines.Add(string.Format("G10 L2 P1 X{0} Y{1}", F(ox), F(oy))); // work XY origin = inset machine corner

            // 2) Auto-raise Z to the top so the dust boot can be fitted hands-free, then prompt. Z0 is already
            // locked, so on OK we just drop back to safe Z above it and start. WAITIDLE so the raise finishes
            // before the prompt, and again so any jog finishes before cutting.
            lines.Add("G53 G0 Z" + F(zTop));                              // raise to the top for boot fitting
            lines.Add("(WAITIDLE)");
            lines.Add("(MBOX, OKCANCEL, Z0 is set and Z is raised to the top. Fit the dust boot / do any final prep, then click OK to start. Z0 is locked. Click Cancel to abort.)");
            lines.Add("(WAITIDLE)");
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
                // Trace the full raster once at safe Z so the extents can be watched - no plunge, spindle off.
                lines.Add("(dry run - tracing the raster at safe Z, no plunge, spindle off)");
                lines.Add("G0 " + XY(path[0]));
                for (int i = 1; i < path.Count; i++)
                    lines.Add("G1 " + XY(path[i]) + " F" + F(Feed));
            }
            else for (int p = 1; p <= nPasses; p++)
            {
                double z = -Math.Min(p * DepthOfCut, TotalDepth);
                lines.Add(string.Format("(depth pass {0} of {1} at Z{2})", p, nPasses, F(z)));
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
            if (string.IsNullOrWhiteSpace(txtProgram.Text))
                Generate();
            if (string.IsNullOrWhiteSpace(txtProgram.Text))
                return;

            MacroProcessor.RunControlPanel?.Invoke(model);
            MacroProcessor.Run(model, "Surface spoilboard", txtProgram.Text, true);
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

            // Show the generated program in the preview buffer; Run streams it via the macro path (like Load Stock).
            txtProgram.Text = string.Join("\r\n", BuildProgram());
            btnRun.IsEnabled = true;
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
                    PlungeFeedProperty, DepthOfCutProperty, TotalDepthProperty, SafeZProperty, MarginProperty,
                    WidthMMProperty, HeightMMProperty, ToolNumberProperty })
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(dp, typeof(SurfaceSpoilboardWizard))
                        .AddValueChanged(this, (s, ev) => SaveParams());
            }

            // Default / clamp the area to the in-bounds travel envelope.
            DefaultArea();

            txtWarnings.Text = string.Empty;
            txtProgram.Text =
                "Surfaces (flattens) the spoilboard with a raster of overlapping passes, referenced to the homed machine envelope.\n\n" +
                "1. Fit the surfacing bit. Enter its diameter and rated max RPM.\n" +
                "2. Set spindle RPM, overlap %, feed/plunge, depth of cut and total skim depth. The area defaults to the full travel envelope less the Edge margin - reduce Width/Height to surface a smaller region (anchored at the home-side corner).\n" +
                "3. Press Generate to build the program (it appears here). Press Run to start - a confirmation and the floating run-control panel appear.\n" +
                "4. The machine must be homed. It first moves to the FRONT-LEFT corner (easier to reach than the home corner). Prompt 1: jog Z down until the bit just touches the surface, click OK - work Z0 is locked there (XY is set automatically). Z then raises to the top automatically. Prompt 2: fit the dust boot / do any final prep, then click OK - it drops back to safe Z above the locked Z0 and starts.\n" +
                "5. It rasters across the envelope, cutting Z0 minus the depth of cut each pass down to the total depth. Because XY is machine-referenced, it cannot overrun a soft limit.\n\n" +
                "Notes: stepover = bit diameter x (1 - overlap). Spindle RPM 0 = 70% of the bit's max RPM. Tool \"Loaded\" skips the tool change; \"Prompt\" issues M6. Edge margin keeps the raster clear of the travel limits (on top of the homing pull-off). Clear any clamps/screws from the raster area first.\n" +
                "Outline only = a LEVELING CHECK: it moves to each corner, lowers to the Z0 plane and pauses for a dial-gauge reading (spindle off) so you can see how far out of level the board is - fit a dial indicator in place of the cutter and zero it at the front-left reference corner. Dry run traces the full raster at safe Z (no plunge, spindle off) to verify extents. An uneven board only cleans fully if you zero Z on its highest point or set the total depth deep enough.";

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
                    TotalDepth = p.TotalDepth; SafeZ = p.SafeZ; Margin = p.Margin; WidthMM = p.WidthMM; HeightMM = p.HeightMM;
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
                    SafeZ = SafeZ, Margin = Margin, WidthMM = WidthMM, HeightMM = HeightMM, ToolNumber = ToolNumber
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
                      PlungeFeed = 500d, DepthOfCut = 0.3d, TotalDepth = 0.3d, SafeZ = 20d, Margin = 5d,
                      WidthMM = 0d, HeightMM = 0d, ToolNumber = 0d;
    }
}
