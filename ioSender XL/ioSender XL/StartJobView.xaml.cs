/*
 * StartJobView.xaml.cs - part of ioSender XL
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
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using CNC.Core;
using CNC.Controls;
using CNC.GCode;

namespace GCode_Sender
{
    public partial class StartJobView : UserControl, ICNCView
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
        // corner-1 DISCOVER pass prints the spoilboard reference Z: "PC z_spoil=..". Thickness = top - spoilboard.
        private static readonly Regex rxSpoil = new Regex(@"PC\s+z_spoil\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        private double? measuredX = null, measuredY = null, spoilZ = null;
        // Per-corner probed machine coords, indexed by the macro's corner id 1..4 = FL,FR,BL,BR.
        private readonly double?[] cornerX = new double?[5], cornerY = new double?[5], cornerZ = new double?[5];
        private bool measureRun = false;

        // Whether Width/Height/Thickness have been explicitly set THIS session - either hand-edited, or (once
        // that exists) auto-filled from a loaded program's stock size. LoadInputs restores last session's
        // saved numbers silently on tab activation; those are leftovers, not necessarily right for THIS stock -
        // Generate warns if none of the three were touched (see Generate_Click).
        private bool sizeFieldsTouched = false;
        private bool loadingInputs = false;   // true only while LoadInputs is assigning - suppresses the touched flag

        // Display-only unit toggle for the Stock size fields (rbUnitsMm/rbUnitsIn) - Width/Height/Thickness/
        // Spacer's NumericField.Value is whatever's on screen, in THIS unit; every consumer (BuildProgram, the
        // drawing, warnings, persistence) needs mm, so reads go through ToMm() and mm-sourced writes (loaded
        // program STOCK comment, persisted config) go through FromMm(). Never touches the fields' own storage
        // semantics - just where the conversion happens.
        private bool isImperial = false;
        private double ToMm(double displayValue) { return isImperial ? displayValue * 25.4d : displayValue; }
        private double FromMm(double mm) { return isImperial ? mm / 25.4d : mm; }

        // Start Job's OWN program view (the ProgramView refactor): created lazily, titled "Start Job", connected
        // to the streamer stack so the overlay hosts it and the run marks it - independent of the Job-tab view.
        private CNC.Controls.ProgramView programView;
        private void EnsureProgramView()
        {
            if (programView == null)
                programView = new CNC.Controls.ProgramView { Title = "Start Job" };
        }
        private string program = string.Empty;   // last generated probe program (run via the macro path)

        public StartJobView()
        {
            InitializeComponent();
            // rbUnitsMm's initial checked state is set HERE, not via IsChecked="True" in XAML - setting it in
            // XAML fires Units_Checked mid-BAML-parse, before rbUnitsIn (declared later in the tree) has been
            // assigned to its field, null-refing on rbUnitsIn.IsChecked and crashing the app on every launch.
            rbUnitsMm.IsChecked = true;
            DataContextChanged += (s, e) => { if (e.NewValue is GrblViewModel m) model = m; };
            WireInputs();
        }

        // Any input edit redraws the stock outline AND invalidates a previously generated program (so Run is
        // disabled until Generate is pressed again - the program would otherwise be stale).
        private void WireInputs()
        {
            cbxWcs.SelectionChanged += (s, e) => InputChanged();
            chkMeasure.Checked += (s, e) => { UpdateSizeHint(); InputChanged(); };
            chkMeasure.Unchecked += (s, e) => { UpdateSizeHint(); InputChanged(); };
            chkRotate.Checked += (s, e) => InputChanged();
            chkRotate.Unchecked += (s, e) => InputChanged();
            chkSetTloRef.Checked += (s, e) => InputChanged();
            chkSetTloRef.Unchecked += (s, e) => InputChanged();
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldWidth, (s, e) => { MarkSizeFieldsTouched(); InputChanged(); });
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldHeight, (s, e) => { MarkSizeFieldsTouched(); InputChanged(); });
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldThickness, (s, e) => { MarkSizeFieldsTouched(); UpdateThicknessWarning(); InputChanged(); });
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldSpacer, (s, e) => InputChanged());
        }

        private void MarkSizeFieldsTouched()
        {
            if (!loadingInputs)
                sizeFieldsTouched = true;
        }

        // rbUnitsMm/rbUnitsIn toggle: convert each Stock size field's CURRENTLY DISPLAYED value to the new
        // unit (so the user sees the same physical size, not the same number relabelled) and swap the Unit
        // labels. Suppressed while LoadInputs is bulk-assigning (it sets isImperial + the radio buttons
        // directly, in the already-correct order - this handler firing on top would double-convert).
        private void Units_Checked(object sender, RoutedEventArgs e)
        {
            if (loadingInputs)
                return;

            bool newImperial = rbUnitsIn.IsChecked == true;
            if (newImperial == isImperial)
                return;

            fldWidth.Value = ConvertUnit(fldWidth.Value, isImperial, newImperial);
            fldHeight.Value = ConvertUnit(fldHeight.Value, isImperial, newImperial);
            fldThickness.Value = ConvertUnit(fldThickness.Value, isImperial, newImperial);
            fldSpacer.Value = ConvertUnit(fldSpacer.Value, isImperial, newImperial);

            isImperial = newImperial;
            string unit = isImperial ? "in" : "mm";
            fldWidth.Unit = fldHeight.Unit = fldThickness.Unit = fldSpacer.Unit = unit;
            UpdateThicknessWarning();
            UpdateDrawing();
        }

        private static double ConvertUnit(double v, bool fromImperial, bool toImperial)
        {
            if (fromImperial == toImperial)
                return v;
            double mm = fromImperial ? v * 25.4d : v;
            return toImperial ? mm / 25.4d : mm;
        }

        // Auto-fill Width/Height/Thickness from the loaded job's own (STOCK X=.. Y=.. Z=..) comment (the
        // Fusion ioSenderBatchPost add-in's format), via ProgramView.LoadedJob.DeclaredStock - computed fresh
        // from GCode.File.Data each time, not a cached global. Only a genuine DECLARED comment counts as
        // "explicitly set for this job" (clears the stale-value warning gate, Generate_Click) - deliberately
        // NOT ProgramView.Stock's envelope-defaulted fallback, which would otherwise silently overwrite the
        // fields with the full machine travel on every tab activate when no program has a STOCK comment.
        // Silent no-op in that case - manual entry still works exactly as before, and Generate's stale-value/
        // envelope checks still catch an unset or oversized guess.
        private void TryAutoFillStockFromProgram()
        {
            var stock = CNC.Controls.ProgramView.LoadedJob?.DeclaredStock;
            if (stock == null)
                return;

            loadingInputs = true;   // these three ARE explicit values (from the program), not a silent restore -
            try                     // suppress MarkSizeFieldsTouched's per-field firing so we can set it once below
            {
                fldWidth.Value = FromMm(stock.Value.X);
                fldHeight.Value = FromMm(stock.Value.Y);
                fldThickness.Value = FromMm(stock.Value.Z);
                UpdateThicknessWarning();
            }
            finally
            {
                loadingInputs = false;
            }
            sizeFieldsTouched = true;
            if (model != null)
                model.Message = "Stock size loaded from the program's (STOCK) comment.";
        }

        // The pcorner probe macro assumes stock <= 1 in (25.4 mm) to start its top probe
        // just above a 1 in top for speed - taller stock would be missed. Flag it when the Z estimate exceeds that.
        private const double MaxStockThicknessMm = 25.4d;
        private void UpdateThicknessWarning()
        {
            if (txtThickWarn != null)
                txtThickWarn.Visibility = ToMm(fldThickness.Value) > MaxStockThicknessMm ? Visibility.Visible : Visibility.Collapsed;
        }

        // The width/height fields are the actual size when not measuring; when Measure is on they are only a
        // conservative estimate pcorner uses to place the far-corner probes (must be a few mm oversize).
        private void UpdateSizeHint()
        {
            if (txtSizeHint != null)
                txtSizeHint.Text = chkMeasure.IsChecked == true
                    ? "Estimate only - make it a few mm larger than actual so the far-corner probes land just outside the stock. Probing measures the true size."
                    : "Actual stock size.";
        }

        // The configured 3D probe supplies the tip radius and the search/latch feeds the program needs; there is
        // no probe picker (Start Job assumes a 3D probe + toolsetter). Null when none is defined.
        private ProbeDefinition ThreeDProbe()
        {
            return ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
        }

        // Enable Generate only when a 3D probe is defined; otherwise show the "define a probe" hint.
        private void UpdateProbeWarning()
        {
            bool ok = ThreeDProbe() != null;
            btnGenerate.IsEnabled = ok;
            txtNoProbe.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }

        // (Re)load the fixture library into the dropdown, preserving the current selection by name. Fixtures
        // are DEFINED (including their position) in Machine Setup > Fixture definitions - Start Job only
        // selects one, and only lists ones with a VALIDATED position: "Test position" (FixtureEditDialog) has
        // actually run the real spoilboard probe search and the controller didn't alarm. A merely-captured-
        // but-untested position is exactly what caused an Alarm:5 probe fail mid-Start-Job (the 12 mm search
        // cap missed because the saved Z was too far above the spoilboard) - offering it here just invites
        // repeating that instead of validating it in Machine Setup first.
        private void RefreshFixtures()
        {
            string current = SelectedFixture?.Name;
            cbxFixture.ItemsSource = Fixtures.Items.Where(f => f.PositionValidated).ToList();
            if (current != null)
                cbxFixture.SelectedItem = (cbxFixture.ItemsSource as List<Fixture>).FirstOrDefault(f => f.Name == current);
        }

        private Fixture SelectedFixture { get { return cbxFixture.SelectedItem as Fixture; } }

        // Gate Generate on a fixture being selected, its kind having a working macro, AND the controller not
        // being in Alarm (a leftover alarm from a prior failed probe silently swallows the G30 park move -
        // MBOX prompts show regardless of controller state, so that looked like "prompts but doesn't move").
        // Also hides Measure/Rotate for a kind that doesn't probe edges (nothing to measure - see the
        // Machinist Vise design note in BuildProgram). The dropdown only lists validated fixtures
        // (RefreshFixtures), so there is no separate "no position" case to gate on here.
        private void UpdateFixtureWarning()
        {
            var fx = SelectedFixture;
            bool noFixtures = (cbxFixture.ItemsSource as List<Fixture>)?.Count == 0;
            bool implemented = fx != null && fx.Implemented;
            bool notAlarmed = model != null && model.GrblState.State != GrblStates.Alarm;
            bool ok = fx != null && implemented && notAlarmed;

            txtNoFixture.Visibility = noFixtures ? Visibility.Visible : Visibility.Collapsed;
            txtFixtureWarning.Visibility = (fx != null && !implemented) ? Visibility.Visible : Visibility.Collapsed;
            btnGenerate.IsEnabled = ok && ThreeDProbe() != null;

            bool showMeasure = fx == null || FixtureKinds.ProbesEdges(fx.Kind);
            chkMeasure.Visibility = showMeasure ? Visibility.Visible : Visibility.Collapsed;
            chkRotate.Visibility = showMeasure && GrblInfo.RotationSupported ? Visibility.Visible : Visibility.Collapsed;

            // Size fields describe THIS fixture's stock - meaningless (and easy to fill in against the wrong
            // assumptions) before a fixture is even chosen. Spacer/backer is a Corner-Fence-style concept (the
            // spoilboard+spacer floor pcorner.macro computes from it) - a vise has no spoilboard probe at all
            // (BuildViseProgram never reads it), so it's hidden rather than just disabled for that kind.
            bool fixtureChosen = fx != null;
            fldWidth.IsEnabled = fldHeight.IsEnabled = fldThickness.IsEnabled = fixtureChosen;
            rbUnitsMm.IsEnabled = rbUnitsIn.IsEnabled = fixtureChosen;
            fldSpacer.Visibility = (fixtureChosen && FixtureKinds.ProbesEdges(fx.Kind)) ? Visibility.Visible : Visibility.Collapsed;
            fldSpacer.IsEnabled = fixtureChosen;

            UpdateDrawing();   // origin-corner marker (+ jaws for a vise) tracks the selected fixture's kind
        }

        private void cbxFixture_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFixtureWarning();
            InputChanged();
        }

        private void InputChanged()
        {
            InvalidateProgram();
            UpdateDrawing();
        }

        // Drop the generated program; Cycle Start (which runs the active program) rebuilds it via Run_Click.
        private void InvalidateProgram()
        {
            program = string.Empty;
        }

        private void DrawingHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateDrawing();
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.StartJob; } }
        public bool CanEnable { get { return true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (activate)
            {
                if (model == null)
                    model = DataContext as GrblViewModel;
                if (!loaded) { LoadInputs(); loaded = true; }   // restore the last estimate/options
                TryAutoFillStockFromProgram();
                RefreshFixtures();
                UpdateSizeHint();
                Subscribe(true);
                RefreshCapabilities();   // EXPR / probe / rotation / ATC gating - refreshed again on connect (see Model_PropertyChanged)
                UpdateDrawing();
                if (!string.IsNullOrEmpty(program))
                {
                    EnsureProgramView();
                    programView.SetProgramText(program);
                    programView.Connect();     // Load Stock's OWN view (titled "Load Stock") shows in the overlay
                }
                MacroProcessor.ActiveRun = () => Run_Click(null, null);            // Cycle Start runs it
            }
            else
            {
                SaveInputs();
                MacroProcessor.ActiveRun = null;
                programView?.Disconnect();                     // active program follows the focused tab
                // Stay subscribed when deactivated: keep parsing the (PRINT PC OUT / LS_X/Y) result messages so
                // the corners populate and the results popup is raised even if the tab is left mid-run. The
                // handler only reacts to our own messages, so it's a no-op otherwise.
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
            // Re-gate Generate live: entering/leaving Alarm should enable/disable it without waiting for the
            // fixture selection to change (see UpdateFixtureWarning).
            if (e.PropertyName == nameof(GrblViewModel.GrblState))
                UpdateFixtureWarning();

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

            var ms = rxSpoil.Match(msg);
            if (ms.Success && double.TryParse(ms.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double zs))
            {
                spoilZ = zs;
                hit = true;
            }

            if (hit)
                Dispatcher.BeginInvoke(new System.Action(ShowResult));
        }

        // Clear measured size + per-corner data for a fresh run, then refresh the readout.
        private void ResetResults()
        {
            measuredX = measuredY = spoilZ = null;
            for (int i = 0; i < cornerX.Length; i++)
                cornerX[i] = cornerY[i] = cornerZ[i] = null;
            ShowResult();
        }

        private void ShowResult()
        {
            int probed = 0;
            for (int i = 1; i <= 4; i++) if (cornerZ[i].HasValue) probed++;

            txtResult.Text = BuildResultText(probed);
            UpdateDrawing();   // the right-panel drawing is the live result view (no separate popup)
            btnCopySize.IsEnabled = measuredX.HasValue && measuredY.HasValue;
            // Verify skew needs all four corners (a full measure run) and a controller that applies WCS rotation.
            btnVerify.IsEnabled = GrblInfo.RotationSupported && Has(1) && Has(2) && Has(3) && Has(4);
        }

        // Copy the measured stock size to the clipboard as "X Y [Z]" (mm) for pasting into the Fusion
        // ioSenderBatchPost add-in's "measured stock" field. X/Y are the footprint; Z (mean probed corner top
        // minus the spoilboard Z) is appended when available, else X/Y only.
        private void CopySize_Click(object sender, RoutedEventArgs e)
        {
            if (!(measuredX.HasValue && measuredY.HasValue))
                return;
            try
            {
                double? z = StockZ();
                string text = z.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###}", measuredX.Value, measuredY.Value, z.Value)
                    : string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###}", measuredX.Value, measuredY.Value);
                Clipboard.SetText(text);
            }
            catch { /* clipboard busy - ignore */ }
        }

        // Rebuild the inline stock drawing to the current host size.
        private void UpdateDrawing()
        {
            if (drawingHost == null)
                return;
            double w = drawingHost.ActualWidth, h = drawingHost.ActualHeight;
            if (w < 20d || h < 20d)
            {
                drawingHost.Child = null;
                return;
            }
            drawingHost.Child = BuildStockDrawing(w, h);
        }

        // The inline stock outline: before a measuring run, a rectangle from the approximate width/height with
        // the chosen origin corner marked and the X/Y dimensions; after all four corners are probed, the true
        // probed quad with the measured X/Y spans and each corner's interior angle (off-square corners in red).
        private UIElement BuildStockDrawing(double W, double H)
        {
            var canvas = new Canvas { Width = W, Height = H, Background = Brushes.White };
            const double margin = 60d;

            // Only a vise has a fixed physical origin corner independent of where the stock happens to sit -
            // its jaw is at the BACK of the setup (see FixtureEditDialog's schematic), so the reference/origin
            // is back-left, not the front-left every edge-probing kind (Corner Fence etc.) always uses.
            bool isVise = SelectedFixture != null && SelectedFixture.Kind == FixtureKind.MachinistVise;

            bool measured = Has(1) && Has(2) && Has(3) && Has(4);

            double[] mx = new double[5], my = new double[5];   // 1..4 = FL,FR,BL,BR
            if (measured)
            {
                for (int c = 1; c <= 4; c++) { mx[c] = cornerX[c].Value; my[c] = cornerY[c].Value; }
            }
            else
            {
                double ew = Math.Max(ToMm(fldWidth.Value), 1d), eh = Math.Max(ToMm(fldHeight.Value), 1d);
                mx[1] = 0d;  my[1] = 0d;     // FL
                mx[2] = ew;  my[2] = 0d;     // FR
                mx[3] = 0d;  my[3] = eh;     // BL
                mx[4] = ew;  my[4] = eh;     // BR
            }

            double minX = Math.Min(Math.Min(mx[1], mx[2]), Math.Min(mx[3], mx[4]));
            double maxX = Math.Max(Math.Max(mx[1], mx[2]), Math.Max(mx[3], mx[4]));
            double minY = Math.Min(Math.Min(my[1], my[2]), Math.Min(my[3], my[4]));
            double maxY = Math.Max(Math.Max(my[1], my[2]), Math.Max(my[3], my[4]));
            double spanX = Math.Max(maxX - minX, 1e-6), spanY = Math.Max(maxY - minY, 1e-6);
            double scale = Math.Min((W - 2d * margin) / spanX, (H - 2d * margin) / spanY);
            if (scale <= 0d || double.IsInfinity(scale) || double.IsNaN(scale))
                return canvas;
            double offX = (W - spanX * scale) / 2d, offY = (H - spanY * scale) / 2d;

            // machine X right / Y up (back) -> screen X right / Y down (flip Y)
            System.Func<int, Point> P = c => new Point(
                offX + (mx[c] - minX) * scale,
                H - offY - (my[c] - minY) * scale);

            var poly = new System.Windows.Shapes.Polygon
            {
                Stroke = Brushes.SteelBlue,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, 70, 130, 180))
            };
            foreach (int c in new[] { 1, 2, 4, 3 })
                poly.Points.Add(P(c));
            canvas.Children.Add(poly);

            // dimensions: X along the front edge (FL->FR), Y along the left edge (FL->BL)
            double dimX = measured && measuredX.HasValue ? measuredX.Value : Math.Max(ToMm(fldWidth.Value), 0d);
            double dimY = measured && measuredY.HasValue ? measuredY.Value : Math.Max(ToMm(fldHeight.Value), 0d);
            AddDimLabel(canvas, P(1), P(2), dimX);
            AddDimLabel(canvas, P(1), P(3), dimY);

            Point ctr = new Point((P(1).X + P(2).X + P(3).X + P(4).X) / 4d, (P(1).Y + P(2).Y + P(3).Y + P(4).Y) / 4d);

            // Vise: draw the fixed jaw (back, BL-BR edge) and moving jaw (front, FL-FR edge) as bars just
            // outside the stock - screen Y already has back=smaller/front=larger from the machine-Y-up flip
            // above (P(3)/P(4) are BL/BR, P(1)/P(2) are FL/FR), matching FixtureEditDialog's schematic.
            if (isVise)
            {
                const double jawBar = 14d, jawOverhang = 12d;
                var jawFill = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB4));
                var jawStroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x60, 0x70));
                double jawX0 = Math.Min(P(3).X, P(4).X) - jawOverhang, jawX1 = Math.Max(P(3).X, P(4).X) + jawOverhang;

                var fixedJaw = new System.Windows.Shapes.Rectangle { Width = jawX1 - jawX0, Height = jawBar, Fill = jawFill, Stroke = jawStroke, StrokeThickness = 1.5 };
                Canvas.SetLeft(fixedJaw, jawX0);
                Canvas.SetTop(fixedJaw, Math.Min(P(3).Y, P(4).Y) - jawBar);
                canvas.Children.Add(fixedJaw);

                var movingJaw = new System.Windows.Shapes.Rectangle { Width = jawX1 - jawX0, Height = jawBar, Fill = jawFill, Stroke = jawStroke, StrokeThickness = 1.5 };
                Canvas.SetLeft(movingJaw, jawX0);
                Canvas.SetTop(movingJaw, Math.Max(P(1).Y, P(2).Y));
                canvas.Children.Add(movingJaw);
            }

            // origin corner marker: red dot only. Every edge-probing kind (Corner Fence etc.) always
            // references front-left; a vise's origin is the jaw's own back-left corner instead (see isVise above).
            int oc = isVise ? CornerId(Corner.BackLeft) : CornerId(SelectedCorner);
            Point op = P(oc);
            var dot = new System.Windows.Shapes.Ellipse { Width = 11d, Height = 11d, Fill = Brushes.OrangeRed };
            Canvas.SetLeft(dot, op.X - 5.5);
            Canvas.SetTop(dot, op.Y - 5.5);
            canvas.Children.Add(dot);

            // Per-corner labels (measuring run only): interior angle (red if off-square) + stock thickness
            // (corner top minus spoilboard Z) at every corner, placed just outside the corner.
            if (measured)
            {
                for (int c = 1; c <= 4; c++)
                {
                    Point pt = P(c);
                    double ang = AngleAt(c);

                    string text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}°", ang);
                    if (spoilZ.HasValue && cornerZ[c].HasValue)
                        text += string.Format(CultureInfo.InvariantCulture, "\nt={0:0.0}", cornerZ[c].Value - spoilZ.Value);

                    var lbl = new TextBlock
                    {
                        Text = text,
                        FontSize = 11d,
                        TextAlignment = TextAlignment.Center,
                        Background = Brushes.White,
                        Padding = new Thickness(2d, 0d, 2d, 0d),
                        Foreground = Math.Abs(ang - 90.0) > 0.5 ? Brushes.Firebrick : Brushes.Black
                    };
                    lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var dir = new Vector(pt.X - ctr.X, pt.Y - ctr.Y);
                    if (dir.Length > 1e-6) dir.Normalize();
                    Canvas.SetLeft(lbl, pt.X + dir.X * 22d - lbl.DesiredSize.Width / 2d);
                    Canvas.SetTop(lbl, pt.Y + dir.Y * 22d - lbl.DesiredSize.Height / 2d);
                    canvas.Children.Add(lbl);
                }
            }

            return canvas;
        }

        // A dimension label (mm) centred on the edge a->b, on a white pad so it reads over the outline.
        private void AddDimLabel(Canvas canvas, Point a, Point b, double mm)
        {
            var lbl = new TextBlock
            {
                Text = mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm",
                FontSize = 12d,
                Foreground = Brushes.DimGray,
                Background = Brushes.White,
                Padding = new Thickness(2d, 0d, 2d, 0d)
            };
            lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(lbl, (a.X + b.X) / 2d - lbl.DesiredSize.Width / 2d);
            Canvas.SetTop(lbl, (a.Y + b.Y) / 2d - lbl.DesiredSize.Height / 2d);
            canvas.Children.Add(lbl);
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

        // Stock THICKNESS (Z, mm) for the Fusion stock box = mean probed stock-top Z minus the spoilboard Z
        // (both machine coords; corner-1 DISCOVER probes the spoilboard -> "PC z_spoil="). Null if the
        // spoilboard wasn't probed - then Copy size emits X Y only and you enter thickness in Fusion.
        private double? StockZ()
        {
            if (!spoilZ.HasValue)
                return null;
            double sum = 0d;
            int n = 0;
            for (int i = 1; i <= 4; i++)
                if (cornerZ[i].HasValue)
                {
                    sum += cornerZ[i].Value;
                    n++;
                }
            return n > 0 ? (double?)(sum / n - spoilZ.Value) : null;
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

        private void UpdateExpressionWarning()
        {
            txtExprWarn.Visibility = GrblInfo.ExpressionsSupported ? Visibility.Collapsed : Visibility.Visible;
        }

        // Capability-driven UI. Depends on the controller's $I (EXPR / WCSROT / ATC), which is only parsed after
        // connect - so this must run both on Activate AND when the controller info arrives (GrblState change),
        // otherwise the tab (now shown first, before connect) would be stuck showing the disconnected state.
        // The rotation/TLO checkboxes are only shown when supported; the actual emission is gated again at
        // Generate time, so a hidden-but-checked box can never emit an R word or M6 T8 the firmware rejects.
        private void RefreshCapabilities()
        {
            UpdateProbeWarning();
            UpdateFixtureWarning();   // also drives chkRotate visibility (gated on RotationSupported AND the fixture type probing edges)
            UpdateExpressionWarning();
            chkSetTloRef.Visibility = GrblInfo.HasATC ? Visibility.Visible : Visibility.Collapsed;
        }

        // Start Job always references the front-left (TFL) corner.
        private Corner SelectedCorner { get { return Corner.FrontLeft; } }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            var p = ThreeDProbe();
            if (p == null)
            {
                AppDialogs.Show(CNC.Controls.LibStrings.FindResource("HmSelectProbe"),
                    "Start Job", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var fx = SelectedFixture;
            if (fx == null || !fx.Implemented || !fx.PositionValidated)
            {
                AppDialogs.Show("Select a fixture with a supported type and a validated position first (Machine Setup > Fixture definitions > Test position).",
                    "Start Job", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            // Width/Height/Thickness are silently restored from LAST session's saved values on tab activation
            // (LoadInputs) - fine as a starting point, but not necessarily right for THIS stock. Confirm before
            // generating from numbers nobody has actually looked at for this job (TryAutoFillStockFromProgram
            // already marks this touched when the loaded program declares its own STOCK size).
            if (!sizeFieldsTouched)
            {
                if (AppDialogs.Show("Est. width/height/thickness haven't been set for this job - they're carried over from last time. Generate anyway?",
                        "Start Job", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                sizeFieldsTouched = true;   // confirmed once - don't nag again this session unless the fields change
            }

            // Sanity-cap against this program's effective stock size: its own declared (STOCK) size if it has
            // one, else the machine's full work envelope (ProgramView.Stock - computed fresh, never a stale
            // cached value). Typed dimensions bigger than that bound are certainly wrong, regardless of who set them.
            var bound = CNC.Controls.ProgramView.LoadedJob?.Stock;
            double widthMm = ToMm(fldWidth.Value), heightMm = ToMm(fldHeight.Value), thicknessMm = ToMm(fldThickness.Value);
            if (bound != null && bound.Value.X > 0d && bound.Value.Y > 0d &&
                (widthMm > bound.Value.X || heightMm > bound.Value.Y || (bound.Value.Z > 0d && thicknessMm > bound.Value.Z)))
            {
                bool declared = CNC.Controls.ProgramView.LoadedJob.DeclaredStock != null;
                if (AppDialogs.Show(string.Format("Est. width/height/thickness ({0} x {1} x {2} mm) exceeds {3} ({4} x {5} x {6} mm) - that can't be right. Generate anyway?",
                        N(widthMm), N(heightMm), N(thicknessMm),
                        declared ? "the loaded program's declared stock size" : "this machine's travel",
                        N(bound.Value.X), N(bound.Value.Y), N(bound.Value.Z)),
                        "Start Job", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            // Gate the capability-dependent options on what the controller actually supports, regardless of the
            // checkbox state - a hidden-but-checked box must never emit a G10 L2 R (errors:20 without WCSROT) or
            // an M6 T8 puck reference (no toolsetter without ATC). A kind that doesn't probe edges has nothing
            // to measure (see the Machinist Vise design note in BuildProgram), so measure is forced off for it.
            bool measure = FixtureKinds.ProbesEdges(fx.Kind) && chkMeasure.IsChecked == true;
            bool applyRotation = measure && chkRotate.IsChecked == true && GrblInfo.RotationSupported;
            bool setTloRef = chkSetTloRef.IsChecked == true && GrblInfo.HasATC;
            program = FixtureKinds.ProbesEdges(fx.Kind)
                ? BuildProgram(p, fx, SelectedCorner, widthMm, heightMm,
                               cbxWcs.SelectedIndex + 1, measure, applyRotation, setTloRef, ToMm(fldSpacer.Value))
                : BuildViseProgram(p, fx, widthMm, heightMm, thicknessMm, cbxWcs.SelectedIndex + 1, setTloRef);
            ResetResults();
            SaveInputs();

            // Re-arm as the active program: a previous run tears this down (handing the source back to the job),
            // so Generate must re-establish it so Cycle Start runs Start Job again without leaving the tab.
            MacroProcessor.ActiveProgramName = "Start Job";
            MacroProcessor.ActiveRun = () => Run_Click(null, null);
            EnsureProgramView();
            programView.SetProgramText(program);
            programView.Connect();   // Start Job owns its ProgramView; the overlay hosts it and it titles itself
        }

        // Persisted as the "StartJob" section of App.config (folded in from StartJob.xml); the DTO + holder
        // live in CNC.Controls (StartJobConfig) so AppConfig can register the section.
        private void LoadInputs()
        {
            loadingInputs = true;
            try
            {
                var s = StartJobConfig.Section;
                if (s == null)
                    return;
                isImperial = s.IsImperial;
                rbUnitsIn.IsChecked = isImperial;
                rbUnitsMm.IsChecked = !isImperial;
                fldWidth.Unit = fldHeight.Unit = fldThickness.Unit = fldSpacer.Unit = isImperial ? "in" : "mm";
                fldWidth.Value = FromMm(s.Width);
                fldHeight.Value = FromMm(s.Height);
                fldThickness.Value = FromMm(s.Thickness);
                fldSpacer.Value = FromMm(s.SpacerThickness);
                cbxWcs.SelectedIndex = Math.Max(0, Math.Min(5, s.Wcs - 1));
                chkMeasure.IsChecked = s.Measure;
                chkRotate.IsChecked = s.ApplyRotation;
                chkSetTloRef.IsChecked = s.SetTloRef;
                // Corner is always front-left now; the probe comes from the 3D-probe definition - both dropped.
                UpdateThicknessWarning();
            }
            catch { /* start with defaults */ }
            finally
            {
                loadingInputs = false;
                sizeFieldsTouched = false;   // these are last session's leftovers, not yet confirmed for THIS job
            }
        }

        private void SaveInputs()
        {
            try
            {
                StartJobConfig.Section = new StartJobSettings
                {
                    Width = ToMm(fldWidth.Value),
                    Height = ToMm(fldHeight.Value),
                    Thickness = ToMm(fldThickness.Value),
                    SpacerThickness = ToMm(fldSpacer.Value),
                    IsImperial = isImperial,
                    Corner = SelectedCorner.ToString(),
                    Wcs = cbxWcs.SelectedIndex + 1,
                    Measure = chkMeasure.IsChecked == true,
                    ApplyRotation = chkRotate.IsChecked == true,
                    SetTloRef = chkSetTloRef.IsChecked == true
                };
                AppConfig.Settings.Save();
            }
            catch { }
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (model == null)
                return;

            if (string.IsNullOrWhiteSpace(program))
                Generate_Click(sender, e);
            if (string.IsNullOrWhiteSpace(program))
                return;

            measureRun = chkMeasure.IsChecked == true;
            ResetResults();

            // Run control (status, feed hold, override, MDI) is fixed at the main-window bottom and always
            // visible (Phase 2c), so the run can be driven without leaving this tab - no floating panel needed.

            // Macro path: NGC-safe, keeps the program out of the loaded job, and shows the (MBOX,...)
            // confirmation. confirm:true gives the operator a final "run?" before any motion.
            MacroProcessor.Run(model, "Start Job", program, true);
        }

        // Verify skew: after a measure run, re-establish the WCS (origin + measured rotation) from the retained
        // probed corners and touch each corner of the ideal rectangle in the rotated work frame. If the rotation
        // is right (and the stock square) every touch lands on the top surface right at the corner; a corner that
        // misses or touches low reveals a bad rotation or an out-of-square (parallelogram) stock. A separate,
        // re-runnable check that reuses the last measurement - it does not disturb the measure program/results.
        private void VerifySkew_Click(object sender, RoutedEventArgs e)
        {
            if (model == null)
                return;
            var p = ThreeDProbe();
            if (p == null)
            {
                AppDialogs.Show(CNC.Controls.LibStrings.FindResource("HmSelectProbe"),
                    "Verify skew", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (!(Has(1) && Has(2) && Has(3) && Has(4)))
            {
                AppDialogs.Show("Measure the stock first - all four corners must be probed before the skew can be verified.",
                    "Verify skew", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            string verify = BuildVerifyProgram(p, cbxWcs.SelectedIndex + 1);
            EnsureProgramView();
            programView.SetProgramText(verify);
            programView.Connect();
            MacroProcessor.Run(model, "Verify skew", verify, true);
        }

        private string BuildVerifyProgram(ProbeDefinition p, int wcsP)
        {
            double r = p.ProbeDiameter / 2d;                         // inset so the tip edge sits at the corner
            double latch = p.LatchFeedRate > 0d ? p.LatchFeedRate : 50d;
            const double safeZ = 10d, probeDepth = 3d;               // work Z: 10 above the top, probe 3 below it

            // Corner work coords in the measure's rotated frame: transform each probed corner (machine) about the
            // FL origin by the applied rotation. FR defines +X (front edge); BL gives the Y direction whose SIGN
            // depends on the machine's handedness - never assume +Y (that sent the probe off the table -> Alarm:2).
            double rot = Math.Atan2(cornerY[2].Value - cornerY[1].Value, cornerX[2].Value - cornerX[1].Value);
            double rotDeg = rot * 180d / Math.PI;
            double cos = Math.Cos(rot), sin = Math.Sin(rot);
            double WX(int c) { double dx = cornerX[c].Value - cornerX[1].Value, dy = cornerY[c].Value - cornerY[1].Value; return dx * cos + dy * sin; }
            double WY(int c) { double dx = cornerX[c].Value - cornerX[1].Value, dy = cornerY[c].Value - cornerY[1].Value; return -dx * sin + dy * cos; }

            double fx = WX(2);                                       // FR work X (front-edge length, positive)
            double ly = WY(3);                                       // BL work Y (signed)
            var b = new StringBuilder();
            void L(string s) { b.Append(SanitizeParens(s)).Append('\n'); }

            // Inset a work-coord touch point toward the stock centre by the tip radius (so the tip edge sits at
            // the point), rapid over it above the stock, drop to safe Z, probe down (G38.3 - a miss won't halt),
            // and retract. Handedness-agnostic: the inset direction comes from the centre, never an assumed sign.
            double cx = fx / 2d, cy = ly / 2d;
            void Touch(double px, double py, string label)
            {
                double dx = cx - px, dy = cy - py, len = Math.Sqrt(dx * dx + dy * dy);
                double ix = len < 1e-6 ? px : px + r * dx / len;
                double iy = len < 1e-6 ? py : py + r * dy / len;
                L(string.Format("(--- {0} ---)", label));
                L(string.Format("G0 X{0} Y{1}", N(ix), N(iy)));     // work XY (rotation applied); above the stock
                L("(WAITIDLE)");
                L("G0 Z" + N(safeZ));                               // drop to safe Z above the stock top
                L(string.Format("G38.3 Z{0} F{1}", N(-probeDepth), N(latch)));   // no-error probe
                L("G0 Z" + N(safeZ));                               // retract above the stock
            }

            L("(Verify skew - touch each corner in the rotated work frame. Each should touch the surface right at the corner.)");
            L("(Front-left/right define the frame (ideal == measured).)");
            L("(Back-left/right are touched twice: the ideal rectangle point, then the actual probed corner - the gap between the two is the out-of-square amount.)");
            L("(Discipline matches Measure: no G53 move runs while the rotation is active.)");
            L("(PREREQ, connected, homed, noalarm, EXPR, G30)");
            L("G21 G90 G94 G17");
            if (GrblInfo.HasToolSetter)
                L(string.Format(GrblCommand.ProbeSelect, p.ProbeType == ProbeType.ToolSetter ? 1 : 0));

            // Use the WCS the measure set (origin). Clear the rotation FIRST so the G53 safe-Z lift is a clean
            // machine move (a G53 move with an active WCS rotation needs a separate firmware exemption).
            L(WcsCode(wcsP) + "  (activate the WCS the measure set - origin)");
            if (GrblInfo.RotationSupported)
                L(string.Format("G10 L2 {0} R0", pCode(wcsP)));     // clear rotation for the G53 lift

            L("G53 G0 Z0");                                         // lift to machine top (rotation cleared - clean)
            L("(WAITIDLE)");
            L("(MBOX, OKCANCEL, Install the 3D probe. This touches each corner to check the skew. Click OK to start.)");
            L("(WAITIDLE)");
            L("G53 G0 Z0");                                         // re-lift after install (still R0)

            // Apply the measured skew rotation (negated - grblHAL's G10 L2 R aligns with -atan2(dy,dx)). From here
            // on ONLY work-coord moves are issued (no G53), so a machine-move/rotation interaction can't arise.
            if (GrblInfo.RotationSupported)
                L(string.Format("G10 L2 {0} R{1}", pCode(wcsP), N(-rotDeg)));

            // Order matches the Measure / Start-Job numbering: 1=FL, 2=FR, 3=BL, 4=BR.
            Touch(0d, 0d, "front-left (origin)");
            Touch(fx, 0d, "front-right");
            Touch(0d, ly, "back-left - ideal rectangle");
            Touch(WX(3), WY(3), "back-left - measured corner");
            Touch(fx, ly, "back-right - ideal rectangle");
            Touch(WX(4), WY(4), "back-right - measured corner");

            // Park back at G30 (where the job started). Clear the rotation for the G53 park moves, park, then
            // restore the skew rotation so the WCS is left as the measure produced it (no move follows - safe).
            L("(--- park at G30 ---)");
            if (GrblInfo.RotationSupported)
                L(string.Format("G10 L2 {0} R0", pCode(wcsP)));
            L("G53 G0 Z0");                                         // lift to machine top
            L("G53 G0 X[#5181] Y[#5182]");                          // traverse to G30 X/Y
            L("G53 G0 Z[#5183]");                                   // descend to G30 Z
            if (GrblInfo.RotationSupported)
                L(string.Format("G10 L2 {0} R{1}", pCode(wcsP), N(-rotDeg)));   // restore the skew rotation
            L("M2");
            return b.ToString();
        }

        private static string WcsCode(int wcsP)
        {
            return "G" + (53 + Math.Min(Math.Max(wcsP, 1), 6)).ToString(CultureInfo.InvariantCulture);
        }

        // Build the NGC probe program: call the tested pcorner.macro per corner (it discovers the spoilboard /
        // stock-top Z and never rapids blind), then set the origin + compute size from the probed corners.
        // Start Job conventions: (PREREQ ...) verbatim, G30 park + install, safe-Z go-to. Origin = the selected
        // corner (probed FIRST from the fixture's saved machine position, decoupled from the firmware's
        // single-slot G28); the other three reuse that start_z and are referenced from the probed origin + the
        // (conservative) estimated size.
        //
        // NOTE on Machinist Vise (FixtureKinds.ProbesEdges == false): its origin is a KNOWN position (the
        // fixture's saved Coords ARE the precise jaw-corner origin, captured via a real probe cycle at
        // Set-time - see FixtureEditDialog.RunViseCornerProbe - not this per-job flow), so it needs zero
        // edge-probing and no Measure/skew support at all. That gets its own generator, BuildViseProgram
        // (below), not this one - Generate_Click branches on ProbesEdges to pick between them.
        private static string BuildProgram(ProbeDefinition p, Fixture fx, Corner corner, double estW, double estH, int wcsP, bool measure, bool applyRotation, bool setTloRef, double spacer)
        {
            double r = p.ProbeDiameter / 2d;                    // tip radius -> edge comp
            string cornerName = Name(corner);
            var fxPos = new Position(fx.Coords);
            string refX = N(fxPos.X), refY = N(fxPos.Y);        // the fixture's saved machine XY - replaces firmware G28 (#5161/#5162)

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
            // call args entirely. No (WAITIDLE) between corners: the whole program is streamed as one job, so the
            // controller runs the four CALLs back-to-back under flow control (each publishes its globals before
            // the next reads them) - which keeps Feed Hold/Stop live and the UI responsive. Results still stream
            // back as (PRINT ...) messages. Bracket only multi-term expressions; a bare param/number is assigned
            // as-is (matches the proven "#<rad>=1" form). grblHAL needs brackets around an expression but not one value.
            string Br(string v) { return v.IndexOf(' ') >= 0 ? "[" + v + "]" : v; }
            void EmitCall(int cornerId, string refx, string refy, string startz)
            {
                L(string.Format("#<_ls_corner> = {0}", cornerId));
                L(string.Format("#<_ls_refx> = {0}", Br(refx)));
                L(string.Format("#<_ls_refy> = {0}", Br(refy)));
                L(string.Format("#<_ls_startz> = {0}", Br(startz)));
                L("O<pcorner> CALL [#<_ls_rad>]");   // single arg (tip radius) - grblHAL's CALL resolves with one arg
            }

            L(string.Format("(Start Job - probe corners via pcorner.macro, set origin{0})", measure ? " + measure size" : ""));
            // Split across short lines - grblHAL rejects a line over its receive-buffer size ("Max characters
            // per line exceeded") outright, and one long sentence with real names substituted in can exceed it.
            L(string.Format("(Probe \"{0}\": tip {1}mm body {2}mm.)", p.Name, N(p.ProbeDiameter), N(p.BodyDiameter)));
            L(string.Format("(Fixture \"{0}\" ({1}): saved position must clear both faces by ~D and be)", fx.Name, cornerName));
            L("(within 10mm above the spoilboard in Z - see Test position in Fixture definitions.)");
            L("(Estimated size MUST be conservative - a few mm larger than actual - so far refs land just outside.)");
            L("(Requires grblHAL NGC expressions + pcorner.macro on the controller. VALIDATE before trusting.)");
            L("(PREREQ, connected, homed, EXPR, ATC=1, G28, G30, G59.3)");
            L("G21 G90 G94 G17");
            L("G49");
            // Select the probe input for the chosen probe (tool setter -> 1, else the main probe -> 0), the same
            // rule the Probing page uses (SelectControllerProbe). Guards against a stale selection from an
            // interrupted tool-setter cycle (tc.macro leaves G65 P5 Q1) sending this 3D-probe descent to the wrong
            // input and driving into the work. Only when the controller has a tool setter to select between.
            if (GrblInfo.HasToolSetter)
                L(string.Format(GrblCommand.ProbeSelect, p.ProbeType == ProbeType.ToolSetter ? 1 : 0));
            // Clear any stale WCS rotation BEFORE probing. pcorner's face probes are work-coordinate G38.2 moves, so
            // a rotation on this WCS (NVS garbage after enabling ROTATION_ENABLE, or one a previous Load Stock run
            // wrote) rotates every probe target -> shifts the measured corners -> inflates the skew, and compounds
            // run-over-run ("never touched the stock but the skew keeps growing"). Zero it so each measurement is
            // clean; the true measured skew is applied at the end. Only on firmware that reports WCSROT ($I) - the
            // R word errors:20 on plain builds.
            if (GrblInfo.RotationSupported)
                L(string.Format("G10 L2 {0} R0", pCode(wcsP)));
            L(string.Format("#<_ls_rad> = {0}", N(r)));   // probe tip radius (global, read by pcorner)
            // Sacrificial spacer/backer thickness under the stock (0 = none). pcorner's effective floor becomes
            // spoilboard + spacer, so a thin sheet on a backer is probed on the metal, not down in the backer.
            L(string.Format("#<_ls_spacer> = {0}", N(spacer)));
            // Probe-geometry offset (into the stock, from the fixture's reference) - derived from the SAME
            // probe definition doing the probing, not stored per-fixture: the fixture only captures the
            // single reference point (fx.Coords, "Set position"), which the Fixture edit dialog's schematic
            // shows as a clearance circle sized to the probe's body diameter. Spoilboard probe stays AT the
            // reference (clear air below it, per the DISCOVER precondition); top-probe clearance keeps the
            // probe BODY off the corner while it seeks down. The edge (X/Y face) probes no longer need a
            // separate offset - pcorner.macro anchors them off the top-probe's own verified XY instead.
            double topClearance = p.MinStandoff + 9d;
            L(string.Format("#<_ls_spoilx> = {0}", N(0d)));
            L(string.Format("#<_ls_spoily> = {0}", N(0d)));
            L(string.Format("#<_ls_topx> = {0}", N(topClearance)));
            L(string.Format("#<_ls_topy> = {0}", N(topClearance)));
            // Z of the fixture instance's saved position - the spoilboard-probe Z-start now comes from THIS
            // (decoupled from the firmware's G28 Z, #5163) so it matches whichever instance is selected.
            L(string.Format("#<_ls_spoilz> = {0}", N(fxPos.Z)));
            L(string.Format("#<_ls_searchf> = {0}", N(p.ProbeFeedRate)));   // fast search feed (from the 3D probe definition)
            L(string.Format("#<_ls_latchf> = {0}", N(p.LatchFeedRate)));    // slow latch/re-probe feed (from the definition)
            // Machine Z soft-limit floor (machine coords): the lowest Z the macro may POSITION a probe to. The
            // face-probe start height is the stock top minus a few mm; when the stock sits low (top near the Z
            // travel bottom) that target can fall below reachable Z and a G53 move to it trips Alarm:2 (soft
            // limit). pcorner clamps start_z to this. Assumes Z homes to the top and travels negative (the usual
            // case); 1 mm above the absolute limit. -9999 (= no clamp) when travel ($132) is unknown/zero.
            L(string.Format("#<_ls_zfloor> = {0}", N(GrblInfo.MaxTravel.Z > 0d ? -(GrblInfo.MaxTravel.Z) + 1.0d : -9999d)));

            L("(park at G30 - install / confirm the probe)");
            EmitGotoG30(L);
            L("(WAITIDLE)");
            L("(MBOX, OKCANCEL, Install and seat the probe, then click OK. Cancel aborts.)");

            // Corner 1 = the selected origin corner: reference = the fixture instance's saved position,
            // start_z = 9999 -> DISCOVER (publishes #<_start_z>).
            L(string.Format("(--- corner 1 = {0} (origin): reference {1}, discover Z ---)", cornerName, fx.Name));
            EmitCall(id1, refX, refY, "9999");
            L("#<c1x> = #<_corner_x>");
            L("#<c1y> = #<_corner_y>");
            L("#<c1z> = #<_corner_z>");

            // Tool-length reference (opt-in): with measure UNCHECKED this makes Load Stock == a plain Start Job
            // (origin + TLO ref). The 3D probe is already in the spindle (installed at the top), so this is the
            // M6 T8 "reference" path in tc.macro: reset the ref, probe the puck at G59.3, store the probe machine-Z
            // as #<_tlo_ref>, park at G30. Emitted right after corner 1 while WCO is still 0, so the remaining
            // corners (probed in work coords) and the end-of-run origin block are unaffected. Needs ATC + a
            // toolsetter at G59.3 (both already in the PREREQ). tc.macro is what applies the ref on later M6s.
            if (setTloRef)
            {
                L("(--- set tool-length reference at the puck (probe already in spindle) ---)");
                L("#<_tlo_ref> = 0");
                L("M6 T8");
                L("(WAITIDLE)");
            }

            if (measure)
            {
                // The other three reuse start_z; references come from the probed origin + estimate on the spanning
                // axis and the fixture instance's saved position on the shared axis. corner 2 = X-neighbour,
                // 3 = Y-neighbour, 4 = diagonal.
                L(string.Format("(--- corner 2 = {0} (X-neighbour) ---)", Name(xn)));
                EmitCall(CornerId(xn), plusMinus("#<c1x>", sox, estW), refY, "#<_start_z>");
                L("#<c2x> = #<_corner_x>");
                L("#<c2y> = #<_corner_y>");

                L(string.Format("(--- corner 3 = {0} (Y-neighbour) ---)", Name(yn)));
                EmitCall(CornerId(yn), refX, plusMinus("#<c1y>", soy, estH), "#<_start_z>");
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

            // Park at G30 BEFORE setting the origin, so every G53 move in this program runs with WCO=0. Firmware
            // bug: once a non-zero WCS offset is active, G53 moves false-alarm (the offset is applied to the G53
            // machine target -> Y drops below travel -> Alarm:2). Origin-last keeps the park (and the M6 T8 TLO
            // reference emitted after corner 1) at WCO=0, where G53 behaves.
            L("(--- park at G30 (before the origin - keeps all G53 moves at WCO=0) ---)");
            EmitGotoG30(L);

            L(string.Format("(--- set work origin at the {0} corner ---)", cornerName));
            // Origin ONLY here - never the rotation R word (that goes in the separate block below). The R word
            // (incl. R0) only exists on ROTATION_ENABLE firmware; without it ANY "G10 L2 ... R..." errors:20 and
            // HALTS the program. This block stays bulletproof on every controller.
            L(string.Format("G10 L2 {0} X[#<c1x>] Y[#<c1y>] Z[#<c1z>]", pCode(wcsP)));
            L(wcs + "  (activate the coordinate system)");

            // Stock skew -> WCS rotation, as a SEPARATE block AFTER the origin is set and the machine has parked.
            // G10 L2 R<deg> stores the rotation in the WCS (grblHAL ROTATION_ENABLE), so every later program run in
            // this WCS - file, folder, Height Map, wizard - is cut aligned to the stock. ATAN returns degrees.
            // Emitted only when the user asked for it; positioned last so that if the firmware lacks ROTATION_ENABLE
            // (error:20) the worst case is "no rotation" - the origin is already set and the machine already parked.
            // Only emit the rotation when the controller actually supports it (reports WCSROT in $I): on firmware
            // without ROTATION_ENABLE the G10 L2 R word errors:20, so gating it here means the R line is never sent
            // to a controller that would reject it - Load Stock always completes, with rotation when available.
            if (measure && applyRotation && GrblInfo.RotationSupported)
            {
                // NOTE the leading negation: grblHAL's G10 L2 R rotates the coordinate frame in the opposite
                // sense to the raw front-edge angle, so the rotation that ALIGNS the work frame to the stock is
                // -atan2(dy,dx). Without the negation the far edge lands off by ~2*width*sin(angle) (the Verify
                // skew check showed the right-hand corners a couple of mm short in Y).
                L("#<rot> = 0 - ATAN[#<c2y> - #<c1y>]/[#<c2x> - #<c1x>]");
                L(string.Format("G10 L2 {0} R[#<rot>]", pCode(wcsP)));
                L("(PRINT, LS_ROT=#<rot>)");
            }
            L("M2");

            return b.ToString();
        }

        // Machinist vise: the fixture's Coords ARE the precise origin X/Y (probed once via pvisecorner.macro at
        // Set position time, FixtureEditDialog.RunViseCornerProbe) - no per-job edge probing (ProbesEdges ==
        // false), so unlike BuildProgram this never calls pcorner.macro at all. Only Z needs finding here, from
        // the ACTUAL stock - fx.Coords.Z is the JAW's top (+ a small safety margin), not the material's, since
        // stock usually sits at an unpredictable height above the jaws (parallels, clamped height, etc.). Probes
        // straight down at the CENTRE of the entered stock footprint, per the user's confirmed design. No
        // measure/skew support - the jaw is rigid and machine-aligned (assumed square to the machine axes),
        // there is nothing to measure. UNVERIFIED on hardware - this is a first cut (see the file header note).
        private static string BuildViseProgram(ProbeDefinition p, Fixture fx, double estW, double estH, double estThickness, int wcsP, bool setTloRef)
        {
            var fxPos = new Position(fx.Coords);
            string wcs = "G" + (53 + Math.Min(Math.Max(wcsP, 1), 6)).ToString(CultureInfo.InvariantCulture);

            // Stock sits BETWEEN the jaws: from the jaw's probed front-left corner, it extends +X (rightward -
            // the SAME direction pvisecorner.macro's own top-probe offset uses, assuming stock butts against the
            // jaw's probed left end) and -Y (toward the operator/moving jaw - the OPPOSITE of the jaw's own bulk,
            // which is +Y from its corner; the stock is clamped IN FRONT of the jaw's face, not behind it).
            double centerX = fxPos.X + estW / 2d;
            double centerY = fxPos.Y - estH / 2d;

            var b = new StringBuilder();
            int lineNo = 0;
            void L(string s)
            {
                s = SanitizeParens(s);
                string t = s.TrimStart();
                bool oword = t.Length > 1 && (t[0] == 'O' || t[0] == 'o') && t[1] == '<';
                if (s.Length > 0 && s[0] != '(' && !oword)
                    b.Append('N').Append((lineNo += 10).ToString(CultureInfo.InvariantCulture)).Append(' ');
                b.Append(s).Append('\n');
            }

            L("(Start Job - vise: origin from the probed jaw corner, Z from a stock-top probe)");
            L(string.Format("(Probe \"{0}\": tip {1}mm body {2}mm.)", p.Name, N(p.ProbeDiameter), N(p.BodyDiameter)));
            L(string.Format("(Fixture \"{0}\": jaw corner from Set position - see Fixture definitions.)", fx.Name));
            L("(Z is probed at the CENTRE of the entered stock footprint - keep width/height/thickness honest,");
            L("not oversized: thickness sets how far above the jaw this descends before it starts probing.)");
            L("(Requires grblHAL NGC expressions. First run for this fixture kind - VALIDATE before trusting.)");

            string prereq = "connected, homed, EXPR, G28, G30";
            if (setTloRef)
                prereq += ", ATC=1, G59.3";
            L(string.Format("(PREREQ, {0})", prereq));

            L("G21 G90 G94 G17");
            L("G49");
            if (GrblInfo.HasToolSetter)
                L(string.Format(GrblCommand.ProbeSelect, p.ProbeType == ProbeType.ToolSetter ? 1 : 0));
            if (GrblInfo.RotationSupported)
                L(string.Format("G10 L2 {0} R0", pCode(wcsP)));   // clear any stale rotation - vise never measures skew

            L("(park at G30 - install / confirm the probe)");
            EmitGotoG30(L);
            L("(WAITIDLE)");
            L("(MBOX, OKCANCEL, Install and seat the probe, then click OK. Cancel aborts.)");

            // Clear G54 so the Z probe below runs in machine coordinates (same reasoning as pcorner.macro).
            L("G10 L2 P1 X0 Y0 Z0");
            L("G90");

            L("(--- stock top Z, probed at the footprint centre ---)");
            L("G53 G1 F1000 Z0");   // retract fully to machine top before crossing - no guessing an intermediate height
            L(string.Format("G53 G1 F1000 X{0} Y{1}", N(centerX), N(centerY)));
            // Safe descent height: above the ESTIMATED stock top (jaw top + typed thickness + margin), not the
            // jaw's own height - the whole point is the stock is a different height than the jaw. If thickness
            // is UNDERESTIMATED this bare (controlled-feed) descent could contact the stock before reaching the
            // computed height - same reasoning as Corner Fence wanting the width/height estimate a bit oversize.
            L(string.Format("G53 G1 F1000 Z[{0} + {1} + 10]", N(fxPos.Z), N(estThickness)));
            L(string.Format("G38.2 Z[{0} - 15] F{1}", N(fxPos.Z), N(p.ProbeFeedRate)));
            L("G91 G1 Z2 F1000");
            L(string.Format("G38.2 Z-5 F{0}", N(p.LatchFeedRate)));
            L("#<_stock_z> = #5063");
            L("G91 G1 Z10 F1000");
            L("G90");
            L("(PRINT, LS_VISE_Z=#<_stock_z>)");

            // Tool-length reference (opt-in), right after the Z probe while WCO is still 0 - same placement/
            // reasoning as BuildProgram's.
            if (setTloRef)
            {
                L("(--- set tool-length reference at the puck (probe already in spindle) ---)");
                L("#<_tlo_ref> = 0");
                L("M6 T8");
                L("(WAITIDLE)");
            }

            L("(--- park at G30 (before the origin - keeps all G53 moves at WCO=0) ---)");
            EmitGotoG30(L);

            L("(--- set work origin: X/Y from the probed jaw corner, Z from the stock-top probe ---)");
            L(string.Format("G10 L2 {0} X{1} Y{2} Z[#<_stock_z>]", pCode(wcsP), N(fxPos.X), N(fxPos.Y)));
            L(wcs + "  (activate the coordinate system)");
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
        // Every G53 SPECIFIES X and Y (held at the current machine position via #<_abs_x>/#<_abs_y>, then at the G30
        // X/Y) instead of leaving them implicit. A firmware bug sign-flips the parser base of a homing-direction-
        // inverted ($23) axis after a G53 move, so a G53 with that axis "unmoved" (e.g. a bare "G53 G0 Z0") targets
        // it from the flipped base -> false Alarm:2. Naming the axis uses the literal value and dodges the bug.
        private static void EmitGotoG30(System.Action<string> L)
        {
            L("G53 G0 X[#<_abs_x>] Y[#<_abs_y>] Z0");   // lift Z to machine top, X/Y held at current
            L("G53 G0 X[#5181] Y[#5182]");              // traverse to G30 X/Y at the top
            L("G53 G0 X[#5181] Y[#5182] Z[#5183]");     // descend to G30 Z (X/Y named to avoid the unmoved-axis bug)
        }

        // P-word for G10 L2 (P1=G54..P6=G59).
        private static string pCode(int wcsP) { return "P" + Math.Min(Math.Max(wcsP, 1), 6).ToString(CultureInfo.InvariantCulture); }

        // "baseExpr + mag" or "baseExpr - mag" depending on dir (keeps generated expressions clean - no "- -5").
        private static string plusMinus(string baseExpr, int dir, double mag) { return baseExpr + (dir >= 0 ? " + " : " - ") + N(mag); }

        private static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
