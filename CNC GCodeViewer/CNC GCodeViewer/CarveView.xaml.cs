/*
 * CarveView.xaml.cs - part of CNC GCodeViewer
 *
 * A live 3D machine view with local g-code playback: the work envelope, a stock block, the loaded
 * program's toolpath, and a cone at the tool position - following the live machine position, or, on
 * Play, a local simulation of the program (no controller motion). Phase 1 + Play of the carve view
 * (see docs/3D-Carve-View-Design.md); real-time material removal is added later. Registered as the
 * Job tab's "3D View" center component.
 *
 * Coordinate note: the envelope/grid/stock and the live cone are in MACHINE coordinates ($130-$132 /
 * MachinePosition); the toolpath + playback are in the program's (work) coordinates starting at 0,0,0.
 * They therefore differ by the work offset - acceptable for a shape/motion preview; aligning them is a
 * later refinement (offset the program by the active work origin).
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
        private Position mpos;
        private TruncatedConeVisual3D toolCone;
        private bool framed;

        // ---- playback ----
        private readonly List<Seg> segs = new List<Seg>();
        private List<GCodeToken> builtTokens;
        private LinesVisual3D cutLines, rapidLines;
        private int segIdx;
        private double segPos;          // distance travelled into the current segment
        private bool playing;
        private double speedMul = 1d;
        private const double BaseSpeed = 40d;            // mm/s at 1x
        private const double TickSeconds = 0.033d;       // ~30 fps
        private DispatcherTimer timer;

        public CarveView()
        {
            InitializeComponent();
        }

        private void CarveView_Loaded(object sender, RoutedEventArgs e)
        {
            if (model == null && DataContext is GrblViewModel m)
            {
                model = m;
                mpos = model.MachinePosition;
                if (mpos != null)
                    mpos.PropertyChanged += Mpos_PropertyChanged;
            }
            BuildScene();
        }

        private void CarveView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (mpos != null)
                mpos.PropertyChanged -= Mpos_PropertyChanged;
            timer?.Stop();
        }

        // The controller settings that size the envelope ($130-$132) and the loaded program may both arrive
        // after this control is built, so rebuild the scene each time the view is shown.
        private void CarveView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
                BuildScene();
        }

        private void Mpos_PropertyChanged(object sender, PropertyChangedEventArgs e)
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

        private void BuildScene()
        {
            if (viewport == null)
                return;

            viewport.Children.Clear();
            viewport.Children.Add(new DefaultLights());

            double xmin = EnvMin(0), xmax = EnvMax(0);
            double ymin = EnvMin(1), ymax = EnvMax(1);
            double zmin = EnvMin(2), zmax = EnvMax(2);
            double xs = Math.Max(xmax - xmin, 1d), ys = Math.Max(ymax - ymin, 1d), zs = Math.Max(zmax - zmin, 1d);

            // work envelope (wireframe)
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

            // stock block - placeholder default size at the home corner until wired to Load Stock's measured size
            double sw = Math.Min(150d, xs), sl = Math.Min(150d, ys), sh = 19d;
            viewport.Children.Add(new BoxVisual3D
            {
                Center = new Point3D(xmin + sw / 2d, ymin + sl / 2d, zmin + sh / 2d),
                Length = sw,
                Width = sl,
                Height = sh,
                Fill = new SolidColorBrush(Color.FromArgb(96, 205, 175, 125))
            });

            // loaded-program toolpath (program coordinates)
            BuildToolpath();

            // tool cone - tip at the cutter, widening upward
            toolCone = new TruncatedConeVisual3D
            {
                Origin = new Point3D(xmin, ymin, zmax),
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

        // Build the ordered motion (segs) + the cut/rapid toolpath lines from the loaded program. Rebuilds only
        // when the program (token list) changes; otherwise re-adds the cached line visuals to the cleared scene.
        private void BuildToolpath()
        {
            var tokens = GCode.File.Tokens;

            if (tokens != builtTokens)
            {
                builtTokens = tokens;
                segs.Clear();
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

                cutLines = new LinesVisual3D { Color = Color.FromRgb(80, 150, 235), Thickness = 1.4d, Points = cut };
                rapidLines = new LinesVisual3D { Color = Color.FromRgb(120, 120, 120), Thickness = 0.6d, Points = rapid };
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
        }

        // The cone follows the live machine position when not simulating; playback owns it while playing.
        private void UpdateTool()
        {
            if (playing || toolCone == null || mpos == null)
                return;

            double x = mpos.X, y = mpos.Y, z = mpos.Z;
            if (double.IsNaN(x)) x = 0d;
            if (double.IsNaN(y)) y = 0d;
            if (double.IsNaN(z)) z = 0d;

            toolCone.Origin = new Point3D(x, y, z);
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
            if (segs.Count > 0 && toolCone != null)
                toolCone.Origin = segs[0].A;   // back to the program start
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
                if (toolCone != null)
                    toolCone.Origin = segs[segs.Count - 1].B;
                playing = false;
                timer?.Stop();
                return;
            }

            var cs = segs[segIdx];
            double t = cs.Len > 0d ? segPos / cs.Len : 0d;
            if (toolCone != null)
                toolCone.Origin = new Point3D(cs.A.X + (cs.B.X - cs.A.X) * t,
                                              cs.A.Y + (cs.B.Y - cs.A.Y) * t,
                                              cs.A.Z + (cs.B.Z - cs.A.Z) * t);
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
