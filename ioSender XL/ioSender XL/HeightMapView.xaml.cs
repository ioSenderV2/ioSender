/*
 * HeightMapView.xaml.cs - part of ioSender XL
 *
 * "Height Map" top-level tab, built on the generate-and-run model (like Load stock / Surface spoilboard):
 * pick a probe + grid + area, Generate a serpentine probe-grid NGC program (shown in the program view),
 * then Cycle Start streams it stay-put through the job streamer. Each point reports its probed Z back via a
 * controller-side (PRINT, HM=i,j Z=..) line; this tab parses those into a HeightMap and draws the surface
 * live. Apply then rewrites the loaded job to follow the surface (CNC.Controls.Probing.GCodeTransform).
 *
 * NOTE: the generated NGC is firmware-dependent (grblHAL NGC expressions + a probe, and #5063 as the probe
 *       Z result) and MUST be validated on the machine before it is trusted - same caveat as Load stock.
 */

using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CNC.Core;
using CNC.Controls;
using CNC.Controls.Probing;
using HelixToolkit.Wpf;

namespace GCode_Sender
{
    public partial class HeightMapView : UserControl, ICNCView
    {
        // Where the probe grid is referenced. Program = the loaded job's XY extent in the active WCS; FullTable =
        // the homed machine envelope (machine-referenced origin, like Surface spoilboard).
        public enum AreaSource { Program, FullTable }

        private GrblViewModel model = null;
        private bool subscribed = false;
        private string program = string.Empty;     // last generated probe program (streamed via the macro path)
        private double? z0 = null;                  // first probed Z (reference); stored heights are Zi - Z0
        private int received = 0;                   // probed points captured this run

        // One (PRINT, HM=i,j Z=z) per probed point - i = X index, j = Y index (so order doesn't matter).
        private static readonly Regex rxPoint = new Regex(
            @"HM\s*=\s*(\d+)\s*[, ]\s*(\d+)\s+Z\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

        // The map/area/grid/viewport bindings (reused from the Probing library's sub-VM).
        public HeightMapViewModel HeightMap { get; } = new HeightMapViewModel();

        private AreaSource _area = AreaSource.Program;
        public AreaSource Area
        {
            get { return _area; }
            set { if (value != _area) { _area = value; DefaultArea(); RefreshPreview(); } }
        }

        public HeightMapView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => { if (e.NewValue is GrblViewModel m) model = m; };
            HeightMap.PropertyChanged += (s, e) =>
            {
                // Re-frame the planned grid when the area / grid size changes (but not on our own map updates).
                if (e.PropertyName == nameof(HeightMapViewModel.MinX) || e.PropertyName == nameof(HeightMapViewModel.MaxX) ||
                    e.PropertyName == nameof(HeightMapViewModel.MinY) || e.PropertyName == nameof(HeightMapViewModel.MaxY) ||
                    e.PropertyName == nameof(HeightMapViewModel.GridSizeX) || e.PropertyName == nameof(HeightMapViewModel.GridSizeY))
                {
                    InvalidateProgram();
                    RefreshPreview();
                }
            };
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.HeightMap; } }
        public bool CanEnable { get { return true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (activate)
            {
                if (model == null)
                    model = DataContext as GrblViewModel;
                Subscribe(true);
                RefreshProbes();
                DefaultArea();
                RefreshPreview();
                UpdateWarnings();
                MacroProcessor.SetActiveProgram?.Invoke("Height map", program);   // Program View shows our program
                MacroProcessor.ActiveRun = Run;                                    // Cycle Start runs it
            }
            else
            {
                MacroProcessor.ActiveRun = null;
                MacroProcessor.ClearActiveProgram?.Invoke();   // active program follows the focused tab
                // Stay subscribed: a stay-put run keeps this tab, but keeping the handler is harmless either way.
            }
        }

        public void CloseFile() { }
        public void Setup(UIViewModel model, AppConfig profile) { }

        #endregion

        private void Subscribe(bool on)
        {
            if (model == null || on == subscribed)
                return;
            if (on)
                model.PropertyChanged += Model_PropertyChanged;
            else
                model.PropertyChanged -= Model_PropertyChanged;
            subscribed = on;
        }

        // ---- machine travel envelope (mirrors Surface spoilboard's machine-referenced origin) ----

        private const double Margin = 5d;   // edge clearance kept from each travel limit (plus the homing pull-off)

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
            return Math.Max(Margin, pulloff + 1d);
        }

