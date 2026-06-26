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
                // Default the area to the machine's travel envelope the first time the tab is shown.
                if (WidthMM <= 0d)
                    WidthMM = AxisTravel(0);
                if (HeightMM <= 0d)
                    HeightMM = AxisTravel(1);
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

        public static readonly DependencyProperty SafeZProperty = Reg(nameof(SafeZ), 5d);
        public double SafeZ { get { return (double)GetValue(SafeZProperty); } set { SetValue(SafeZProperty, value); } }

        // Area is WidthMM/HeightMM (FrameworkElement already owns Width/Height); the program math uses these directly.
        public static readonly DependencyProperty WidthMMProperty = Reg(nameof(WidthMM), 0d);
        public double WidthMM { get { return (double)GetValue(WidthMMProperty); } set { SetValue(WidthMMProperty, value); } }

        public static readonly DependencyProperty HeightMMProperty = Reg(nameof(HeightMM), 0d);
        public double HeightMM { get { return (double)GetValue(HeightMMProperty); } set { SetValue(HeightMMProperty, value); } }

        public static readonly DependencyProperty ToolNumberProperty = Reg(nameof(ToolNumber), 0d);
        public double ToolNumber { get { return (double)GetValue(ToolNumberProperty); } set { SetValue(ToolNumberProperty, value); } }

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

            string warn = string.Empty;

            if (BitDiameter <= 0d)
                warn = "Bit diameter must be greater than 0.";
            else if (stepover <= 0d)
                warn = "Overlap is too high - the stepover is zero or negative. Reduce overlap below 100%.";
            else if (WidthMM <= 0d || HeightMM <= 0d)
                warn = "Set the area width and height (they default to the machine travel envelope).";
            else if (SpindleRPM > BitMaxRPM && BitMaxRPM > 0d)
                warn = string.Format("Spindle RPM ({0:0}) exceeds the bit's rated max RPM ({1:0}) - reduce RPM.", SpindleRPM, BitMaxRPM);

            if (txtWarnings != null)
                txtWarnings.Text = warn;

            if (txtSummary != null)
            {
                if (stepover > 0d && WidthMM > 0d && HeightMM > 0d)
                {
                    double crossLen = Math.Min(WidthMM, HeightMM), longLen = Math.Max(WidthMM, HeightMM);
                    int rows = Math.Max(2, (int)Math.Ceiling(crossLen / stepover) + 1);
                    int passes = TotalDepth > 0d && DepthOfCut > 0d ? (int)Math.Ceiling(TotalDepth / DepthOfCut) : 1;
                    double metres = (double)rows * longLen * passes / 1000d;
                    txtSummary.Text = string.Format(
                        "Area {0:0} x {1:0} mm · stepover {2:0.0} mm · {3} rows · {4} depth pass{5} · ~{6:0.0} m of cutting",
                        WidthMM, HeightMM, stepover, rows, passes, passes == 1 ? "" : "es", metres);
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
            double w = WidthMM, h = HeightMM;
            bool longIsX = w >= h;
            double longLen = longIsX ? w : h;
            double crossLen = longIsX ? h : w;

            int nRows = Math.Max(2, (int)Math.Ceiling(crossLen / stepover) + 1);
            double step = crossLen / (nRows - 1);   // even spacing, <= requested stepover, last row exactly at the far edge
            int nPasses = TotalDepth > 0d && DepthOfCut > 0d ? (int)Math.Ceiling(TotalDepth / DepthOfCut) : 1;
            int tool = (int)Math.Round(ToolNumber);

            lines.Add(string.Format("(ioSender spoilboard surfacing - {0} x {1} mm area, {2} mm bit, {3:0}% overlap)", F(w), F(h), F(d), Overlap));
            lines.Add(string.Format("(stepover {0} mm, {1} rows, {2} depth pass(es), DOC {3} mm to {4} mm total)", F(step), nRows, nPasses, F(DepthOfCut), F(TotalDepth)));
            lines.Add("(Jog to the front-left corner, touch the bit to the surface and zero work XYZ there - Z0 = surface top.)");
            // Prolog - mirrors the other generated programs (units, plane, absolute, machine safe-Z).
            lines.Add("G90 G94");
            lines.Add("G17");
            lines.Add("G21");
            lines.Add("G53 G0 Z0");
            if (tool > 0)
                lines.Add("M6 T" + tool.ToString(CultureInfo.InvariantCulture));
            if (SpindleRPM > 0d)
                lines.Add("S" + ((int)Math.Round(SpindleRPM)).ToString(CultureInfo.InvariantCulture) + " M3");
            lines.Add("G17 G90 G94");
            lines.Add("G54");
            lines.Add("G0 Z" + F(SafeZ));

            for (int p = 1; p <= nPasses; p++)
            {
                double z = -Math.Min(p * DepthOfCut, TotalDepth);
                lines.Add(string.Format("(depth pass {0} of {1} at Z{2})", p, nPasses, F(z)));
                lines.Add("G0 " + XY(longIsX, 0d, 0d));               // rapid to the start corner at safe Z
                lines.Add(string.Format("G1 Z{0} F{1}", F(z), F(PlungeFeed)));

                double curLong = 0d;
                for (int i = 0; i < nRows; i++)
                {
                    double cross = Math.Min(i * step, crossLen);
                    if (i > 0)                                       // step across to this row at cutting feed
                        lines.Add("G1 " + XY(longIsX, curLong, cross) + " F" + F(Feed));
                    double endLong = curLong == 0d ? longLen : 0d;   // cut along the long axis, alternating direction
                    lines.Add("G1 " + XY(longIsX, endLong, cross) + " F" + F(Feed));
                    curLong = endLong;
                }
                lines.Add("G0 Z" + F(SafeZ));
            }

            if (SpindleRPM > 0d)
                lines.Add("M5");
            lines.Add("G53 G0 Z0");
            lines.Add("M30");

            return lines;
        }

        // Map a (long-axis, cross-axis) position back to an "X.. Y.." word pair.
        private static string XY(bool longIsX, double longVal, double crossVal)
        {
            double x = longIsX ? longVal : crossVal;
            double y = longIsX ? crossVal : longVal;
            return string.Format("X{0} Y{1}", F(x), F(y));
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if ((string)((Button)sender).Tag == "generate")
                Generate();
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
            if (WidthMM <= 0d || HeightMM <= 0d)
            {
                MessageBox.Show("Set the area width and height first (they default to the machine travel envelope).",
                                "Surface spoilboard", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (SpindleRPM > BitMaxRPM && BitMaxRPM > 0d &&
                MessageBox.Show(string.Format("Spindle RPM ({0:0}) exceeds the bit's rated max RPM ({1:0}).\n\nGenerate anyway?", SpindleRPM, BitMaxRPM),
                                "Surface spoilboard", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                return;

            var lines = BuildProgram();

            // Optionally save the program to disk (Cancel just loads it into the program view).
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save surfacing program (optional - Cancel just loads it)",
                Filter = "GCode files (*.nc)|*.nc|All files (*.*)|*.*",
                FileName = "spoilboard_surface.nc"
            };
            if (save.ShowDialog() == true)
            {
                try
                {
                    System.IO.File.WriteAllLines(save.FileName, lines);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not save the program:\n" + ex.Message, "Surface spoilboard", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            // Load the program into the job view (in memory), like the stepper-calibration generator.
            GCode.File.AddBlock("spoilboard_surface", Core.Action.New);
            GCode.File.AddLineNumbers = GrblInfo.UseLinenumbers && AppConfig.Settings.Base.AddLineNumbers;
            foreach (var line in lines)
                GCode.File.AddBlock(line);
            GCode.File.AddBlock("", Core.Action.End);

            MessageBox.Show("Surfacing program loaded into the program view.\n\nSwitch to the main view, jog to the front-left corner, zero work XYZ with the bit touching the surface, then run it.",
                            "Surface spoilboard", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    PlungeFeedProperty, DepthOfCutProperty, TotalDepthProperty, SafeZProperty, WidthMMProperty,
                    HeightMMProperty, ToolNumberProperty })
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(dp, typeof(SurfaceSpoilboardWizard))
                        .AddValueChanged(this, (s, ev) => SaveParams());
            }

            // Default the area to the machine travel envelope if still unset.
            if (WidthMM <= 0d)
                WidthMM = AxisTravel(0);
            if (HeightMM <= 0d)
                HeightMM = AxisTravel(1);

            txtWarnings.Text = string.Empty;
            txtInstructions.Text =
                "Surfaces (flattens) the spoilboard with a raster of overlapping passes.\n\n" +
                "1. Fit the surfacing bit. Enter its diameter and rated max RPM.\n" +
                "2. Set the spindle RPM, overlap %, feed/plunge, depth of cut and total skim depth. The area defaults to the machine travel envelope - edit Width/Height to surface a smaller region.\n" +
                "3. Press Generate to build the program and load it into the program view.\n" +
                "4. Switch to the main view. Jog to the FRONT-LEFT corner of the area, lower Z until the bit just touches the surface (ideally the highest spot), and zero work XYZ there - work Z0 becomes the surface top.\n" +
                "5. Run the program. It rasters from the corner across the area, cutting Z0 minus the depth of cut each pass down to the total depth.\n" +
                "Notes: stepover = bit diameter x (1 - overlap). Tool \"Loaded\" skips the tool change; \"Prompt\" issues M6. RPM 0 omits spindle commands. An uneven board only cleans fully if you zero Z on its highest point or set the total depth deep enough.";

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
                    TotalDepth = p.TotalDepth; SafeZ = p.SafeZ; WidthMM = p.WidthMM; HeightMM = p.HeightMM;
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
                    SafeZ = SafeZ, WidthMM = WidthMM, HeightMM = HeightMM, ToolNumber = ToolNumber
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
                      PlungeFeed = 500d, DepthOfCut = 0.3d, TotalDepth = 0.3d, SafeZ = 5d, WidthMM = 0d,
                      HeightMM = 0d, ToolNumber = 0d;
    }
}
