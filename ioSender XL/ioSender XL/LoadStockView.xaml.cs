/*
 * LoadStockView.xaml.cs - part of ioSender XL
 *
 * "Load stock" top-level tab. Pick a probe definition + the stock corner the probe is parked over +
 * the approximate stock size, then Generate an inline grblHAL NGC probe program (shown locally) and
 * Run it through the macro path (MacroProcessor / ExecuteMacro) - which passes NGC expressions, #params
 * and O-words through to the controller and never touches the loaded job. The program sets the work
 * origin at the corner and (optionally) probes the far faces to measure the stock size, printing the
 * result back over the console where this tab captures it.
 *
 * NOTE: the generated NGC is a first cut and MUST be validated on the machine before it is trusted.
 *       It assumes grblHAL with NGC expressions enabled (GrblInfo.ExpressionsSupported).
 *
 */

using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;
using CNC.Controls;

namespace GCode_Sender
{
    public partial class LoadStockView : UserControl, ICNCView
    {
        // The four stock corners the probe can be parked over. Sign factors below turn FL geometry into
        // any corner: probe +X for left corners / -X for right; probe +Y for front / -Y for back.
        public enum Corner { FrontLeft, FrontRight, BackLeft, BackRight }

        private GrblViewModel model = null;
        private bool subscribed = false;
        private static readonly Regex rxResult = new Regex(@"LS_([XY])\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        private double? measuredX = null, measuredY = null;

        public LoadStockView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => { if (e.NewValue is GrblViewModel m) model = m; };
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.LoadStock; } }
        public bool CanEnable { get { return true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (activate)
            {
                if (model == null)
                    model = DataContext as GrblViewModel;
                RefreshProbes();
                Subscribe(true);
                UpdateExpressionWarning();
            }
            else
                Subscribe(false);
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

        // Phase C: pull our (PRINT, LS_X=.. / LS_Y=..) lines out of the controller's console messages and
        // surface the measured size in the tab. Controller print/debug comments arrive as Message updates.
        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GrblViewModel.Message))
                return;

            string msg = model.Message;
            if (string.IsNullOrEmpty(msg))
                return;

