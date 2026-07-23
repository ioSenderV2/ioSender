/*
 * StartJobView.xaml.cs - part of ioSender XL
 *
 * "Load stock" top-level tab. Pick a probe definition + the stock corner the probe is parked over +
 * the approximate stock size, then Generate an inline grblHAL NGC probe program (shown locally) and
 * Run it through the macro path (MacroProcessor.Run) - which streams NGC expressions, #params
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
        // BuildViseProgram's corner 1/3 Z probes (jaw-covered edge, Y-face blocked): "LS_VISE_ZC1=.." / "LS_VISE_ZC3=..".
        private static readonly Regex rxViseCornerZ = new Regex(@"LS_VISE_ZC([13])\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // BuildViseProgram's corner 1/3 left-edge X-face probes: "LS_VISE_CX1=.." / "LS_VISE_CX3=..".
        private static readonly Regex rxViseCornerX = new Regex(@"LS_VISE_CX([13])\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // BuildViseProgram's left-edge skew (corner 1 vs corner 3 X, over the entered height): "LS_VISE_SKEW=..".
        private static readonly Regex rxViseSkew = new Regex(@"LS_VISE_SKEW\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // BuildViseProgram's centre-footprint stock-top Z probe: "LS_VISE_Z=..".
        private static readonly Regex rxViseCenterZ = new Regex(@"LS_VISE_Z\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        private double? measuredX = null, measuredY = null, spoilZ = null, viseLeftEdgeSkewDeg = null, viseCenterZ = null;
        // One-shot per run: suppresses the calibration-suggestion dialog (ShowResult) from firing again on
        // every later PRINT line once all 4 corners have already arrived once this run.
        private bool sizeWarningShown = false;
        // Per-corner probed machine coords, indexed by the macro's corner id 1..4 = FL,FR,BL,BR.
        private readonly double?[] cornerX = new double?[5], cornerY = new double?[5], cornerZ = new double?[5];
        private bool measureRun = false;

        // Whether Width/Height/Thickness have been explicitly set THIS session - either hand-edited, or (once
        // that exists) auto-filled from a loaded program's stock size. LoadInputs restores last session's
        // saved numbers silently on tab activation; those are leftovers, not necessarily right for THIS stock -
        // Generate warns if none of the three were touched (see Generate_Click).
        private bool sizeFieldsTouched = false;
        private bool loadingInputs = false;   // true only while LoadInputs is assigning - suppresses the touched flag

        // True only between Activate(true) and Activate(false). Model_PropertyChanged stays subscribed even
        // while deactivated (see Activate's own comment - it keeps parsing result messages after the tab is
        // left mid-run), so anything it triggers that writes a MacroProcessor Generate-mode static (which is
        // GLOBAL, shared with whichever tab is now actually focused) must be gated on this - otherwise a stale
        // GrblState/Message event on this tab after leaving it could stomp the NEW active tab's IsGenerateReady.
        private bool isActiveTab = false;

        // Unit toggle for the Stock size fields, via the Stock panel's right-click header menu
        // (UnitToggleMenu, wired in WireInputs). NumericField.Value is ALWAYS canonical mm now
        // (NumericField/NumericTextBox's own mm<->in conversion, driven by the inherited
        // NumericField.IsImperial attached property set on grpStock) - every consumer (BuildProgram,
        // persistence, CheckStockAgainstProgram) reads/writes fldWidth.Value etc. directly, no ToMm/FromMm
        // wrapping needed there anymore. isImperial + ToMm/FromMm are kept ONLY for the free-standing
        // mm-in/text-out formatting helpers below (AddDimLabel, FormatLen) that format an arbitrary mm
        // value for display without going through a NumericField at all.
        private bool isImperial = false;
        private double ToMm(double displayValue) { return isImperial ? displayValue * 25.4d : displayValue; }
        private double FromMm(double mm) { return isImperial ? mm / 25.4d : mm; }

        // Probe type is a ComboBox now (cbxProbeType: index 0 = 3D Probe, 1 = Touch Plate), not a radio pair -
        // this is the single read/write point every other call site goes through instead of touching
        // SelectedIndex directly.
        private bool IsTouchPlate
        {
            get { return cbxProbeType.SelectedIndex == 1; }
            set { cbxProbeType.SelectedIndex = value ? 1 : 0; }
        }

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
            DataContextChanged += (s, e) => { if (e.NewValue is GrblViewModel m) model = m; };
            WireInputs();
        }

        // Any input edit redraws the stock outline AND invalidates a previously generated program (so Run is
        // disabled until Generate is pressed again - the program would otherwise be stale).
        private void WireInputs()
        {
            cbxWcs.SelectionChanged += (s, e) => InputChanged();
            chkSetOrigin.Checked += (s, e) => InputChanged();
            chkSetOrigin.Unchecked += (s, e) => InputChanged();
            chkMeasure.Checked += (s, e) => { UpdateSizeHint(); InputChanged(); };
            chkMeasure.Unchecked += (s, e) => { UpdateSizeHint(); InputChanged(); };
            chkExactSize.Checked += (s, e) => { UpdateSizeHint(); InputChanged(); };
            chkExactSize.Unchecked += (s, e) => { UpdateSizeHint(); InputChanged(); };
            chkRotate.Checked += (s, e) => InputChanged();
            chkRotate.Unchecked += (s, e) => InputChanged();
            chkSetTloRef.Checked += (s, e) => InputChanged();
            chkSetTloRef.Unchecked += (s, e) => InputChanged();
            chkStockConductive.Checked += (s, e) => { UpdateMeasureAvailability(); InputChanged(); };
            chkStockConductive.Unchecked += (s, e) => { UpdateMeasureAvailability(); InputChanged(); };
            // Switching probe type changes which ProbeDefinition Generate needs (and whether it's defined) -
            // re-gate immediately rather than waiting for the next Activate/capability refresh.
            cbxProbeType.SelectionChanged += (s, e) => { UpdateProbeWarning(); InputChanged(); };
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldWidth, (s, e) => { MarkSizeFieldsTouched(); CheckStockAgainstProgram(); InputChanged(); });
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldHeight, (s, e) => { MarkSizeFieldsTouched(); CheckStockAgainstProgram(); InputChanged(); });
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldThickness, (s, e) => { MarkSizeFieldsTouched(); CheckStockAgainstProgram(); UpdateThicknessWarning(); InputChanged(); });
            DependencyPropertyDescriptor.FromProperty(NumericField.ValueProperty, typeof(NumericField)).AddValueChanged(fldSpacer, (s, e) => InputChanged());

            // Stock panel's right-click header menu (UnitToggleMenu) replaces the old rbUnitsMm/rbUnitsIn
            // radio pair - it sets NumericField.IsImperial on grpStock directly. Mirror that back into the
            // local isImperial field (used by ToMm/FromMm's free-standing formatting helpers below, which
            // don't go through a NumericField) whenever it changes, same as Units_Checked used to.
            CNC.Controls.UnitToggleMenu.Attach(grpStock);
            DependencyPropertyDescriptor.FromProperty(NumericField.IsImperialProperty, typeof(NumericField)).AddValueChanged(grpStock, (s, e) =>
            {
                if (loadingInputs)
                    return;

                bool newImperial = NumericField.GetIsImperial(grpStock);
                if (newImperial == isImperial)
                    return;

                isImperial = newImperial;
                UpdateThicknessWarning();
                ShowResult();   // re-format the bottom-left readout (X=/Y=/etc, FormatLen) too, not just the drawing - it was left stale on a unit toggle
            });
        }

        private void MarkSizeFieldsTouched()
        {
            if (!loadingInputs)
                sizeFieldsTouched = true;
        }

        // Width/Height/Thickness are the operator's own entered numbers for THIS stock - Start Job must never
        // silently overwrite them from whatever program happens to be loaded (confirmed unwanted: a job loaded
        // for an unrelated reason, or loaded AFTER the fields were already set for the stock on the machine,
        // was clobbering the just-entered values). Instead: if the loaded job's own (STOCK X=.. Y=.. Z=..)
        // comment (the Fusion ioSenderBatchPost add-in's format, via ProgramView.LoadedJob.DeclaredStock -
        // computed fresh from GCode.File.Data each time, not cached) differs from what's currently entered,
        // reveal btnCopyFromStock instead of applying anything - the operator decides. No declared comment,
        // or it matches what's already entered (within rounding), and the button just stays hidden.
        private const double StockMatchToleranceMm = 0.05d;
        private void CheckStockAgainstProgram()
        {
            if (btnCopyFromStock == null)
                return;

            var stock = CNC.Controls.ProgramView.LoadedJob?.DeclaredStock;
            if (stock == null)
            {
                btnCopyFromStock.Visibility = Visibility.Collapsed;
                return;
            }

            bool matches = Math.Abs(fldWidth.Value - stock.Value.X) < StockMatchToleranceMm &&
                           Math.Abs(fldHeight.Value - stock.Value.Y) < StockMatchToleranceMm &&
                           Math.Abs(fldThickness.Value - stock.Value.Z) < StockMatchToleranceMm;
            btnCopyFromStock.Visibility = matches ? Visibility.Collapsed : Visibility.Visible;
        }

        private void CopyFromStock_Click(object sender, RoutedEventArgs e)
        {
            var stock = CNC.Controls.ProgramView.LoadedJob?.DeclaredStock;
            if (stock == null)
                return;

            loadingInputs = true;   // these three ARE explicit values (from the program), not a silent restore -
            try                     // suppress MarkSizeFieldsTouched's per-field firing so we can set it once below
            {
                fldWidth.Value = stock.Value.X;
                fldHeight.Value = stock.Value.Y;
                fldThickness.Value = stock.Value.Z;
                UpdateThicknessWarning();
            }
            finally
            {
                loadingInputs = false;
            }
            sizeFieldsTouched = true;
            btnCopyFromStock.Visibility = Visibility.Collapsed;
            if (model != null)
                model.Message = "Stock size copied from the program's (STOCK) comment.";
        }

        // The pcorner probe macro assumes stock <= 1 in (25.4 mm) to start its top probe
        // just above a 1 in top for speed - taller stock would be missed. Flag it when the Z estimate exceeds that.
        private const double MaxStockThicknessMm = 25.4d;
        private void UpdateThicknessWarning()
        {
            if (txtThickWarn != null)
                txtThickWarn.Visibility = fldThickness.Value > MaxStockThicknessMm ? Visibility.Visible : Visibility.Collapsed;
        }

        // The width/height fields are the actual size when not measuring; when Measure is on they are only a
        // conservative estimate pcorner uses to place the far-corner probes (must be a few mm oversize).
        // Exception: the Machinist Vise's entered sizes are ALWAYS exact (precision machinist stock, not an
        // estimate to buffer against - see BuildViseProgram's own comment on this), regardless of Measure.
        private void UpdateSizeHint()
        {
            if (txtSizeHint == null)
                return;

            bool isVise = SelectedFixture != null && SelectedFixture.Kind == FixtureKind.MachinistVise;
            // "Stock size is exact" (chkExactSize) is only enabled/meaningful while Measure is also checked
            // (see its XAML IsEnabled binding) - same "the entered numbers ARE the true size, not a
            // conservative buffer" case the vise is always in, just opt-in per run instead of per fixture kind.
            bool isExact = !isVise && chkMeasure.IsChecked == true && chkExactSize.IsChecked == true;

            if (isVise)
            {
                fldWidth.Label = "Exact width (X):";
                fldHeight.Label = "Exact height (Y):";
                fldThickness.Label = "Exact thickness (Z):";
                txtSizeHint.Text = "Sizes should be exact.";
            }
            else if (isExact)
            {
                fldWidth.Label = "Width (X):";
                fldHeight.Label = "Height (Y):";
                fldThickness.Label = "Thickness (Z):";
                txtSizeHint.Text = "Exact size - corners 2-4 probe close to their computed true position instead of a conservative estimate. Only enable if you trust these numbers; a wrong size can miss the stock.";
            }
            else
            {
                fldWidth.Label = "Est. width (X):";
                fldHeight.Label = "Est. height (Y):";
                fldThickness.Label = "Est. thickness (Z):";
                txtSizeHint.Text = chkMeasure.IsChecked == true
                    ? "Estimate only - make it a few mm larger than actual so the far-corner probes land just outside the stock. Probing measures the true size."
                    : "Actual stock size.";
            }
        }

        // The configured 3D probe supplies the tip radius and the search/latch feeds the program needs.
        // Null when none is defined.
        private ProbeDefinition ThreeDProbe()
        {
            return ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
        }

        // Touch plate: probes by electrical continuity (same physical probe input as the 3D probe - see
        // pcorner.macro's _ls_mode). ProbeDiameter here is the "fallback diameter" field (whatever bit/dowel
        // is actually in the collet for THIS Start Job run) - unlike the general Probing tab, Start Job does
        // not read the loaded program's own (TOOL T=n D=..) comment, since Start Job typically runs before a
        // job is loaded. Null when none is defined.
        private ProbeDefinition TouchPlateProbe()
        {
            return ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.TouchPlate);
        }

        // Touch plate diameter: prefer the loaded program's own (TOOL T=n D=...) comment for the CURRENTLY
        // loaded tool (GCodeProgramComments.DiameterFor, same source ProbingViewModel.SelectedProbe already
        // uses for the general Probing tab) over the probe definition's fallback field. There's no firmware
        // tool table here (N_TOOLS=0), so the program's own comments are the only live source of "what's
        // actually in the collet"; falls back when no program is loaded yet or its comments don't mention
        // this tool number.
        private double ActiveOrFallbackProbeDiameter(ProbeDefinition p)
        {
            if (model != null && model.Tool != GrblConstants.NO_TOOL && int.TryParse(model.Tool, out int currentTool))
            {
                double? live = GCodeProgramComments.DiameterFor(currentTool);
                if (live.HasValue)
                    return live.Value;
            }
            return p.ProbeDiameter;
        }

        // The probe definition Generate should actually use, per the Probe: radio selection.
        private ProbeDefinition ActiveProbe()
        {
            return IsTouchPlate ? TouchPlateProbe() : ThreeDProbe();
        }

        // Touch Plate is only selectable when a touch-plate probe is actually defined (same rule the 3D probe
        // already follows) - fall back to 3D Probe if the definition disappears (library edited) while it was
        // selected, rather than leave a disabled-but-selected combo item. Enable Generate only when the
        // CURRENTLY SELECTED probe type is defined; otherwise show the "define a probe" hint.
        private void UpdateProbeWarning()
        {
            bool touchAvailable = TouchPlateProbe() != null;
            cbiProbeTouch.IsEnabled = touchAvailable;
            if (!touchAvailable && IsTouchPlate)
                IsTouchPlate = false;

            // The conductive checkbox only matters for Touch Plate probing - mirrors what the old
            // rbProbeTouch.IsChecked ElementName binding on chkStockConductive.IsEnabled did.
            chkStockConductive.IsEnabled = IsTouchPlate;

            bool ok = ActiveProbe() != null;
            if (isActiveTab)
                MacroProcessor.IsGenerateReady = ok;
            txtNoProbe.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;

            UpdateMeasureAvailability();
        }

        // A separate touch plate against non-conductive stock only closes the circuit at the plate itself -
        // the stock's own edges never trigger it, so the edge probing Measure depends on can't work. Disable
        // (and force off, so a stale checked-but-disabled state can't sneak into Generate) rather than just
        // hide, since the reason ("switch to 3D Probe, or check Stock conductive") is worth surfacing via the
        // tooltip rather than silently vanishing.
        private void UpdateMeasureAvailability()
        {
            bool touchNonConductive = IsTouchPlate && chkStockConductive.IsChecked != true;
            chkMeasure.IsEnabled = !touchNonConductive;
            if (touchNonConductive && chkMeasure.IsChecked == true)
                chkMeasure.IsChecked = false;
            chkMeasure.ToolTip = touchNonConductive
                ? "Not available with Touch Plate probing on non-conductive stock: the plate closes the circuit itself, so the stock's edges never trigger it and can't be measured. Switch to 3D Probe, or check 'Stock conductive' if the stock is touched directly."
                : "Probe all four corners to measure the true stock size and skew. When off, the width/height above are used as-is and only the front-left origin (and optional TLO reference) is set.";
        }

        // (Re)load the fixture library into the dropdown, preserving the current selection by name - falling
        // back to the persisted selection (StartJobConfig, see LoadInputs/SaveInputs) when nothing is
        // currently selected, i.e. on the very first activation each session. Fixtures are DEFINED (including
        // their position) in Machine Setup > Fixture definitions - Start Job only selects one, and only lists
        // ones with a VALIDATED position: "Test position" (FixtureEditDialog) has actually run the real
        // spoilboard probe search and the controller didn't alarm. A merely-captured-but-untested position is
        // exactly what caused an Alarm:5 probe fail mid-Start-Job (the 12 mm search cap missed because the
        // saved Z was too far above the spoilboard) - offering it here just invites repeating that instead of
        // validating it in Machine Setup first.
        // Synthetic "fixture" for the raw firmware G28 stored position - never added to Fixtures.Items/App.config,
        // just an always-available dropdown entry that recreates the ORIGINAL pre-Fixture-library Start Job
        // behavior: a loose DISCOVER-mode probe of corner 1 anchored at G28 (read live from the controller at
        // run-time - #5161/#5162/#5163 - never known to ioSender at generate-time, unlike a real Fixture's cached
        // Coords/CornerOffsetX/Y/SpoilboardZ), then everything downstream (corners 2-4, Measure, rotation, TLO
        // ref, origin) runs exactly like a real Corner Fence fixture - it only ever consumes corner 1's OUTPUT
        // (c1x/c1y/c1z), not how it was obtained. Identified by REFERENCE equality (IsG28), not a name/Kind
        // match, so a user-named fixture happening to also be called "G28" can never collide with this.
        // PositionValidated=true so it clears Generate_Click's own "fx.PositionValidated" gate below - it's not
        // a REAL validated position (there is none to validate - see the class comment), just enough to make
        // this synthetic entry behave like a normal selectable fixture. Whether G28 itself is actually set is
        // checked separately, explicitly, in Generate_Click (jog-and-confirm dialog), not via this flag.
        private static readonly Fixture G28Fixture = new Fixture { Name = "G28 (loose probe)", Kind = FixtureKind.CornerFence, PositionValidated = true };
        private static bool IsG28(Fixture fx) { return ReferenceEquals(fx, G28Fixture); }

        private void RefreshFixtures()
        {
            string current = SelectedFixture?.Name;
            if (string.IsNullOrEmpty(current))
                current = StartJobConfig.Section?.Fixture;
            var items = Fixtures.Items.Where(f => f.PositionValidated).ToList();
            items.Add(G28Fixture);   // always available - synthetic, not a saved/validated fixture (see its own comment)
            cbxFixture.ItemsSource = items;
            if (!string.IsNullOrEmpty(current))
                cbxFixture.SelectedItem = items.FirstOrDefault(f => f.Name == current);
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
            if (isActiveTab)
                MacroProcessor.IsGenerateReady = ok && ActiveProbe() != null;

            bool showMeasure = fx == null || FixtureKinds.CanMeasure(fx.Kind);
            chkMeasure.Visibility = showMeasure ? Visibility.Visible : Visibility.Collapsed;
            // Rotation (skew correction) needs a REAL 4th corner to check the stock is actually square - a
            // vise's partial measure never gets one (corner 1's Y is an estimate, never face-probed - see
            // BuildViseProgram's measure block), so rotate stays gated on the full-4-corner kinds only.
            bool showRotate = (fx == null || FixtureKinds.ProbesEdges(fx.Kind)) && GrblInfo.RotationSupported;
            chkRotate.Visibility = showRotate ? Visibility.Visible : Visibility.Collapsed;

            // Every field below (size, units, probe type, set-origin WCS, measure/rotate/exact-size/TLO-ref,
            // Safe Z delta) describes something meaningless before a fixture is even chosen - its saved
            // reference is what the generated program probes from. Gated as ONE block per panel
            // (pnlSetupGated/pnlStock/pnlActions, see the XAML - split across the Setup/Stock/Actions
            // groupboxes now, so the 3 panel roots are set together here instead of 1) rather than
            // field-by-field: individually gating only size/units/spacer used to leave the rest (Probe type,
            // Set origin, Measure, Rotate, TLO ref, Safe Z delta) fully interactive with no fixture selected -
            // confusing (some fields greyed, some not, for no visible reason) and easy to forget when adding
            // a new field. A disabled ancestor overrides each control's own IsEnabled/binding (e.g. Rotate's
            // own chkMeasure-driven binding), so this doesn't fight them.
            bool fixtureChosen = fx != null;
            pnlSetupGated.IsEnabled = pnlStock.IsEnabled = pnlActions.IsEnabled = fixtureChosen;
            fldSpacer.Visibility = fixtureChosen ? Visibility.Visible : Visibility.Collapsed;

            UpdateDrawing();   // origin-corner marker (+ jaws for a vise) tracks the selected fixture's kind
        }

        private void cbxFixture_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFixtureWarning();
            UpdateSizeHint();   // vise sizes are exact, unlike every other fixture kind - see UpdateSizeHint
            InputChanged();
        }

        private void InputChanged()
        {
            InvalidateProgram();
            UpdateDrawing();
        }

        // Drop the generated program; Cycle Start (which runs the active program) rebuilds it via Run_Click.
        // Also registered as MacroProcessor.DiscardGenerated (see Activate) - called there too, right after a
        // clean run finishes, so the Run bar reverts to "Generate" for the next job rather than re-running
        // a stale program. Only touch the shared static while THIS tab is actually the focused one (see
        // isActiveTab's own comment) - an input change firing after the tab was left, or a discard call that
        // raced a tab switch, must not stomp whichever OTHER Generate-capable tab is now active.
        private void InvalidateProgram()
        {
            program = string.Empty;
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = false;
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
            isActiveTab = activate;
            if (activate)
            {
                if (model == null)
                    model = DataContext as GrblViewModel;
                if (!loaded) { LoadInputs(); loaded = true; }   // restore the last estimate/options
                CheckStockAgainstProgram();
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

                // Generate-mode registration (see MacroProcessor's own comments): the Run bar itself reads
                // "Generate" until this tab has built its program, then "Run" - no standalone Generate button
                // of this tab's own any more. IsProgramGenerated picks up whatever 'program' already holds
                // (e.g. reactivating this tab after a run elsewhere without an intervening input change).
                MacroProcessor.SupportsGenerateMode = true;
                MacroProcessor.ActiveGenerate = () => Generate_Click(null, null);
                MacroProcessor.DiscardGenerated = InvalidateProgram;
                MacroProcessor.IsProgramGenerated = !string.IsNullOrEmpty(program);
                UpdateFixtureWarning();   // also (re)establishes MacroProcessor.IsGenerateReady for the bar
            }
            else
            {
                SaveInputs();
                MacroProcessor.ActiveRun = null;
                MacroProcessor.SupportsGenerateMode = false;
                MacroProcessor.ActiveGenerate = null;
                MacroProcessor.DiscardGenerated = null;
                // Discard the generated program on tab-leave too (not just after a run finishes - see
                // InvalidateProgram's own comment) - so the tab is always back at "Generate" next time it's
                // focused. Not routed through InvalidateProgram() itself: its isActiveTab guard would block
                // the MacroProcessor.IsProgramGenerated write here, since isActiveTab was already set false
                // at the top of this same Activate() call - but this IS the moment that write belongs.
                program = string.Empty;
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

            foreach (Match m in rxViseCornerZ.Matches(msg))
            {
                int c = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                {
                    cornerZ[c] = z;
                    hit = true;
                }
            }

            // X only (never Y) for corners 1/3 - Has(c) still requires both, so this never fools the
            // full-quad gates (measured/skew/diagonal) that assume Corner Fence's 4-point geometry.
            foreach (Match m in rxViseCornerX.Matches(msg))
            {
                int c = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                {
                    cornerX[c] = x;
                    hit = true;
                }
            }

            var msk = rxViseSkew.Match(msg);
            if (msk.Success && double.TryParse(msk.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double skew))
            {
                viseLeftEdgeSkewDeg = skew;
                hit = true;
            }

            var mcz = rxViseCenterZ.Match(msg);
            if (mcz.Success && double.TryParse(mcz.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double vcz))
            {
                viseCenterZ = vcz;
                hit = true;
            }

            if (hit)
                Dispatcher.BeginInvoke(new System.Action(ShowResult));
        }

        // Clear measured size + per-corner data for a fresh run, then refresh the readout.
        private void ResetResults()
        {
            measuredX = measuredY = spoilZ = viseLeftEdgeSkewDeg = viseCenterZ = null;
            for (int i = 0; i < cornerX.Length; i++)
                cornerX[i] = cornerY[i] = cornerZ[i] = null;
            sizeWarningShown = false;
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

            CheckSizeAgainstEntered(probed);
        }

        // "Stock size is exact" means the operator is claiming Width/Height ARE the true size (not a
        // conservative over-estimate) - so once all 4 corners are in, a real mismatch against the measured
        // size isn't probe noise, it's the machine not moving the commanded distance. Confirmed on real
        // hardware: a 429mm (per a Woodpeckers precision ruler) MDF panel measured 427.787 x 427.464mm - a
        // ~1.2-1.5mm error over 429mm (~0.3%) is exactly the size/shape of a steps-per-mm calibration error,
        // not measurement noise, and it's what caused corners 2-4's face probes to nearly miss the stock
        // (their reference point is derived from the ENTERED exact size, so this class of error compounds
        // there too). One-shot per run (sizeWarningShown) - only checked once all 4 corners have arrived,
        // not re-shown as later PRINT lines (spoilZ, skew, ...) keep calling ShowResult.
        private const double SizeMismatchWarnMm = 0.5d;
        private void CheckSizeAgainstEntered(int probed)
        {
            if (sizeWarningShown || probed < 4 || chkExactSize.IsChecked != true || !measuredX.HasValue || !measuredY.HasValue)
                return;

            double dx = Math.Abs(measuredX.Value - fldWidth.Value);
            double dy = Math.Abs(measuredY.Value - fldHeight.Value);
            if (dx <= SizeMismatchWarnMm && dy <= SizeMismatchWarnMm)
                return;

            sizeWarningShown = true;
            Dispatcher.BeginInvoke(new System.Action(() => AppDialogs.Show(string.Format(
                "Measured size ({0} x {1}) differs from the entered exact size ({2} x {3}) by more than {4} - " +
                "X off by {5}, Y off by {6}. That's larger than normal probe noise for stock claimed to be exact; " +
                "it looks like the machine isn't moving the commanded distance. Consider running Stepper calibration " +
                "(Machine Setup > Tools) to check steps/mm on each axis.",
                FormatLen(measuredX.Value), FormatLen(measuredY.Value), FormatLen(fldWidth.Value), FormatLen(fldHeight.Value),
                FormatLen(SizeMismatchWarnMm), FormatLen(dx), FormatLen(dy)),
                "Start Job", MessageBoxButton.OK, MessageBoxImage.Warning)));
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
            // Vise jaw geometry, in the SAME local mm space as mx/my - the fixed jaw's own front-left corner
            // (the probed reference, where the origin dot goes) is exactly BL = (0, eh), so the jaw rectangle
            // below starts there with NO overhang; JawWidth/MaxOpening (0 = not set) come from the fixture.
            double jawWidthMm = 0d, openingMm = 0d;
            if (measured)
            {
                for (int c = 1; c <= 4; c++) { mx[c] = cornerX[c].Value; my[c] = cornerY[c].Value; }
            }
            else
            {
                double ew = Math.Max(fldWidth.Value, 1d), eh = Math.Max(fldHeight.Value, 1d);
                mx[1] = 0d;  my[1] = 0d;     // FL
                mx[2] = ew;  my[2] = 0d;     // FR
                mx[3] = 0d;  my[3] = eh;     // BL
                mx[4] = ew;  my[4] = eh;     // BR

                if (isVise)
                {
                    jawWidthMm = SelectedFixture.JawWidth > 0d ? SelectedFixture.JawWidth : ew + 20d;
                    openingMm = SelectedFixture.MaxOpening > 0d ? SelectedFixture.MaxOpening : eh;
                }
            }

            double minX = Math.Min(Math.Min(mx[1], mx[2]), Math.Min(mx[3], mx[4]));
            double maxX = Math.Max(Math.Max(mx[1], mx[2]), Math.Max(mx[3], mx[4]));
            double minY = Math.Min(Math.Min(my[1], my[2]), Math.Min(my[3], my[4]));
            double maxY = Math.Max(Math.Max(my[1], my[2]), Math.Max(my[3], my[4]));
            if (isVise)
            {
                // Widen the bounding box to the jaw's own footprint too - it can extend past the stock in X
                // (jawWidthMm > ew) or in Y (the moving jaw at MaxOpening can sit past the stock's front edge),
                // and everything must still fit the canvas.
                maxX = Math.Max(maxX, jawWidthMm);
                minY = Math.Min(minY, maxY - openingMm);
            }
            double spanX = Math.Max(maxX - minX, 1e-6), spanY = Math.Max(maxY - minY, 1e-6);
            double scale = Math.Min((W - 2d * margin) / spanX, (H - 2d * margin) / spanY);
            if (scale <= 0d || double.IsInfinity(scale) || double.IsNaN(scale))
                return canvas;
            double offX = (W - spanX * scale) / 2d, offY = (H - spanY * scale) / 2d;

            // machine X right / Y up (back) -> screen X right / Y down (flip Y)
            System.Func<double, double, Point> P2 = (x, y) => new Point(
                offX + (x - minX) * scale,
                H - offY - (y - minY) * scale);
            System.Func<int, Point> P = c => P2(mx[c], my[c]);

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
            double dimX = measured && measuredX.HasValue ? measuredX.Value : Math.Max(fldWidth.Value, 0d);
            double dimY = measured && measuredY.HasValue ? measuredY.Value : Math.Max(fldHeight.Value, 0d);
            AddDimLabel(canvas, P(1), P(2), dimX);
            AddDimLabel(canvas, P(1), P(3), dimY);

            Point ctr = new Point((P(1).X + P(2).X + P(3).X + P(4).X) / 4d, (P(1).Y + P(2).Y + P(3).Y + P(4).Y) / 4d);

            // Vise: fixed jaw at the probed corner (BL = local (0, eh)); moving jaw drawn adjacent to the
            // stock's front edge - where it actually clamps THIS stock, not the theoretical max-open position.
            // Both bars start EXACTLY at local X=0 (no overhang), so the fixed jaw's own corner lands precisely
            // on the origin dot below instead of being inset from it. A separate dashed line marks where the
            // moving jaw would sit fully open (MaxOpening) - red if the entered stock height won't fit inside it.
            if (isVise)
            {
                const double jawBar = 14d;   // fixed visual depth (px) - not a tracked dimension
                var jawFill = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB4));
                var jawStroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x60, 0x70));
                double eh = my[3];   // BL.y - the fixed jaw's clamping face, in local mm

                Point fixedFace0 = P2(0d, eh), fixedFace1 = P2(jawWidthMm, eh);
                var fixedJaw = new System.Windows.Shapes.Rectangle { Width = Math.Abs(fixedFace1.X - fixedFace0.X), Height = jawBar, Fill = jawFill, Stroke = jawStroke, StrokeThickness = 1.5 };
                Canvas.SetLeft(fixedJaw, Math.Min(fixedFace0.X, fixedFace1.X));
                Canvas.SetTop(fixedJaw, fixedFace0.Y - jawBar);
                canvas.Children.Add(fixedJaw);

                Point movFace0 = P2(0d, 0d), movFace1 = P2(jawWidthMm, 0d);
                var movingJaw = new System.Windows.Shapes.Rectangle { Width = Math.Abs(movFace1.X - movFace0.X), Height = jawBar, Fill = jawFill, Stroke = jawStroke, StrokeThickness = 1.5 };
                Canvas.SetLeft(movingJaw, Math.Min(movFace0.X, movFace1.X));
                Canvas.SetTop(movingJaw, movFace0.Y);
                canvas.Children.Add(movingJaw);

                if (SelectedFixture.MaxOpening > 0d)
                {
                    bool tooTall = eh > openingMm;
                    var openBrush = tooTall ? Brushes.Red : Brushes.DimGray;
                    Point openL = P2(0d, eh - openingMm), openR = P2(jawWidthMm, eh - openingMm);
                    var openLine = new System.Windows.Shapes.Line
                    {
                        X1 = openL.X, Y1 = openL.Y, X2 = openR.X, Y2 = openR.Y,
                        Stroke = openBrush,
                        StrokeThickness = tooTall ? 2d : 1.5d,
                        StrokeDashArray = new DoubleCollection(new[] { 4d, 3d })
                    };
                    canvas.Children.Add(openLine);

                    var openLbl = new TextBlock
                    {
                        Text = string.Format(CultureInfo.InvariantCulture, "max opening {0:0.#} mm", openingMm),
                        FontSize = 11d, Foreground = openBrush, Background = Brushes.White, Padding = new Thickness(2d, 0d, 2d, 0d)
                    };
                    openLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(openLbl, Math.Min(openL.X, openR.X));
                    Canvas.SetTop(openLbl, openL.Y - openLbl.DesiredSize.Height - 2d);
                    canvas.Children.Add(openLbl);
                }
            }

            // origin corner marker: red dot only. Every edge-probing kind (Corner Fence etc.) always
            // references front-left; a vise's origin is the jaw's own front-left corner instead - which in
            // this drawing's overall stock-relative frame is back-left (see isVise above).
            int oc = isVise ? CornerId(Corner.BackLeft) : CornerId(SelectedCorner);
            Point op = P(oc);
            var dot = new System.Windows.Shapes.Ellipse { Width = 11d, Height = 11d, Fill = Brushes.OrangeRed };
            Canvas.SetLeft(dot, op.X - 5.5);
            Canvas.SetTop(dot, op.Y - 5.5);
            canvas.Children.Add(dot);

            // Per-corner labels (measuring run only): interior angle (red if off-square) INSIDE the box -
            // it's a property of the drawn shape itself - and stock thickness/Z (a reading ABOUT the
            // material at that point, not the shape) OUTSIDE the corner, as two separate labels.
            if (measured)
            {
                const double labelOffset = 30d;   // px from the corner, in/out along the corner-to-centre direction
                for (int c = 1; c <= 4; c++)
                {
                    Point pt = P(c);
                    double ang = AngleAt(c);
                    var dir = new Vector(pt.X - ctr.X, pt.Y - ctr.Y);
                    if (dir.Length > 1e-6) dir.Normalize();

                    var angLbl = new TextBlock
                    {
                        Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}Â°", ang),
                        FontSize = 22d,
                        TextAlignment = TextAlignment.Center,
                        Background = Brushes.White,
                        Padding = new Thickness(2d, 0d, 2d, 0d),
                        Foreground = Math.Abs(ang - 90.0) > 0.5 ? Brushes.Firebrick : Brushes.Black
                    };
                    angLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(angLbl, pt.X - dir.X * labelOffset - angLbl.DesiredSize.Width / 2d);
                    Canvas.SetTop(angLbl, pt.Y - dir.Y * labelOffset - angLbl.DesiredSize.Height / 2d);
                    canvas.Children.Add(angLbl);

                    string zText = null;
                    if (spoilZ.HasValue && cornerZ[c].HasValue)
                        zText = "t=" + FormatLen(cornerZ[c].Value - spoilZ.Value);
                    else if (cornerZ[c].HasValue)
                        // Touch plate: no spoilboard reference (continuity probing can't detect a
                        // non-conductive spoilboard), so there's no real thickness to compute - show each
                        // corner's own absolute measured top Z instead. Still a genuine reading (useful for
                        // spotting unevenness across corners), just not labelled as "thickness".
                        zText = "z=" + FormatLen(cornerZ[c].Value);

                    if (zText != null)
                    {
                        var zLbl = new TextBlock
                        {
                            Text = zText,
                            FontSize = 22d,
                            TextAlignment = TextAlignment.Center,
                            Background = Brushes.White,
                            Padding = new Thickness(2d, 0d, 2d, 0d),
                            Foreground = Brushes.Black
                        };
                        zLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Canvas.SetLeft(zLbl, pt.X + dir.X * labelOffset - zLbl.DesiredSize.Width / 2d);
                        Canvas.SetTop(zLbl, pt.Y + dir.Y * labelOffset - zLbl.DesiredSize.Height / 2d);
                        canvas.Children.Add(zLbl);
                    }
                }
            }
            else if (isVise)
            {
                // The vise's partial Measure never has all 4 corners' XY (1/3 are Z-only - the jaw blocks
                // face-probing there, see BuildViseProgram), so `measured` above is never true here even
                // though up to 4 Z readings may be in cornerZ[]. There's also no spoilboard reference to
                // subtract (pcorner.macro's z_spoil is only printed by its DISCOVER pass, and every vise
                // corner call runs in REUSE mode - see EmitPcornerCall's startz="0" - so spoilZ is always
                // null for a vise run). Show each probed corner - and the centre-footprint probe - relative
                // to the fixture's own jaw origin instead: SelectedFixture.Coords is stored corner_z + 8mm
                // (FixtureKinds.VisePositionMarginMm, see RunViseCornerProbe/BuildViseProgram's jawTopZ), so
                // back that out to the true bare-jaw-top Z first, matching the descent math Start Job used.
                double? jawTopZ = null;
                if (SelectedFixture != null && SelectedFixture.HasPosition)
                    jawTopZ = new Position(SelectedFixture.Coords).Z - FixtureKinds.VisePositionMarginMm;

                if (jawTopZ.HasValue)
                {
                    for (int c = 1; c <= 4; c++)
                    {
                        if (!cornerZ[c].HasValue)
                            continue;

                        Point pt = P(c);
                        string text = string.Format(CultureInfo.InvariantCulture, "z+{0:0.0}", cornerZ[c].Value - jawTopZ.Value);
                        var lbl = new TextBlock
                        {
                            Text = text,
                            FontSize = 11d,
                            TextAlignment = TextAlignment.Center,
                            Background = Brushes.White,
                            Padding = new Thickness(2d, 0d, 2d, 0d),
                            Foreground = Brushes.Black
                        };
                        lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var dir = new Vector(pt.X - ctr.X, pt.Y - ctr.Y);
                        if (dir.Length > 1e-6) dir.Normalize();
                        Canvas.SetLeft(lbl, pt.X + dir.X * 22d - lbl.DesiredSize.Width / 2d);
                        Canvas.SetTop(lbl, pt.Y + dir.Y * 22d - lbl.DesiredSize.Height / 2d);
                        canvas.Children.Add(lbl);
                    }

                    if (viseCenterZ.HasValue)
                    {
                        string ctext = string.Format(CultureInfo.InvariantCulture, "centre z+{0:0.0}", viseCenterZ.Value - jawTopZ.Value);
                        var clbl = new TextBlock
                        {
                            Text = ctext,
                            FontSize = 11d,
                            TextAlignment = TextAlignment.Center,
                            Background = Brushes.White,
                            Padding = new Thickness(2d, 0d, 2d, 0d),
                            Foreground = Brushes.DimGray
                        };
                        clbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Canvas.SetLeft(clbl, ctr.X - clbl.DesiredSize.Width / 2d);
                        Canvas.SetTop(clbl, ctr.Y - clbl.DesiredSize.Height / 2d);
                        canvas.Children.Add(clbl);
                    }
                }
            }

            return canvas;
        }

        // A dimension label (mm in, display units out) centred on the edge a->b, on a white pad so it reads
        // over the outline.
        private void AddDimLabel(Canvas canvas, Point a, Point b, double mm)
        {
            var lbl = new TextBlock
            {
                Text = FromMm(mm).ToString("0.#", CultureInfo.InvariantCulture) + (isImperial ? " in" : " mm"),
                FontSize = 24d,
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

        // All internal state (measuredX/Y, corner*, spoilZ) is mm - format through this so the readout follows
        // whichever unit is currently selected (the Stock panel's right-click toggle), same as the input fields.
        private string FormatLen(double mm)
        {
            return FromMm(mm).ToString("0.###", CultureInfo.InvariantCulture) + (isImperial ? " in" : " mm");
        }

        // Live readout: size, plus flatness and squareness once enough corners are probed.
        private string BuildResultText(int probed)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Measured stock:  X = {0}   Y = {1}",
                measuredX.HasValue ? FormatLen(measuredX.Value) : "-",
                measuredY.HasValue ? FormatLen(measuredY.Value) : "-");

            double? flat = Flatness();
            if (flat.HasValue)
                sb.AppendFormat("\nFlatness (Z range): {0}", FormatLen(flat.Value));

            double? skew = SkewDegrees(), diag = DiagonalDelta();
            if (skew.HasValue && diag.HasValue)
                sb.AppendFormat("\nSquareness: skew {0}Â°   (diagonal Î” {1})",
                    skew.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    FormatLen(diag.Value));

            // Vise-only: SkewDegrees()/DiagonalDelta() need a full 4-corner XY quad, which the vise never has
            // (corners 1/3 are Y-face-blocked - see BuildViseProgram). This is its own left-edge-vs-jaw-Y-face
            // check, from corners 1/3's probed X alone (see rxViseSkew/LS_VISE_SKEW).
            if (viseLeftEdgeSkewDeg.HasValue)
                sb.AppendFormat("\nVise left-edge skew: {0}Â°", viseLeftEdgeSkewDeg.Value.ToString("0.###", CultureInfo.InvariantCulture));

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
            var p = ActiveProbe();
            if (p == null)
            {
                AppDialogs.Show(CNC.Controls.LibStrings.FindResource("HmSelectProbe"),
                    "Start Job", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            bool touchPlate = IsTouchPlate;
            bool stockConductive = chkStockConductive.IsChecked == true;

            var fx = SelectedFixture;
            if (fx == null || !fx.Implemented || !fx.PositionValidated)
            {
                AppDialogs.Show("Select a fixture with a supported type and a validated position first (Machine Setup > Fixture definitions > Test position).",
                    "Start Job", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            // G28 has no per-fixture saved position to validate (see G28Fixture's own comment) - instead check
            // the controller's ACTUAL G28 directly, and if it was never set, offer to set it right here rather
            // than a generic PREREQ failure: the operator still needs to jog somewhere first regardless, so
            // asking them to do that AND set G28 in one step is more useful than a bare "G28 is not set" refusal
            // that leaves them to go do it some other way. Cancel aborts Generate entirely, same as everywhere
            // else a prerequisite isn't met.
            if (IsG28(fx))
            {
                GrblWorkParameters.Get(model);   // fresh $# read - a stale cached value could misreport "set"
                if (!MacroProcessor.CoordinateSystemDefined("G28"))
                {
                    if (AppDialogs.Show("G28 is not set. Jog to the position you want to probe the spoilboard Z from - clear of the stock in X/Y, within ~10mm above the spoilboard in Z - then click OK to set G28 there. Cancel aborts.",
                            "Start Job", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                        return;
                    if (!MacroProcessor.Run(model, "Set G28", "G28.1\nM2", false))
                        return;
                }
            }

            // Corner 1's probe now points straight at Fixture.CornerOffsetX/Y and reuses Fixture.SpoilboardZ
            // instead of locating the corner + spoilboard fresh (see BuildProgram) - a fixture saved/tested
            // before those features shipped (or one whose Coords was re-set since, which zeros both - see
            // Fixture.Coords) has 0s here, which would aim the tight probe at a point right next to the jogged
            // reference and/or seed a bogus safety floor. Neither is ever legitimately exactly 0 (Coords is
            // always jogged clear of the corner and above the spoilboard), so this is a safe "never actually
            // tested under this scheme" check.
            // OR, not AND: SpoilboardZ was added after CornerOffsetX/Y (same Test run captures all three
            // together now), so a fixture tested between those two changes could have X/Y populated but
            // SpoilboardZ still 0 - any ONE of the three being unset makes #<_bottom> below untrustworthy.
            if (!IsG28(fx) && FixtureKinds.ProbesEdges(fx.Kind) && fx.Implemented
                && (fx.CornerOffsetX == 0d || fx.CornerOffsetY == 0d || fx.SpoilboardZ == 0d))
            {
                AppDialogs.Show("This fixture's corner position hasn't been located yet - run Test position again in Machine Setup > Fixture definitions.",
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
            double widthMm = fldWidth.Value, heightMm = fldHeight.Value, thicknessMm = fldThickness.Value;
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

            if (fx.Kind == FixtureKind.MachinistVise)
                CheckViseKeepOut();

            // Gate the capability-dependent options on what the controller actually supports, regardless of the
            // checkbox state - a hidden-but-checked box must never emit a G10 L2 R (errors:20 without WCSROT) or
            // an M6 T8 puck reference (no toolsetter without ATC). A kind that doesn't probe edges has nothing
            // to measure via the full-4-corner path (see the Machinist Vise design note in BuildProgram); the
            // vise gets its OWN partial measure instead (BuildViseProgram's measure block - corners 2/4 only).
            bool measure = FixtureKinds.ProbesEdges(fx.Kind) && chkMeasure.IsChecked == true;
            bool measureVise = fx.Kind == FixtureKind.MachinistVise && chkMeasure.IsChecked == true;
            bool applyRotation = measure && chkRotate.IsChecked == true && GrblInfo.RotationSupported;
            bool setTloRef = chkSetTloRef.IsChecked == true && GrblInfo.HasATC;
            bool exactSize = measure && chkExactSize.IsChecked == true;
            bool setOrigin = chkSetOrigin.IsChecked == true;

            // Safe Z delta only matters once corners 2-4 are actually crossed (measure) - it's the height ABOVE
            // corner 1's own measured top that corner-to-corner travel and each corner's pre-top-probe descent
            // trusts as clear, instead of retracting fully to machine top every time (see pcorner.macro's
            // #<_ls_maxz>). Too small and that crossing height can clip the stock/fixture on the way to a
            // corner whose own top sits higher than corner 1's (flatness/spacer variance) - confirmed on real
            // hardware: 5mm was not enough and broke a probe tip. 10mm is the field's own default; warn (not
            // block) below that so an operator who knows their setup can still go tighter deliberately.
            const double minSafeZDeltaMm = 10d;
            double safeZDeltaMm = fldCornerMargin.Value;
            if (measure && safeZDeltaMm < minSafeZDeltaMm)
            {
                if (AppDialogs.Show(string.Format("Safe Z delta is {0} mm - less than the recommended {1} mm minimum. Too little clearance here can clip the stock/fixture crossing between corners. Generate anyway?",
                        N(safeZDeltaMm), N(minSafeZDeltaMm)),
                        "Start Job", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            program = FixtureKinds.ProbesEdges(fx.Kind)
                ? BuildProgram(p, fx, SelectedCorner, widthMm, heightMm,
                               cbxWcs.SelectedIndex + 1, measure, applyRotation, setTloRef, fldSpacer.Value, thicknessMm, touchPlate, stockConductive, fldCornerMargin.Value, exactSize, setOrigin)
                : BuildViseProgram(p, fx, widthMm, heightMm, thicknessMm, cbxWcs.SelectedIndex + 1, setTloRef, touchPlate, stockConductive, measureVise, ActiveOrFallbackProbeDiameter(p), setOrigin);
            ResetResults();
            SaveInputs();

            // Re-arm as the active program: a previous run tears this down (handing the source back to the job),
            // so Generate must re-establish it so Cycle Start runs Start Job again without leaving the tab.
            MacroProcessor.ActiveProgramName = "Start Job";
            MacroProcessor.ActiveRun = () => Run_Click(null, null);
            // Start Job owns its ProgramView; the overlay hosts it and it titles itself
            MacroProcessor.PublishGenerated("Start Job " + fx.Name, program, EnsureProgramView, () => programView);
            // Flips the Run bar from "Generate" to "Run" (see isActiveTab's own comment on why this is gated).
            if (isActiveTab)
                MacroProcessor.IsProgramGenerated = true;
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
                NumericField.SetIsImperial(grpStock, isImperial);
                fldWidth.Value = s.Width;
                fldHeight.Value = s.Height;
                fldThickness.Value = s.Thickness;
                fldSpacer.Value = s.SpacerThickness;
                fldCornerMargin.Value = s.CornerTravelMarginMm;
                cbxWcs.SelectedIndex = Math.Max(0, Math.Min(5, s.Wcs - 1));
                chkSetOrigin.IsChecked = s.SetOrigin;
                chkMeasure.IsChecked = s.Measure;
                chkRotate.IsChecked = s.ApplyRotation;
                chkExactSize.IsChecked = s.ExactSize;
                chkSetTloRef.IsChecked = s.SetTloRef;
                chkStockConductive.IsChecked = s.StockConductive;
                IsTouchPlate = s.Probe == "TouchPlate";
                UpdateProbeWarning();   // may fall back to 3D Probe if the touch-plate definition no longer exists
                // Corner is always front-left now; the probe comes from the selected probe definition - both dropped.
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
                    Width = fldWidth.Value,
                    Height = fldHeight.Value,
                    Thickness = fldThickness.Value,
                    SpacerThickness = fldSpacer.Value,
                    CornerTravelMarginMm = fldCornerMargin.Value,
                    IsImperial = isImperial,
                    Corner = SelectedCorner.ToString(),
                    Wcs = cbxWcs.SelectedIndex + 1,
                    SetOrigin = chkSetOrigin.IsChecked == true,
                    Measure = chkMeasure.IsChecked == true,
                    ApplyRotation = chkRotate.IsChecked == true,
                    ExactSize = chkExactSize.IsChecked == true,
                    SetTloRef = chkSetTloRef.IsChecked == true,
                    StockConductive = chkStockConductive.IsChecked == true,
                    Probe = IsTouchPlate ? "TouchPlate" : "ThreeDProbe",
                    Fixture = SelectedFixture?.Name ?? string.Empty
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
            MacroProcessor.Run(model, "Start Job " + (SelectedFixture?.Name ?? string.Empty), program, true);
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
        // edge-probing here. That gets its own generator, BuildViseProgram (below), not this one -
        // Generate_Click branches on ProbesEdges to pick between them. BuildViseProgram has its OWN partial
        // Measure (2 of 4 corners, no skew) via the same pcorner.macro this function calls.
        private static string BuildProgram(ProbeDefinition p, Fixture fx, Corner corner, double estW, double estH, int wcsP, bool measure, bool applyRotation, bool setTloRef, double spacer, double thickness, bool touchPlate, bool stockConductive, double cornerTravelMarginMm, bool exactSize, bool setOrigin)
        {
            double r = p.ProbeDiameter / 2d;                    // tip radius (3D probe) / bit radius (touch plate) -> edge comp
            // Touch plate against non-conductive stock needs a real physical plate, whose known PlateThickness
            // is subtracted from the probed Z (work Z0 = probed top - thickness). Conductive stock is touched
            // directly (no plate, no offset) - see pcorner.macro's _ls_plateoffset.
            double plateOffset = touchPlate && !stockConductive ? p.PlateThickness : 0d;
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
            // back as (PRINT ...) messages.
            void EmitCall(int cornerId, string refx, string refy, string startz, string maxz = "0", string appz = "9999") { EmitPcornerCall(L, cornerId, refx, refy, startz, maxz, appz); }

            L(string.Format("(Start Job - probe corners via pcorner.macro, set origin{0})", measure ? " + measure size" : ""));
            // Split across short lines - grblHAL rejects a line over its receive-buffer size ("Max characters
            // per line exceeded") outright, and one long sentence with real names substituted in can exceed it.
            L(string.Format("(Probe \"{0}\": tip {1}mm body {2}mm.)", p.Name, N(p.ProbeDiameter), N(p.BodyDiameter)));
            L(string.Format("(Fixture \"{0}\" ({1}): saved position must clear both faces by ~D and be)", fx.Name, cornerName));
            L("(within 10mm above the spoilboard in Z - see Test position in Fixture definitions.)");
            L("(Estimated size MUST be conservative - a few mm larger than actual - so far refs land just outside.)");
            L("(Requires grblHAL NGC expressions + pcorner.macro on the controller. VALIDATE before trusting.)");
            // G28 (the loose-probe synthetic fixture) reads its corner-1 reference live from the controller's
            // own G28 stored position at run-time. NOT required here via a plain PREREQ condition - Generate_Click
            // handles an unset G28 explicitly (jog-and-confirm dialog, sets it itself), so by the time this
            // program ever streams, G28 is guaranteed set.
            L("(PREREQ, connected, homed, EXPR, ATC=1, G30, G59.3)");
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
            // Estimated stock thickness - the face probe searches at the material's MIDPOINT (top -
            // thickness/2), safely inside the material for any thickness, rather than a fixed offset below top.
            L(string.Format("#<_ls_thickness> = {0}", N(thickness)));
            // 0 = 3D probe (spoilboard-discovery DISCOVER/REUSE, unchanged). 1 = touch plate: continuity never
            // triggers on non-conductive spoilboard, so pcorner skips the spoilboard probe and anchors its
            // safety floor to the DISCOVER corner's own measured stock-top instead.
            L(string.Format("#<_ls_mode> = {0}", touchPlate ? 1 : 0));
            L(string.Format("#<_ls_plateoffset> = {0}", N(plateOffset)));
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
            // _ls_spoilz (used only by pcorner.macro's DISCOVER-mode spoilboard probe) is no longer emitted -
            // corner 1 now runs REUSE mode too (Fixture.SpoilboardZ seeds #<_bottom> directly, below), so no
            // call in this whole program ever takes the DISCOVER branch anymore.
            L(string.Format("#<_ls_searchf> = {0}", N(SearchFeed(p))));   // fast search feed (from the 3D probe definition)
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

            // Corner 1 = the selected origin corner.
            L(string.Format("(--- corner 1 = {0} (origin): reference {1} ---)", cornerName, fx.Name));
            if (IsG28(fx))
            {
                // G28 (loose-probe synthetic fixture, see its own comment): DISCOVER mode (9999), anchored at
                // the controller's own G28 stored position - read live via #5161/#5162/#5163 (grblHAL's G28
                // named parameters), never known to ioSender at generate-time, unlike a real Fixture's cached
                // Coords. pcorner.macro probes the spoilboard AND the stock top itself here (both unknown), so
                // topx/topy use the same LOOSE topClearance corners 2-4 already fall back to - there is no tight
                // per-fixture offset to point at. This recreates the exact behavior Start Job had before
                // Fixture.CornerOffsetX/Y/SpoilboardZ existed (see pcorner.macro's own "old firmware G28 slot"
                // reference).
                L("#<_ls_spoilz> = #5163");
                L(string.Format("#<_ls_topx> = {0}", N(topClearance)));
                L(string.Format("#<_ls_topy> = {0}", N(topClearance)));
                EmitCall(id1, "#5161", "#5162", "9999");
            }
            else
            {
                // REUSE mode (NOT DISCOVER/9999) - Fixture.SpoilboardZ (FixtureEditDialog's "Test position", a
                // one-time probe against the physical fence/spoilboard) seeds #<_bottom> directly, so this call
                // skips pcorner.macro's own spoilboard probe entirely instead of re-running it every job (same
                // "trust the once-tested fixture reference" model CornerOffsetX/Y already uses for the corner
                // XY - see the "double probe of corner 1" backlog item). topx/topy point straight at
                // CornerOffsetX/Y's tight ~5mm-inset anchor, same as before. CornerOffsetX/Y encode the true
                // corner's position under sx=sy=+1 (SelectedCorner is always FrontLeft today - see its getter)
                // plus the same 5mm interior inset the old exact-size re-probe used (topx = offset + inset
                // lands 5mm inside the true corner, same derivation as corner 2's own hand-specified anchor
                // below).
                const double cornerInsetMm = 5d;
                L(string.Format("#<_bottom> = [{0} + {1}]", N(fx.SpoilboardZ), N(spacer)));
                L(string.Format("#<_ls_topx> = {0}", N(fx.CornerOffsetX + cornerInsetMm)));
                L(string.Format("#<_ls_topy> = {0}", N(fx.CornerOffsetY + cornerInsetMm)));
                EmitCall(id1, refX, refY, "0");
            }
            L(string.Format("#<_ls_topx> = {0}", N(topClearance)));   // restore for corners 2-4's default (non-exact) path below
            L(string.Format("#<_ls_topy> = {0}", N(topClearance)));
            L("#<c1x> = #<_corner_x>");
            L("#<c1y> = #<_corner_y>");
            L("#<c1z> = #<_corner_z>");

            // Known-safe travel height for corners 2-4 (see pcorner.macro's #<_ls_maxz>) - corner 1's own
            // measured stock top plus the operator-set travel margin, NOT hardcoded (adjustable in case a
            // given fence/clamp setup needs more clearance than the default). Computed as its own variable,
            // not inlined into the EmitCall args, so Br()'s "wrap in brackets if it contains a space" heuristic
            // doesn't double-bracket an already-bracketed expression - a bare variable reference has no spaces.
            L(string.Format("#<c1_maxz> = [#<c1z> + {0}]", N(cornerTravelMarginMm)));

            // Tool-length reference (opt-in): with measure UNCHECKED this makes Load Stock == a plain Start Job
            // (origin + TLO ref). The 3D probe is already in the spindle (installed at the top), so this is the
            // M6 T8 "reference" path in tc.macro: reset the ref, probe the puck at G59.3, store the probe machine-Z
            // as #<_tlo_ref>, park at G30. Emitted right after corner 1 while WCO is still 0, so the remaining
            // corners (probed in work coords) and the end-of-run origin block are unaffected. Needs ATC + a
            // toolsetter at G59.3 (both already in the PREREQ). tc.macro is what applies the ref on later M6s.
            if (setTloRef)
            {
                EmitTloReference(L, p, touchPlate);
                // tc.macro's M6 T8 parks at G30 - an arbitrary, possibly-distant point not verified clear of
                // any fence/clamp hardware, unlike corner-to-corner travel (a known area, already crossed
                // once). Explicit full retract for JUST this leg, as its own discrete step - not baked into
                // maxz (that used to zero out maxz for corner 2's ENTIRE call, which also meant its internal
                // face-probe repositioning fell back to a freshly-computed height instead of the same trusted
                // one corners 3/4 use, for no reason - see the conversation this came from). Once retracted,
                // corner 2 gets the same trusted #<c1_maxz> as everything else below.
                L("G53 G0 Z0");
            }

            if (measure)
            {
                // The other three reuse start_z; references come from the probed origin + estimate on the spanning
                // axis and the fixture instance's saved position on the shared axis. corner 2 = X-neighbour,
                // 3 = Y-neighbour, 4 = diagonal.
                //
                // maxz = #<c1_maxz> (corner 1's own measured Zstock + the travel margin, computed above) -
                // replaces BOTH the full retract-to-machine-Z0 between corners AND the generic floor+30
                // pre-top-probe estimate with this tighter, ACTUALLY-MEASURED height, same mechanism the vise
                // path already uses for its own centre probe. Confirmed with the operator this clears the
                // fence/clamp hardware between corners on this setup before wiring it in. Used uniformly for
                // every corner including corner 2 - the G30-detour's OWN full retract is now its own explicit
                // step above (see setTloRef), not folded into a corner-2-specific maxz override.
                const string maxz = "#<c1_maxz>";

                // Corner 2's exact-mode waypoints (all three - top-probe, X-face-seek start, Y-face-seek start -
                // hand-specified, no nudges: symmetric +-5mm inset for the top-probe / +-10mm outset for each
                // face-seek's own crossing axis, minimal skew so no separate secondary-axis correction needed).
                // X and Y are NOT symmetric in which DIRECTION is "inset toward interior", despite the equal
                // magnitudes: corner 2 sits at the FAR X edge (trueX = c1x + sox*width), so inset-toward-
                // interior in X means SUBTRACTING sox*5 (back toward the origin); it sits at the SAME Y edge as
                // corner 1 (shares c1y), so inset-toward-interior in Y means ADDING soy*5 (soy is documented as
                // the stock interior direction FROM the origin - corner 2 doesn't have its own "far Y edge" to
                // subtract from the way it does in X). Confirmed on real hardware: using -soy*5 for Y sent the
                // top-probe to Corner1Y-5, which alarmed "Probe fail" - Alarm:5, dead air, not the intended
                // inset-into-material point.
                //   top-probe    = (trueX - sox*5,  trueY + soy*5)
                //   X-face start = (trueX + sox*10, trueY + soy*5)    <- same Y as top-probe (pcorner's own _topy)
                //   Y-face start = (trueX - sox*5,  trueY - soy*10)   <- same X as top-probe (pcorner's own _topx)
                // pcorner.macro ties all three to one ref/topx/topy triple (_topx = rx + topx*sx; X-face start =
                // (rx, _topy); Y-face start = (_topx, ry)) - solving for that triple against the three targets
                // above: ref = trueCorner + (sox*10, -soy*10), topx = 15, topy = 15 (both positive this time -
                // the earlier -15 came from wrongly mirroring X's sign convention onto Y, see above).
                double outset = estW + 10d;
                string refX2 = plusMinus("#<c1x>", sox, outset);
                string refY2 = plusMinus("#<c1y>", -soy, 10d);   // c1y - soy*10

                if (exactSize)
                {
                    L("(--- exact-size: corner 2's hand-specified top-probe/face-seek geometry ---)");
                    L(string.Format("#<_ls_topx> = {0}", N(15d)));
                    L(string.Format("#<_ls_topy> = {0}", N(15d)));
                }
                else
                {
                    refX2 = plusMinus("#<c1x>", sox, estW);
                    refY2 = refY;
                }

                L(string.Format("(--- corner 2 = {0} (X-neighbour) ---)", Name(xn)));
                // setTloRef: the TLO-ref detour just parked at G30 (an arbitrary, possibly-distant point
                // not verified clear - see the comment above) and retracted to Z0. maxz (#<c1_maxz>) is
                // only trusted WITHIN the fixture's footprint, so override just this call's first height
                // change to Z0 (pcorner.macro's #<_ls_appz>) - same structure as every other corner
                // (straight to the inset XY, then drop for the probe), just approaching at Z0 instead.
                EmitCall(CornerId(xn), refX2, refY2, "#<_start_z>", maxz, setTloRef ? "0" : "9999");
                L("#<c2x> = #<_corner_x>");
                L("#<c2y> = #<_corner_y>");

                string c3refx, c3refy, c4refx, c4refy;
                if (exactSize)
                {
                    // Dropped the skew-corrected perpendicular-vector prediction (measured c1->c2 direction) -
                    // skew is minimal on this setup, and that correction was never re-derived for the new hand-
                    // specified 5mm-inset/10mm-outset pattern; combined with corner 2's topx/topy=15 (tuned for
                    // ITS OWN sign relationship to host sox/soy) it put corner 3's probe 20mm off in X on real
                    // hardware. Use corner 1's coordinates directly instead, same +-5mm/+-10mm pattern as corner
                    // 1 and 2, re-derived fresh for each corner's own pcorner sx/sy relationship to host sox/soy
                    // rather than assumed to match (that assumption is what broke corner 2, then corner 3):
                    //   corner 3 (Y-neighbour, BackLeft when origin=FrontLeft): sx=+sox (shares X with origin,
                    //     like corner 1's own axes - "interior" = +sox), sy=-soy (far Y edge, like corner 2's X -
                    //     "interior" = -soy). ref = (c1x - sox*10, c1y + soy*(height+10)).
                    //   corner 4 (diagonal): sx=-sox, sy=-soy (far edge on BOTH axes, like corner 2 on both) -
                    //     ref = (c1x + sox*(width+10), c1y + soy*(height+10)).
                    // topx/topy come out 15/15 for every corner regardless of which axis is negated - it's a
                    // magnitude relationship (outset - (-inset) = 10-(-5) = 15) independent of sign convention -
                    // so the values corner 2 already set stay correct here, nothing to re-emit.
                    c3refx = plusMinus("#<c1x>", -sox, 10d);
                    c3refy = plusMinus("#<c1y>", soy, estH + 10d);
                    c4refx = plusMinus("#<c1x>", sox, estW + 10d);
                    c4refy = plusMinus("#<c1y>", soy, estH + 10d);
                }
                else
                {
                    c3refx = refX; c3refy = plusMinus("#<c1y>", soy, estH);
                    c4refx = plusMinus("#<c1x>", sox, estW); c4refy = plusMinus("#<c1y>", soy, estH);
                }

                L(string.Format("(--- corner 3 = {0} (Y-neighbour) ---)", Name(yn)));
                EmitCall(CornerId(yn), c3refx, c3refy, "#<_start_z>", maxz);
                L("#<c3x> = #<_corner_x>");
                L("#<c3y> = #<_corner_y>");

                L(string.Format("(--- corner 4 = {0} (diagonal) ---)", Name(dg)));
                EmitCall(CornerId(dg), c4refx, c4refy, "#<_start_z>", maxz);
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

            // setOrigin off: probing/measuring above still ran (and still reports/prints its numbers) - just
            // skip committing anything to the WCS. Rotation is gated on setOrigin too - writing a rotation to
            // a WCS this run never touched the origin of would be meaningless.
            if (setOrigin)
            {
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
            }
            L("M2");

            return b.ToString();
        }

        // First-pass vise keep-out check: walks the LOADED JOB's toolpath (not the Start Job NGC above) and
        // warns if any move's swept bounding box crosses into either jaw's footprint. Works entirely in WORK
        // coordinates - BuildViseProgram sets the work origin AT fx.Coords with no rotation ("vise never
        // measures skew"), so in that frame the fixed jaw's clamping face is Y=0 (its bulk is +Y from there,
        // per BuildViseProgram's own comment) and the moving jaw's face - as clamped for THIS stock - is
        // Y=-stockH, bulk further -Y. Both jaws span X in [0, JawWidth]. Assumes the job runs unshifted in
        // that same WCS (no G92/additional offset) - reasonable for a first pass, not guaranteed.
        // Inflates the keep-out boundary by the active tool's radius, read live off Grbl's tool table
        // (GrblWorkParameters.Tools, R field) as the emulator processes T/M6 - a tool with no table entry
        // (R=0, the common case for mill setups that never populate it) is treated as zero-diameter/centerline
        // only, per design: never over-warn on an untabled tool.
        // Also exempts moves that never reach jaw height: work Z=0 is the probed stock TOP. We do NOT know
        // how far below that the jaw top actually sits - spacer+thickness only holds if the spacer rests ON
        // the jaw's probed top surface, but it may instead sit lower, on the vise's base (jaw face height
        // isn't a tracked dimension), in which case stock top can be flush with - or even below - the jaw
        // top. So the safe default is jaw top = stock top (Z=0): only moves that stay entirely ABOVE the
        // material (work Z > 0, e.g. rapid/retract clearance) are exempt from the XY check. Flags and warns;
        // never blocks Generate.
        private void CheckViseKeepOut()
        {
            var fx = SelectedFixture;
            if (fx == null || fx.Kind != FixtureKind.MachinistVise || !fx.PositionValidated || fx.JawWidth <= 0d)
                return;

            var tokens = CNC.Controls.GCode.File?.Tokens;
            if (tokens == null || tokens.Count == 0)
                return;

            double stockH = fldHeight.Value;
            if (stockH <= 0d)
                return;

            double jawX0 = 0d, jawX1 = fx.JawWidth;
            double fixedFaceY = 0d;
            double movingFaceY = -stockH;
            const double jawTopZ = 0d;   // conservative default: jaw top assumed flush with the probed stock top

            int hitCount = 0;
            uint firstHitLine = 0;

            try
            {
                var emu = new GCodeEmulator();
                foreach (var cmd in emu.Execute(tokens))
                {
                    GcodeBoundingBox box = null;
                    if (cmd.Token is GCArc)
                        box = (cmd.Token as GCArc).GetBoundingBox(emu.Plane, new double[] { cmd.Start.X, cmd.Start.Y, cmd.Start.Z }, emu.DistanceMode == DistanceMode.Incremental);
                    else if (cmd.Token is GCCubicSpline)
                        box = (cmd.Token as GCCubicSpline).GetBoundingBox(emu.Plane, new double[] { cmd.Start.X, cmd.Start.Y, cmd.Start.Z }, emu.DistanceMode == DistanceMode.Incremental);
                    else if (cmd.Token is GCQuadraticSpline)
                        box = (cmd.Token as GCQuadraticSpline).GetBoundingBox(emu.Plane, new double[] { cmd.Start.X, cmd.Start.Y, cmd.Start.Z }, emu.DistanceMode == DistanceMode.Incremental);
                    else if (cmd.Token is GCAxisCommand9)
                    {
                        box = new GcodeBoundingBox();
                        box.AddPoint(cmd.Start);
                        box.AddPoint(cmd.End);
                        box.Conclude();
                    }

                    if (box == null || box.Min[2] > jawTopZ)   // whole move stays above the jaw - can't hit it
                        continue;

                    double r = emu.SelectedTool?.R ?? 0d;
                    bool xOverlap = box.Max[0] >= jawX0 - r && box.Min[0] <= jawX1 + r;
                    if (!xOverlap)
                        continue;

                    if (box.Max[1] >= fixedFaceY - r || box.Min[1] <= movingFaceY + r)
                    {
                        hitCount++;
                        if (firstHitLine == 0)
                            firstHitLine = cmd.LineNumber;
                    }
                }
            }
            catch { return; }   // exotic/unparseable program (heavy NGC expressions etc.) - skip rather than fault Generate

            if (hitCount > 0)
            {
                AppDialogs.Show(string.Format(CultureInfo.InvariantCulture,
                    "Heads up: {0} loaded-program move(s) appear to enter the vise jaws' footprint (first at line {1}). " +
                    "This is an XY-only estimate (tool radius from the grblHAL tool table where set, otherwise centerline) " +
                    "- verify clearance before running.", hitCount, firstHitLine),
                    "Start Job", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Machinist vise: the fixture's Coords ARE the precise origin X/Y (probed once via pvisecorner.macro at
        // Set position time, FixtureEditDialog.RunViseCornerProbe) - no per-job edge probing (ProbesEdges ==
        // false), so unlike BuildProgram this never calls pcorner.macro at all. Only Z needs finding here, from
        // the ACTUAL stock. Descent height is a FIXED 10mm above the fixture's saved reference (fxPos.Z) with
        // a generous 30mm search below it - deliberately NOT derived from spacer/typed thickness (see the
        // comment on that line): three hardware attempts trying to compute an exact height from that math all
        // missed, and a live measurement (2026-07-12) showed the stock top can sit either side of the vise
        // origin depending on how tall the clamped stock is relative to the jaw, which spacer/thickness typed
        // for a different purpose don't reliably predict. This is only as good as fxPos.Z itself - a STALE
        // Set position capture is a likely root cause of the earlier misses, so re-run Set position (ideally
        // with Touch Plate, since the jaw is bare conductive metal) before trusting this on a given fixture.
        // Probes straight down at the CENTRE of the entered stock footprint, per the user's confirmed design.
        // Optional partial Measure (corners 2/4 full XY+Z, corners 1/3 Z-only, see the `measure` block below) -
        // the jaw is assumed machine-aligned (square to the axes), so skew is never computed here, only
        // width/height/flatness.
        // UNVERIFIED on hardware - this is a first cut (see the file header note).
        private static string BuildViseProgram(ProbeDefinition p, Fixture fx, double estW, double estH, double thickness, int wcsP, bool setTloRef, bool touchPlate, bool stockConductive, bool measure, double activeProbeDiameterMm, bool setOrigin)
        {
            var fxPos = new Position(fx.Coords);
            // Same touch-plate/conductive-stock rule as BuildProgram - see its comment. The vise's XY origin
            // is already fixed from the fixture's Set position (never re-probed here); only the stock-top Z
            // probe below is affected.
            double plateOffset = touchPlate && !stockConductive ? p.PlateThickness : 0d;
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
            // Each L() call becomes its OWN G-code line - a comment must open and close ')' on the same
            // line (grblHAL has no multi-line comment continuation); splitting one across two calls without
            // closing the first left a dangling '(' that corrupted the parser for every line that followed
            // until a lone blank line happened to reset it (root cause of a long-chased "error:71" streak).
            L("(Z is probed at the CENTRE of the entered stock footprint - keep width/height/thickness honest)");
            L("(not oversized: thickness sets how far above the jaw this descends before it starts probing.)");
            L("(Requires grblHAL NGC expressions. First run for this fixture kind - VALIDATE before trusting.)");

            string prereq = "connected, homed, EXPR, G30";
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
            L("G53 G0 Z0");   // rapid - Z0 is the machine top, always clear
            L(string.Format("G53 G0 X{0} Y{1}", N(centerX), N(centerY)));   // rapid - travelling AT Z0
            // Expected stock-top Z, from the vise's FIXED physical geometry (2026-07-13, user-specified;
            // supersedes the earlier flat "+10mm above the origin" guess, which drove the probe at F1000
            // straight into a thin 1/4" plate - that fudge assumed the stock top was always ~10mm above the
            // jaw origin, which isn't true for stock thinner than the frame's recess). fxPos.Z is NOT the
            // true jaw top - FixtureEditDialog.RunViseCornerProbe stores the resolved corner PLUS
            // FixtureKinds.VisePositionMarginMm (8mm) above the actual probed surface, so that has to be
            // backed out first (missed this on the previous attempt - fxPos.Z used directly landed the
            // whole estimate 8mm high). The vise then has a FIXED 1in (25.4mm) recess below the TRUE jaw
            // top: stock >= 1in thick always bottoms out ON that recess, so its top sits above the jaw top
            // by however much it's over 1in; stock < 1in thick sits on a spacer filling the rest of that
            // same recess, so its top is (approximately) flush WITH the true jaw top regardless of its
            // exact thickness. Same frameRecessMm idiom as the measure block's #<_bottom> below, solving for
            // the opposite end (the top) instead of the floor - that calculation has the same fxPos.Z vs.
            // true-jaw-top gap, harmless there since #<_bottom> is a conservative floor, not a contact point.
            const double frameRecessMm = 25.4d;   // 1 inch
            double jawTopZ = fxPos.Z - FixtureKinds.VisePositionMarginMm;
            double expectedTopZ = thickness < frameRecessMm ? jawTopZ : jawTopZ + (thickness - frameRecessMm);
            // Safe Z = expected top + 5mm (user-specified) - the controlled-feed approach stops there, THEN
            // the G38.2 search takes over for the actual contact.
            L(string.Format("#<_lv_descent> = [{0} + 5]", N(expectedTopZ)));
            L("(PRINT, LS_VISE_APPROACH descent=#<_lv_descent>)");
            L("G53 G1 F1000 Z[#<_lv_descent>]");
            L(string.Format("G38.2 Z[#<_lv_descent> - 15] F{0}", N(SearchFeed(p))));
            L("G91 G1 Z2 F1000");
            L(string.Format("G38.2 Z-5 F{0}", N(p.LatchFeedRate)));
            // Touch plate + non-conductive stock: subtract the plate's known thickness (0 for the 3D probe and
            // for touch plate run directly on conductive stock, so this is a no-op then).
            L(string.Format("#<_stock_z> = [#5063 - {0}]", N(plateOffset)));
            L("G91 G1 Z10 F1000");
            L("G90");
            L("(PRINT, LS_VISE_Z=#<_stock_z>)");
            // #<_stock_z> is now a REAL measured value (not an estimate) - everything after this point can
            // rapid to within a small, trusted margin of it instead of retracting all the way to machine top.
            L("#<_lv_safe_z> = [#<_stock_z> + 5]");

            // Tool-length reference (opt-in), right after the Z probe while WCO is still 0 - same placement/
            // reasoning as BuildProgram's.
            if (setTloRef)
                EmitTloReference(L, p, touchPlate);

            // Partial Measure (opt-in): corners 2 (FR, diagonal) and 4 (BR, X-neighbour) are clear of the jaw
            // on both faces, so they get the full pcorner.macro face+Z probe. Corners 1 (FL) and 3 (BL) sit
            // along the jaw-covered EDGE, but the jaw's own body only blocks their Y face (it sits at the
            // BACK, at/near the origin's Y) - both are clear in X, so EmitViseCornerProbe gets each corner's
            // own Z (inset 5mm into the stock) AND its true left-edge X (backed off 15mm outward to open air,
            // then sought back in). Width/height still come from corner 2+4 against the known jaw origin (X
            // averaged since both sit on the same far edge; Y from corner 2 alone - corner 4 shares the
            // origin's Y and contributes nothing there, see the size_y comment below). Left-edge skew now
            // DOES get computed, from corner 1 vs corner 3's X over the entered (exact) height - see below.
            // Corner 3's own probed X/Z also become the WCS origin itself (see the G10 line at the end of
            // this method) instead of the fixed fxPos.X / centre-probe Z. HARDWARE-VERIFIED end-to-end
            // (2026-07-13/14: full Start Job run, corners 2/4 through park/set-origin/M2, succeeded after the
            // margin tuning above) - the corner 1/3 X-face probe and origin/skew wiring above are UNVERIFIED,
            // first cut (2026-07-15).
            if (measure)
            {
                L("(--- measure (partial): corners 2+4 face-probed via pcorner.macro ---)");
                L(string.Format("#<_ls_rad> = {0}", N(p.ProbeDiameter / 2d)));
                L(string.Format("#<_ls_mode> = {0}", touchPlate ? 1 : 0));
                L(string.Format("#<_ls_plateoffset> = {0}", N(plateOffset)));
                L(string.Format("#<_ls_spacer> = {0}", N(0d)));
                L(string.Format("#<_ls_thickness> = {0}", N(thickness)));   // face probe depth = top - thickness/2 (see pcorner.macro)
                // Entered stock sizes are EXACT for the vise (precision machinist stock, not an estimate to
                // buffer against) - refMarginMm/topClearance below exist purely so the vertical descent
                // points don't catch the physical corner/edge, sized off the ACTUAL tip radius of whatever's
                // in the collet (activeProbeDiameterMm - live from the loaded program's (TOOL T=n D=...)
                // comment when available, else the touch-plate probe definition's fallback field) rather than
                // a flat guess. refMarginMm (rx/ry, pushed OUTSIDE the exact edge) just needs to clear the tip
                // radius so the descent there doesn't overlap the corner; topClearance (topx/topy, pulled
                // INSIDE) must net out past refMarginMm PLUS its own clearance, since it's computed as an
                // offset FROM rx/ry inside pcorner.macro (see #<_ls_topx> there) - not two independent points.
                // +5d (not +2d): hardware run 2026-07-14 landed <3mm actual clearance off the +2d version -
                // the configured touch-plate fallback diameter is 1/4in (6.35mm, radius ~3.2mm), so +5d gives
                // real working room rather than a bare-minimum theoretical clearance.
                double touchTipRadius = activeProbeDiameterMm / 2d;
                double refMarginMm = touchTipRadius + 5d;
                // Touch plate has no probe body, so MinStandoff (body radius) is meaningless here. A real 3D
                // probe keeps its body-standoff-derived formula.
                double topClearance = touchPlate ? refMarginMm + 10d : p.MinStandoff + 9d;
                L(string.Format("#<_ls_topx> = {0}", N(topClearance)));
                L(string.Format("#<_ls_topy> = {0}", N(topClearance)));
                L(string.Format("#<_ls_searchf> = {0}", N(SearchFeed(p))));
                L(string.Format("#<_ls_latchf> = {0}", N(p.LatchFeedRate)));
                L(string.Format("#<_ls_zfloor> = {0}", N(GrblInfo.MaxTravel.Z > 0d ? -(GrblInfo.MaxTravel.Z) + 1.0d : -9999d)));
                // Seed the REUSE fail-fast #<_bottom> from the KNOWN stock bottom, not an arbitrary buffer -
                // this also drives the rapid approach height (_bottom+30) for corner 2/4's own top-probe, so
                // getting it too HIGH isn't just "slower fail-fast", it risks that rapid driving into the
                // stock (2026-07-13: this had the same fxPos.Z-vs-true-jaw-top gap as expectedTopZ above -
                // corner probes were starting too high and timing out for the same reason the centre probe
                // did, now fixed by reusing jawTopZ here too). The vise's frame has a fixed recess exactly
                // 1in (frameRecessMm) below the TRUE jaw top: stock >= 1in thick always bottoms out ON that
                // recess, regardless of how much thicker it is (more thickness just makes it protrude higher)
                // - bottom = jaw_top - 1in. Stock < 1in thick sits on a spacer that fills the REST of that
                // same 1in gap down to the frame, so the stock's OWN bottom (what matters here, not the
                // spacer under it) is simply jaw_top - thickness.
                double bottomDepth = Math.Min(thickness, frameRecessMm);
                L(string.Format("#<_bottom> = [{0} - {1} - 5]", N(jawTopZ), N(bottomDepth)));

                // No separate "approach" rapid needed before handing off to pcorner.macro - it retracts to
                // #<_ls_maxz> (passed below as #<_lv_safe_z>, trusted/verified by the centre probe above)
                // and moves XY to its own top-probe position itself as its very first actions, so a prior
                // move here would just be immediately-superseded, wasted motion. Never travels above Safe Z.

                // pcorner.macro's rx/ry (passed below) are its own reference for "just outside this corner's
                // face - clear air", and its face probes retract-and-rapid straight back out TO that point
                // before descending (no probe-verified clearance check there, just trust). Zero margin
                // (2026-07-13's earlier "trust the entered size" attempt) caught the stock edge on the way
                // down (Alarm:4, probe already triggered before the search even started) - the descent point
                // sat exactly ON the exact edge with no clearance for the tip's own physical footprint.
                // refMarginMm (computed above from the tip radius) fixes that without over- or under-shooting.
                double refXOut = fxPos.X + estW + refMarginMm;

                L("(--- corner 4 = back-right (X-neighbour of the jaw origin) ---)");
                EmitPcornerCall(L, 4, N(refXOut), N(fxPos.Y + refMarginMm), "0", "#<_lv_safe_z>");
                L("#<c4x> = #<_corner_x>");
                L("#<c4y> = #<_corner_y>");

                double refYOut2 = fxPos.Y - estH - refMarginMm;

                L("(--- corner 2 = front-right (diagonal from the jaw origin) ---)");
                EmitPcornerCall(L, 2, N(refXOut), N(refYOut2), "0", "#<_lv_safe_z>");
                L("#<c2x> = #<_corner_x>");
                L("#<c2y> = #<_corner_y>");

                // Corners 1/3 (jaw-covered edge, Y-face only - the jaw's own body sits at the BACK, near the
                // origin's Y, so it never blocks an X-face approach at either corner): straight-down Z at a
                // point inset 5mm into the stock (clear of the unverified edge), then an X-face probe backed
                // off 15mm outward from the true corner to open air, seeking back in - see
                // EmitViseCornerProbe. Corner 3's X doubles as a real measurement of the origin corner itself
                // (used below to set the WCS origin); corner 1's X pairs with it to compute left-edge skew
                // (the Y separation between them is the entered height, trusted exact for a vise - see the
                // "Exact height" field label logic above).
                double inset = 5d;
                L("(--- corner 1 = front-left (Z + X-face probe, inset 5mm for Z / 15mm backoff for X) ---)");
                EmitViseCornerProbe(L, 1, fxPos.X + inset, fxPos.Y - estH + inset, fxPos.X, expectedTopZ, p, plateOffset, thickness, touchTipRadius, "#<c1z>", "#<c1x>");

                L("(--- corner 3 = back-left / jaw origin (Z + X-face probe) ---)");
                EmitViseCornerProbe(L, 3, fxPos.X + inset, fxPos.Y - inset, fxPos.X, expectedTopZ, p, plateOffset, thickness, touchTipRadius, "#<c3z>", "#<c3x>");

                // X: corner 2 and corner 4 BOTH sit at the far X edge (corner 4 is the X-neighbour, corner 2
                // the diagonal) - two independent measurements of the same span, so averaging is correct.
                // Y: only corner 2 (the diagonal) is offset in Y from the origin - corner 4 is the
                // X-neighbour, sharing the origin's OWN Y - so it contributes ~0, not a second measurement.
                // Averaging it in against corner 2's real span silently halved size_y (caught 2026-07-15 on a
                // 12x3in stock reading back 12 x 1.49in - corner 4's near-zero Y delta was dragging the
                // average down). Y must come from corner 2 alone.
                L("(--- size: X from corner 2/4 averaged, Y from corner 2 alone (see comment above) ---)");
                L(string.Format("#<size_x> = [[[#<c2x> - {0}] + [#<c4x> - {0}]] / 2]", N(fxPos.X)));
                L(string.Format("#<size_y> = [{0} - #<c2y>]", N(fxPos.Y)));
                L("(PRINT, LS_X=#<size_x>)");
                L("(PRINT, LS_Y=#<size_y>)");

                // Left-edge skew: corner 1 vs corner 3 X, over the KNOWN (entered, exact) Y separation - the
                // jaw's own Y-face is trusted machine-aligned (never itself re-probed), so this checks whether
                // the LEFT edge is actually parallel to it. ATAN[y]/[x] returns degrees (RS274NGC convention).
                L(string.Format("#<_vise_skew> = ATAN[#<c1x> - #<c3x>]/[{0}]", N(estH)));
                L("(PRINT, LS_VISE_SKEW=#<_vise_skew>)");
            }

            L("(--- park at G30 (before the origin - keeps all G53 moves at WCO=0) ---)");
            EmitGotoG30(L);

            // setOrigin off: the probe/measure above still ran - just skip committing it to the WCS.
            if (setOrigin)
            {
                // Origin: when Measure ran, corner 3's own probed X/Z (the jaw-origin corner itself) replace the
                // fixed fxPos.X and the centre-footprint Z probe - a real measurement of the origin instead of an
                // assumption. Without Measure, fall back to the always-available fxPos.X/_stock_z as before.
                string originX = measure ? "[#<c3x>]" : N(fxPos.X);
                string originZ = measure ? "[#<c3z>]" : "[#<_stock_z>]";
                L("(--- set work origin: X/Y from the probed jaw corner, Z from the stock-top probe ---)");
                L(string.Format("G10 L2 {0} X{1} Y{2} Z{3}", pCode(wcsP), originX, N(fxPos.Y), originZ));
                L(wcs + "  (activate the coordinate system)");
            }
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
        // Bracket only multi-term expressions; a bare param/number is assigned as-is (matches the proven
        // "#<rad>=1" form). grblHAL needs brackets around an expression but not one value.
        private static string Br(string v) { return v.IndexOf(' ') >= 0 ? "[" + v + "]" : v; }

        // Call pcorner.macro for one corner - see pcorner.macro's own header for the globals/outputs contract.
        // Shared by BuildProgram (Corner Fence's own origin corner + REUSE corners) and BuildViseProgram (the
        // two vise-reachable corners, see EmitViseMeasure). maxz defaults to "0" (Corner Fence's existing,
        // more conservative behavior - full retract to machine Z0 between corners, no prior stock-top
        // knowledge); the vise passes its own already-verified safe height instead, so the macro never
        // travels any higher than necessary. Always emitted (never left to a stale value from a prior call).
        // appz: OPTIONAL machine Z override for ONLY this call's very first height change (pcorner.macro's
        // #<_ls_appz>, see its file header) - everything else this call still uses maxz. For a corner reached
        // via a detour that left the fixture's footprint entirely (the TLO-reference park at G30 below), so
        // that one return crossing happens at Z0 instead of the footprint-scoped maxz, without losing the
        // trusted height for the rest of this corner's own probe sequence. "9999" (default) = no override -
        // NOT "0": Z0 (machine top) is itself a legitimate override value, so it can't double as "unset".
        private static void EmitPcornerCall(System.Action<string> L, int cornerId, string refx, string refy, string startz, string maxz = "0", string appz = "9999")
        {
            L(string.Format("#<_ls_corner> = {0}", cornerId));
            L(string.Format("#<_ls_refx> = {0}", Br(refx)));
            L(string.Format("#<_ls_refy> = {0}", Br(refy)));
            L(string.Format("#<_ls_startz> = {0}", Br(startz)));
            L(string.Format("#<_ls_maxz> = {0}", Br(maxz)));
            L(string.Format("#<_ls_appz> = {0}", Br(appz)));
            L("O<pcorner> CALL [#<_ls_rad>]");   // single arg (tip radius) - grblHAL's CALL resolves with one arg
        }

        // Stock-top Z + left-edge X probe for a vise's jaw-covered corners (1=FL, 3=BL/origin). The jaw only
        // blocks Y-face probing there (its own body sits at the BACK, at/near the origin's Y - see
        // BuildViseProgram's header) - both corners are clear of it in X, so unlike the Z-only-first-cut this
        // now also finds the true left edge:
        //   1) Z: straight down at (zProbeX, zProbeY) - caller insets that 5mm off the true corner (into the
        //      stock) so the tip lands on solid material, not right at the unverified edge.
        //   2) X face: back off 15mm OUTWARD (-X) from the TRUE corner (trueX, trueY) to open air, drop to
        //      this corner's own just-probed top minus half the entered thickness (material midpoint - same
        //      "always inside the material" reasoning as pcorner.macro's start_z), then seek back INWARD
        //      (+X) to find the actual face. Radius-compensated with sx=+1, the same convention pcorner.macro
        //      uses for LEFT corners (1,3).
        // outZVar/outXVar are GLOBAL named params (e.g. "#<c1z>"/"#<c1x>") so the caller can read them back for
        // size/skew/origin use, same idiom as EmitPcornerCall's #<c2x> etc.
        private static void EmitViseCornerProbe(System.Action<string> L, int cornerId, double zProbeX, double zProbeY, double trueX, double expectedTopZ, ProbeDefinition p, double plateOffset, double thickness, double tipRadius, string outZVar, string outXVar)
        {
            L("G53 G0 Z[#<_lv_safe_z>]");                                   // rapid - VERIFIED safe height (centre probe)
            L(string.Format("G53 G0 X{0} Y{1}", N(zProbeX), N(zProbeY)));   // rapid - travelling AT that height
            L(string.Format("#<_lv_descent> = [{0} + 5]", N(expectedTopZ)));
            L("G53 G1 F1000 Z[#<_lv_descent>]");
            L(string.Format("G38.2 Z[#<_lv_descent> - 15] F{0}", N(SearchFeed(p))));
            L("G91 G1 Z2 F1000");
            L(string.Format("G38.2 Z-5 F{0}", N(p.LatchFeedRate)));
            L(string.Format("{0} = [#5063 - {1}]", outZVar, N(plateOffset)));
            L("G91 G1 Z10 F1000");
            L("G90");
            L(string.Format("(PRINT, LS_VISE_ZC{0}={1})", cornerId, outZVar));

            // X face probe: same Y as the Z probe above (zProbeY, already inset 5mm off the true corner into
            // the stock) - NOT the true corner Y. At the true corner Y the vise jaw itself is right there
            // (2026-07-15 HW run: probed straight into the jaw), so the whole X-face approach needs to be off
            // that line by the same 5mm the Z probe already uses to clear it.
            L("G53 G0 Z[#<_lv_safe_z>]");
            L(string.Format("G53 G0 X{0} Y{1}", N(trueX - 15d), N(zProbeY)));
            L(string.Format("#<_vsz> = [{0} - {1}]", outZVar, N(thickness / 2d)));
            L(string.Format("G53 G0 Z[{0} + 12]", outZVar));   // rapid down to within 12mm of the KNOWN top (just probed)
            L("G53 G1 F1000 Z[#<_vsz>]");                      // controlled - final descent to the face-probe depth
            L(string.Format("G38.2 X{0} F{1}", N(trueX + 20d), N(SearchFeed(p))));   // coarse seek back toward/past the corner
            L("#<_vxc> = #5061");
            L("G1 X[#<_vxc> - 4] F1000");                      // back off so the deflecting ball releases before the slow re-probe
            L(string.Format("G38.2 X[#<_vxc> + 6] F{0}", N(p.LatchFeedRate)));   // slow re-probe
            L("#<_vxtrig> = #5061");
            L("G1 X[#<_vxtrig> - 10] F1000");
            L("G90");
            L(string.Format("{0} = [#<_vxtrig> + {1}]", outXVar, N(tipRadius)));
            L(string.Format("(PRINT, LS_VISE_CX{0}={1})", cornerId, outXVar));
        }

        private static void EmitGotoG30(System.Action<string> L) => CNC.Controls.MacroProcessor.EmitGotoG30(L);

        // Tool-length reference at the toolsetter puck, right after the origin corner while WCO is still 0.
        // "M6 T8" is tc.macro's own sentinel for this ("probe already in spindle, skip the swap prompt") but
        // it ALSO hardcodes the MAIN probe input (tc.macro:73-75) on the assumption that T8 means a
        // self-triggering 3D mechanical probe stylus is in the spindle - true for Start Job's 3D-probe path,
        // but NOT for Touch Plate: there is no self-triggering probe there, just a bare bit/tool relying on
        // electrical continuity through the STOCK, and the puck was never wired into that circuit. Confirmed
        // on real hardware: M6 T8 in Touch Plate mode drove the tool straight into the puck, fully compressing
        // it without ever triggering - the main input genuinely saw nothing. For Touch Plate, bypass tc.macro's
        // M6 flow entirely and inline the SAME probe-the-puck sequence it uses for a rigid tool (its non-T8
        // branch, tc.macro:76-90), explicitly selecting the TOOLSETTER input instead - using the probe
        // definition's OWN feeds rather than tc.macro's hardcoded F500/F25, matching every other Start Job
        // probe move.
        private static void EmitTloReference(System.Action<string> L, ProbeDefinition p, bool touchPlate)
        {
            L("(--- set tool-length reference at the puck ---)");
            L("#<_tlo_ref> = 0");
            if (touchPlate)
            {
                L("(touch plate - no self-triggering probe in the spindle, use the toolsetter input directly)");
                L("G53 G0 Z-5");
                L("G59.3");
                L("G0 X0 Y0");
                L("G0 Z0");
                L("G65 P5 Q1");   // select the TOOLSETTER input (tc.macro's non-T8/rigid-tool convention)
                L("G91");
                L(string.Format("G38.2 Z-80 F{0}", N(SearchFeed(p))));
                L("G0 Z2");
                L(string.Format("G38.2 Z-5 F{0}", N(p.LatchFeedRate)));
                L("#<_probe_z> = #5063");
                L("G0 Z10");
                L("G90");
                L("G65 P5 Q0");   // restore the main/default probe input
                L("G54");
                L("#<_tlo_ref> = #<_probe_z>");
                L("(PRINT, LS_TLO_REF ref=#<_tlo_ref>)");
                L("G53 G0 Z-5");
                L("G53 G0 X#5181 Y#5182");
                L("G53 G0 Z#5183");
            }
            else
            {
                L("(3D probe already in spindle - M6 T8 selects the main probe input itself, see tc.macro)");
                L("M6 T8");
            }
            L("(WAITIDLE)");
        }

        // P-word for G10 L2 (P1=G54..P6=G59).
        private static string pCode(int wcsP) { return "P" + Math.Min(Math.Max(wcsP, 1), 6).ToString(CultureInfo.InvariantCulture); }

        // Floor the probe definition's search (fast/coarse) feed at 200 mm/min - a slower configured value
        // makes a multi-mm search take long enough to look stalled/hung rather than just slow. Search-only;
        // latch feed is deliberately slow for accuracy and is NOT clamped here.
        private static double SearchFeed(ProbeDefinition p) { return Math.Max(p.ProbeFeedRate, 200d); }

        // "baseExpr + mag" or "baseExpr - mag" depending on dir (keeps generated expressions clean - no "- -5").
        private static string plusMinus(string baseExpr, int dir, double mag) { return baseExpr + (dir >= 0 ? " + " : " - ") + N(mag); }

        private static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