        private static double MaxArea(int axis)
        {
            return Math.Max(0d, AxisTravel(axis) - 2d * Inset());
        }

        // Size/position the probe area for the current source: the loaded job's XY extent, or the in-bounds
        // machine envelope. Leaves a sane default if neither is known.
        private void DefaultArea()
        {
            if (Area == AreaSource.FullTable)
            {
                double inset = Inset(), w = MaxArea(0), h = MaxArea(1);
                if (w > 0d && h > 0d)
                {
                    HeightMap.MinX = EnvMin(0) + inset;
                    HeightMap.MaxX = HeightMap.MinX + w;
                    HeightMap.MinY = EnvMin(1) + inset;
                    HeightMap.MaxY = HeightMap.MinY + h;
                }
            }
            else if (model != null && model.IsFileLoaded)
            {
                HeightMap.MinX = model.ProgramLimits.MinX;
                HeightMap.MaxX = model.ProgramLimits.MaxX;
                HeightMap.MinY = model.ProgramLimits.MinY;
                HeightMap.MaxY = model.ProgramLimits.MaxY;
            }
        }

        private void RefreshProbes()
        {
            var usable = ProbeDefinitions.Items
                .Where(p => p.ProbeType == ProbeType.ThreeDProbe || p.ProbeType == ProbeType.TouchPlate || p.ProbeType == ProbeType.EdgeFinder)
                .ToList();

            var sel = cbxProbe.SelectedItem as ProbeDefinition;
            cbxProbe.ItemsSource = usable;
            if (sel != null && usable.Contains(sel))
                cbxProbe.SelectedItem = sel;
            else
                cbxProbe.SelectedItem = usable.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe) ?? usable.FirstOrDefault();

