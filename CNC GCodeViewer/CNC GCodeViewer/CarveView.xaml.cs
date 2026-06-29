/*
 * CarveView.xaml.cs - part of CNC GCodeViewer
 *
 * A live 3D machine view: the work envelope, a stock block and a cone at the current tool position
 * (machine coordinates). Phase 1 of the carve view (see docs/3D-Carve-View-Design.md); real-time
 * material removal is added in a later phase. Registered as the Job tab's "3D View" center component.
 */

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using CNC.Core;

namespace CNC.Controls.Viewer
{
    public partial class CarveView : UserControl
    {
        private GrblViewModel model;
        private Position mpos;
        private TruncatedConeVisual3D toolCone;
        private bool framed;

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
        }

        // The controller settings that size the envelope ($130-$132) may load after this control is built
        // (connection is deferred), so rebuild the static scene each time the view is shown.
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

            if (!framed && xs > 1d)   // frame once, when a real envelope is known
            {
                framed = true;
                viewport.ZoomExtents(0);
            }
        }

        private void UpdateTool()
        {
            if (toolCone == null || mpos == null)
                return;

            double x = mpos.X, y = mpos.Y, z = mpos.Z;
            if (double.IsNaN(x)) x = 0d;
            if (double.IsNaN(y)) y = 0d;
            if (double.IsNaN(z)) z = 0d;

            toolCone.Origin = new Point3D(x, y, z);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            viewport.ZoomExtents(0);
        }
    }
}
