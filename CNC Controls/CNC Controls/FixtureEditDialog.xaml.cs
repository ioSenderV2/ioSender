/*
 * FixtureEditDialog.xaml.cs - part of CNC Controls library
 *
 * Edits a single Fixture. The Kind dropdown drives which schematic is shown. The caller passes a clone and
 * copies it back on OK so Cancel reverts. "Set position" here does exactly what the fixture list's own Set
 * position button does - captures the CURRENT machine position into this fixture's Coords. It is NOT a
 * firmware G28 write; the position lives only in this fixture's own definition (see Fixtures.CurrentCoordsCsv).
 * For the edge-probing kinds there are no separate offset fields to fill in: the schematic's clearance circle
 * (sized to the current 3D probe's body diameter) is a jog target - position the probe tip inside it, clear of
 * both corner faces, AND within ~10 mm above the spoilboard (the spoilboard probe's search is capped to 12 mm
 * below this point - see pcorner.macro), then click Set position. pcorner.macro derives every probe move from
 * that one point plus the live probe definition, not from anything stored per-fixture.
 *
 */

using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    public partial class FixtureEditDialog : Window
    {
        private readonly GrblViewModel model;
        private bool _probing;   // true while RunViseCornerProbe's async streamed run is in flight

        // Picks up Test position's (PRINT, SPOIL_Z=..) line - same (PRINT, TAG=value) idiom StartJobView.
        // rxResult already parses for LS_X/LS_Y - controller print/debug comments arrive as Message updates.
        private static readonly Regex rxSpoilZ = new Regex(@"SPOIL_Z\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        private double? _capturedSpoilZ;

        public FixtureEditDialog(Fixture fixture, GrblViewModel model)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            DataContext = fixture;
            this.model = model;

            rbFxProbe3d.Checked += (s, e) => UpdateProbeCircleLabel();
            rbFxProbeTouch.Checked += (s, e) => UpdateProbeCircleLabel();

            SelectKind(fixture.Kind);
            UpdateFieldVisibility(fixture.Kind);
            UpdateFxProbeWarning();
            UpdateProbeCircleLabel();
            UpdatePositionDisplay();
            UpdateTestPositionEnabled();
        }

        // "Test position" only makes sense for a kind that probes the spoilboard (see pcorner.macro's DISCOVER
        // phase) and only once a position is actually saved to run the search from. Also disabled while a vise
        // corner probe (RunViseCornerProbe) is in flight - both buttons are, see SetBusy.
        private void UpdateTestPositionEnabled()
        {
            var fx = DataContext as Fixture;
            btnTestPosition.IsEnabled = !_probing && fx != null && fx.HasPosition && FixtureKinds.ProbesSpoilboard(fx.Kind);
        }

        // Vise-only probe picker (jaw metal is always conductive - Touch Plate needs no thickness offset here,
        // unlike Start Job's stock, which may or may not be conductive). Touch Plate is only selectable when a
        // touch-plate probe is actually defined - falls back to 3D Probe if the definition disappears while it
        // was selected, same rule StartJobView.UpdateProbeWarning follows.
        private void UpdateFxProbeWarning()
        {
            bool touchAvailable = ProbeDefinitions.Items.Any(p => p.ProbeType == ProbeType.TouchPlate);
            rbFxProbeTouch.IsEnabled = touchAvailable;
            if (!touchAvailable && rbFxProbeTouch.IsChecked == true)
                rbFxProbe3d.IsChecked = true;
        }

        // The probe definition Set/Test position should actually use, per the Probe: radio selection (vise
        // only - other kinds don't run a probe at Set time and always use the 3D probe for Test position).
        private ProbeDefinition FixtureActiveProbe()
        {
            return rbFxProbeTouch.IsChecked == true
                ? ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.TouchPlate)
                : ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
        }

        // Disable both position buttons AND OK while a vise corner probe is running (it moves the machine
        // asynchronously - see RunViseCornerProbe) so a second click can't overlap it, and so OK/Enter can't
        // close the dialog before the probe finishes. Closing mid-probe would copy the clone's STILL-STALE
        // Coords back to the real fixture (EditSelectedFixture's sel.CopyFrom(edit)) - the probe's own eventual
        // OnViseCornerProbeDone write lands on the now-discarded clone, so the fresh result is silently lost.
        // Confirmed on real hardware: btnOk's IsDefault="True" let Enter (or an impatient click) close the
        // dialog mid-probe, and Start Job then ran off the old stale corner with no visible error.
        private void SetBusy(bool busy)
        {
            _probing = busy;
            btnSetPosition.IsEnabled = !busy;
            btnOk.IsEnabled = !busy;
            UpdateTestPositionEnabled();
        }

        // Corner-schematic scale: px per mm for the drawing's own fixed coordinate space (Grid Width=220,
        // corner anchored at (60,85) - see the XAML comments by pathSmallZone/rectRedV/rectRedH).
        private const double SchematicPxPerMm = 4d / 3d;
        private const double DefaultBodyDiameterMm = 42d;   // used only if no 3D probe is defined yet

        // Redraws the corner schematic's D/2-driven geometry - both the small quarter-disk's radius (cream:
        // stock < 1") AND the red fence rails' "outward" width equal HALF the active 3D probe's body
        // diameter (user-specified rule 2026-07-18: the boundary is a circle centered on the stock origin
        // with radius D/2) - and updates the legend text to match. Called whenever the active probe could
        // have changed (vise Probe: radio, or just on load).
        private void UpdateProbeCircleLabel()
        {
            var probe = FixtureActiveProbe();
            double bodyDiameter = probe?.BodyDiameter > 0 ? probe.BodyDiameter : DefaultBodyDiameterMm;
            double radiusPx = (bodyDiameter / 2d) * SchematicPxPerMm;

            const double cornerX = 60d, cornerY = 85d, railLengthPx = 40d;   // 30 mm, fixed - see XAML comment

            // Green quarter-disk: a true circle centered ON the corner, radius D/2 - both arc endpoints are
            // exactly radiusPx from (cornerX,cornerY) by construction, so that's the arc's real center.
            // sweep-flag MUST be 0 here (large-arc=0, sweep=0) - sweep=1 looks equally plausible from the
            // endpoints/radius alone but silently picks the OTHER of the 2 circles through those points,
            // rendering a much smaller wrong sliver instead of the disk (see the XAML comment - found by
            // rendering all 4 flag combos and sampling pixel colors, not visible at a glance).
            pathSmallZone.Data = Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
                "M{0},{1} L{2},{1} A{3},{3} 0 0 0 {0},{4} Z",
                cornerX, cornerY, cornerX - radiusPx, radiusPx, cornerY + radiusPx));

            // Cream backdrop: kept a bit larger than the disk cut into it, proportionally, so there's
            // always visible cream margin around the green disk regardless of D.
            double zoneWidth = radiusPx * 1.8d, zoneHeight = radiusPx * 1.6d;
            rectLargeZone.Width = zoneWidth;
            rectLargeZone.Height = zoneHeight;
            Canvas.SetLeft(rectLargeZone, cornerX - zoneWidth);
            Canvas.SetTop(rectLargeZone, cornerY);

            rectRedV.Width = radiusPx;
            rectRedV.Height = railLengthPx;
            Canvas.SetLeft(rectRedV, cornerX - radiusPx);
            Canvas.SetTop(rectRedV, cornerY - railLengthPx);

            rectRedH.Width = railLengthPx;
            rectRedH.Height = radiusPx;
            Canvas.SetLeft(rectRedH, cornerX);
            Canvas.SetTop(rectRedH, cornerY);

            string radiusText = string.Format(CultureInfo.InvariantCulture, "{0:0.#} mm", bodyDiameter / 2d);
            txtRedLegend.Text = "Keep clear (fence rails, 30 x " + radiusText + ")";
            txtGreenLegend.Text = "Stock < 1\" (radius " + radiusText + ")";
            txtProbeCircle.Text = probe != null
                ? string.Format(CultureInfo.InvariantCulture, "Stock >= 1\" with 3D probe (~{0:0.#} mm)", probe.BodyDiameter)
                : "Stock >= 1\" with 3D probe";
        }

        private void UpdatePositionDisplay()
        {
            var fx = DataContext as Fixture;
            if (fx == null || !fx.HasPosition)
            {
                txtValidatedCheck.Visibility = Visibility.Collapsed;
            }
            else
            {
                txtValidatedCheck.Visibility = Visibility.Visible;
                txtValidatedCheck.Foreground = fx.PositionValidated
                    ? new SolidColorBrush(Color.FromRgb(0x1B, 0xC4, 0x4B))    // bright/saturated green - was too muted (0x2E7D32) to read against gray at small size
                    : new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xBD));  // light gray - not tested (yet, or since last change)
            }
        }

        private void btnSetPosition_Click(object sender, RoutedEventArgs e)
        {
            var fx = DataContext as Fixture;
            if (fx == null)
                return;

            string coords = Fixtures.CurrentCoordsCsv(model);
            if (coords == null)
            {
                if (model != null)
                    model.Message = "Machine position unknown - home first to save a fixture position.";
                return;
            }

            if (fx.Kind == FixtureKind.MachinistVise)
            {
                // Set position probes the FIXED JAW's own corner (a one-time fixture calibration, independent
                // of any job's stock) - if stock is still clamped in the vise it sits right where the jog
                // reference/probe search expects bare jaw, so the probe finds the stock's own top instead and
                // silently saves a wrong reference (every later Start Job run then measures from the wrong
                // point). Confirmed by the user hitting exactly this on real hardware - the vise must be EMPTY.
                if (AppDialogs.Show("Set position probes the vise's own fixed-jaw corner - not the stock. The vise must be EMPTY (no stock clamped), or the probe may find the stock instead of the jaw and save a wrong reference. Is the vise empty?",
                        "Set position", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                RunViseCornerProbe(fx, coords);
                return;
            }

            fx.Coords = coords;
            // A stale CornerOffsetX/Y/SpoilboardZ is meaningless once the reference it was measured from moves -
            // clear it here (the one place a re-jog genuinely happens), not in the Coords setter itself (see the
            // setter's own comment for why that broke on real hardware).
            fx.CornerOffsetX = 0d;
            fx.CornerOffsetY = 0d;
            fx.SpoilboardZ = 0d;
            UpdatePositionDisplay();
            UpdateTestPositionEnabled();
        }

        // Cancel closes the dialog regardless (IsCancel="True") - the vise corner probe (RunViseCornerProbe)
        // streams asynchronously and isn't owned by this window, so closing mid-probe would otherwise leave it
        // running unsupervised while WatchAsyncCompletion's callback waits to touch a disposed window. Feed
        // Hold only (never Stop/Reset here) - see the streamer-thread wedge notes: Stop/Reset during an
        // in-flight move can leave grblHAL unrecoverable without a controller power-cycle, but Hold is safe.
        // This pauses the motion; it does NOT abort the stream or resume it - the operator does that themselves
        // from the main window's run controls once this dialog is gone.
        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_probing)
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
        }

        // Vise Set position: the jogged position (within the schematic's circle, OVER the jaw, near its
        // front-left corner) is only the RAW REFERENCE - same jog-then-Set idiom as every other kind - but
        // unlike them, Set here actually RUNS pvisecorner.macro against the fixed jaw right now and stores the
        // RESOLVED, probed-precise corner instead of the raw jog: the jaw is bolted down and doesn't move job
        // to job, so nailing it down once here means Start Job (once wired) never needs to re-probe it.
        // pvisecorner.macro is a DEDICATED macro, not pcorner.macro - pcorner needs its reference OUTSIDE both
        // faces (over open spoilboard), the opposite of this dialog's "jog over the jaw" convention; forcing
        // pcorner to work from an inside reference sent the probe the wrong direction on real hardware.
        // pvisecorner.macro contains an O-word CALL, so MacroProcessor.Flush must stream it through the
        // flow-controlled job streamer (RunStreamedJobInPlace) - which is ASYNCHRONOUS (Cycle Start is
        // deferred to a background dispatcher cycle, see MainWindow.RunStreamedJobInPlace) - so Run() returns
        // long before the probe actually happens. The result can't be read back the instant Run() returns;
        // instead watch StreamingState the same way MainWindow.RestoreSourceOnEnd does (arm on Send/SendMDI,
        // fire on the next Idle/NoFile) and read the machine's position back only once the run has genuinely
        // finished - by which point the final G53 move below has physically parked it at the resolved corner.
        private void RunViseCornerProbe(Fixture fx, string joggedCoords)
        {
            if (model == null)
                return;

            var probe = FixtureActiveProbe();
            if (probe == null)
            {
                model.Message = rbFxProbeTouch.IsChecked == true
                    ? "Define a touch plate probe first (Machine Setup > Probe definitions)."
                    : "Define a 3D probe first (Machine Setup > Probe definitions).";
                return;
            }

            var pos = new Position(joggedCoords);
            string x = pos.X.ToInvariantString("0.0##"), y = pos.Y.ToInvariantString("0.0##"), z = pos.Z.ToInvariantString("0.0##");
            // Distance to back off outward from the reference before each face seek - same probe-geometry-derived
            // margin StartJobView.BuildProgram uses for pcorner's topx/topy (clears the probe BODY off the corner).
            double clearance = probe.MinStandoff + 9d;
            // Store Coords a small margin ABOVE the resolved corner, not the literal touched height - a bare
            // rapid straight to the exact probed surface (what Test position, and any future Start Job use,
            // does first) would plunge onto solid jaw metal. Matches the "~10 mm above" convention every other
            // kind's Coords already uses.
            const double zMargin = FixtureKinds.VisePositionMarginMm;

            var b = new StringBuilder();
            b.AppendLine("(Set position - vise: probe the fixed jaw's front-left corner via pvisecorner.macro)");
            // Diagnostic: 2026-07 hardware run saw the macro print rx=0.000 ry=0.000 rz=0.000 instead of the
            // jogged position - pvisecorner.macro only echoes whatever #<_lv_ref*> it's handed below, so that
            // was wrong before the CALL even ran. Print the RAW captured CSV (joggedCoords, pre-Position-parse)
            // so a repeat shows whether CurrentCoordsCsv/the click-time capture was already wrong, or something
            // between here and the #<_lv_ref*> lines below corrupted it.
            b.AppendLine(string.Format("(PRINT, SV joggedCoords={0})", joggedCoords));
            b.AppendLine("(PREREQ, connected, homed, noalarm)");
            b.AppendLine("G21 G90 G94 G17");
            b.AppendLine("G49");
            if (GrblInfo.HasToolSetter)
                b.AppendLine(string.Format(GrblCommand.ProbeSelect, probe.ProbeType == ProbeType.ToolSetter ? 1 : 0));
            b.AppendLine(string.Format("#<_lv_rad> = {0}", (probe.ProbeDiameter / 2d).ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_lv_clear> = {0}", clearance.ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_lv_searchf> = {0}", probe.ProbeFeedRate.ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_lv_latchf> = {0}", probe.LatchFeedRate.ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_lv_zfloor> = {0}", (GrblInfo.MaxTravel.Z > 0d ? -(GrblInfo.MaxTravel.Z) + 1.0d : -9999d).ToInvariantString("0.0##")));
            b.AppendLine(string.Format("#<_lv_refx> = {0}", x));
            b.AppendLine(string.Format("#<_lv_refy> = {0}", y));
            b.AppendLine(string.Format("#<_lv_refz> = {0}", z));
            b.AppendLine("O<pvisecorner> CALL [#<_lv_rad>]");
            b.AppendLine(string.Format("G53 G1 F1000 X[#<_corner_x>] Y[#<_corner_y>] Z[#<_corner_z> + {0}]", zMargin.ToInvariantString("0.0##")));

            SetBusy(true);
            model.Message = "Probing the fixed jaw's corner...";

            var started = new RunStarted();
            var handler = WatchAsyncCompletion(() => OnViseCornerProbeDone(fx), started);

            // confirm:false - clicking "Set position" IS the explicit confirmation. A "Run macro?" Yes/No gate
            // here would block between the jogged position being captured (above) and the probe actually
            // running - and it's a genuine gap: a hardware MPG pendant jogs the controller directly over serial,
            // bypassing this (blocked) WPF window, so the machine can move to a NEW position while the dialog
            // sits waiting for a click. The macro would then run its probe from the STALE captured X/Y/Z, not
            // wherever the operator actually ended up - confirmed on real hardware: the printed rx/ry exactly
            // matched an EARLIER jog position, not the later one the operator had settled on before clicking Yes.
            bool ran = MacroProcessor.Run(model, "Set fixture position", b.ToString(), false);
            if (ran)
                started.Value = true;
            else
            {
                // Aborted before streaming even started (PREREQ failed) - unhook here instead of waiting forever.
                model.PropertyChanged -= handler;
                SetBusy(false);
            }
        }

        // Mutable "has the run genuinely started" flag, set by the CALLER right after MacroProcessor.Run
        // returns true - see WatchAsyncCompletion below for why this replaced watching for a Send/SendMDI
        // PropertyChanged transition.
        private class RunStarted { public bool Value; }

        // Watches a just-started macro run (MacroProcessor.Run, called by the caller right after this) to its
        // TRUE completion and invokes onDone then - necessary whenever the code contains an O-word CALL or a
        // G1/G2/G3 feed move, since MacroProcessor.Flush routes those through the async flow-controlled job
        // streamer (RunStreamedJobInPlace): Cycle Start is deferred to a background dispatcher tick, so Run()
        // returns as soon as the stream is KICKED OFF - well before the probe motion (and its result) actually
        // happens. Reading GrblState/machine position immediately after Run() returns sees STALE values.
        // Confirmed on real hardware twice: RunViseCornerProbe's first attempt used a stale jogged position for
        // exactly this reason (fixed via confirm:false above), and - found while investigating that - Test
        // position's own snippet has the same bug (its G91 G1 retract lines force the same streamed path; its
        // old comment claiming a synchronous MDI path was simply wrong). Mirrors MainWindow.RestoreSourceOnEnd's
        // arm-on-Send/fire-on-Idle pattern.
        //
        // "started" used to be armed by OBSERVING a PropertyChanged transition into Send/SendMDI - but
        // GrblViewModel.StreamingState's setter (and JobControl's own internal dedup before assigning it)
        // no-ops when the value doesn't actually CHANGE. If StreamingState already equalled Send/SendMDI from
        // unrelated prior activity (e.g. a jog run right before clicking Test position - confirmed on real
        // hardware via the console log: a jog immediately preceded a Test click that never got its completion
        // callback), the transition INTO that same value fires no event at all, "started" never arms, and the
        // eventual return to Idle is silently ignored forever - Test position would then probe successfully but
        // the checkmark never turns green, because OnTestPositionDone simply never runs. Fixed by having the
        // CALLER arm "started" from MacroProcessor.Run's own return value (already a reliable synchronous
        // "streaming has begun" signal - see the comment above) instead of inferring it from an event that can
        // be silently suppressed.
        //
        // Returns the subscribed handler so the caller can unhook it if MacroProcessor.Run itself reports the
        // run never started (PREREQ failed) - otherwise it waits forever for a transition that will never come.
        private System.ComponentModel.PropertyChangedEventHandler WatchAsyncCompletion(System.Action onDone, RunStarted started)
        {
            System.ComponentModel.PropertyChangedEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                var st = model.StreamingState;
                // Idle/NoFile is normal completion; Stop is JobControl's Alarm-abort route (GrblStateChanged's
                // GrblStates.Alarm case calls streamingHandler.Call(StreamingState.Stop, false) - see
                // JobControl.xaml.cs) - an Alarm mid-probe (e.g. Test position's G38.2 search coming up empty)
                // never reaches Idle/NoFile at all. Without this case the handler stayed subscribed forever -
                // found on real hardware: an Alarm here left it firing onDone (touching this now-closed
                // dialog's controls and the cloned Fixture) on literally the next unrelated StreamingState
                // Idle/NoFile transition anywhere else in the app, whenever that happened to occur.
                if (started.Value && (st == StreamingState.Idle || st == StreamingState.NoFile || st == StreamingState.Stop))
                {
                    model.PropertyChanged -= handler;
                    Dispatcher.BeginInvoke(new System.Action(onDone));
                }
            };
            model.PropertyChanged += handler;
            return handler;
        }

        // Runs once the async streamed probe (RunViseCornerProbe) has genuinely finished (StreamingState back
        // to Idle/NoFile) - the final G53 move in that program parked the machine at the resolved corner, so
        // the current machine position now IS the value to save.
        private void OnViseCornerProbeDone(Fixture fx)
        {
            string coords = Fixtures.CurrentCoordsCsv(model);
            bool ok = coords != null && model.GrblState.State != GrblStates.Alarm;
            if (ok)
                fx.Coords = coords;   // setter resets PositionValidated - set true right after
            fx.PositionValidated = ok;
            model.Message = ok ? "Jaw corner probed and saved." : "Jaw corner probe failed or alarmed - position not saved.";
            UpdatePositionDisplay();
            UpdateTestPositionEnabled();
            SetBusy(false);
        }

        // Run the REAL spoilboard probe search (the same 12 mm-capped G38.2 pcorner.macro's DISCOVER phase
        // uses) from the saved position, right now - so a bad Z capture (too far above the spoilboard for the
        // capped search to reach) is caught here, before it aborts a real Start Job run. For edge-probing kinds
        // (CornerFence) this also locates the true stock corner and stores it as Fixture.CornerOffsetX/Y - see
        // the block below the spoilboard search.
        private void btnTestPosition_Click(object sender, RoutedEventArgs e)
        {
            var fx = DataContext as Fixture;
            if (fx == null || !fx.HasPosition || model == null)
                return;

            var probe = FixtureActiveProbe();
            if (probe == null)
            {
                model.Message = rbFxProbeTouch.IsChecked == true
                    ? "Define a touch plate probe first (Machine Setup > Probe definitions)."
                    : "Define a 3D probe first (Machine Setup > Probe definitions).";
                return;
            }

            var pos = new Position(fx.Coords);
            string x = pos.X.ToInvariantString("0.0##"), y = pos.Y.ToInvariantString("0.0##"), z = pos.Z.ToInvariantString("0.0##");
            string searchF = probe.ProbeFeedRate.ToInvariantString("0.0##"), latchF = probe.LatchFeedRate.ToInvariantString("0.0##");

            var b = new StringBuilder();
            b.AppendLine("(Test position - spoilboard probe search, 12 mm cap matching pcorner.macro's DISCOVER phase)");
            // EXPR (grblHAL NGC expressions) is only actually exercised by the corner-locate O<pcorner> CALL
            // below (edge-probing kinds), but requiring it up front for every kind is harmless - keeps this in
            // sync with Start Job's own PREREQ for the same macro.
            b.AppendLine("(PREREQ, connected, homed, noalarm, EXPR)");
            b.AppendLine("G21 G90 G94 G17");
            b.AppendLine("G49");
            b.AppendLine("G10 L2 P1 X0 Y0 Z0");   // clear G54 - absolute Z probe below runs in machine coords, same as pcorner.macro
            // Safe-Z travel to the saved reference: rapid (G0) retract to machine Z0, rapid XY, rapid drop to
            // the saved Z - the same staged Z0/XY/Z pattern and G53G0 rapids GotoBaseControl.SafeGotoMachine
            // uses for every other "go to a saved position" button in the app (found on real hardware to be
            // needed here too - the earlier G1 F1000 feed-rate version crawled to XY instead of rapiding).
            // Only the G38.2 probe searches below (already feed-limited to searchF/latchF) actually touch
            // anything - this travel never reaches the target surface, so a rapid here is no less safe than
            // any other Goto button's rapid.
            b.AppendLine("G53G0Z0");
            b.AppendLine(string.Format("G53G0X{0}Y{1}", x, y));
            b.AppendLine(string.Format("G53G0Z{0}", z));
            b.AppendLine(string.Format("G38.2 Z[{0} - 12] F{1}", z, searchF));
            b.AppendLine("G91 G1 Z2 F1000");
            b.AppendLine(string.Format("G38.2 Z-5 F{0}", latchF));
            b.AppendLine("#<_tp_z> = #5063");
            b.AppendLine("G91 G1 Z10 F1000");
            b.AppendLine("G90");
            b.AppendLine(string.Format("#<_tp_margin> = [{0} - #<_tp_z>]", z));
            b.AppendLine("(PRINT, Test position OK - spoilboard is #<_tp_margin> mm below the saved Z (cap is 12 mm).)");
            // Machine-readable echo of the raw probed Z, same (PRINT, TAG=value) idiom StartJobView.rxResult
            // already parses for LS_X/LS_Y - picked up below via Model_PropertyChanged so it can be stored as
            // Fixture.SpoilboardZ (edge-probing kinds only, see below).
            b.AppendLine("(PRINT, SPOIL_Z=#<_tp_z>)");

            // Edge-probing kinds only (CornerFence today): also locate the true stock corner, ONCE, via a real
            // pcorner.macro DISCOVER pass (same wide-clearance search Start Job's own corner-1 probe used to run
            // EVERY job) - then park at a point 5mm INSIDE that corner (the same tight anchor Start Job's old
            // "exact size" re-probe used) so OnTestPositionDone can read the machine's resting XY back and store
            // it as Fixture.CornerOffsetX/Y (relative to Coords). The fence is bolted down, so this only needs
            // doing once - Start Job then points its own single corner-1 probe straight at this stored anchor
            // instead of locating it fresh every run. See the "double probe of corner 1" backlog item.
            if (FixtureKinds.ProbesEdges(fx.Kind) && fx.Implemented)
            {
                double topClearance = probe.MinStandoff + 9d;   // same wide clearance Start Job's DISCOVER pass uses
                const double thicknessAssumedMm = 6d;           // small on purpose - lands the face search near the top, safely inside any real stock
                b.AppendLine("(--- locate the true corner (edge-probing kinds only) ---)");
                b.AppendLine("#<_ls_corner> = 1");   // FrontLeft - the only origin StartJobView.SelectedCorner ever uses
                b.AppendLine(string.Format("#<_ls_refx> = {0}", x));
                b.AppendLine(string.Format("#<_ls_refy> = {0}", y));
                b.AppendLine(string.Format("#<_ls_rad> = {0}", (probe.ProbeDiameter / 2d).ToInvariantString("0.0##")));
                b.AppendLine("#<_ls_spacer> = 0");
                b.AppendLine(string.Format("#<_ls_thickness> = {0}", thicknessAssumedMm.ToInvariantString("0.0##")));
                b.AppendLine("#<_ls_mode> = 0");
                b.AppendLine("#<_ls_plateoffset> = 0");
                b.AppendLine("#<_ls_spoilx> = 0");
                b.AppendLine("#<_ls_spoily> = 0");
                b.AppendLine(string.Format("#<_ls_topx> = {0}", topClearance.ToInvariantString("0.0##")));
                b.AppendLine(string.Format("#<_ls_topy> = {0}", topClearance.ToInvariantString("0.0##")));
                b.AppendLine(string.Format("#<_ls_spoilz> = {0}", z));
                b.AppendLine(string.Format("#<_ls_searchf> = {0}", searchF));
                b.AppendLine(string.Format("#<_ls_latchf> = {0}", latchF));
                b.AppendLine(string.Format("#<_ls_zfloor> = {0}", (GrblInfo.MaxTravel.Z > 0d ? -(GrblInfo.MaxTravel.Z) + 1.0d : -9999d).ToInvariantString("0.0##")));
                // REUSE mode (startz < 9000), NOT DISCOVER (9999): the spoilboard search just above already
                // found the spoilboard, so pcorner.macro's own internal spoilboard probe (DISCOVER's o20 block)
                // would be a second, redundant one - confirmed on real hardware (Test position visibly probed
                // the spoilboard twice before ever reaching the corner). REUSE needs #<_bottom> pre-set (the
                // global pcorner's REUSE path reads for its seek-depth cap) - #<_ls_startz> itself is never
                // read for its VALUE, only compared against 9000, so any REUSE-range literal works; this
                // block never explicitly set it before, so it silently inherited whatever a PRIOR pcorner call
                // on the controller left behind (Start Job's own corner-1 call leaves 9999 = DISCOVER) - that
                // stale global, not a deliberate choice, is what put this in DISCOVER mode.
                b.AppendLine("#<_bottom> = #<_tp_z>");   // the spoilboard search above already found this; spacer unknown at fixture-test time (0)
                b.AppendLine("#<_ls_startz> = 0");
                b.AppendLine("#<_ls_maxz> = 0");
                b.AppendLine("#<_ls_appz> = 9999");
                b.AppendLine(string.Format("O<pcorner> CALL [#<_ls_rad>]"));
                // Park AT the true corner itself (not an inset/outset point) - CornerOffsetX/Y must be the raw
                // true-corner-minus-Coords delta, because StartJobView.BuildProgram is the one place that adds
                // the +5mm interior inset (#<_ls_topx> = CornerOffsetX + 5) on top of it. Parking at an already-
                // adjusted point here would double up/cancel that adjustment - confirmed on real hardware:
                // parking 10mm OUTWARD here (matching the old exact-size re-probe's own reference point) made
                // BuildProgram's "+5 inward" net to 5mm OUTSIDE the corner instead of 5mm inside it.
                b.AppendLine("G53 G1 F1000 X[#<_corner_x>] Y[#<_corner_y>] Z[#<_corner_z> + 20]");
                b.AppendLine("(PRINT, Corner located - CX=#<_corner_x> CY=#<_corner_y>)");
            }

            // The G91 G1 retract lines above are feed moves, so MacroProcessor.Flush routes this through the
            // ASYNC streamed job path, not the synchronous MDI path (see WatchAsyncCompletion's comment) - Run()
            // returns as soon as the stream is kicked off, well before the probe actually happens. Reading
            // GrblState right after Run() returns (the old approach here) saw a STALE state, not the probe's
            // real result - found on real hardware while diagnosing a related staleness bug in Set position.
            fx.PositionValidated = false;
            UpdatePositionDisplay();
            SetBusy(true);

            _capturedSpoilZ = null;
            PropertyChangedEventHandler spoilZHandler = (s, pe) =>
            {
                if (pe.PropertyName != nameof(GrblViewModel.Message) || string.IsNullOrEmpty(model.Message))
                    return;
                var m = rxSpoilZ.Match(model.Message);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    _capturedSpoilZ = v;
            };
            model.PropertyChanged += spoilZHandler;

            var started = new RunStarted();
            var handler = WatchAsyncCompletion(() => { model.PropertyChanged -= spoilZHandler; OnTestPositionDone(fx); }, started);
            bool ran = MacroProcessor.Run(model, "Test fixture position", b.ToString(), true);
            if (ran)
                started.Value = true;
            else
            {
                model.PropertyChanged -= handler;
                model.PropertyChanged -= spoilZHandler;
                SetBusy(false);
            }
        }

        // Runs once Test position's async streamed probe has genuinely finished.
        private void OnTestPositionDone(Fixture fx)
        {
            fx.PositionValidated = model.GrblState.State != GrblStates.Alarm;
            // Edge-probing kinds: the macro above parked the machine at the tight corner anchor as its very
            // last move, so the CURRENT machine position now IS that anchor - same "read back after the macro
            // parks there" idiom OnViseCornerProbeDone uses for Set position. Store it relative to Coords (the
            // saved reference), not as an absolute XY - Coords is what Start Job re-reads it against later.
            if (fx.PositionValidated && FixtureKinds.ProbesEdges(fx.Kind) && fx.Implemented)
            {
                var refPos = new Position(fx.Coords);
                fx.CornerOffsetX = model.MachinePosition.X - refPos.X;
                fx.CornerOffsetY = model.MachinePosition.Y - refPos.Y;
                // Captured off the (PRINT, SPOIL_Z=..) line via spoilZHandler above - lets Start Job reuse this
                // spoilboard search instead of repeating it every job (StartJobView.BuildProgram).
                if (_capturedSpoilZ.HasValue)
                    fx.SpoilboardZ = _capturedSpoilZ.Value;
            }
            UpdatePositionDisplay();
            SetBusy(false);
            // The macro's own (PRINT, Test position OK - ...) message gets clobbered by JobControl's generic
            // "<Program> ready - press Cycle Start to run" banner (SetActiveProgramReady) - Test position reuses
            // the same stay-put-program machinery as a wizard tool, and that banner fires synchronously as part
            // of the Idle transition, before this deferred callback runs. Re-assert a clear final message here,
            // same as OnViseCornerProbeDone already does for Set position.
            model.Message = fx.PositionValidated ? "Test position OK - validated." : "Test position failed or alarmed - not validated.";
        }

        private void SelectKind(FixtureKind kind)
        {
            foreach (ComboBoxItem item in cbxKind.Items)
                if ((string)item.Tag == kind.ToString())
                {
                    cbxKind.SelectedItem = item;
                    break;
                }
        }

        private FixtureKind SelectedKind
        {
            get { return (FixtureKind)Enum.Parse(typeof(FixtureKind), (string)((ComboBoxItem)cbxKind.SelectedItem).Tag); }
        }

        private void cbxKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbxKind.SelectedItem == null)
                return;

            var kind = SelectedKind;
            var fx = DataContext as Fixture;
            if (fx != null)
                fx.Kind = kind;

            UpdateFieldVisibility(kind);
            UpdateTestPositionEnabled();
        }

        // Switch to the schematic matching the selected kind (same reasoning as ProbeDefinitionEditDialog).
        private void UpdateFieldVisibility(FixtureKind kind)
        {
            bool edges = FixtureKinds.ProbesEdges(kind);

            // The three edge-probing kinds (Corner fence / Dog-hole / Vacuum) share one schematic - only the
            // known-position kind (Vise) differs in shape.
            Show(drwCornerStyle, edges);
            Show(drwKnownPosition, !edges);

            // Jaw width/Max opening/probe picker are vise-only - drawing dimensions and probe choice,
            // meaningless for an edge-probing kind (its Set position is a raw jog-capture, no probe run).
            bool isVise = kind == FixtureKind.MachinistVise;
            Show(fldJawWidth, isVise);
            Show(fldMaxOpening, isVise);
            Show(pnlFxProbeType, isVise);

            txtNotImplemented.Visibility = FixtureKinds.Implemented(kind) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void Show(UIElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
