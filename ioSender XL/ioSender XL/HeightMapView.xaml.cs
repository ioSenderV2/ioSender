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
using System.Collections.Generic;
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
        /// How much of the probe distance is held back as headroom for the board being proud at the next
        /// point. The retract between points sits this far inside the probe depth, so a high spot still
        /// triggers instead of being driven into.
        /// </summary>
        private const double ProbeVariationMargin = 3d;

        /// <summary>
        /// How far the tool lifts between points: enough for a touch plate to go under it, and no more.
        ///
        /// Deliberately a small constant rather than a fraction of the probe's search distance. What this
        /// height has to clear is the plate, which does not get thicker because the probe searches further -
        /// and everything above the plate is search range spent before the probe starts looking for the
        /// board.
        /// </summary>
        private const double PlateClearance = 15d;

        /// <summary>
        /// How much LOWER than the previous point the next one is allowed to be and still be found.
        ///
        /// After the first probe the surface height is known, so every later probe only has to cover the lift
        /// off the last point plus however much the board falls away between the two. Using the probe
        /// definition's full search distance instead is both pointless and dangerous: from a start already
        /// most of the way down the Z axis, a 50mm search targets a depth past the soft limit and alarms
        /// before it moves (2026-08-19, G38.3 Z-50 from Z-81.8 targeting -131.8 against a floor near -129 -
        /// travel less the homing pull-off, which grblHAL reserves).
        /// </summary>
        private const double BoardVariation = 10d;

        /// <summary>
        /// Thickness to take off every probed Z, in mm.
        ///
        /// A touch plate triggers at its OWN top face, so the board is one plate-thickness below where the
        /// probe stopped. The relative map does not care - the same constant is in every point, so it cancels
        /// out of the deltas - but the absolute Z0 this mode sets very much does, and without this it sits a
        /// whole plate high, which is a surfacing pass that cuts nothing.
        ///
        /// Gated on the probe TYPE, not on the number: ProbingViewModel copies PlateThickness off whatever
        /// definition is selected regardless of type, so a 3D probe would otherwise inherit a plate thickness
        /// it does not have and be corrected by a plate that is not there. Same test FixtureEditDialog makes.
        /// </summary>
        private double PlateThickness()
        {
            var p = cbxProbe.SelectedItem as ProbeDefinition;
            return p != null && p.ProbeType == ProbeType.TouchPlate ? p.PlateThickness : 0d;
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
                if (primaryStyle == null)
                {
                    primaryStyle = btnStart.Style;     // PrimaryButtonStyle, from the XAML
                    plainStyle = btnContinue.Style;    // an ordinary button in this same bar
                }

                UpdateAreaModeUi();
                UpdateRunUi();
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
                    Dispatcher.BeginInvoke(new System.Action(() => UpdateRunUi()));
                };
            }
            return probing;
        }

        // The travel envelope and the board's extent both live in WorkSurface now - this view had its own
        // copy of the envelope maths, and so did WorkOrderCompiler, which is part of how the two came to
        // disagree about what area they were covering.

        // Size/position the probe area for the current source. Both are work-coordinate regions (the engine
        // probes in work coordinates); Full travel converts the machine envelope via the current work offset.
        private void DefaultArea()
        {
            if (Area == AreaSource.FullTravel)
            {
                var surface = WorkSurface.Current;
                double w = surface.UsableSpan(0), h = surface.UsableSpan(1);
                if (w > 0d && h > 0d && model != null)
                {
                    HeightMap.MinX = surface.UsableMin(0) - model.WorkPositionOffset.X;
                    HeightMap.MaxX = HeightMap.MinX + w;
                    HeightMap.MinY = surface.UsableMin(1) - model.WorkPositionOffset.Y;
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

        // True for the WHOLE of a run, not just the engine's part of it.
        //
        // Start was bound to IsJobRunning, which the probing engine only sets once it begins executing - so
        // through the seconds of setup before that (writing the work origin, parking at machine Z0,
        // traversing to the first point) the button stayed live and a second press would have started a
        // second run on top of the first. Stop was bound to the same flag, so it was dead over exactly that
        // window - which is when the machine is making its longest unattended moves and Stop is most wanted.
        private bool runActive = false;

        // Captured once from the buttons themselves. plainStyle is whatever a button in this bar looks like
        // WITHOUT the primary treatment - an implicit style, not necessarily null - so swapping back cannot
        // strip styling the theme applied.
        private Style primaryStyle, plainStyle;

        /// <summary>Put the run bar in the state the run is actually in.</summary>
        private void UpdateRunUi()
        {
            if (btnStart == null)
                return;

            bool busy = runActive || (model != null && model.IsJobRunning);
            bool holding = busy && probing != null && probing.IsPaused;

            btnStart.IsEnabled = !busy;
            btnStop.IsEnabled = busy;
            btnContinue.IsEnabled = holding;

            // The blue button is whichever one the operator is meant to press next. Idle, that is Start;
            // holding for the plate to be moved, it is Continue - the one moment in the run where the machine
            // is waiting on a person, and it should be obvious which button ends the wait.
            if (primaryStyle != null)
            {
                btnStart.Style = holding ? plainStyle : primaryStyle;
                btnContinue.Style = holding ? primaryStyle : plainStyle;
            }
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (runActive)
                return;   // a second Start must not stack a run on top of the one already going

            runActive = true;
            UpdateRunUi();

            try
            {
                StartProbing();
            }
            catch (Exception ex)
            {
                AppDialogs.Show(Loc("HmStartError") + "\r\n\r\n" + ex.Message, "Height map", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // In a finally so an early return, a cancelled run and a thrown exception all leave the bar
                // usable again. A Start button stuck disabled is its own kind of hang.
                runActive = false;
                UpdateRunUi();
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

            // Feeds, search distance and latch come from the SELECTED PROBE, not from this tab.
            //
            // ProbingViewModel already copies all four off the probe definition when one is selected, and this
            // page then overrode three of them with its own boxes - so a probe carefully set up in Machine
            // Setup was ignored here, and the settings had to be kept in agreement by hand in two places.
            //
            // Forcing LatchDistance = 0 in particular removed the fast-search-then-slow-retouch the probe
            // definition asks for, which is why a touch produced no retract and no second pass: the tab had
            // disabled the behaviour, not the probe.
            //
            // The XY offset stays zeroed: that is an edge-finder's tip offset, and a height map probes
            // straight down on the spindle centreline, so applying it would shift the whole grid.
            pr.ProbeOffsetX = 0d;
            pr.ProbeOffsetY = 0d;
            pr.HeightMap.MinX = HeightMap.MinX; pr.HeightMap.MaxX = HeightMap.MaxX;
            pr.HeightMap.MinY = HeightMap.MinY; pr.HeightMap.MaxY = HeightMap.MaxY;
            // Break the X/Y grid-size lock on the ENGINE's copy before assigning. HeightMapViewModel couples
            // the two: setting GridSizeY writes GridSizeX as well while the lock is on, so assigning X then Y
            // left BOTH holding the Y value and the X axis silently gained a point - a 4x4 asked for came back
            // as 5x4, 20 probes (2026-08-19). The lock is a convenience for the operator typing one number
            // into two mm fields; it is meaningless for per-axis divisions, and for a spacing computed
            // separately for each axis it is actively wrong.
            //
            // Only the engine's instance, which nothing binds - the view's own HeightMap keeps the operator's
            // lock exactly as they set it.
            pr.HeightMap.GridSizeLockXY = false;

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

            // Full work surface starts its search from the top of travel: the first probe is the one that has
            // no idea where the board is, and starting from wherever Z happened to be parked would make the
            // required search distance depend on where the operator left the spindle.
            if (Area == AreaSource.FullTravel)
                pr.WaitForIdle("G53G0Z0");

            // Map origin = the area's min corner in the current work coordinates (e.g. G54 X0Y0).
            var startpos = new Position(pr.HeightMap.MinX, pr.HeightMap.MinY, 0d);

            if (!pr.WaitForIdle(string.Format("G90G0X{0}Y{1}", startpos.X.ToInvariantString(model.Format), startpos.Y.ToInvariantString(model.Format))))
            {
                AppDialogs.Show(string.Format(Loc("HmNotIdle"), model.GrblState.State),
                    "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
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

            // Relative probing, serpentine (flip Y direction each column) to minimise travel - the read-back
            // below matches that order.
            //
            // Two probe geometries here, and they are different problems.
            //
            // The FIRST point is a search: nothing yet knows where the board is, so it starts from the top of
            // travel and probes the whole way down, the same shape Setup uses for its first Z probe. That is
            // the only point that has to travel far.
            //
            // EVERY LATER point retracts RELATIVE to the height it just triggered at, rather than returning
            // to the parked Z. That is what lets the bit sit a fixed, small clearance above where the plate
            // will next be placed instead of climbing back to the top of travel each time - and it means the
            // subsequent probes are short, so a plate that never gets placed fails fast instead of driving
            // the full travel into bare board.
            //
            // The retract deliberately sits INSIDE the probe distance (by ProbeVariationMargin) so that a
            // board sitting proud at the next point still triggers rather than being crashed into.
            // The probe's own search distance is what each later point drops by.
            double probeDrop = pr.ProbeDistance > 0d ? pr.ProbeDistance : 5d;

            // How far to lift between points. This is a CLEARANCE - just enough for the touch plate to be
            // slid under the tool - and it must stay a small fraction of the search, because every
            // millimetre of it is a millimetre the next probe has already spent before it starts looking.
            //
            // It was probeDrop - 3, which reads sensibly for a 5mm probe depth and is nonsense for a real
            // probe definition: with the 50mm search this probe asks for, it lifted 47mm and then searched
            // 50mm, leaving three millimetres of margin for the whole board's variation plus wherever the
            // operator set the plate down. It survived fourteen points and missed the fifteenth entirely
            // (2026-08-19, PRB ...:0 after running the full 50mm without contact).
            double hover = Math.Max(2d, Math.Min(PlateClearance, probeDrop - ProbeVariationMargin));

            // How far the FIRST probe may travel down. It starts at machine Z0 (the top), so the distance
            // available is the drop from there to the safe floor - NOT the axis travel.
            //
            // It was the axis travel (135 on this machine), which from Z0 targets exactly Z-135: the far
            // limit itself. grblHAL rejects a target ON the boundary as readily as one past it, so the very
            // first probe of every run died with ALARM:2 before touching anything (2026-08-19,
            // "G38.3F70Z-135" from MPos Z 0.000). The full travel is the one distance that is guaranteed
            // unusable from the top of that travel.
            double searchZ = Math.Max(1d, -(WorkSurface.TravelMin(2) + WorkSurface.Inset()));

            CNC.Core.DebugLog.Write("heightmap", string.Format(CultureInfo.InvariantCulture,
                "run: {0} points ({1}x{2}), first probe searches {3:0.###} mm from machine Z0 (Z travel {4:0.###}, inset {5:0.###}), " +
                "later probes {6:0.###} mm with {7:0.###} mm retract, feed {8:0.###}",
                map.TotalPoints, map.SizeX, map.SizeY, searchZ, WorkSurface.AxisTravel(2), WorkSurface.Inset(),
                probeDrop, hover, pr.ProbeFeedRate));

            pr.Program.Add(string.Format("G91F{0}", pr.ProbeFeedRate.ToInvariantString()));
            double dir = 1d;
            int point = 0, points = map.SizeX * map.SizeY;
            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    ++point;

                    // Target of THIS point in the work frame, so the log names where it is going rather than
                    // the relative step that gets it there - a serpentine of G91 increments is unreadable
                    // after the fact, and "which point was it on" is the first question every failure asks.
                    double tx = HeightMap.MinX + x * map.GridX;
                    double ty = HeightMap.MinY + (dir > 0d ? y : map.SizeY - 1 - y) * map.GridY;
                    // The first point searches from the top of travel because nothing knows where the board
                    // is; every later one starts a known 15mm above it and needs only that plus the board's
                    // fall-away. Capped by the probe's own search distance, never exceeding it.
                    double thisSearch = point == 1 ? searchZ : Math.Min(probeDrop, hover + BoardVariation);

                    pr.Program.AddMessage(string.Format(CultureInfo.InvariantCulture,
                        "Probing point {0} of {1} at X{2:0.###} Y{3:0.###}, searching {4:0.###} mm down...",
                        point, points, tx, ty, thisSearch));

                    // Same three facts to the log, where they survive the next status message overwriting
                    // the line above. The search distance in particular is not visible anywhere else, and it
                    // is what a soft-limit alarm on a probe is almost always about.
                    CNC.Core.DebugLog.Write("heightmap", string.Format(CultureInfo.InvariantCulture,
                        "point {0}/{1}: target X{2:0.###} Y{3:0.###} (work), probe down {4:0.###} mm, retract {5:0.###} mm",
                        point, points, tx, ty, thisSearch, hover));

                    // Hold before each point (never the first - the operator is already standing at it) so a
                    // touch plate can be moved to the next spot. Without this the run rapids straight on and
                    // probes bare board, which on a spoilboard means descending into something that will
                    // never make contact.
                    //
                    // The mechanism is the Probing library's own: AddPause sets IsPaused and the run resumes
                    // on Cycle Start (or the Continue button). This view had never called it, so the whole
                    // movable-plate workflow was unavailable here even though the engine and the view model
                    // property both existed.
                    if (HeightMap.AddPause && point > 1)
                        pr.Program.AddPause();


                    // AddProbingAction composes the distance into the program text as it is added, so setting
                    // ProbeDistance here varies it PER POINT even though it is a single view-model property.
                    pr.ProbeDistance = thisSearch;
                    pr.Program.AddProbingAction(AxisFlags.Z, true);

                    // Relative retract from wherever this point triggered - NOT AddRapidToMPos(StartPosition),
                    // which would climb back to the parked height after every single point.
                    pr.Program.AddRapid(string.Format("Z{0}", hover.ToInvariantString(model.Format)));

                    if (y < map.SizeY - 1)
                        pr.Program.AddRapid(string.Format("Y{0}", (map.GridY * dir).ToInvariantString(model.Format)));
                }
                if (x < map.SizeX - 1)
                    pr.Program.AddRapid(string.Format("X{0}", map.GridX.ToInvariantString(model.Format)));
                dir *= -1d;
            }

            // Publish the program so it can be READ before it moves anything - the Generate half of the
            // Generate/Run idiom, without changing how it runs. Same seam Start Job publishes its "Setup"
            // program through, so it lands in the same place, is saved as a generated copy alongside the
            // others, and needs no hosting of its own here.
            //
            // The listing is a RENDERING, not the literal program: the engine's own markers are not g-code
            // and would be dropped or mangled by the viewer's parser, and a preview that quietly omits lines
            // is worse than none. See PreviewText.
            ShowProgram(PreviewText(pr.Program.ToString()));

            // Show what is about to be sent, at the moment it is about to be sent. The operator pressed
            // Start looking at the Steps text; what matters from here is the program.
            if (tabView != null && tabProgram != null)
                tabView.SelectedItem = tabProgram;

            // The whole program, numbered, before a byte of it goes out.
            //
            // Written after being unable to say from the logs WHICH step a stalled run was sitting on: the
            // wire shows what was sent, and the response log shows what came back, but neither shows what the
            // engine INTENDED to send next - so a step that is consumed without reaching the wire (a message,
            // a pause, or a command that never made it) is invisible from both. With the program on record,
            // "step 4 produced no traffic" names the line instead of inviting a theory about it.
            var programLines = pr.Program.ToString().Split(new[] { char.ConvertFromUtf32(10) }, StringSplitOptions.None);
            for (int i = 0; i < programLines.Length; i++)
                CNC.Core.DebugLog.Write("heightmap", string.Format("program[{0}] = {1}", i, programLines[i]));

            pr.Program.Execute(true);
            model.Message = string.Empty;   // clear the stale "Probing point N of M..." progress line

            // Build the map BEFORE End(): End() unsubscribes the engine's probe handlers, and doing it first could
            // drop the final captured point. End() afterwards does the cleanup the engine skips on success - clears
            // IsJobRunning (UI was left locked), unsubscribes, and restores absolute mode (probing ran in G91).
            //
            // BuildMap must not TALK to the operator while that is still pending, though. It used to raise the
            // short-capture warning itself, which is modal - so a cancelled run sat with a dialog up, End()
            // unreached and IsJobRunning still set, and the app then refused to shut down for the build script
            // ("asked to close gracefully and is still up after 60s") long after the machine had gone idle.
            // A dialog nobody has dismissed is not a reason to still be busy.
            string mapWarning;
            BuildMap(pr, map, out mapWarning);
            pr.Program.End(string.Empty);

            if (mapWarning != null)
                AppDialogs.Show(mapWarning, "Height map", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Full work surface finishes the job it started: the origin it planted at the corner now gets its
            // Z, taken from the HIGHEST point measured. Surfacing can then be asked for a depth and run - the
            // thing this mode exists to make possible without a Setup pass or a touch-off.
            if (Area == AreaSource.FullTravel && HeightMap.HasHeightMap)
                SetWorkZeroToHighest(pr);
        }

        // ---- full work surface: origin handling ----

        /// <summary>
        /// Whether the active work offset already places X0 Y0 at the given machine position.
        ///
        /// Compared to the tolerance the offset is WRITTEN at (3 decimals), not to an exact double equality:
        /// the value makes a round trip through g-code text and back through the status report, so the bits
        /// that come home are not the bits that went out.
        /// </summary>
        private bool OriginMatches(double ox, double oy)
        {
            if (model == null)
                return false;
            return Math.Abs(model.WorkPositionOffset.X - ox) < 0.001d &&
                   Math.Abs(model.WorkPositionOffset.Y - oy) < 0.001d;
        }

        /// <summary>
        /// One probed point, as a line worth reading months later: where it is on the grid, where it is on
        /// the table, the board height there, and how far that sits below the highest point.
        ///
        /// The height has the touch plate taken off, so it is the BOARD, matching the Z0 this run sets - a
        /// log that disagreed with the origin by a plate thickness would be worse than no log.
        /// </summary>
        private string DescribeProbedPoint(int n, int col, int row, CNC.Controls.Probing.HeightMap map,
                                           double machineZ, double firstZ, double plateOff)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "point {0,2} [{1},{2}] X{3,8:0.###} Y{4,8:0.###}   Z {5,9:0.###}   {6,+7:0.###} vs first",
                n, col, row,
                HeightMap.MinX + col * map.GridX,
                HeightMap.MinY + row * map.GridY,
                machineZ - plateOff,
                machineZ - firstZ);
        }

        // Hosted in this page's own Program tab, NOT through MacroProcessor.PublishGenerated.
        //
        // PublishGenerated routes to MainWindow's overlay, which is right for a tab that lives in the main
        // window and wrong for this one: Height Map is menu-hostable, and when it opens in its own
        // ViewHostWindow the overlay renders on the main window - behind the window being looked at. The
        // program was published correctly and simply could not be seen (2026-08-19).
        //
        // AutoShow off for the same reason: nothing here should try to pop the main window's overlay.
        private CNC.Controls.ProgramView programView;

        private void ShowProgram(string text)
        {
            if (programHost == null)
                return;

            if (programView == null)
            {
                programView = new CNC.Controls.ProgramView { Title = "Height map", AutoShow = false };
                programHost.Content = programView;
            }

            programView.SetProgramText(text);

            // Keep a generated copy alongside the other tabs' programs. Worth having on its own: a probing
            // run that drives the length of the Z axis should leave a record of exactly what it sent.
            CNC.Core.MacroRunner.SaveGeneratedCopy("Height map", text);
        }

        /// <summary>
        /// Render the probing engine's program as readable g-code.
        ///
        /// Three of its lines are engine markers rather than g-code, and handing them to a g-code viewer
        /// would get them dropped or misparsed - which in a PREVIEW is the worst possible failure, because
        /// the operator reads it as "this is what will run":
        ///
        ///   "#text"   a status message      -&gt; shown as a comment
        ///   "pause"   hold for the operator -&gt; shown as a comment naming what it waits for
        ///   "!G0Z2"   a move whose captured position is discarded (the latch retract between the fast probe
        ///             and the slow re-touch) -&gt; shown as the move, annotated
        ///
        /// Everything else passes through untouched, so what is listed is what is sent.
        /// </summary>
        private static string PreviewText(string program)
        {
            var sb = new System.Text.StringBuilder();

            foreach (var raw in (program ?? string.Empty).Split(new[] { char.ConvertFromUtf32(10) }, StringSplitOptions.None))
            {
                string line = raw.TrimEnd();

                if (line.Length == 0)
                    continue;
                else if (line.StartsWith("#"))
                    sb.AppendLine("(" + line.Substring(1) + ")");
                else if (line == "pause")
                    sb.AppendLine("(HOLD - reposition the touch plate, then press Continue)");
                else if (line.StartsWith("!"))
                    sb.AppendLine(line.Substring(1) + " (retract between the fast probe and the slow re-touch)");
                else
                    sb.AppendLine(line);
            }

            return sb.ToString();
        }

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
            // The BOARD's extent, which is not the machine's reach whenever something (a toolsetter) is
            // mounted off the edge of the board - see WorkSurface. Undefined means the whole table.
            var surface = WorkSurface.Current;
            double w = surface.UsableSpan(0), h = surface.UsableSpan(1);
            if (w <= 0d || h <= 0d)
            {
                AppDialogs.Show("The work surface does not describe a usable area. Check the machine travel limits ($130-$132), and the work surface size in Machine Setup if one is set.",
                                "Height map", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            double ox = surface.UsableMin(0), oy = surface.UsableMin(1);
            string wcs = ActiveWcs();

            if (AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "Full work surface will set the {0} work origin.\n\n" +
                    "X0 Y0 moves to the table corner (machine X{1:0.###} Y{2:0.###}), and after probing, Z0 is set to the " +
                    "highest point found.\n\n" +
                    "Any work origin {0} is holding now will be overwritten. Continue?",
                    wcs, ox, oy),
                    "Height map", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;

            // Whether the origin is ALREADY where this run wants it - which it is on every run after the
            // first, and which decides whether there is anything to wait for below.
            bool already = OriginMatches(ox, oy);

            model.ExecuteCommand(string.Format(CultureInfo.InvariantCulture, "G10L2P{0}X{1:0.###}Y{2:0.###}", ActiveWcsP(), ox, oy));

            // The offset has to be in effect before the area below is read in the new frame - a grid computed
            // against the OLD offset would be placed off the table by however far the origin just moved.
            //
            // Wait only when the value is actually going to CHANGE. OnWCOUpdated fires on change, not on
            // write, so re-writing an origin that already holds the wanted value produces no event at all -
            // and waiting for one then failed the whole run after a four second pause, on the very case
            // where nothing was wrong (2026-08-19: WCO 7.000,-780.000 written again as 7,-780).
            var pr = EnsureProbing();
            if (!already && pr != null)
                pr.WaitForWcoUpdate();

            // Then gate on the VALUE, never on the event. This is the question actually being asked - "is
            // the origin where I need it" - and it answers correctly whether the write changed anything,
            // changed it before the wait was armed, or had nothing to change.
            if (!OriginMatches(ox, oy))
            {
                AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                        "The work origin did not take: {0} still reads X{1:0.###} Y{2:0.###}, not X{3:0.###} Y{4:0.###}. Nothing has been probed.",
                        wcs, model.WorkPositionOffset.X, model.WorkPositionOffset.Y, ox, oy),
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

            // The probe stopped on the plate's top face; the board is a plate-thickness below that. See
            // PlateThickness() - zero for a 3D probe, which touches the surface itself.
            double plate = PlateThickness();
            double zHigh = pr.Positions.Max(x => x.Z) - plate, zLow = pr.Positions.Min(x => x.Z) - plate;
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
        private void BuildMap(ProbingViewModel pr, CNC.Controls.Probing.HeightMap map, out string warning)
        {
            warning = null;
            model.ResponseLog.Add(string.Format("HeightMap: captured {0} of {1} points", pr.Positions.Count, map.TotalPoints));

            // Build from the probed points as long as we captured one per grid point - tolerate IsSuccess being
            // cleared by a late probe-release event after the final point. Report clearly either way.
            if (pr.Positions.Count != map.TotalPoints)
            {
                // Handed back rather than shown - see the call site. Telling the operator has to wait until
                // the run has actually been ended.
                warning = string.Format(Loc("HmCaptureShort"),
                    pr.Positions.Count, map.TotalPoints, string.IsNullOrEmpty(pr.Message) ? "" : "\r\n\r\n" + pr.Message);
                return;
            }

            double z0 = pr.Positions[0].Z;
            int i = 0;

            // Grid cell each reading landed in, in probe order, so the log can name a point by where it is
            // rather than by when it happened. Filled by the same serpentine walk that populates the map, so
            // the two cannot disagree about which reading belongs where.
            var probed = new List<string>();
            double plateOff = PlateThickness();

            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    probed.Add(DescribeProbedPoint(probed.Count + 1, x, y, map, pr.Positions[i].Z, z0, plateOff));
                    map.AddPoint(x, y, Math.Round(pr.Positions[i++].Z - z0, model.Precision));
                }
                if (++x < map.SizeX)
                    for (int y = map.SizeY - 1; y >= 0; y--)
                    {
                        probed.Add(DescribeProbedPoint(probed.Count + 1, x, y, map, pr.Positions[i].Z, z0, plateOff));
                        map.AddPoint(x, y, Math.Round(pr.Positions[i++].Z - z0, model.Precision));
                    }
            }

            // Every reading into the status log, not just the range. The range answers "how much has to come
            // off"; the individual heights answer "where is it high", which is the question you have as soon
            // as a surfacing pass leaves something behind - and the map's 3D view cannot be read to a
            // hundredth. Written straight to StatusLog rather than through Message, which would flash sixteen
            // lines through the status line to say them.
            double hi = pr.Positions.Max(q => q.Z) - plateOff, lo = pr.Positions.Min(q => q.Z) - plateOff;
            CNC.Core.StatusLog.Write("info", "heightmap", string.Format(CultureInfo.InvariantCulture,
                "{0} points probed, {1} x {2} grid{3}", map.TotalPoints, map.SizeX, map.SizeY,
                plateOff > 0d ? string.Format(CultureInfo.InvariantCulture, " (touch plate {0:0.###} mm removed from every reading)", plateOff) : string.Empty));

            foreach (var line in probed)
                CNC.Core.StatusLog.Write("info", "heightmap", line);

            CNC.Core.StatusLog.Write("info", "heightmap", string.Format(CultureInfo.InvariantCulture,
                "highest {0:0.###}, lowest {1:0.###}, range {2:0.###} mm", hi, lo, hi - lo));

            HeightMap.Map = map;
            HeightMap.HasHeightMap = true;
            HeightMap.CanApply = model.IsFileLoaded;
            RefreshSurface();

            // A finished run has nothing more to say about the program - the result is the surface. Only on
            // SUCCESS: a run that fell short leaves the program up, which is where the failure is legible.
            if (tabView != null && tabSurface != null)
                tabView.SelectedItem = tabSurface;
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
            // Nothing to seed any more: feeds, search distance and latch are read from the selected probe at
            // run time (see StartProbing) rather than copied into fields on this page that then had to be
            // kept in agreement with it by hand.
        }
    }
}
