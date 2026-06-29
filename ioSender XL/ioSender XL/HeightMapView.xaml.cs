/*
 * HeightMapView.xaml.cs - part of ioSender XL
 *
 * "Height Map" top-level tab. Probe a grid over the work surface and apply the resulting map to the loaded
 * job so every move follows the surface. The probing is driven by ioSender's dedicated Probing engine
 * (CNC.Controls.Probing.Program) - one synchronised probe at a time, capturing each result - exactly as the
 * original Probing > Height map tab did, which is robust for a whole grid of probes (the job streamer is not).
 *
 * The map is relative: heights are stored as the delta from the first probed point, so set the work origin on
 * the stock and park Z at a safe clearance first - the engine probes down from there and retracts back to it.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CNC.Core;
using CNC.GCode;
using CNC.Controls;
using CNC.Controls.Probing;
using HelixToolkit.Wpf;

namespace GCode_Sender
{
    public partial class HeightMapView : UserControl, ICNCView
    {
        // Where the probe grid is referenced (work-coordinate area). Program = the loaded job's XY extent;
        // FullTravel = the in-bounds machine envelope expressed in the current work frame.
        public enum AreaSource { Program, FullTravel }

        private GrblViewModel model = null;
        private ProbingViewModel probing = null;     // the Probing engine + its view model (created lazily)
        private ProbingProfiles profiles = null;

        // The map/area/grid/viewport bindings (the Probing library's sub-VM, reused).
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
                if (e.PropertyName == nameof(HeightMapViewModel.MinX) || e.PropertyName == nameof(HeightMapViewModel.MaxX) ||
                    e.PropertyName == nameof(HeightMapViewModel.MinY) || e.PropertyName == nameof(HeightMapViewModel.MaxY) ||
                    e.PropertyName == nameof(HeightMapViewModel.GridSizeX) || e.PropertyName == nameof(HeightMapViewModel.GridSizeY))
                    RefreshPreview();
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
                RefreshProbes();
                DefaultArea();
                RefreshPreview();
                UpdateWarnings();
            }
        }

        public void CloseFile() { }
        public void Setup(UIViewModel m, AppConfig profile) { }

        #endregion

        // Create (once) the Probing engine view model bound to the live controller model.
        private ProbingViewModel EnsureProbing()
        {
            if (model == null)
                return null;
            if (probing == null)
            {
                profiles = new ProbingProfiles();
                profiles.Load();
                probing = new ProbingViewModel(model, profiles);
            }
            return probing;
        }

        // ---- machine travel envelope (for the "Full travel" area, expressed in the current work frame) ----

        private const double Margin = 5d;

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

        // Size/position the probe area for the current source. Both are work-coordinate regions (the engine
        // probes in work coordinates); Full travel converts the machine envelope via the current work offset.
        private void DefaultArea()
        {
            if (Area == AreaSource.FullTravel)
            {
                double inset = Inset(), w = MaxArea(0), h = MaxArea(1);
                if (w > 0d && h > 0d && model != null)
                {
                    HeightMap.MinX = EnvMin(0) + inset - model.WorkPositionOffset.X;
                    HeightMap.MaxX = HeightMap.MinX + w;
                    HeightMap.MinY = EnvMin(1) + inset - model.WorkPositionOffset.Y;
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
        }

        // ---- preview: show the planned grid (points + boundary) before probing ----

        private void RefreshPreview()
        {
            if (HeightMap.HasHeightMap)   // a probed surface is showing - don't overwrite it with the bare grid
                return;
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

        // ---- run: probe the grid through the Probing engine (mirrors the original Height map tab) ----

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (model == null)
                return;

            var p = cbxProbe.SelectedItem as ProbeDefinition;
            if (p == null)
            {
                MessageBox.Show("Select a probe definition first (Machine Setup Wizard > Probe definitions).",
                    "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var pr = EnsureProbing();
            if (pr == null)
                return;

            // Single gentle probe at one approach feed (LatchDistance = 0 -> no fast/slow two-stage), from the
            // parked Z down by Probe depth, then retract to the parked Z. Keeps a fragile probe happy.
            pr.ProbeFeedRate = HeightMap.ProbeFeed > 0d ? HeightMap.ProbeFeed : (p.LatchFeedRate > 0d ? p.LatchFeedRate : p.ProbeFeedRate);
            pr.ProbeDistance = HeightMap.ProbeDepth;
            pr.LatchDistance = 0d;
            pr.ProbeOffsetX = p.ProbeOffsetX;
            pr.ProbeOffsetY = p.ProbeOffsetY;
            pr.HeightMap.MinX = HeightMap.MinX; pr.HeightMap.MaxX = HeightMap.MaxX;
            pr.HeightMap.MinY = HeightMap.MinY; pr.HeightMap.MaxY = HeightMap.MaxY;
            pr.HeightMap.GridSizeX = HeightMap.GridSizeX; pr.HeightMap.GridSizeY = HeightMap.GridSizeY;

            // Map origin (work coords), backed off by the probe XY offset so the tip lands on the corner.
            var startpos = new Position(pr.HeightMap.MinX - pr.ProbeOffsetX, pr.HeightMap.MinY - pr.ProbeOffsetY, 0d);

            if (!pr.WaitForIdle(string.Format("G90G0X{0}Y{1}", startpos.X.ToInvariantString(model.Format), startpos.Y.ToInvariantString(model.Format))))
                return;
            if (!pr.VerifyProbe())
            {
                MessageBox.Show("The probe is not ready (not connected, or already triggered).", "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (!pr.Program.Init())
                return;

            CNC.Controls.Probing.HeightMap map;
            try
            {
                map = new CNC.Controls.Probing.HeightMap(pr.HeightMap.GridSizeX, pr.HeightMap.GridSizeY,
                    new Vector2(pr.HeightMap.MinX, pr.HeightMap.MinY), new Vector2(pr.HeightMap.MaxX, pr.HeightMap.MaxY));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            pr.HeightMap.Map = map;
            HeightMap.HasHeightMap = false;
            HeightMap.CanApply = false;

            // Relative probing: probe down, retract to the parked (start) Z, step to the next point. Serpentine
            // (flip Y direction each column) to minimise travel - matched by the result read-back below.
            pr.Program.Add(string.Format("G91F{0}", pr.ProbeFeedRate.ToInvariantString()));
            double dir = 1d;
            int point = 0, points = map.SizeX * map.SizeY;
            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    pr.Program.AddMessage(string.Format("Probing point {0} of {1}...", ++point, points));
                    if (HeightMap.SettleDwell > 0d)
                        pr.Program.Add("G4P" + HeightMap.SettleDwell.ToInvariantString());   // let a fragile probe release/settle
                    pr.Program.AddProbingAction(AxisFlags.Z, true);
                    pr.Program.AddRapidToMPos(pr.StartPosition, AxisFlags.Z);
                    if (y < map.SizeY - 1)
                        pr.Program.AddRapid(string.Format("Y{0}", (map.GridY * dir).ToInvariantString(model.Format)));
                }
                if (x < map.SizeX - 1)
                    pr.Program.AddRapid(string.Format("X{0}", map.GridX.ToInvariantString(model.Format)));
                dir *= -1d;
            }

            pr.Program.Execute(true);
            BuildMap(pr, map);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            probing?.Program.Cancel();
        }

        // Build the height map from the probed positions (delta from the first point). The read-back order
        // mirrors the serpentine probe order above so each result lands in the right grid cell.
        private void BuildMap(ProbingViewModel pr, CNC.Controls.Probing.HeightMap map)
        {
            if (!(pr.IsSuccess && pr.Positions.Count == map.TotalPoints))
            {
                MessageBox.Show(string.Format("Height map probing did not complete ({0} of {1} points). {2}",
                    pr.Positions.Count, map.TotalPoints, pr.Message), "Height map", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double z0 = pr.Positions[0].Z;
            int i = 0;
            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                    map.AddPoint(x, y, Math.Round(pr.Positions[i++].Z - z0, model.Precision));
                if (++x < map.SizeX)
                    for (int y = map.SizeY - 1; y >= 0; y--)
                        map.AddPoint(x, y, Math.Round(pr.Positions[i++].Z - z0, model.Precision));
            }

            HeightMap.Map = map;
            HeightMap.HasHeightMap = true;
            HeightMap.CanApply = model.IsFileLoaded;
            RefreshSurface();
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
        private void AreaTable_Checked(object sender, RoutedEventArgs e) { Area = AreaSource.FullTravel; }

        private void cbxProbe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Default the probe feed to the chosen probe's latch (slow) feed, without clobbering a user override.
            if (HeightMap.ProbeFeed <= 0d && cbxProbe.SelectedItem is ProbeDefinition p)
                HeightMap.ProbeFeed = p.LatchFeedRate > 0d ? p.LatchFeedRate : p.ProbeFeedRate;
        }
    }
}
