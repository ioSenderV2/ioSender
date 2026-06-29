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
        private struct Seg { public Point3D A, B; public bool Rapid; public double Len; }

        private GrblViewModel model;
        private Position wpos;           // live WORK position (drives the cone when not playing)
        private TruncatedConeVisual3D toolCone;
        private bool framed;

        // ---- program / stock ----
        private readonly List<Seg> segs = new List<Seg>();
        private List<GCodeToken> builtTokens;
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
            }
            BuildScene();
        }

        private void CarveView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (wpos != null)
                wpos.PropertyChanged -= Wpos_PropertyChanged;
            timer?.Stop();
        }

        // The controller settings/offsets that size + place the envelope and the loaded program may arrive after
        // this control is built, so rebuild the scene each time the view is shown.
        private void CarveView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
                BuildScene();
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

            // stock - sized to the loaded program (so it contains the cut), top at work Z0; default block if no program
            BuildToolpath();
            AddStock();
            if (carveVisual != null)
                viewport.Children.Add(carveVisual);   // carved top surface (deforms as the cutter passes)

            // tool cone - tip at the cutter, widening upward
            toolCone = new TruncatedConeVisual3D
            {
                Origin = new Point3D(0d, 0d, 0d),
                Normal = new Vector3D(0, 0, 1),
                Height = Math.Max(zs * 0.2d, 15d),
                BaseRadius = 0d,
                TopRadius = 3d,
                Fill = Brushes.OrangeRed
            };
            viewport.Children.Add(toolCone);

            UpdateTool();

            if (!framed && (xs > 1d || segs.Count > 0))   // frame once, when there is something to see
            {
                framed = true;
                viewport.ZoomExtents(0);
            }
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

            if (tokens != builtTokens)
            {
                builtTokens = tokens;
                segs.Clear();
                haveBox = false;
                StopPlayback();

                var cut = new Point3DCollection();
                var rapid = new Point3DCollection();

                if (tokens != null)
                {
                    var emu = new GCodeEmulator(true);   // translate canned cycles / G28 / G30 into moves
                    emu.SetStartPosition(new Point3D(0d, 0d, 0d));

                    foreach (var a in emu.Execute(tokens))
                    {
                        switch (a.Token.Command)
                        {
                            case Commands.G0:
                                AddSeg(a.Start, a.End, true, cut, rapid);
                                break;
                            case Commands.G1:
                                AddSeg(a.Start, a.End, false, cut, rapid);
                                break;
                            case Commands.G2:
                            case Commands.G3:
                                var pts = (a.Token as GCArc).GeneratePoints(emu.Plane, a.Start.ToArray(), 5, emu.DistanceMode == DistanceMode.Incremental);
                                var p = a.Start;
                                foreach (var q in pts)
                                {
                                    AddSeg(p, q, false, cut, rapid);
                                    p = q;
                                }
                                break;
                            default:
                                if (!a.End.Equals(a.Start))
                                    AddSeg(a.Start, a.End, a.IsRetract, cut, rapid);
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

        private void AddSeg(Point3D a, Point3D b, bool rapid, Point3DCollection cut, Point3DCollection rapidColl)
        {
            double len = (b - a).Length;
            segs.Add(new Seg { A = a, B = b, Rapid = rapid, Len = len });

            var coll = rapid ? rapidColl : cut;
            coll.Add(a);
            coll.Add(b);

            Grow(a);
            Grow(b);
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
            htop = Math.Max(bMax.Z, 0d);
            hbot = Math.Min(bMin.Z, htop - 1d);

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

            // triangle indices are fixed for the grid; built once, only Positions change as we carve
            var tris = new Int32Collection(hnx * hny * 6);
            int stride = hnx + 1;
            for (int j = 0; j < hny; j++)
                for (int i = 0; i < hnx; i++)
                {
                    int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }

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

        // Rebuild the surface positions from the heightmap (indices/normals: indices fixed, normals auto by WPF).
        private void RebuildMesh()
        {
            if (carveMesh == null || hmap == null)
                return;

            var pos = new Point3DCollection((hnx + 1) * (hny + 1));
            for (int j = 0; j <= hny; j++)
                for (int i = 0; i <= hnx; i++)
                    pos.Add(new Point3D(hx0 + i * hcell, hy0 + j * hcell, hmap[i, j]));
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
            if (timer == null)
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickSeconds * 1000d) };
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
            if (segs.Count > 0 && toolCone != null)
                toolCone.Origin = segs[0].A;    // back to the program start
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
                toolCone.Origin = pos;

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
            viewport.ZoomExtents(0);
        }
    }
}
