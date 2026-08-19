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
using System.Globalization;
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

        // The map/area/grid/viewport bindings (the Probing library's sub-VM, reused).
        public HeightMapViewModel HeightMap { get; } = new HeightMapViewModel();

        private AreaSource _area = AreaSource.Program;
        public AreaSource Area
        {
            get { return _area; }
            set
            {
                if (value == _area)
                    return;
                _area = value;
                // Full work surface is the movable-touch-plate case by default: the board cannot be probed
                // without putting something conductive under the bit, so holding at each point is the norm
                // rather than the exception. Set only on the transition, so an explicit untick survives.
                if (_area == AreaSource.FullTravel)
                    HeightMap.AddPause = true;
                DefaultArea();
                RefreshPreview();
                UpdateAreaModeUi();
            }
        }

        // ---- Full work surface: grid stated as DIVISIONS, not millimetres ----
        //
        // Over a whole table a spacing in mm is the wrong unit to think in: what you want to say is "sample it
        // four by four", and the spacing that implies depends on a table size you should not have to look up.
        // Divisions are POINTS PER AXIS - 4 x 4 is 16 probes, not 25 - so the number typed is the number of
        // probes per side, which is what is actually being decided.
        //
        // Only used when Area is FullTravel; the program-extent path keeps the mm spacing, where the job's
        // size is known and the spacing is the thing that matters to the surface.
        //
        // Plain CLR properties, deliberately: the bindings are TwoWay and driven by the operator typing, so
        // they need no change notification back the other way, and this view is not an INotifyPropertyChanged
        // (its bindings otherwise go through the HeightMap sub-VM, which is).

        private int _divX = 4, _divY = 4;

        public int DivisionsX
        {
            get { return _divX; }
            set { _divX = Math.Max(2, value); UpdateAreaModeUi(); }
        }

        public int DivisionsY
        {
            get { return _divY; }
            set { _divY = Math.Max(2, value); UpdateAreaModeUi(); }
        }

        /// <summary>
        /// The mm spacing that yields exactly <paramref name="points"/> points across <paramref name="span"/>.
        ///
        /// HeightMap's constructor derives its point count as ceil(span / spacing) + 1, so the exact quotient
        /// span/(points-1) sits one floating-point rounding away from producing an extra row: any result a
        /// hair above the true value makes that ceil round up. Shrinking the spacing very slightly biases the
        /// ceil downward, so the count comes out as asked rather than occasionally one too many.
        /// </summary>
        private static double SpacingFor(double span, int points)
        {
            return span / (points - 1) * 1.000001d;
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
                // Status polling is turned off when the Grbl tab is left; the probe run (WaitForIdle) needs
                // realtime reports, and the DRO/preview want them too - so turn it back on while this tab is up.
                model?.Poller.SetState(AppConfig.Settings.Base.PollInterval);
                RefreshProbes();
                DefaultArea();
                RefreshPreview();
                UpdateAreaModeUi();
                UpdateWarnings();
            }
        }

        public void CloseFile() { }
        public void Setup(UIViewModel m, AppConfig profile) { }

        #endregion

        // Public entry point for Start Job's "Probe height map" checkbox (Dynamic mode, see StartJobView) -
        // reuses THIS tab's own probing engine + Apply logic rather than re-deriving them, per the "reuse
        // existing engines" convention. Blocking: StartProbing's Program.Execute pumps synchronously, the
        // same as every other Probing-engine caller in this codebase (CenterFinderControl etc.) - fine to
        // call from Start Job's own post-run continuation. area is in the WORK coordinates Start Job just set.
        public void RunHeightMapAndApply(GrblViewModel m, double minX, double minY, double maxX, double maxY, double gridX, double gridY)
        {
            model = m;
            RefreshProbes();
            HeightMap.MinX = minX; HeightMap.MaxX = maxX;
            HeightMap.MinY = minY; HeightMap.MaxY = maxY;
            HeightMap.GridSizeX = Math.Max(gridX, 1d);
            HeightMap.GridSizeY = Math.Max(gridY, 1d);
            StartProbing();
            if (HeightMap.HasHeightMap)
                Apply_Click(null, null);
        }

        // Create (once) the Probing engine view model bound to the live controller model.
        private ProbingViewModel EnsureProbing()
        {
            if (model == null)
                return null;
            if (probing == null)
            {
                probing = new ProbingViewModel(model);   // params come from the shared probe library now

                // Light up Continue while the run is holding. The engine signals a hold by setting IsPaused
                // and waits for it to be cleared (Cycle Start does the same); without something bound to it
                // this view would hold with no way to resume from the app at all - the old Probing tab had a
                // button for exactly this and the port here never brought one across.
                probing.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName != nameof(ProbingViewModel.IsPaused))
                        return;
                    // Property changes arrive off the engine's own pump, not necessarily the UI thread.
                    Dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        if (btnContinue != null)
                            btnContinue.IsEnabled = probing.IsPaused;
                    }));
                };
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

        // Localized string via LibStrings, with \n expanded to real newlines. Empty (missing key) is harmless
        // for these transient messages; the keys are added alongside in LibStrings.xaml.
        private static string Loc(string key) => CNC.Controls.LibStrings.FindResource(key).Replace("\\n", "\n");

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartProbing();
            }
            catch (Exception ex)
            {
                AppDialogs.Show(Loc("HmStartError") + "\r\n\r\n" + ex.Message, "Height map", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartProbing()
        {
            if (model == null)
                model = DataContext as GrblViewModel ?? CNC.Core.Grbl.GrblViewModel;
            if (model == null)
            {
                AppDialogs.Show(Loc("HmNoController"), "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var p = cbxProbe.SelectedItem as ProbeDefinition;
            if (p == null)
            {
                AppDialogs.Show(Loc("HmSelectProbe"), "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var pr = EnsureProbing();
            if (pr == null)
                return;

            // ---- Full work surface: anchor the origin FIRST, then map in it ----
            //
            // This is what makes the mode need no Setup. The probing engine works in work coordinates, so
            // rather than forking a machine-referenced copy of it, the work origin is planted at the table
            // corner up front - which is where this mode is going to leave it anyway. After that the grid is
            // simply 0..W by 0..H and the ordinary probe path below runs unchanged.
            //
            // Placing the XY origin before probing rather than after is not just convenience: the probed
            // points are recorded as machine coordinates (PRB:), so anchoring afterwards would mean holding
            // the grid in one frame and the results in another for the whole run.
            if (Area == AreaSource.FullTravel && !AnchorWorkOriginToTable())
                return;

            // Watch the correct probe input for the chosen probe (main probe for height mapping), the same rule the
            // Probing page uses. Guards against a stale tool-setter selection (G65 P5 Q1 left by an interrupted
            // tc.macro) sending the descent to the wrong input. Only when the controller has a tool setter.
            if (GrblInfo.HasToolSetter)
                model.ExecuteCommand(string.Format(GrblCommand.ProbeSelect, p.ProbeType == ProbeType.ToolSetter ? 1 : 0));

            // Ensure realtime reports are flowing (WaitForIdle waits for one) - polling may be off on this tab.
            model.Poller.SetState(AppConfig.Settings.Base.PollInterval);

            // Single gentle probe at one approach feed (LatchDistance = 0 -> no fast/slow two-stage), from the
            // parked Z down by Probe depth, then retract to the parked Z. Keeps a fragile probe happy.
            pr.ProbeFeedRate = HeightMap.ProbeFeed > 0d ? HeightMap.ProbeFeed : (p.LatchFeedRate > 0d ? p.LatchFeedRate : p.ProbeFeedRate);
            pr.ProbeDistance = HeightMap.ProbeDepth;
            pr.LatchDistance = 0d;
            // Height mapping probes straight down with a spindle-centred Z-probe, so do NOT apply the probe's XY
            // (edge-finder) offset - it would shift the grid off the corner. Probe the grid at the work coords.
            pr.ProbeOffsetX = 0d;
            pr.ProbeOffsetY = 0d;
            pr.HeightMap.MinX = HeightMap.MinX; pr.HeightMap.MaxX = HeightMap.MaxX;
            pr.HeightMap.MinY = HeightMap.MinY; pr.HeightMap.MaxY = HeightMap.MaxY;
            // Divisions -> spacing for the full-table mode; the program-extent mode keeps its mm spacing.
            if (Area == AreaSource.FullTravel)
            {
                pr.HeightMap.GridSizeX = SpacingFor(HeightMap.MaxX - HeightMap.MinX, DivisionsX);
                pr.HeightMap.GridSizeY = SpacingFor(HeightMap.MaxY - HeightMap.MinY, DivisionsY);
            }
            else
            {
                pr.HeightMap.GridSizeX = HeightMap.GridSizeX; pr.HeightMap.GridSizeY = HeightMap.GridSizeY;
            }

            // Map origin = the area's min corner in the current work coordinates (e.g. G54 X0Y0).
            var startpos = new Position(pr.HeightMap.MinX, pr.HeightMap.MinY, 0d);

            if (!pr.WaitForIdle(string.Format("G90G0X{0}Y{1}", startpos.X.ToInvariantString(model.Format), startpos.Y.ToInvariantString(model.Format))))
            {
                AppDialogs.Show(string.Format(Loc("HmNotIdle"), model.GrblState.State),
                    "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (!pr.VerifyProbe())
            {
                AppDialogs.Show(Loc("HmProbeNotReady"), "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (!pr.Program.Init())
            {
                AppDialogs.Show(string.IsNullOrEmpty(pr.Message) ? Loc("HmInitFailed") : pr.Message,
                    "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            CNC.Controls.Probing.HeightMap map;
            try
            {
                map = new CNC.Controls.Probing.HeightMap(pr.HeightMap.GridSizeX, pr.HeightMap.GridSizeY,
                    new Vector2(pr.HeightMap.MinX, pr.HeightMap.MinY), new Vector2(pr.HeightMap.MaxX, pr.HeightMap.MaxY));
            }
            catch (Exception ex)
            {
                AppDialogs.Show(ex.Message, "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
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

                    // Hold before each point (never the first - the operator is already standing at it) so a
                    // touch plate can be moved to the next spot. Without this the run rapids straight on and
                    // probes bare board, which on a spoilboard means descending the full probe depth into
                    // something that will never make contact.
                    //
                    // The mechanism is the Probing library's own: AddPause sets IsPaused and the run resumes
                    // on Cycle Start. This view had never called it, so the whole movable-plate workflow was
                    // unavailable here even though the engine and the view model property both existed.
                    if (HeightMap.AddPause && point > 1)
                        pr.Program.AddPause();

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
            model.Message = string.Empty;   // clear the stale "Probing point N of M..." progress line

            // Build the map BEFORE End(): End() unsubscribes the engine's probe handlers, and doing it first could
            // drop the final captured point. End() afterwards does the cleanup the engine skips on success - clears
            // IsJobRunning (UI was left locked), unsubscribes, and restores absolute mode (probing ran in G91).
            BuildMap(pr, map);
            pr.Program.End(string.Empty);

            // Full work surface finishes the job it started: the origin it planted at the corner now gets its
            // Z, taken from the HIGHEST point measured. Surfacing can then be asked for a depth and run - the
            // thing this mode exists to make possible without a Setup pass or a touch-off.
            if (Area == AreaSource.FullTravel && HeightMap.HasHeightMap)
                SetWorkZeroToHighest(pr);
        }

        // ---- full work surface: origin handling ----

        /// <summary>The WCS the controller currently has active, e.g. "G54"; G54 if it has not reported one.</summary>
        private string ActiveWcs()
        {
            string wcs = model?.WorkCoordinateSystem;
            return string.IsNullOrEmpty(wcs) ? "G54" : wcs;
        }

        /// <summary>G10 L2's P number for a WCS code - P1 is G54 ... P6 is G59.</summary>
        private int ActiveWcsP()
        {
            int n;
            string wcs = ActiveWcs();
            if (!int.TryParse(wcs.Length >= 3 ? wcs.Substring(1, 2) : "54", out n) || n < 54 || n > 59)
                n = 54;
            return n - 53;
        }

        /// <summary>
        /// Put the active WCS's XY origin on the spoilboard corner - the in-bounds minimum of the travel
        /// envelope - and re-express the probe area as 0..W by 0..H in that new frame.
        ///
        /// Asks first. This overwrites a stored work origin, which for anyone who has a workpiece set up is
        /// the most destructive thing this tab can do, and it is not recoverable by undo.
        /// </summary>
        private bool AnchorWorkOriginToTable()
        {
            double inset = Inset(), w = MaxArea(0), h = MaxArea(1);
            if (w <= 0d || h <= 0d)
            {
                AppDialogs.Show("The machine travel limits ($130-$132) do not describe a usable area, so the table corner cannot be found.",
                                "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            double ox = EnvMin(0) + inset, oy = EnvMin(1) + inset;
            string wcs = ActiveWcs();

            if (AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "Full work surface will set the {0} work origin.\n\n" +
                    "X0 Y0 moves to the table corner (machine X{1:0.###} Y{2:0.###}), and after probing, Z0 is set to the " +
                    "highest point found.\n\n" +
                    "Any work origin {0} is holding now will be overwritten. Continue?",
                    wcs, ox, oy),
                    "Height map", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;

            model.ExecuteCommand(string.Format(CultureInfo.InvariantCulture, "G10L2P{0}X{1:0.###}Y{2:0.###}", ActiveWcsP(), ox, oy));

            // The offset has to be in effect before the area below is read in the new frame - a grid computed
            // against the OLD offset would be placed off the table by however far the origin just moved.
            var pr = EnsureProbing();
            if (pr != null && !pr.WaitForWcoUpdate())
            {
                AppDialogs.Show("The controller did not report the new work offset, so the probe area cannot be trusted. Nothing has been probed.",
                                "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            HeightMap.MinX = 0d; HeightMap.MaxX = w;
            HeightMap.MinY = 0d; HeightMap.MaxY = h;
            return true;
        }

        /// <summary>
        /// Set work Z0 to the highest point probed. The probed positions are machine coordinates (PRB:), so
        /// G10 L2 can place the offset directly - no need to drive the tool back to that point to zero on it.
        /// </summary>
        private void SetWorkZeroToHighest(ProbingViewModel pr)
        {
            if (pr.Positions.Count == 0)
                return;

            double zHigh = pr.Positions.Max(x => x.Z), zLow = pr.Positions.Min(x => x.Z);
            string wcs = ActiveWcs();

            model.ExecuteCommand(string.Format(CultureInfo.InvariantCulture, "G10L2P{0}Z{1:0.###}", ActiveWcsP(), zHigh));
            model.ResponseLog.Add(string.Format(CultureInfo.InvariantCulture,
                "HeightMap: {0} origin set - XY at the table corner, Z0 at machine Z {1:0.###} (highest of {2} points, range {3:0.###})",
                wcs, zHigh, pr.Positions.Count, zHigh - zLow));

            // The range is the number that decides the surfacing pass, and the one a single touch-off can
            // never tell you: a cutting plane only flattens the board where it sits BELOW the existing
            // surface, so this is how deep the job has to go to clean up the whole table rather than just
            // skim the high spots.
            AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "{0} points probed.\n\n" +
                    "Work origin {1} is now set:\n" +
                    "    X0 Y0 at the table corner\n" +
                    "    Z0 at the highest point (machine Z {2:0.###})\n\n" +
                    "Lowest point is {3:0.###} mm below that, so a surfacing pass has to go at least that deep " +
                    "to clean up the whole board rather than only the high spots.",
                    pr.Positions.Count, wcs, zHigh, zHigh - zLow),
                "Height map", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Show the inputs the chosen area actually uses: divisions for the full table (its extent is derived,
        /// so the X/W/Y/H boxes would be describing something the operator cannot change), mm spacing and the
        /// explicit area for the program extent.
        /// </summary>
        private void UpdateAreaModeUi()
        {
            if (txtAreaNote == null || pnlDivisions == null)
                return;

            bool full = Area == AreaSource.FullTravel;

            pnlDivisions.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
            pnlGridMm.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            pnlAreaXW.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            pnlAreaYH.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            btnFromLimits.Visibility = full ? Visibility.Collapsed : Visibility.Visible;

            txtAreaNote.Text = full
                ? "No Setup needed. The origin is set from the table: X0 Y0 at the corner before probing, Z0 at the highest point after."
                : string.Empty;

            if (stepsProgram != null)
                stepsProgram.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            if (stepsFullSurface != null)
                stepsFullSurface.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

            txtDivisionsNote.Text = full
                ? string.Format("{0} probes ({1} x {2} points across the table).", DivisionsX * DivisionsY, DivisionsX, DivisionsY)
                : string.Empty;
        }

        /// <summary>Release a hold between probe points - the operator has moved the touch plate.</summary>
        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (probing != null && probing.IsPaused)
                probing.IsPaused = false;   // the engine resumes on the property changing, not on a command
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            probing?.Program.Cancel();
        }

        // Build the height map from the probed positions (delta from the first point). The read-back order
        // mirrors the serpentine probe order above so each result lands in the right grid cell.
        private void BuildMap(ProbingViewModel pr, CNC.Controls.Probing.HeightMap map)
        {
            model.ResponseLog.Add(string.Format("HeightMap: captured {0} of {1} points", pr.Positions.Count, map.TotalPoints));

            // Build from the probed points as long as we captured one per grid point - tolerate IsSuccess being
            // cleared by a late probe-release event after the final point. Report clearly either way.
            if (pr.Positions.Count != map.TotalPoints)
            {
                AppDialogs.Show(string.Format(Loc("HmCaptureShort"),
                    pr.Positions.Count, map.TotalPoints, string.IsNullOrEmpty(pr.Message) ? "" : "\r\n\r\n" + pr.Message),
                    "Height map", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            model.Message = string.Format(Loc("HmComplete"),
                map.TotalPoints, Math.Round(map.MinHeight, model.Precision).ToInvariantString(), Math.Round(map.MaxHeight, model.Precision).ToInvariantString());
        }

        // ---- apply / save / load ----

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (model == null || HeightMap.Map == null || !HeightMap.HasHeightMap)
                return;
            if (!model.IsFileLoaded)
            {
                AppDialogs.Show(Loc("HmApplyNeedsJob"), "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
                    AppDialogs.Show(ex.Message, "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (HeightMap.Map == null)
                return;

            var file = new SaveFileDialog { AddExtension = true, Title = Loc("HmSaveTitle"), Filter = Loc("HmFileFilter") };
            if (file.ShowDialog() == true)
                HeightMap.Map.Save(file.FileName);
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var file = new OpenFileDialog { Title = Loc("HmLoadTitle"), Filter = Loc("HmFileFilter") };
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
