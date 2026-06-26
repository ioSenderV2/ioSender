/*
 * LoadStockControl.xaml.cs - part of CNC Probing library
 *
 * Load Stock: probe a corner-referenced workpiece to set the work origin at the front-left corner
 * and (step 2) measure its X/Y size. Built on the ProbingViewModel framework; the corner probe reuses
 * the external edge-finder geometry for the front-left (A) corner.
 *
 */

using System.Windows;
using System.Windows.Controls;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls.Probing
{
    /// <summary>
    /// Interaction logic for LoadStockControl.xaml
    /// </summary>
    public partial class LoadStockControl : UserControl, IProbeTab
    {
        private volatile bool isCancelled = false;
        private AxisFlags axisflags = AxisFlags.None;
        private double[] af = new double[3];

        public LoadStockControl()
        {
            InitializeComponent();
        }

        public ProbingType ProbingType { get { return ProbingType.LoadStock; } }

        // Approximate stock size - used in step 2 to traverse safely to the far X/Y faces.
        public static readonly DependencyProperty ApproxWidthProperty = DependencyProperty.Register(nameof(ApproxWidth), typeof(double), typeof(LoadStockControl), new PropertyMetadata(0d));
        public double ApproxWidth { get { return (double)GetValue(ApproxWidthProperty); } set { SetValue(ApproxWidthProperty, value); } }

        public static readonly DependencyProperty ApproxHeightProperty = DependencyProperty.Register(nameof(ApproxHeight), typeof(double), typeof(LoadStockControl), new PropertyMetadata(0d));
        public double ApproxHeight { get { return (double)GetValue(ApproxHeightProperty); } set { SetValue(ApproxHeightProperty, value); } }

        public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(nameof(Result), typeof(string), typeof(LoadStockControl), new PropertyMetadata(string.Empty));
        public string Result { get { return (string)GetValue(ResultProperty); } set { SetValue(ResultProperty, value); } }

        public void Activate(bool activate)
        {
            if (activate)
            {
                var probing = DataContext as ProbingViewModel;
                probing.AllowMeasure = false;
                probing.Instructions =
                    "Push the stock against the corner fence. Jog the probe to just OUTSIDE the front-left corner, " +
                    "a little below the top surface, then Start. The probe finds the left and front faces and sets the work origin there.";
            }
        }

        public void Start(bool preview = false)
        {
            var probing = DataContext as ProbingViewModel;

            if (!probing.ValidateInput(false))
                return;

            if (!probing.VerifyProbe())
                return;

            if (!probing.Program.Init())
                return;

            isCancelled = false;
            Result = string.Empty;

            if (preview)
                probing.StartPosition.Zero();

            var XYClearance = probing.XYClearance + probing.ProbeDiameter / 2d;

            probing.Program.Add(string.Format("G91F{0}", probing.ProbeFeedRate.ToInvariantString()));

            // Front-left (A) corner: probe +X to the left face, +Y to the front face.
            AddCorner(probing, false, false, XYClearance);

            if (preview)
            {
                probing.PreviewText = probing.Program.ToString().Replace("G53", string.Empty);
                PreviewOnCompleted();
                probing.PreviewText += "\n; Post XY probe\n" + probing.Program.ToString().Replace("G53", string.Empty);
            }
            else
            {
                probing.Program.Execute(true);
                OnCompleted();
            }
        }

        // Probe the front-left corner: X toward the left face, retract, reposition, Y toward the front face.
        // Adapted from EdgeFinderControl.AddCorner for the A (negx=false, negy=false) corner.
        private void AddCorner(ProbingViewModel probing, bool negx, bool negy, double XYClearance)
        {
            af[GrblConstants.X_AXIS] = negx ? -1d : 1d;
            af[GrblConstants.Y_AXIS] = negy ? -1d : 1d;

            axisflags = AxisFlags.X | AxisFlags.Y;

            Position rapidto = new Position(probing.StartPosition);
            rapidto.X -= XYClearance * af[GrblConstants.X_AXIS];
            rapidto.Y += probing.Offset * af[GrblConstants.Y_AXIS];
            rapidto.Z -= probing.Depth;

            probing.Program.AddRapidToMPos(rapidto, AxisFlags.Y);
            probing.Program.AddRapidToMPos(rapidto, AxisFlags.X);
            probing.Program.AddRapidToMPos(rapidto, AxisFlags.Z);

            probing.Program.AddProbingAction(AxisFlags.X, negx);

            probing.Program.AddRapidToMPos(rapidto, AxisFlags.X);
            probing.Program.AddRapidToMPos(probing.StartPosition, AxisFlags.Z);
            probing.Program.AddRapidToMPos(probing.StartPosition, AxisFlags.X);

            rapidto.X = probing.StartPosition.X + probing.Offset * af[GrblConstants.X_AXIS];
            rapidto.Y = probing.StartPosition.Y;
            probing.Program.AddRapidToMPos(rapidto, AxisFlags.X | AxisFlags.Y);
            rapidto.Y = probing.StartPosition.Values[GrblConstants.Y_AXIS] - XYClearance * af[GrblConstants.Y_AXIS];
            probing.Program.AddRapidToMPos(rapidto, AxisFlags.Y);
            probing.Program.AddRapidToMPos(rapidto, AxisFlags.Z);

            probing.Program.AddProbingAction(AxisFlags.Y, negy);

            probing.Program.AddRapidToMPos(rapidto, AxisFlags.Y);
            probing.Program.AddRapidToMPos(probing.StartPosition, AxisFlags.Z);
        }

        public void Stop()
        {
            isCancelled = true;
            (DataContext as ProbingViewModel).Program.Cancel();
        }

        // Compute the corner from the captured probe touch points (+ probe-radius comp), optionally probe Z
        // on top, then set the work origin (per the selected coordinate mode). Adapted from EdgeFinderControl.
        private void OnCompleted()
        {
            bool ok;
            var probing = DataContext as ProbingViewModel;

            if ((ok = probing.IsSuccess && probing.Positions.Count > 0))
            {
                int p = 0;
                Position pos = new Position(probing.StartPosition);

                foreach (int i in axisflags.ToIndices())
                    pos.Values[i] = probing.Positions[p++].Values[i] + (i == GrblConstants.Z_AXIS ? 0d : probing.ProbeDiameter / 2d * af[i]);

                if (double.IsNaN(pos.Z))
                {
                    probing.Grbl.IsJobRunning = false;
                    probing.Program.End("Probing failed, machine position not known.");
                    return;
                }

                if (probing.ProbeZ && axisflags != AxisFlags.Z)
                {
                    Position pz = new Position(pos);
                    double xyOffset = probing.WorkpieceXYEdgeOffset == 0d ? probing.ProbeDiameter / 2d : probing.WorkpieceXYEdgeOffset;

                    pz.X += xyOffset * af[GrblConstants.X_AXIS];
                    pz.Y += xyOffset * af[GrblConstants.Y_AXIS];
                    if ((ok = !isCancelled && probing.GotoMachinePosition(pz, axisflags)))
                    {
                        ok = !isCancelled && probing.WaitForResponse(probing.FastProbe + "Z-" + probing.Depth.ToInvariantString());
                        ok = ok && !isCancelled && probing.WaitForResponse(probing.RapidCommand + "Z" + probing.LatchDistance.ToInvariantString());
                        ok = ok && !isCancelled && probing.RemoveLastPosition();
                        if ((ok = ok && !isCancelled && probing.WaitForResponse(probing.SlowProbe + "Z-" + probing.Depth.ToInvariantString())))
                        {
                            pos.Z = probing.Grbl.ProbePosition.Z * probing.Grbl.UnitFactor;
                            ok = !isCancelled && probing.GotoMachinePosition(probing.StartPosition, AxisFlags.Z);
                        }
                    }
                }

                ok = ok && !isCancelled && probing.GotoMachinePosition(pos, AxisFlags.Y);
                ok = ok && !isCancelled && probing.GotoMachinePosition(pos, AxisFlags.X);

                if (probing.ProbeZ)
                    axisflags |= AxisFlags.Z;

                if (ok)
                {
                    switch (probing.CoordinateMode)
                    {
                        case ProbingViewModel.CoordMode.G92:
                            if ((ok = !isCancelled && probing.GotoMachinePosition(pos, AxisFlags.Z)))
                            {
                                pos.X = -probing.ProbeTPOffsetX;
                                pos.Y = -probing.ProbeTPOffsetY;
                                pos.Z = probing.WorkpieceHeight + probing.TouchPlateHeight;
                                probing.WaitForResponse("G92" + pos.ToString(axisflags));
                                if (!isCancelled && axisflags.HasFlag(AxisFlags.Z))
                                    probing.GotoMachinePosition(probing.StartPosition, AxisFlags.Z);
                            }
                            break;

                        default:    // G10 (set work coordinate system origin at the corner)
                            pos.X += probing.ProbeTPOffsetX;
                            pos.Y += probing.ProbeTPOffsetY;
                            pos.Z -= probing.WorkpieceHeight + probing.TouchPlateHeight + probing.Grbl.ToolOffset.Z;
                            probing.WaitForResponse(string.Format("G10L2P{0}{1}", probing.CoordinateSystem, pos.ToString(axisflags)));
                            break;
                    }

                    Result = "Origin set at the front-left corner. Size measurement is step 2.";
                }

                probing.Program.End(ok ? "Load stock: origin set." : "Probing failed");
            }

            if (!probing.Grbl.IsParserStateLive && probing.CoordinateMode == ProbingViewModel.CoordMode.G92)
                probing.Grbl.ExecuteCommand(GrblConstants.CMD_GETPARSERSTATE);

            probing.Grbl.IsJobRunning = false;
            probing.Program.OnCompleted?.Invoke(ok);
        }

        private void PreviewOnCompleted()
        {
            var probing = DataContext as ProbingViewModel;
            Position pos = new Position(probing.StartPosition);

            probing.Program.Clear();

            foreach (int i in axisflags.ToIndices())
                pos.Values[i] = probing.StartPosition.Values[i] + (i == GrblConstants.Z_AXIS ? 0d : probing.ProbeDiameter / 2d * af[i]);

            probing.Program.AddRapidToMPos(pos, AxisFlags.Y);
            probing.Program.AddRapidToMPos(pos, AxisFlags.X);

            pos.X += probing.ProbeOffsetX;
            pos.Y += probing.ProbeOffsetY;
            pos.Z -= probing.WorkpieceHeight + probing.TouchPlateHeight + probing.Grbl.ToolOffset.Z;
            probing.Program.Add(string.Format("G10L2P{0}{1}", probing.CoordinateSystem, pos.ToString(axisflags)));
        }

        private void start_Click(object sender, RoutedEventArgs e)
        {
            Start((DataContext as ProbingViewModel).PreviewEnable);
        }

        private void stop_Click(object sender, RoutedEventArgs e)
        {
            Stop();
        }
    }
}