            UpdateWarnings();
        }

        private void UpdateWarnings()
        {
            txtNoProbe.Visibility = cbxProbe.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            txtExprWarn.Visibility = (model != null && !GrblInfo.ExpressionsSupported) ? Visibility.Visible : Visibility.Collapsed;
        }

        // Drop the generated program (Cycle Start re-generates via Run); clear the captured map's "applied" state.
        private void InvalidateProgram()
        {
            program = string.Empty;
            HeightMap.CanApply = false;
        }

        // ---- preview: show the planned grid (points + boundary) before probing ----

        private void RefreshPreview()
        {
            try
            {
                var border = new LinesVisual3D();
                var points = new PointsVisual3D();
                CNC.Controls.Probing.HeightMap.GetPreviewModel(
                    new Vector2(HeightMap.MinX, HeightMap.MinY), new Vector2(HeightMap.MaxX, HeightMap.MaxY),
                    Math.Min(HeightMap.GridSizeX, HeightMap.GridSizeY), border, points);
                HeightMap.BoundaryPoints = border.Points;
                HeightMap.MapPoints = points.Points;
                HeightMap.MeshGeometry = null;
            }
            catch { /* degenerate area - leave the previous preview */ }
        }

        // Rebuild the 3D surface from the (partially) probed map - called live as points arrive.
        private void RefreshSurface()
        {
            if (HeightMap.Map == null)
                return;
            var border = new LinesVisual3D();
            var points = new PointsVisual3D();
            var mesh = new MeshGeometryVisual3D();
            HeightMap.Map.GetModel(mesh);
            HeightMap.Map.GetPreviewModel(border, points);
            HeightMap.MeshGeometry = mesh.MeshGeometry;
            HeightMap.BoundaryPoints = border.Points;
            HeightMap.MapPoints = points.Points;
        }

        // ---- generate the serpentine probe-grid program ----

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            Generate();
            if (!string.IsNullOrEmpty(program))
                MacroProcessor.ProgramPreview?.Invoke("Height map", program);   // refresh + pop the program view
        }

        private bool Generate()
        {
            if (model == null)
                return false;

            var p = cbxProbe.SelectedItem as ProbeDefinition;
            if (p == null)
            {
                MessageBox.Show("Select a probe definition first (Machine Setup Wizard > Probe definitions).",
                    "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            HeightMap map;
            try
            {
                map = new CNC.Controls.Probing.HeightMap(HeightMap.GridSizeX, HeightMap.GridSizeY,
                    new Vector2(HeightMap.MinX, HeightMap.MinY), new Vector2(HeightMap.MaxX, HeightMap.MaxY));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            // Fresh empty map (cleared of any previous probe data) bound to the viewport for the live run.
            HeightMap.Map = map;
            HeightMap.GridSizeX = map.GridX;
            HeightMap.GridSizeY = map.GridY;
            HeightMap.HasHeightMap = false;
            HeightMap.CanApply = false;

            program = BuildProgram(p, map);
            return !string.IsNullOrWhiteSpace(program);
        }

        private string BuildProgram(ProbeDefinition p, CNC.Controls.Probing.HeightMap map)
        {
            double safeZ = HeightMap.SafeZ;
            double depth = HeightMap.ProbeDepth;          // how far below the start plane the probe may travel
            // Every point probes at this slow approach feed (no fast search pass): the surface is roughly known,
            // so a single gentle probe avoids overshoot/false triggers on a fragile probe. Defaults to the probe's
            // latch (slow) feed; the user can override in the Probe feed field.
            double probeF = HeightMap.ProbeFeed > 0d ? HeightMap.ProbeFeed
                          : p.LatchFeedRate > 0d ? p.LatchFeedRate
                          : p.ProbeFeedRate > 0d ? p.ProbeFeedRate : 100d;

            var b = new StringBuilder();
            void L(string s) { b.Append(s).Append('\n'); }

            L("(Height map - probe a grid, report Z per point via (PRINT, HM=i,j Z=..))");
            L(string.Format("(grid {0} x {1} points, {2} mm area, probe \"{3}\")",
                map.SizeX, map.SizeY, string.Format(CultureInfo.InvariantCulture, "{0:0.#} x {1:0.#}", map.Delta.X, map.Delta.Y), p.Name));
            L("(Requires grblHAL NGC expressions + a probe; #5063 = probe Z result. VALIDATE before trusting.)");
            L("(PREREQ, connected, homed, EXPR)");
            L("G21 G90 G94 G17 G49");

            if (Area == AreaSource.FullTable)
            {
                // Machine-referenced origin, like Surface spoilboard: park, touch off Z0 at the surface, then set
                // the work XY origin at the inset machine corner so the grid (in work coords below) lines up.
                double zTop = EnvMin(2) + AxisTravel(2) - Inset();
                L("G53 G0 Z" + N(zTop));
                L(string.Format("G53 G0 X{0} Y{1}", N(HeightMap.MinX), N(HeightMap.MinY)));
                L("(WAITIDLE)");
                L("(MBOX, OKCANCEL, Jog the bit/probe down until it just touches the surface, then OK to set work Z0. XY is referenced to the machine corner automatically. Cancel aborts.)");
                L("(WAITIDLE)");
                L("G10 L20 P0 Z0");                                          // work Z0 = surface (active WCS)
                L(string.Format("G10 L2 P0 X{0} Y{1}", N(HeightMap.MinX), N(HeightMap.MinY)));   // work XY origin = inset corner
            }
            else
            {
                // Program-referenced: probe over the loaded job's extent in the EXISTING work coordinate system.
                // Z0 must already be at the surface (the map stores deltas from the first probed point).
                L("(MBOX, OKCANCEL, Probing the loaded program's area in the current work coordinates. Ensure work Z0 is at the surface, then OK. Cancel aborts.)");
                L("(WAITIDLE)");
            }

            L("G0 Z" + N(safeZ));

            // Serpentine raster over the grid: row j sweeps +X on even rows, -X on odd, to minimise travel.
            for (int j = 0; j < map.SizeY; j++)
            {
                bool fwd = (j & 1) == 0;
                for (int k = 0; k < map.SizeX; k++)
                {
                    int i = fwd ? k : map.SizeX - 1 - k;
                    var c = map.GetCoordinates(i, j);
                    L(string.Format("G0 X{0} Y{1}", N(c.X), N(c.Y)));
                    L(string.Format("G38.3 Z{0} F{1}", N(-depth), N(probeF)));   // gentle probe down to the surface
                    L(string.Format("(PRINT, HM={0},{1} Z=#5063)", i, j));        // report the probe Z result
                    L("G0 Z" + N(safeZ));                                          // retract before the next point
                }
            }

            L("M2");
            return b.ToString();
        }

        private static string N(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // ---- run (Cycle Start / ActiveRun): stream stay-put and capture the PRINTed grid ----

        private void Run()
        {
            if (model == null)
                return;
            if (string.IsNullOrWhiteSpace(program) && !Generate())
                return;
            if (string.IsNullOrWhiteSpace(program))
                return;

            z0 = null;
            received = 0;
            Subscribe(true);

            MacroProcessor.Run(model, "Height map", program, true, stayPut: true);
        }

        // Pull each (PRINT, HM=i,j Z=z) out of the controller's console messages and drop it into the map,
        // redrawing the surface live. Heights are stored relative to the first probed point.
        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GrblViewModel.Message))
                return;

            string msg = model.Message;
            if (string.IsNullOrEmpty(msg) || HeightMap.Map == null)
                return;

            bool hit = false;
            foreach (Match m in rxPoint.Matches(msg))
            {
                if (int.TryParse(m.Groups[1].Value, out int i) &&
                    int.TryParse(m.Groups[2].Value, out int j) &&
                    double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) &&
                    i >= 0 && i < HeightMap.Map.SizeX && j >= 0 && j < HeightMap.Map.SizeY)
                {
                    if (z0 == null)
                        z0 = z;
                    HeightMap.Map.AddPoint(i, j, Math.Round(z - z0.Value, model.Precision));
                    received++;
                    hit = true;
                }
            }

            if (!hit)
                return;

            RefreshSurface();

            if (received >= HeightMap.Map.TotalPoints)
            {
                HeightMap.HasHeightMap = true;
                HeightMap.CanApply = model.IsFileLoaded;   // a job must be loaded to apply the map to it
            }
        }

        // ---- apply / save / load ----

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (model == null || HeightMap.Map == null || !HeightMap.HasHeightMap)
                return;
            if (!model.IsFileLoaded)
            {
                MessageBox.Show("Load the program to compensate first - Apply rewrites the loaded job to follow the surface.",
                    "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            using (new UIUtils.WaitCursor())
            {
                try
                {
                    new GCodeTransform().ApplyHeightMap(model, HeightMap.Map);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (HeightMap.Map == null)
                return;

            var file = new SaveFileDialog { AddExtension = true, Title = "Save height map", Filter = "Height map files (*.map)|*.map|All files (*.*)|*.*" };
            if (file.ShowDialog() == true)
                HeightMap.Map.Save(file.FileName);
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var file = new OpenFileDialog { Title = "Load height map", Filter = "Height map files (*.map)|*.map|All files (*.*)|*.*" };
            if (file.ShowDialog() == true)
                LoadMap(file.FileName);
        }

        private void LoadMap(string fileName)
        {
            HeightMap.HasHeightMap = false;
            HeightMap.Map = CNC.Controls.Probing.HeightMap.Load(fileName);
            HeightMap.GridSizeX = HeightMap.Map.GridX;
            HeightMap.GridSizeY = HeightMap.Map.GridY;
            HeightMap.MinX = HeightMap.Map.Min.X;
            HeightMap.MinY = HeightMap.Map.Min.Y;
            HeightMap.MaxX = HeightMap.Map.Max.X;
            HeightMap.MaxY = HeightMap.Map.Max.Y;
            RefreshSurface();
            HeightMap.HasHeightMap = true;
            HeightMap.CanApply = model != null && model.IsFileLoaded;
        }

        private void Limits_Click(object sender, RoutedEventArgs e)
        {
            Area = AreaSource.Program;
            DefaultArea();
            RefreshPreview();
        }

        private void AreaProgram_Checked(object sender, RoutedEventArgs e) { Area = AreaSource.Program; }
        private void AreaTable_Checked(object sender, RoutedEventArgs e) { Area = AreaSource.FullTable; }

        private void cbxProbe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Default the probe feed to the chosen probe's latch (slow) feed, but never clobber a value the user
            // has set - so a fragile probe maps at the gentle approach feed out of the box, overridable per tab.
            if (HeightMap.ProbeFeed <= 0d && cbxProbe.SelectedItem is ProbeDefinition p)
                HeightMap.ProbeFeed = p.LatchFeedRate > 0d ? p.LatchFeedRate : p.ProbeFeedRate;
            InvalidateProgram();
        }
    }
}