            bool hit = false;
            foreach (Match m in rxResult.Matches(msg))
            {
                if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    if (m.Groups[1].Value.ToUpperInvariant() == "X")
                        measuredX = v;
                    else
                        measuredY = v;
                    hit = true;
                }
            }
            if (hit)
                Dispatcher.BeginInvoke(new System.Action(ShowResult));
        }

        private void ShowResult()
        {
            txtResult.Text = string.Format("Measured stock:  X = {0}   Y = {1}",
                measuredX.HasValue ? measuredX.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm" : "-",
                measuredY.HasValue ? measuredY.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm" : "-");
        }

        private void RefreshProbes()
        {
            var sel = cbxProbe.SelectedItem as ProbeDefinition;
            cbxProbe.ItemsSource = ProbeDefinitions.Items;
            if (sel != null && ProbeDefinitions.Items.Contains(sel))
                cbxProbe.SelectedItem = sel;
            else if (cbxProbe.SelectedIndex < 0 && ProbeDefinitions.Items.Count > 0)
                cbxProbe.SelectedIndex = 0;
        }

        private void UpdateExpressionWarning()
        {
            txtExprWarn.Visibility = GrblInfo.ExpressionsSupported ? Visibility.Collapsed : Visibility.Visible;
        }

        private Corner SelectedCorner
        {
            get
            {
                if (rbFR.IsChecked == true) return Corner.FrontRight;
                if (rbBL.IsChecked == true) return Corner.BackLeft;
                if (rbBR.IsChecked == true) return Corner.BackRight;
                return Corner.FrontLeft;
            }
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            var p = cbxProbe.SelectedItem as ProbeDefinition;
            if (p == null)
            {
                MessageBox.Show("Select a probe definition first (Machine Setup Wizard > Probe definitions).",
                    "Load stock", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            txtProgram.Text = BuildProgram(p, SelectedCorner, fldWidth.Value, fldHeight.Value,
                                           cbxWcs.SelectedIndex + 1, chkMeasure.IsChecked == true);
            measuredX = measuredY = null;
            txtResult.Text = "Measured stock:  X = -   Y = -";
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (model == null)
                return;

            if (string.IsNullOrWhiteSpace(txtProgram.Text))
                Generate_Click(sender, e);
            if (string.IsNullOrWhiteSpace(txtProgram.Text))
                return;

            measuredX = measuredY = null;
            ShowResult();

            // Macro path: NGC-safe, keeps the program out of the loaded job, and shows the (MBOX,...)
            // confirmation. confirm:true gives the operator a final "run?" before any motion.
            MacroProcessor.Run(model, "Load stock", txtProgram.Text, true);
        }

        // Build the inline NGC probe program. Geometry is parameterised by corner via sign factors:
        //   sxn = +1 probe toward +X (left corners), -1 toward -X (right corners)
        //   syn = +1 probe toward +Y (front corners), -1 toward -Y (back corners)
        // Start position: probe ball parked just above the TOP of the chosen corner.
        private static string BuildProgram(ProbeDefinition p, Corner corner, double approxW, double approxH, int wcsP, bool measure)
        {
            double d = p.ProbeDiameter, r = d / 2d;
            double c = Math.Max(p.XYClearance, r);     // never approach closer than the ball radius (standoff)
            double zd = p.Depth, pd = p.ProbeDistance, ld = Math.Max(p.LatchDistance, 0.5);
            double fs = p.ProbeFeedRate, fl = p.LatchFeedRate;
            double zClear = 5d;                          // extra lift above the start (stock top) for cross traverses

            int sxn = (corner == Corner.FrontLeft || corner == Corner.BackLeft) ? 1 : -1;
            int syn = (corner == Corner.FrontLeft || corner == Corner.FrontRight) ? 1 : -1;
            string wcs = "G" + (53 + Math.Min(Math.Max(wcsP, 1), 6)).ToString(CultureInfo.InvariantCulture); // P1->G54..P6->G59
            string cornerName = corner == Corner.FrontLeft ? "front-left" : corner == Corner.FrontRight ? "front-right"
                              : corner == Corner.BackLeft ? "back-left" : "back-right";

            var b = new StringBuilder();
            void L(string s) { b.Append(s).Append('\n'); }

            L(string.Format("(Load stock - probe the {0} corner, set work origin{1})", cornerName, measure ? " and measure size" : ""));
            L(string.Format("(Probe \"{0}\": ball dia {1} mm, radius {2} mm for comp/standoff)", p.Name, N(d), N(r)));
            L("(Requires grblHAL with NGC expressions enabled. VALIDATE ON THE MACHINE before trusting.)");
            L(string.Format("(Start: jog the probe so the ball is just above the TOP of the {0} corner.)", cornerName));
            L("G21 G90 G94 G17");
            L("(capture start machine position)");
            L("#<sx> = #<_abs_x>");
            L("#<sy> = #<_abs_y>");
            L("#<sz> = #<_abs_z>");

            // --- Step 1: near X face (sets work X0) ---
            L("(--- near X face ---)");
            EmitFaceProbeX(L, sxn, "#<sx>", "#<sy>", "#<sz>", c, r, zd, pd, ld, fs, fl, "#<xnear>");
            // --- Step 1: near Y face (sets work Y0) ---
            L("(--- near Y face ---)");
            EmitFaceProbeY(L, syn, "#<sx>", "#<sy>", "#<sz>", c, r, zd, pd, ld, fs, fl, "#<ynear>");

            L("(--- set work origin at the corner ---)");
            L(string.Format("G10 L2 {0} X[{1}] Y[{2}]",
                pCode(wcsP), plusMinus("#<xnear>", sxn, r), plusMinus("#<ynear>", syn, r)));
            L(wcs + "  (activate the coordinate system)");
            L("G90");

            if (measure)
            {
                // --- Step 2: far X face ---
                L("(--- far X face ---)");
                EmitFarFaceX(L, sxn, "#<sx>", "#<sy>", "#<sz>", approxW, c, r, zd, pd, ld, fs, fl, zClear, "#<xfar>");
                // --- Step 2: far Y face ---
                L("(--- far Y face ---)");
                EmitFarFaceY(L, syn, "#<sx>", "#<sy>", "#<sz>", approxH, c, r, zd, pd, ld, fs, fl, zClear, "#<yfar>");

                L("(--- compute and report size ---)");
                L(string.Format("#<size_x> = [{0} * [#<xfar> - #<xnear>] - {1}]", sxn, N(d)));
                L(string.Format("#<size_y> = [{0} * [#<yfar> - #<ynear>] - {1}]", syn, N(d)));
                L("(PRINT, LS_X=#<size_x>)");
                L("(PRINT, LS_Y=#<size_y>)");
            }

            // Park back above the corner.
            L("(--- park above the corner ---)");
            L(string.Format("G53 G0 Z[#<sz> + {0}]", N(zClear)));
            L("G53 G0 X[#<sx>] Y[#<sy>]");
            L("M2");

            return b.ToString();
        }

        // Probe a near face along X: approach from the near side, drop, fast+slow probe, capture #5061, retract.
        // G53 rapids reposition in machine coords (computed from the captured start); G91 G38.2 are relative strokes.
        private static void EmitFaceProbeX(System.Action<string> L, int sxn, string sx, string sy, string sz,
            double c, double r, double zd, double pd, double ld, double fs, double fl, string outVar)
        {
            L(string.Format("G53 G0 X[{0}] Y[{1}]", plusMinus(sx, -sxn, c + r), sy));   // near-X approach, start Y
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, -1, zd)));
            L(string.Format("G91 G38.2 X{0} F{1}", signed(sxn, pd), N(fs)));            // fast probe toward the face
            L(string.Format("G91 G0 X{0}", signed(-sxn, ld)));                          // back off
            L(string.Format("G91 G38.2 X{0} F{1}", signed(sxn, ld + 1d), N(fl)));       // slow re-probe
            L(string.Format("{0} = #5061", outVar));
            L("G90");
            L(string.Format("G53 G0 Z[{0}]", sz));
        }

        private static void EmitFaceProbeY(System.Action<string> L, int syn, string sx, string sy, string sz,
            double c, double r, double zd, double pd, double ld, double fs, double fl, string outVar)
        {
            L(string.Format("G53 G0 X[{0}] Y[{1}]", sx, plusMinus(sy, -syn, c + r)));
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, -1, zd)));
            L(string.Format("G91 G38.2 Y{0} F{1}", signed(syn, pd), N(fs)));
            L(string.Format("G91 G0 Y{0}", signed(-syn, ld)));
            L(string.Format("G91 G38.2 Y{0} F{1}", signed(syn, ld + 1d), N(fl)));
            L(string.Format("{0} = #5062", outVar));
            L("G90");
            L(string.Format("G53 G0 Z[{0}]", sz));
        }

        // Probe the far X face: lift, traverse beyond the far face, drop, probe back toward the corner.
        private static void EmitFarFaceX(System.Action<string> L, int sxn, string sx, string sy, string sz,
            double approxW, double c, double r, double zd, double pd, double ld, double fs, double fl, double zClear, string outVar)
        {
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, 1, zClear)));
            L(string.Format("G53 G0 X[{0}] Y[{1}]", plusMinus(sx, sxn, approxW + c + r), sy));
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, -1, zd)));
            L(string.Format("G91 G38.2 X{0} F{1}", signed(-sxn, pd), N(fs)));           // probe back toward the corner
            L(string.Format("G91 G0 X{0}", signed(sxn, ld)));
            L(string.Format("G91 G38.2 X{0} F{1}", signed(-sxn, ld + 1d), N(fl)));
            L(string.Format("{0} = #5061", outVar));
            L("G90");
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, 1, zClear)));
        }

        private static void EmitFarFaceY(System.Action<string> L, int syn, string sx, string sy, string sz,
            double approxH, double c, double r, double zd, double pd, double ld, double fs, double fl, double zClear, string outVar)
        {
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, 1, zClear)));
            L(string.Format("G53 G0 X[{0}] Y[{1}]", sx, plusMinus(sy, syn, approxH + c + r)));
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, -1, zd)));
            L(string.Format("G91 G38.2 Y{0} F{1}", signed(-syn, pd), N(fs)));
            L(string.Format("G91 G0 Y{0}", signed(syn, ld)));
            L(string.Format("G91 G38.2 Y{0} F{1}", signed(-syn, ld + 1d), N(fl)));
            L(string.Format("{0} = #5062", outVar));
            L("G90");
            L(string.Format("G53 G0 Z[{0}]", plusMinus(sz, 1, zClear)));
        }

        // P-word for G10 L2 (P1=G54..P6=G59).
        private static string pCode(int wcsP) { return "P" + Math.Min(Math.Max(wcsP, 1), 6).ToString(CultureInfo.InvariantCulture); }

        // A signed magnitude as a relative-stroke literal: +1/5 -> "5", -1/5 -> "-5".
        private static string signed(int sign, double mag) { return (sign < 0 ? "-" : "") + N(Math.Abs(mag)); }

        // "baseExpr + mag" or "baseExpr - mag" depending on dir (keeps generated expressions clean - no "- -5").
        private static string plusMinus(string baseExpr, int dir, double mag) { return baseExpr + (dir >= 0 ? " + " : " - ") + N(mag); }

        private static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
