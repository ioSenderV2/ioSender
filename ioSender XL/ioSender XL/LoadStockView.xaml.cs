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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
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
        private bool loaded = false;
        private static readonly Regex rxResult = new Regex(@"LS_([XY])\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // pcorner.macro prints one of these per probed corner: "PC OUT c=<1-4> x=.. y=.. z=..".
        private static readonly Regex rxCorner = new Regex(
            @"PC\s+OUT\s+c=(\d+)(?:\.\d+)?\s+x=(-?\d+(?:\.\d+)?)\s+y=(-?\d+(?:\.\d+)?)\s+z=(-?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        private double? measuredX = null, measuredY = null;
        // Per-corner probed machine coords, indexed by the macro's corner id 1..4 = FL,FR,BL,BR.
        private readonly double?[] cornerX = new double?[5], cornerY = new double?[5], cornerZ = new double?[5];
        private bool measureRun = false, resultShown = false;

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
                if (!loaded) { LoadInputs(); loaded = true; }   // restore the last estimate/corner/options
                Subscribe(true);
                UpdateExpressionWarning();
            }
            else
            {
                SaveInputs();
                Subscribe(false);
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

            foreach (Match m in rxCorner.Matches(msg))
            {
                int c = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (c >= 1 && c <= 4 &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                    double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                {
                    cornerX[c] = x; cornerY[c] = y; cornerZ[c] = z;
                    hit = true;
                }
            }

            if (hit)
                Dispatcher.BeginInvoke(new System.Action(ShowResult));
        }

        // Clear measured size + per-corner data for a fresh run, then refresh the readout.
        private void ResetResults()
        {
            measuredX = measuredY = null;
            for (int i = 0; i < cornerX.Length; i++)
                cornerX[i] = cornerY[i] = cornerZ[i] = null;
            resultShown = false;
            ShowResult();
        }

        private void ShowResult()
        {
            int probed = 0;
            for (int i = 1; i <= 4; i++) if (cornerZ[i].HasValue) probed++;

            string text = BuildResultText(probed);
            txtResult.Text = text;

            // One summary popup (outline drawing + numbers) when a measuring run has all four corners.
            if (measureRun && !resultShown && probed == 4 && measuredX.HasValue && measuredY.HasValue)
            {
                resultShown = true;
                ShowResultsPopup(text);
            }
        }

        // Results window: a scaled drawing of the four probed corners (interior angle at each, off-square
        // corners flagged) above the size / flatness / squareness summary.
        private void ShowResultsPopup(string summary)
        {
            // Owned by the main window (stable - survives the floating run panel closing) and Topmost so it is
            // always visible. Shown MODELESS (Show, not ShowDialog): a modal dialog raised from the message
            // pump while the program is still finishing can render behind the Topmost panel and block the app.
            Window owner = Window.GetWindow(this);

            var win = new Window
            {
                Title = "Load stock - results",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Topmost = true,
                Owner = owner,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(BuildOutlineDrawing());
            root.Children.Add(new TextBlock { Margin = new Thickness(0, 12, 0, 0), Text = summary });

            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            ok.Click += (s, e) => win.Close();
            root.Children.Add(ok);

            win.Content = root;
            win.Show();
        }

        // Draw the probed quad (FL-FR-BR-BL) to true proportions, with each corner's interior angle.
        // Near-square corners are black; corners off 90 deg by > 0.5 deg are flagged red.
        private UIElement BuildOutlineDrawing()
        {
            const double W = 340, H = 300, margin = 48;
            var canvas = new Canvas { Width = W, Height = H, Background = Brushes.Transparent };

            if (!(Has(1) && Has(2) && Has(3) && Has(4)))
                return canvas;

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 1; i <= 4; i++)
            {
                minX = Math.Min(minX, cornerX[i].Value); maxX = Math.Max(maxX, cornerX[i].Value);
                minY = Math.Min(minY, cornerY[i].Value); maxY = Math.Max(maxY, cornerY[i].Value);
            }
            double spanX = Math.Max(maxX - minX, 1e-6), spanY = Math.Max(maxY - minY, 1e-6);
            double scale = Math.Min((W - 2 * margin) / spanX, (H - 2 * margin) / spanY);
            double offX = (W - spanX * scale) / 2, offY = (H - spanY * scale) / 2;

            // machine X right / Y up (back) -> screen X right / Y down (flip Y)
            System.Func<int, Point> P = c => new Point(
                offX + (cornerX[c].Value - minX) * scale,
                H - offY - (cornerY[c].Value - minY) * scale);

            var poly = new System.Windows.Shapes.Polygon
            {
                Stroke = Brushes.SteelBlue,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, 70, 130, 180))
            };
            foreach (int c in new[] { 1, 2, 4, 3 })
                poly.Points.Add(P(c));
            canvas.Children.Add(poly);

            Point ctr = new Point((P(1).X + P(2).X + P(3).X + P(4).X) / 4, (P(1).Y + P(2).Y + P(3).Y + P(4).Y) / 4);
            string[] name = { "", "FL", "FR", "BL", "BR" };

            for (int c = 1; c <= 4; c++)
            {
                Point pt = P(c);

                var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.DarkRed };
                Canvas.SetLeft(dot, pt.X - 3.5);
                Canvas.SetTop(dot, pt.Y - 3.5);
                canvas.Children.Add(dot);

                double ang = AngleAt(c);
                var lbl = new TextBlock
                {
                    Text = string.Format(CultureInfo.InvariantCulture, "{0}\n{1:0.0}°", name[c], ang),
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Math.Abs(ang - 90.0) > 0.5 ? Brushes.Firebrick : Brushes.Black
                };

                var dir = new Vector(pt.X - ctr.X, pt.Y - ctr.Y);
                if (dir.Length > 1e-6) dir.Normalize();
                Canvas.SetLeft(lbl, pt.X + dir.X * 24 - 14);
                Canvas.SetTop(lbl, pt.Y + dir.Y * 24 - 14);
                canvas.Children.Add(lbl);
            }

            return canvas;
        }

        // Interior angle (degrees) of the FL-FR-BR-BL quad at corner c, from its two neighbouring edges.
        private double AngleAt(int c)
        {
            int a, b;
            switch (c)
            {
                case 1: a = 2; b = 3; break;   // FL: FR, BL
                case 2: a = 1; b = 4; break;   // FR: FL, BR
                case 3: a = 4; b = 1; break;   // BL: BR, FL
                default: a = 2; b = 3; break;  // BR: FR, BL
            }
            double ax = cornerX[a].Value - cornerX[c].Value, ay = cornerY[a].Value - cornerY[c].Value;
            double bx = cornerX[b].Value - cornerX[c].Value, by = cornerY[b].Value - cornerY[c].Value;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6)
                return 90.0;
            double cos = Math.Max(-1.0, Math.Min(1.0, (ax * bx + ay * by) / (la * lb)));
            return Math.Acos(cos) * 180.0 / Math.PI;
        }

        // Live readout: size, plus flatness and squareness once enough corners are probed.
        private string BuildResultText(int probed)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Measured stock:  X = {0}   Y = {1}",
                measuredX.HasValue ? measuredX.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm" : "-",
                measuredY.HasValue ? measuredY.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm" : "-");

            double? flat = Flatness();
            if (flat.HasValue)
                sb.AppendFormat("\nFlatness (Z range): {0} mm", flat.Value.ToString("0.###", CultureInfo.InvariantCulture));

            double? skew = SkewDegrees(), diag = DiagonalDelta();
            if (skew.HasValue && diag.HasValue)
                sb.AppendFormat("\nSquareness: skew {0}°   (diagonal Δ {1} mm)",
                    skew.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    diag.Value.ToString("0.###", CultureInfo.InvariantCulture));

            if (measureRun && probed < 4)
                sb.AppendFormat("\n(probing... {0}/4 corners)", probed);

            return sb.ToString();
        }

        // Flatness = spread of the per-corner stock-top Z values (needs at least two corners).
        private double? Flatness()
        {
            double min = double.MaxValue, max = double.MinValue;
            int n = 0;
            for (int i = 1; i <= 4; i++)
                if (cornerZ[i].HasValue)
                {
                    double z = cornerZ[i].Value;
                    if (z < min) min = z;
                    if (z > max) max = z;
                    n++;
                }
            return n >= 2 ? (double?)(max - min) : null;
        }

        // Skew = deviation from 90 deg between the front edge (FL->FR) and left edge (FL->BL).
        // 0 = perfectly square; the sign shows the direction of the skew. Needs FL, FR, BL (1,2,3).
        private double? SkewDegrees()
        {
            if (!(Has(1) && Has(2) && Has(3)))
                return null;

            double ax = cornerX[2].Value - cornerX[1].Value, ay = cornerY[2].Value - cornerY[1].Value;  // FL->FR
            double bx = cornerX[3].Value - cornerX[1].Value, by = cornerY[3].Value - cornerY[1].Value;  // FL->BL
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6)
                return null;

            double cos = Math.Max(-1.0, Math.Min(1.0, (ax * bx + ay * by) / (la * lb)));
            return Math.Acos(cos) * 180.0 / Math.PI - 90.0;
        }

        // |diagonal FL-BR| - |diagonal FR-BL|; 0 for a true rectangle. Needs all four corners.
        private double? DiagonalDelta()
        {
            if (!(Has(1) && Has(2) && Has(3) && Has(4)))
                return null;
            return Dist(1, 4) - Dist(2, 3);
        }

        private bool Has(int c) { return cornerX[c].HasValue && cornerY[c].HasValue; }

        private double Dist(int a, int b)
        {
            double dx = cornerX[a].Value - cornerX[b].Value, dy = cornerY[a].Value - cornerY[b].Value;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void RefreshProbes()
        {
            // Load Stock needs horizontal probing - only a 3D probe or an edge finder qualifies.
            var usable = ProbeDefinitions.Items
                .Where(p => p.ProbeType == ProbeType.ThreeDProbe || p.ProbeType == ProbeType.EdgeFinder).ToList();

            var sel = cbxProbe.SelectedItem as ProbeDefinition;
            cbxProbe.ItemsSource = usable;
            if (sel != null && usable.Contains(sel))
                cbxProbe.SelectedItem = sel;
            else                                                  // prefer a 3D probe, else an edge finder
                cbxProbe.SelectedItem = usable.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe) ?? usable.FirstOrDefault();

            bool ok = usable.Count > 0;
            btnGenerate.IsEnabled = btnRun.IsEnabled = ok;
            txtNoProbe.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
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
            ResetResults();
            SaveInputs();
        }

        private static string SettingsPath
        {
            get { return Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, "LoadStock.xml"); }
        }

        private void LoadInputs()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return;
                var xs = new XmlSerializer(typeof(LoadStockSettings));
                using (var fs = File.OpenRead(SettingsPath))
                {
                    var s = (LoadStockSettings)xs.Deserialize(fs);
                    fldWidth.Value = s.Width;
                    fldHeight.Value = s.Height;
                    cbxWcs.SelectedIndex = Math.Max(0, Math.Min(5, s.Wcs - 1));
                    chkMeasure.IsChecked = s.Measure;
                    rbFL.IsChecked = s.Corner == "FrontLeft";
                    rbFR.IsChecked = s.Corner == "FrontRight";
                    rbBL.IsChecked = s.Corner == "BackLeft";
                    rbBR.IsChecked = s.Corner == "BackRight";
                    if (!string.IsNullOrEmpty(s.Probe))
                        foreach (var item in cbxProbe.Items)
                            if ((item as ProbeDefinition)?.Name == s.Probe) { cbxProbe.SelectedItem = item; break; }
                }
            }
            catch { /* start with defaults */ }
        }

        private void SaveInputs()
        {
            try
            {
                var s = new LoadStockSettings
                {
                    Width = fldWidth.Value,
                    Height = fldHeight.Value,
                    Corner = SelectedCorner.ToString(),
                    Wcs = cbxWcs.SelectedIndex + 1,
                    Measure = chkMeasure.IsChecked == true,
                    Probe = (cbxProbe.SelectedItem as ProbeDefinition)?.Name ?? string.Empty
                };
                var xs = new XmlSerializer(typeof(LoadStockSettings));
                using (var fs = File.Create(SettingsPath))
                    xs.Serialize(fs, s);
            }
            catch { }
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (model == null)
                return;

            if (string.IsNullOrWhiteSpace(txtProgram.Text))
                Generate_Click(sender, e);
            if (string.IsNullOrWhiteSpace(txtProgram.Text))
                return;

            measureRun = chkMeasure.IsChecked == true;
            ResetResults();

            // Float the machine-control panel (status, feed hold, override, MDI) so the run can be driven
            // without leaving this tab.
            MachineControlWindow.ShowFor(model, Window.GetWindow(this));

            // Macro path: NGC-safe, keeps the program out of the loaded job, and shows the (MBOX,...)
            // confirmation. confirm:true gives the operator a final "run?" before any motion.
            MacroProcessor.Run(model, "Load stock", txtProgram.Text, true);
        }

        // Build the NGC probe program: call the tested pcorner.macro per corner (it discovers the spoilboard /
        // stock-top Z and never rapids blind), then set the origin + compute size from the probed corners.
        // Start Job conventions: (PREREQ ...) verbatim, G30 park + install, safe-Z go-to. Origin = the selected
        // corner (probed FIRST from G28, which discovers start_z); the other three reuse that start_z and are
        // referenced from the probed origin + the (conservative) estimated size.
        private static string BuildProgram(ProbeDefinition p, Corner corner, double estW, double estH, int wcsP, bool measure)
        {
            double r = p.ProbeDiameter / 2d;                    // tip radius -> edge comp
            string cornerName = Name(corner);

            int id1 = CornerId(corner);
            Corner xn = XNeighbor(corner), yn = YNeighbor(corner), dg = Diagonal(corner);
            int sox = (corner == Corner.FrontLeft || corner == Corner.BackLeft) ? 1 : -1;   // stock interior X direction
            int soy = (corner == Corner.FrontLeft || corner == Corner.FrontRight) ? 1 : -1; // stock interior Y direction
            string wcs = "G" + (53 + Math.Min(Math.Max(wcsP, 1), 6)).ToString(CultureInfo.InvariantCulture);

            var b = new StringBuilder();
            int lineNo = 0;
            // Number every emitted code line (N10, N20, ...) so console errors map back to a generated line.
            // Skip:
            //  - pure comments / sender directives (start with '(') - they carry no executable word; and
            //  - named O-word flow lines (O<name> CALL/...): a leading N-word breaks the controller's
            //    O-word routing (the line faults with error:1 and never runs the sub), and ioSender's
            //    unparsed-forward path keys off the line starting with 'O'. Leave these unnumbered.
            void L(string s)
            {
                s = SanitizeParens(s);   // grblHAL ends a comment at the FIRST ')', so neutralise interior parens
                string t = s.TrimStart();
                bool oword = t.Length > 1 && (t[0] == 'O' || t[0] == 'o') && t[1] == '<';
                if (s.Length > 0 && s[0] != '(' && !oword)
                    b.Append('N').Append((lineNo += 10).ToString(CultureInfo.InvariantCulture)).Append(' ');
                b.Append(s).Append('\n');
            }

            // Pass args to pcorner via GLOBALS set before a NO-ARG call: grblHAL's O-word CALL does not take
            // multiple bracketed args reliably (it parses the extra brackets as G-code -> error:1/2), so we avoid
            // call args entirely. Then (WAITIDLE) so the subroutine finishes before the next line is sent.
            // Bracket only multi-term expressions; a bare param/number is assigned as-is (matches the proven
            // "#<rad>=1" form). grblHAL needs brackets around an expression but not a single value.
            string Br(string v) { return v.IndexOf(' ') >= 0 ? "[" + v + "]" : v; }
            void EmitCall(int cornerId, string refx, string refy, string startz)
            {
                L(string.Format("#<_ls_corner> = {0}", cornerId));
                L(string.Format("#<_ls_refx> = {0}", Br(refx)));
                L(string.Format("#<_ls_refy> = {0}", Br(refy)));
                L(string.Format("#<_ls_startz> = {0}", Br(startz)));
                L("O<pcorner> CALL [#<_ls_rad>]");   // single arg (tip radius) - grblHAL's CALL resolves with one arg
                L("(WAITIDLE)");
            }

            L(string.Format("(Load stock - probe corners via pcorner.macro, set origin{0})", measure ? " + measure size" : ""));
            L(string.Format("(Probe \"{0}\": tip {1} mm. Set G28 10-30 mm OUTSIDE the {2} corner in BOTH X and Y.)", p.Name, N(p.ProbeDiameter), cornerName));
            L("(Estimated size MUST be conservative - a few mm larger than actual - so far refs land just outside.)");
            L("(Requires grblHAL NGC expressions + pcorner.macro on the controller. VALIDATE before trusting.)");
            L("(PREREQ, connected, homed, EXPR, ATC=1, G28, G30, G59.3)");
            L("G21 G90 G94 G17");
            L("G49");
            L(string.Format("#<_ls_rad> = {0}", N(r)));   // probe tip radius (global, read by pcorner)

            L("(park at G30 - install / confirm the probe)");
            EmitGotoG30(L);
            L("(WAITIDLE)");
            L("(MBOX, OKCANCEL, Install and seat the probe, then click OK. Cancel aborts.)");

            // Corner 1 = the selected origin corner: reference = G28, start_z = 9999 -> DISCOVER (publishes #<_start_z>).
            L(string.Format("(--- corner 1 = {0} (origin): reference G28, discover Z ---)", cornerName));
            EmitCall(id1, "#5161", "#5162", "9999");
            L("#<c1x> = #<_corner_x>");
            L("#<c1y> = #<_corner_y>");
            L("#<c1z> = #<_corner_z>");

            if (measure)
            {
                // The other three reuse start_z; references come from the probed origin + estimate on the spanning
                // axis and G28 on the shared axis. corner 2 = X-neighbour, 3 = Y-neighbour, 4 = diagonal.
                L(string.Format("(--- corner 2 = {0} (X-neighbour) ---)", Name(xn)));
                EmitCall(CornerId(xn), plusMinus("#<c1x>", sox, estW), "#5162", "#<_start_z>");
                L("#<c2x> = #<_corner_x>");
                L("#<c2y> = #<_corner_y>");

                L(string.Format("(--- corner 3 = {0} (Y-neighbour) ---)", Name(yn)));
                EmitCall(CornerId(yn), "#5161", plusMinus("#<c1y>", soy, estH), "#<_start_z>");
                L("#<c3x> = #<_corner_x>");
                L("#<c3y> = #<_corner_y>");

                L(string.Format("(--- corner 4 = {0} (diagonal) ---)", Name(dg)));
                EmitCall(CornerId(dg), plusMinus("#<c1x>", sox, estW), plusMinus("#<c1y>", soy, estH), "#<_start_z>");
                L("#<c4x> = #<_corner_x>");
                L("#<c4y> = #<_corner_y>");

                L("(--- size = mean of the two opposite spans ---)");
                L(string.Format("#<size_x> = [{0} * [[#<c2x> - #<c1x>] + [#<c4x> - #<c3x>]] / 2]", sox));
                L(string.Format("#<size_y> = [{0} * [[#<c3y> - #<c1y>] + [#<c4y> - #<c2y>]] / 2]", soy));
                L("(PRINT, LS_X=#<size_x>)");
                L("(PRINT, LS_Y=#<size_y>)");
            }

            L(string.Format("(--- set work origin at the {0} corner ---)", cornerName));
            L(string.Format("G10 L2 {0} X[#<c1x>] Y[#<c1y>] Z[#<c1z>]", pCode(wcsP)));
            L(wcs + "  (activate the coordinate system)");

            L("(--- park at G30 ---)");
            EmitGotoG30(L);
            L("M2");

            return b.ToString();
        }

        // grblHAL ends a g-code comment at the FIRST ')', so any '(' or ')' INSIDE a (comment) corrupts
        // the block: the text after the inner ')' is parsed as g-code. E.g. "(corner 1 = FL (origin): x)"
        // closes at ")" after "origin", then ": x" parses as g-code -> "error:1" on the stray ':'.
        // Replace parens between the outer '(' .. ')' with '[' .. ']' so a comment is always well-formed.
        private static string SanitizeParens(string s)
        {
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open < 0 || close <= open + 1)
                return s;

            var sb = new StringBuilder(s.Length);
            sb.Append(s, 0, open + 1);
            for (int i = open + 1; i < close; i++)
                sb.Append(s[i] == '(' ? '[' : s[i] == ')' ? ']' : s[i]);
            sb.Append(s, close, s.Length - close);
            return sb.ToString();
        }

        private static string Name(Corner c)
        {
            return c == Corner.FrontLeft ? "front-left" : c == Corner.FrontRight ? "front-right"
                 : c == Corner.BackLeft ? "back-left" : "back-right";
        }

        // pcorner.macro corner ids: 1=FL 2=FR 3=BL 4=BR.
        private static int CornerId(Corner c)
        {
            switch (c) { case Corner.FrontLeft: return 1; case Corner.FrontRight: return 2; case Corner.BackLeft: return 3; default: return 4; }
        }

        // Neighbours of the origin: X-neighbour flips left/right, Y-neighbour flips front/back, diagonal flips both.
        private static Corner XNeighbor(Corner c)
        {
            switch (c) { case Corner.FrontLeft: return Corner.FrontRight; case Corner.FrontRight: return Corner.FrontLeft;
                         case Corner.BackLeft: return Corner.BackRight; default: return Corner.BackLeft; }
        }
        private static Corner YNeighbor(Corner c)
        {
            switch (c) { case Corner.FrontLeft: return Corner.BackLeft; case Corner.FrontRight: return Corner.BackRight;
                         case Corner.BackLeft: return Corner.FrontLeft; default: return Corner.FrontRight; }
        }
        private static Corner Diagonal(Corner c)
        {
            switch (c) { case Corner.FrontLeft: return Corner.BackRight; case Corner.FrontRight: return Corner.BackLeft;
                         case Corner.BackLeft: return Corner.FrontRight; default: return Corner.FrontLeft; }
        }

        // Safe-Z go-to G30 (probe-install / park): lift to machine top, traverse X/Y, descend - never a bare diagonal.
        private static void EmitGotoG30(System.Action<string> L)
        {
            L("G53 G0 Z0");
            L("G53 G0 X[#5181] Y[#5182]");
            L("G53 G0 Z[#5183]");
        }

        // P-word for G10 L2 (P1=G54..P6=G59).
        private static string pCode(int wcsP) { return "P" + Math.Min(Math.Max(wcsP, 1), 6).ToString(CultureInfo.InvariantCulture); }

        // "baseExpr + mag" or "baseExpr - mag" depending on dir (keeps generated expressions clean - no "- -5").
        private static string plusMinus(string baseExpr, int dir, double mag) { return baseExpr + (dir >= 0 ? " + " : " - ") + N(mag); }

        private static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    // Persisted Load Stock inputs (LoadStock.xml in the config folder) so the estimate/corner/options survive restarts.
    public class LoadStockSettings
    {
        public double Width = 100d;
        public double Height = 100d;
        public string Corner = "FrontLeft";
        public int Wcs = 1;            // 1 = G54
        public bool Measure = true;
        public string Probe = string.Empty;
    }
}
