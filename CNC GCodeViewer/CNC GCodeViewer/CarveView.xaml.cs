/*
 * CarveView.xaml.cs - part of CNC GCodeViewer
 *
 * A live 3D machine view with local g-code playback, in WORK coordinates: the work envelope, a stock
 * block sized to the loaded program, the program's toolpath, and a cone at the tool position - following
 * the live work position, or, on Play, a local simulation of the program (no controller motion). Phase 2
 * of the carve view (see docs/3D-Carve-View-Design.md); real-time material removal is added in Phase 3.
 * Registered as the Job tab's "3D View" center component.
 *
 * Coordinates: everything is in WORK coordinates so the toolpath, playback, stock and live cone all align.
 * The toolpath/playback are the program's own (work) coordinates; the live cone uses WorkPosition; the
 * machine envelope ($130-$132) is drawn shifted by the work offset (WorkPositionOffset) into work space.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls.Viewer
{
    public partial class CarveView : UserControl
    {
        private struct Seg { public Point3D A, B; public bool Rapid; public double Len; public double Radius; }

        // Tool info parsed from the program's (TOOL T=n D=d TYPE=FLAT|BALL|VBIT [A=angle]) comments (the Fusion
        // ioSenderBatchPost emits these). Maps tool number -> cutter geometry; drives the carve radius + cone.
        private struct ToolInfo { public double Dia; public string Shape; public double Angle; }
        private readonly Dictionary<int, ToolInfo> tools = new Dictionary<int, ToolInfo>();
        private double defaultToolRadius = 3d;
        private double stockZ;            // stock thickness from the program's (STOCK ... Z=..) comment; 0 = unknown
        private static readonly Regex rxTool =
            new Regex(@"\(\s*TOOL\s+T=(\d+)\s+D=([0-9.]+)\s+TYPE=(\w+)(?:\s+A=([0-9.]+))?", RegexOptions.IgnoreCase);
        private static readonly Regex rxStock =
            new Regex(@"\(\s*STOCK\s+X=([0-9.]+)\s+Y=([0-9.]+)\s+Z=([0-9.]+)", RegexOptions.IgnoreCase);

        private GrblViewModel model;
        private Position wpos;           // live WORK position (drives the cone when not playing)
        private TruncatedConeVisual3D toolCone;
        private bool framed;

        // ---- program / stock ----
        private readonly List<Seg> segs = new List<Seg>();
        private int builtCount = -1;          // token count of the program the toolpath was last built from
        private string builtName;             // FileName likewise (Parser.Tokens is a reused list, so we can't
                                              // compare by reference - detect a new program by name + count)
        private LinesVisual3D cutLines, rapidLines;
        private Point3D bMin, bMax;     // program bounding box (work coords)
        private bool haveBox;

        // ---- playback ----
        private int segIdx;
        private double segPos;          // distance travelled into the current segment
        private bool playing;
        private double speedMul = 1d;
        private const double BaseSpeed = 40d;            // mm/s at 1x
        private const double TickSeconds = 0.033d;       // ~30 fps
        private DispatcherTimer timer;

        // ---- material removal (dexel heightmap) ----
        private double[,] hmap;                          // per-cell top-Z height of the stock
        private int hnx, hny;
        private double hx0, hy0, hcell, htop, hbot;
        private MeshGeometry3D carveMesh;
        private ModelVisual3D carveVisual;
        private Point3D lastPos;                         // previous cutter position (swept-path carve)
        private bool haveLast, carveDirty;
        private int rebuildSkip;

        public CarveView()
        {
            InitializeComponent();
        }

        private void CarveView_Loaded(object sender, RoutedEventArgs e)
        {
            if (model == null && DataContext is GrblViewModel m)
            {
                model = m;
                wpos = model.WorkPosition;
                if (wpos != null)
                    wpos.PropertyChanged += Wpos_PropertyChanged;
                model.PropertyChanged += Model_PropertyChanged;
            }
            viewport.SizeChanged += (s, ev) => FrameIfNeeded();   // frame once the viewport has a render size
            ScheduleBuild();
        }

        // Build the scene on a fresh dispatcher cycle. BuildToolpath runs GCodeEmulator.Execute, which pumps the
        // dispatcher (Machine.Reset -> GrblParserState.Get -> DoEvents); doing that directly from a layout/focus
        // pass (e.g. the IsVisibleChanged raised while a TabItem takes focus) throws "dispatcher processing is
        // suspended". Deferring runs it after the pass. The flag coalesces repeated requests.
        private bool buildScheduled;
        private void ScheduleBuild()
        {
            if (buildScheduled)
                return;
            buildScheduled = true;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                buildScheduled = false;
                if (IsVisible)
                    BuildScene();
            }), DispatcherPriority.Background);
        }

        private void CarveView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (wpos != null)
                wpos.PropertyChanged -= Wpos_PropertyChanged;
            if (model != null)
                model.PropertyChanged -= Model_PropertyChanged;
            timer?.Stop();
        }

        // A program load (FileName change) while the view is open: rebuild the toolpath/stock.
        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GrblViewModel.FileName) && IsVisible)
                ScheduleBuild();
        }

        // The controller settings/offsets that size + place the envelope and the loaded program may arrive after
        // this control is built, so rebuild the scene each time the view is shown.
        private void CarveView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
                ScheduleBuild();
        }

        private void Wpos_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            UpdateTool();
        }

        private static double Travel(int axis)
        {
            double t = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + axis);
            return double.IsNaN(t) ? 0d : Math.Abs(t);
        }

        // +machine direction away from home (mirrors SurfaceSpoilboard): with force-set-origin the home corner
        // sits at machine 0 and travel is positive on the homing-dir axes; otherwise grbl keeps travel <= 0.
        private static double AxisDir(int axis)
        {
            if (GrblInfo.ForceSetOrigin)
                return GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(axis)) ? 1d : -1d;
            return -1d;
        }

        private static double EnvMin(int axis) { return AxisDir(axis) > 0d ? 0d : -Travel(axis); }
        private static double EnvMax(int axis) { return AxisDir(axis) > 0d ? Travel(axis) : 0d; }

        // Work offset (machine = work + WCO), 0 when unknown - used to draw the machine envelope in work space.
        private double Wco(int axis)
        {
            var o = model?.WorkPositionOffset;
            if (o == null)
                return 0d;
            double v = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
            return double.IsNaN(v) ? 0d : v;
        }

        private void BuildScene()
        {
            if (viewport == null)
                return;

            viewport.Children.Clear();
            viewport.Children.Add(new DefaultLights());

            // machine envelope shifted into work coordinates
            double xmin = EnvMin(0) - Wco(0), xmax = EnvMax(0) - Wco(0);
            double ymin = EnvMin(1) - Wco(1), ymax = EnvMax(1) - Wco(1);
            double zmin = EnvMin(2) - Wco(2), zmax = EnvMax(2) - Wco(2);
            double xs = Math.Max(xmax - xmin, 1d), ys = Math.Max(ymax - ymin, 1d), zs = Math.Max(zmax - zmin, 1d);

            viewport.Children.Add(new BoundingBoxWireFrameVisual3D
            {
                BoundingBox = new Rect3D(xmin, ymin, zmin, xs, ys, zs),
                Color = Colors.DimGray,
                Thickness = 1d
            });

            // bed grid at the envelope floor
            viewport.Children.Add(new GridLinesVisual3D
            {
                Center = new Point3D((xmin + xmax) / 2d, (ymin + ymax) / 2d, zmin),
                LengthDirection = new Vector3D(1, 0, 0),
                Normal = new Vector3D(0, 0, 1),
                Length = xs,
                Width = ys,
                MinorDistance = 50d,
                MajorDistance = 100d,
                Thickness = 0.6d,
                Fill = Brushes.Gray
            });

            // stock: the solid carve mesh (deforms as the cutter passes) when a program is loaded; otherwise a
            // plain default block. Only one of them is shown so there is no z-fighting/see-through.
            BuildToolpath();
            if (carveVisual != null)
                viewport.Children.Add(carveVisual);
            else
                AddStock();

            // tool cone - tip at the cutter, widening upward
            toolCone = new TruncatedConeVisual3D
            {
                Origin = new Point3D(0d, 0d, 0d),
                Normal = new Vector3D(0, 0, 1),
                Height = Math.Max(zs * 0.2d, 15d),
                BaseRadius = 0d,
                TopRadius = Math.Max(defaultToolRadius, 0.5d),   // real cutter radius from the program's (TOOL ...)
                Fill = Brushes.OrangeRed
            };
            viewport.Children.Add(toolCone);

            UpdateTool();
            FrameIfNeeded();
        }

        // Frame the scene once - but only after the viewport has a render size. ZoomExtents before layout frames a
        // degenerate box (it ends up zoomed onto the tool tip). Re-armed (framed=false) when a program loads, and
        // also driven from the viewport's SizeChanged so the first real layout triggers it.
        private void FrameIfNeeded()
        {
            if (framed || viewport == null || viewport.ActualWidth < 1d || viewport.ActualHeight < 1d)
                return;
            framed = true;
            viewport.ZoomExtents(0);
        }

        // The stock block: the program's XY bounding box plus a margin, from the deepest cut up to work Z0.
        private void AddStock()
        {
            double margin = 6d, top, bottom, cx, cy, sx, sy;

            if (haveBox)
            {
                top = Math.Max(bMax.Z, 0d);
                bottom = Math.Min(bMin.Z, top - 1d);
                sx = (bMax.X - bMin.X) + 2d * margin;
                sy = (bMax.Y - bMin.Y) + 2d * margin;
                cx = (bMin.X + bMax.X) / 2d;
                cy = (bMin.Y + bMax.Y) / 2d;
            }
            else
            {
                top = 0d; bottom = -19d; sx = sy = 150d; cx = cy = 0d;
            }

            double h = Math.Max(top - bottom, 1d);
            viewport.Children.Add(new BoxVisual3D
            {
                Center = new Point3D(cx, cy, bottom + h / 2d),
                Length = Math.Max(sx, 1d),
                Width = Math.Max(sy, 1d),
                Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(80, 150, 100, 55))   // translucent brown stock
            });
        }

        // Build the ordered motion (segs) + the cut/rapid toolpath lines + bounding box from the loaded program.
        // Rebuilds only when the program (token list) changes; otherwise re-adds the cached line visuals.
        private void BuildToolpath()
        {
            var tokens = GCode.File.Tokens;
            int cnt = tokens?.Count ?? 0;
            string name = model?.FileName ?? string.Empty;

            if (cnt != builtCount || name != builtName)
            {
                builtCount = cnt;
                builtName = name;
                segs.Clear();
                haveBox = false;
                StopPlayback();

                var cut = new Point3DCollection();
                var rapid = new Point3DCollection();

                ParseTools();
                double curRad = defaultToolRadius;

                if (tokens != null)
                {
                    var emu = new GCodeEmulator(true);   // translate canned cycles / G28 / G30 into moves
                    emu.SetStartPosition(new Point3D(0d, 0d, 0d));

                    foreach (var a in emu.Execute(tokens))
                    {
                        switch (a.Token.Command)
                        {
                            case Commands.ToolSelect:
                            case Commands.M61:
                                if (a.Token is GCToolSelect ts && tools.TryGetValue(ts.Tool, out var ti))
                                    curRad = Math.Max(ti.Dia / 2d, 0.1d);
                                break;
                            case Commands.G0:
                                AddSeg(a.Start, a.End, true, curRad, cut, rapid);
                                break;
                            case Commands.G1:
                                AddSeg(a.Start, a.End, false, curRad, cut, rapid);
                                break;
                            case Commands.G2:
                            case Commands.G3:
                                var pts = (a.Token as GCArc).GeneratePoints(emu.Plane, a.Start.ToArray(), 5, emu.DistanceMode == DistanceMode.Incremental);
                                var p = a.Start;
                                foreach (var q in pts)
                                {
                                    AddSeg(p, q, false, curRad, cut, rapid);
                                    p = q;
                                }
                                break;
                            default:
                                if (!a.End.Equals(a.Start))
                                    AddSeg(a.Start, a.End, a.IsRetract, curRad, cut, rapid);
                                break;
                        }
                    }
                }

                cutLines = new LinesVisual3D { Color = Color.FromRgb(20, 90, 210), Thickness = 1.4d, Points = cut };    // blue carve trails
                rapidLines = new LinesVisual3D { Color = Color.FromRgb(160, 160, 160), Thickness = 0.6d, Points = rapid };

                InitHeightmap();   // fresh stock surface sized to the new program
                framed = false;    // re-frame the camera to the new program/stock on the next build
            }

            if (rapidLines != null)
                viewport.Children.Add(rapidLines);
            if (cutLines != null)
                viewport.Children.Add(cutLines);
        }

        private void AddSeg(Point3D a, Point3D b, bool rapid, double radius, Point3DCollection cut, Point3DCollection rapidColl)
        {
            double len = (b - a).Length;
            segs.Add(new Seg { A = a, B = b, Rapid = rapid, Len = len, Radius = radius });

            var coll = rapid ? rapidColl : cut;
            coll.Add(a);
            coll.Add(b);

            Grow(a);
            Grow(b);
        }

        // Parse the program's (TOOL T=n D=d TYPE=t [A=a]) comment lines into the tool table; defaultToolRadius is
        // the lowest-numbered tool's radius (used before the first tool change and for the live cone).
        private void ParseTools()
        {
            tools.Clear();
            defaultToolRadius = 3d;
            stockZ = 0d;

            var data = GCode.File.Data;
            if (data == null)
                return;

            foreach (var b in data)
            {
                string line = b.Data ?? string.Empty;

                var ms = rxStock.Match(line);
                if (ms.Success)
                    double.TryParse(ms.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out stockZ);

                var m = rxTool.Match(line);
                if (!m.Success)
                    continue;
                if (int.TryParse(m.Groups[1].Value, out int t) &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    double ang = 0d;
                    if (m.Groups[4].Success)
                        double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ang);
                    tools[t] = new ToolInfo { Dia = d, Shape = m.Groups[3].Value.ToUpperInvariant(), Angle = ang };
                }
            }

            int lowest = int.MaxValue;
            foreach (var kv in tools)
                if (kv.Key < lowest)
                {
                    lowest = kv.Key;
                    defaultToolRadius = Math.Max(kv.Value.Dia / 2d, 0.1d);
                }
        }

        // Cut moves define the part footprint; rapids (often above the stock at safe Z) don't grow the stock.
        private void Grow(Point3D p)
        {
            if (!haveBox)
            {
                bMin = bMax = p;
                haveBox = true;
                return;
            }
            bMin = new Point3D(Math.Min(bMin.X, p.X), Math.Min(bMin.Y, p.Y), Math.Min(bMin.Z, p.Z));
            bMax = new Point3D(Math.Max(bMax.X, p.X), Math.Max(bMax.Y, p.Y), Math.Max(bMax.Z, p.Z));
        }

        // ---- dexel material removal ----

        // Allocate the stock heightmap (cells start at the stock top) + the surface mesh, sized to the program.
        private void InitHeightmap()
        {
            carveVisual = null;
            carveMesh = null;
            hmap = null;
            haveLast = false;
            carveDirty = false;
            if (!haveBox)
                return;

            const double margin = 6d;
            double x0 = bMin.X - margin, y0 = bMin.Y - margin, x1 = bMax.X + margin, y1 = bMax.Y + margin;
            htop = 0d;                                       // stock top = work Z0 (the material top); rapids above don't cut
            hbot = stockZ > 0d ? -stockZ : Math.Min(bMin.Z, -1d);
            hbot = Math.Min(hbot, bMin.Z);                  // include any cut deeper than the stock thickness
            if (hbot >= htop) hbot = htop - 1d;

            double w = Math.Max(x1 - x0, 1d), h = Math.Max(y1 - y0, 1d);
            const int maxCells = 150;
            hcell = Math.Max(Math.Max(w, h) / maxCells, 0.5d);
            hnx = Math.Max(1, (int)Math.Ceiling(w / hcell));
            hny = Math.Max(1, (int)Math.Ceiling(h / hcell));
            hx0 = x0;
            hy0 = y0;

            hmap = new double[hnx + 1, hny + 1];
            for (int i = 0; i <= hnx; i++)
                for (int j = 0; j <= hny; j++)
                    hmap[i, j] = htop;

            // A solid block: the (nx+1)x(ny+1) deforming top grid + 4 bottom corners, with side walls and a
            // bottom face so it reads as a solid stock (no see-through). Indices are fixed; only Positions change.
            int stride = hnx + 1;
            int topN = stride * (hny + 1);
            int B0 = topN, B1 = topN + 1, B2 = topN + 2, B3 = topN + 3;   // bottom corners (x0y0,x1y0,x0y1,x1y1)
            int TL = 0, TR = hnx, BL = hny * stride, BR = hny * stride + hnx;

            var tris = new Int32Collection(hnx * hny * 6 + 36);
            for (int j = 0; j < hny; j++)
                for (int i = 0; i < hnx; i++)
                {
                    int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            AddQuad(tris, TL, TR, B1, B0);   // front side (y0)
            AddQuad(tris, BR, BL, B2, B3);   // back side  (y1)
            AddQuad(tris, BL, TL, B0, B2);   // left side  (x0)
            AddQuad(tris, TR, BR, B3, B1);   // right side (x1)
            AddQuad(tris, B0, B1, B3, B2);   // bottom

            carveMesh = new MeshGeometry3D { TriangleIndices = tris };
            var model = new GeometryModel3D
            {
                Geometry = carveMesh,
                Material = MaterialHelper.CreateMaterial(Color.FromRgb(155, 105, 60)),       // brown stock surface
                BackMaterial = MaterialHelper.CreateMaterial(Color.FromRgb(120, 80, 45))
            };
            carveVisual = new ModelVisual3D { Content = model };
            RebuildMesh();
        }

        private static void AddQuad(Int32Collection t, int a, int b, int c, int d)
        {
            t.Add(a); t.Add(b); t.Add(c);
            t.Add(a); t.Add(c); t.Add(d);
        }

        // Rebuild the solid stock positions from the heightmap: the deforming top grid + 4 bottom corners
        // (indices fixed, normals auto by WPF).
        private void RebuildMesh()
        {
            if (carveMesh == null || hmap == null)
                return;

            var pos = new Point3DCollection((hnx + 1) * (hny + 1) + 4);
            for (int j = 0; j <= hny; j++)
                for (int i = 0; i <= hnx; i++)
                    pos.Add(new Point3D(hx0 + i * hcell, hy0 + j * hcell, hmap[i, j]));

            double x1 = hx0 + hnx * hcell, y1 = hy0 + hny * hcell;
            pos.Add(new Point3D(hx0, hy0, hbot));   // B0
            pos.Add(new Point3D(x1, hy0, hbot));    // B1
            pos.Add(new Point3D(hx0, y1, hbot));    // B2
            pos.Add(new Point3D(x1, y1, hbot));     // B3

            carveMesh.Positions = pos;
        }

        // Reset the stock to an uncut block (e.g. on Stop, before a replay).
        private void ResetStock()
        {
            if (hmap == null)
                return;
            for (int i = 0; i <= hnx; i++)
                for (int j = 0; j <= hny; j++)
                    hmap[i, j] = htop;
            haveLast = false;
            carveDirty = false;
            RebuildMesh();
        }

        // Carve the swept path from the previous cutter position to p (flat tool of the cone's radius).
        private void CarveTo(Point3D p)
        {
            if (hmap != null && haveLast)
                CarveSegment(lastPos, p, toolCone != null ? toolCone.TopRadius : 3d);
            lastPos = p;
            haveLast = true;
        }

        private void CarveSegment(Point3D a, Point3D b, double r)
        {
            double dist = (b - a).Length;
            int steps = Math.Max(1, (int)(dist / (hcell * 0.5d)));
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                double pz = a.Z + (b.Z - a.Z) * t;
                if (pz >= htop)
                    continue;   // above the stock - no cut
                CarveDisc(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, Math.Max(pz, hbot), r);
            }
        }

        private void CarveDisc(double px, double py, double pz, double r)
        {
            int i0 = (int)Math.Floor((px - r - hx0) / hcell), i1 = (int)Math.Ceiling((px + r - hx0) / hcell);
            int j0 = (int)Math.Floor((py - r - hy0) / hcell), j1 = (int)Math.Ceiling((py + r - hy0) / hcell);
            if (i0 < 0) i0 = 0;
            if (j0 < 0) j0 = 0;
            if (i1 > hnx) i1 = hnx;
            if (j1 > hny) j1 = hny;

            double r2 = r * r;
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    double dx = (hx0 + i * hcell) - px, dy = (hy0 + j * hcell) - py;
                    if (dx * dx + dy * dy <= r2 && hmap[i, j] > pz)
                    {
                        hmap[i, j] = pz;
                        carveDirty = true;
                    }
                }
        }

        // The cone follows the live work position when not simulating; playback owns it while playing.
        private void UpdateTool()
        {
            if (playing || toolCone == null || wpos == null)
                return;

            double x = wpos.X, y = wpos.Y, z = wpos.Z;
            if (double.IsNaN(x)) x = 0d;
            if (double.IsNaN(y)) y = 0d;
            if (double.IsNaN(z)) z = 0d;

            var p = new Point3D(x, y, z);
            toolCone.Origin = p;
            CarveTo(p);                  // live machine motion carves the stock too
            if (carveDirty)              // poll rate is low, so refresh the surface each update
            {
                RebuildMesh();
                carveDirty = false;
            }
        }

        // ---- playback control ----

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (segs.Count == 0)
                return;
            playing = true;
            SetToolpathVisible(false);   // hide the blue toolpath while simulating - just show the cut
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickSeconds * 1000d) };
                timer.Tick += (s, ev) => StepPlayback();
            }
            timer.Start();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            playing = false;
            timer?.Stop();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
            ResetStock();                       // uncut block again, ready for a fresh replay
            SetToolpathVisible(true);           // show the toolpath again
            if (segs.Count > 0 && toolCone != null)
                toolCone.Origin = segs[0].A;    // back to the program start
        }

        // Show/hide the cut + rapid toolpath lines (hidden during playback so the carve is unobstructed).
        private void SetToolpathVisible(bool show)
        {
            SetChild(cutLines, show);
            SetChild(rapidLines, show);
        }

        private void SetChild(System.Windows.Media.Media3D.Visual3D v, bool show)
        {
            if (v == null || viewport == null)
                return;
            bool present = viewport.Children.Contains(v);
            if (show && !present)
                viewport.Children.Add(v);
            else if (!show && present)
                viewport.Children.Remove(v);
        }

        private void StopPlayback()
        {
            playing = false;
            timer?.Stop();
            segIdx = 0;
            segPos = 0d;
        }

        private void StepPlayback()
        {
            if (!playing || segIdx >= segs.Count)
            {
                playing = false;
                timer?.Stop();
                return;
            }

            double dist = BaseSpeed * speedMul * TickSeconds;
            while (dist > 0d && segIdx < segs.Count)
            {
                double left = segs[segIdx].Len - segPos;
                if (left <= 1e-9d)
                {
                    segIdx++;
                    segPos = 0d;
                    continue;
                }
                if (dist < left)
                {
                    segPos += dist;
                    dist = 0d;
                }
                else
                {
                    dist -= left;
                    segIdx++;
                    segPos = 0d;
                }
            }

            if (segIdx >= segs.Count)
            {
                var end = segs[segs.Count - 1].B;
                if (toolCone != null)
                    toolCone.Origin = end;
                CarveTo(end);
                RebuildMesh();           // final refresh so the last cut shows
                carveDirty = false;
                playing = false;
                timer?.Stop();
                return;
            }

            var cs = segs[segIdx];
            double t = cs.Len > 0d ? segPos / cs.Len : 0d;
            var pos = new Point3D(cs.A.X + (cs.B.X - cs.A.X) * t,
                                  cs.A.Y + (cs.B.Y - cs.A.Y) * t,
                                  cs.A.Z + (cs.B.Z - cs.A.Z) * t);
            if (toolCone != null)
            {
                toolCone.Origin = pos;
                if (cs.Radius > 0d)
                    toolCone.TopRadius = cs.Radius;   // match the active tool; CarveTo carves with this radius
            }

            CarveTo(pos);
            if (carveDirty && ++rebuildSkip >= 3)   // refresh the carved surface a few times/sec
            {
                rebuildSkip = 0;
                RebuildMesh();
                carveDirty = false;
            }
        }

        private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch (cbxSpeed.SelectedIndex)
            {
                case 1: speedMul = 2d; break;
                case 2: speedMul = 5d; break;
                case 3: speedMul = 10d; break;
                default: speedMul = 1d; break;
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            // Restore the default orientation (the XAML iso view) so Reset always lands in the same place,
            // even after switching to a side/top view, then zoom to fit.
            if (viewport.Camera is PerspectiveCamera cam)
            {
                cam.LookDirection = new Vector3D(-1d, 1d, -1d);
                cam.UpDirection = new Vector3D(0d, 0d, 1d);
            }
            viewport.ZoomExtents(0);
        }
    }
}
