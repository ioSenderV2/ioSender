/*
 * FixtureEditDialog.xaml.cs - part of CNC Controls library
 *
 * Edits a single Fixture. The Kind dropdown drives which schematic is shown. The caller passes a clone and
 * copies it back on OK so Cancel reverts. "Set position" here does exactly what the fixture list's own Set
 * position button does - captures the CURRENT machine position into this fixture's Coords. It is NOT a
 * firmware G28 write; the position lives only in this fixture's own definition (see Fixtures.CurrentCoordsCsv).
 * For the edge-probing kinds there are no separate offset fields to fill in: the schematic's clearance circle
 * (sized to the current 3D probe's body diameter) is a jog target - position the probe tip inside it, clear of
 * both corner faces, then click Set position. pcorner.macro derives every probe move from that one point plus
 * the live probe definition, not from anything stored per-fixture. Test position's own Z-safety no longer
 * depends on how close Coords sits to the spoilboard - see RunTestPositionMacro's own comment.
 *
 */

using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
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

        public FixtureEditDialog(Fixture fixture, GrblViewModel model)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            // Shown non-modally (see Saved's comment) with ShowInTaskbar="False", the combination that leaves
            // the main window minimized on close - see UIUtils.ActivateOwnerOnClose. Done here rather than at
            // the two MachineSetupWizard call sites so it can't be forgotten by a third one.
            UIUtils.ActivateOwnerOnClose(this);
            DataContext = fixture;
            this.model = model;

            rbFxProbe3d.Checked += (s, e) => OnProbeSelectionChanged(ProbeType.ThreeDProbe);
            rbFxProbeTouch.Checked += (s, e) => OnProbeSelectionChanged(ProbeType.TouchPlate);

            // Restore THIS fixture's own probe selection instead of always landing on 3D Probe - reopening a
            // touch-plate fixture in 3D-probe mode meant the next Set/Test silently ran with the wrong probe
            // geometry (plate thickness/lip offset dropped) until the operator noticed and re-picked.
            // UpdateFxProbeWarning below still overrides this when the chosen probe isn't actually defined.
            if (fixture.ProbeType == ProbeType.TouchPlate)
                rbFxProbeTouch.IsChecked = true;
            else
                rbFxProbe3d.IsChecked = true;

            SelectKind(fixture.Kind);
            UpdateFieldVisibility(fixture.Kind);
            UpdateFxProbeWarning();
            UpdateProbeCircleLabel();
            UpdateProbeNote();
            UpdatePositionDisplay();
            UpdateTestPositionEnabled();
            // From here on, a Checked event means the OPERATOR changed the probe selection, not dialog
            // startup wiring its default - see ClearValidationOnProbeChange's own comment.
            _initializing = false;
        }

        // False once the constructor's own initial radio-button wiring/defaults have settled - guards
        // ClearValidationOnProbeChange so it doesn't fire on the dialog simply opening.
        private bool _initializing = true;

        // One place for "the operator picked a probe": refresh the probe-dependent UI, record the choice on the
        // fixture so it survives closing the dialog, and invalidate a checkmark earned by the other probe.
        // The record is skipped while _initializing so neither the restore above nor UpdateFxProbeWarning's
        // availability fallback is mistaken for an operator decision and written back.
        private void OnProbeSelectionChanged(ProbeType type)
        {
            UpdateProbeCircleLabel();
            UpdateProbeNote();

            var fx = DataContext as Fixture;
            if (fx != null && !_initializing)
                fx.ProbeType = type;

            ClearValidationOnProbeChange();
        }

        // A saved position validated under one probe was only ever probed by THAT probe - switching to the
        // other one (e.g. 3D probe went dead, switching to Touch Plate to revalidate) means nothing has
        // actually confirmed the NEW probe can reach this position yet, so the checkmark saying otherwise is
        // now a lie until Test position runs again for real.
        private void ClearValidationOnProbeChange()
        {
            if (_initializing)
                return;
            var fx = DataContext as Fixture;
            if (fx != null && fx.PositionValidated)
            {
                fx.PositionValidated = false;
                UpdatePositionDisplay();
                UpdateTestPositionEnabled();
            }
        }

        // The red warning banner at the bottom - was hardcoded to the 3D-probe wording regardless of which
        // probe is actually selected, which stopped being true the moment Touch Plate became selectable for
        // every fixture kind (not just Vise).
        private void UpdateProbeNote()
        {
            txtProbeNote.Text = rbFxProbeTouch.IsChecked == true
                ? "A conductive object (e.g. a touch plate) must be staged at the saved position before Test position - it senses contact by electrical continuity, not a mechanical probe. Set position itself is a raw jog-capture and needs no probe."
                : "The 3D probe must be installed before Test position. Set position itself is a raw jog-capture and needs no probe.";
        }

        // "Test position" only makes sense for a kind that probes the spoilboard (see pcorner.macro's DISCOVER
        // phase) and only once a position is actually saved to run the search from. Also disabled while a vise
        // corner probe (RunViseCornerProbe) is in flight - both buttons are, see SetBusy.
        private void UpdateTestPositionEnabled()
        {
            var fx = DataContext as Fixture;
            btnTestPosition.IsEnabled = !_probing && fx != null && fx.HasPosition && FixtureKinds.ProbesSpoilboard(fx.Kind);
        }

        // Touch Plate is only selectable when a touch-plate probe is actually defined - falls back to 3D
        // Probe if the definition disappears while it was selected, same rule StartJobView.UpdateProbeWarning
        // follows. Applies to every fixture kind now, not just Vise - see UpdateFieldVisibility's own comment.
        private void UpdateFxProbeWarning()
        {
            bool touchAvailable = ProbeDefinitions.Items.Any(p => p.ProbeType == ProbeType.TouchPlate);
            bool probe3dAvailable = ProbeDefinitions.Items.Any(p => p.ProbeType == ProbeType.ThreeDProbe);

            rbFxProbeTouch.IsEnabled = touchAvailable;
            // 3D Probe used to be permanently enabled and was the unconditional default, so a machine with only
            // a touch plate defined still opened every new fixture in 3D-probe mode - nothing on screen said
            // the selected probe didn't exist, and Set/Test then failed on a probe definition that was never
            // there. Gate it the same way, and fall back to whichever one IS defined.
            rbFxProbe3d.IsEnabled = probe3dAvailable;

            if (!touchAvailable && probe3dAvailable)
                rbFxProbe3d.IsChecked = true;
            else if (!probe3dAvailable && touchAvailable)
                rbFxProbeTouch.IsChecked = true;
        }

        // The probe definition Set/Test position should actually use, per the Probe: radio selection - every
        // fixture kind now (see UpdateFieldVisibility's own comment). Set position itself still never runs a
        // probe for the edge-probing kinds (a raw jog-capture); this only matters for Test position and, for
        // CornerFence, the corner-locate pass inside it.
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
                // model.Message only reaches MainWindow's own status label, which sits BEHIND this modal
                // dialog - invisible while it's open, so a click here looked like it silently did nothing.
                // AppDialogs.Show is guaranteed visible regardless of what's behind it (same idiom the vise
                // "is it empty?" confirmation below already uses).
                AppDialogs.Show("Machine position unknown - home first (or jog once a position is known) before setting a fixture position.",
                    "Set position", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // A stale CornerOffsetX/Y is meaningless once the reference it was measured from moves - clear it
            // here (the one place a re-jog genuinely happens), not in the Coords setter itself (see the
            // setter's own comment for why that broke on real hardware).
            fx.CornerOffsetX = 0d;
            fx.CornerOffsetY = 0d;
            UpdatePositionDisplay();
            UpdateTestPositionEnabled();
        }

        // Cancel closes the dialog regardless (IsCancel="True") - the vise corner probe (RunViseCornerProbe)
        // streams asynchronously and isn't owned by this window, so closing mid-probe would otherwise leave it
        // running unsupervised while WatchAsyncCompletion's callback waits to touch a disposed window. Feed
        // Hold first (never Reset while still in-flight here) - see the streamer-thread wedge notes: Reset
        // during an in-flight MOVE can leave grblHAL unrecoverable without a controller power-cycle, but Hold
        // is safe and brings the machine to a full stop. Once actually stopped, follow with a real Reset -
        // "resume from the main window later" doesn't make sense once this dialog (the only thing tracking
        // what was running) is gone, so leaving it merely paused just strands the operator in an unexplained
        // Hold state - confirmed as confusing on real hardware 2026-07-30. Same delay idiom as
        // JobControl.ResetAndUnlock (give the controller time to actually land in Hold before Reset).
        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_probing)
            {
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (s, e2) =>
                {
                    timer.Stop();
                    Comms.com.WriteByte((byte)GrblConstants.CMD_RESET);
                };
                timer.Start();
            }
            // IsCancel's built-in auto-close (and its Window.DialogResult=false) only fires for a window
            // shown via ShowDialog() - this one is now shown with Show(), so Cancel has to close it itself.
            Close();
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
            b.AppendLine(string.Format("#<_lv_zfloor> = {0}", (GrblInfo.MaxTravel.Z > 0d ? -(GrblInfo.MaxTravel.Z) + 10.0d : -9999d).ToInvariantString("0.0##")));
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

            if (!ResolveTestPosition(fx))
                return;

            RunTestPositionMacro(fx);
        }

        // How far the machine has to have moved from the saved position before Test offers to adopt it.
        // Below this the corrective rapid is physically meaningless, so asking would just be noise.
        private const double PositionMovedToleranceMm = 0.1d;

        // Test position rapids to the SAVED position (fx.Coords) before probing anything. If the operator has
        // jogged since saving - which is exactly what the failed-search recovery below asks them to do - that
        // rapid silently undoes the jog and re-runs the identical failing probe from the identical place.
        // Confirmed from a real hardware log 2026-08-02: saved Z -80.032, operator jogged to -87.033 (8mm over
        // the touch plate), pressed Test, and it drove back UP to -78.236 and alarmed 4mm short of the plate -
        // having followed the app's own on-screen instruction to jog closer and press Test again.
        //
        // So when the two disagree, ask, and default to the live position: the operator jogged there
        // deliberately and it is the better datum. Nothing moves until they answer. Returns false to abort.
        private bool ResolveTestPosition(Fixture fx)
        {
            string current = Fixtures.CurrentCoordsCsv(model);
            if (current == null)
                return true;   // position unknown - nothing to compare; the macro's own PREREQ will catch it

            var now = new Position(current);
            var saved = new Position(fx.Coords);

            if (Math.Abs(now.X - saved.X) <= PositionMovedToleranceMm &&
                Math.Abs(now.Y - saved.Y) <= PositionMovedToleranceMm &&
                Math.Abs(now.Z - saved.Z) <= PositionMovedToleranceMm)
                return true;

            var answer = AppDialogs.Show(string.Format(
                    "The machine has moved since this fixture position was saved.\n\n" +
                    "Saved:      X{0}  Y{1}  Z{2}\n" +
                    "Current:  X{3}  Y{4}  Z{5}\n\n" +
                    "Test from the current position? This replaces the saved position with where the machine " +
                    "is now - the same thing Set position would do.",
                    saved.X.ToInvariantString("0.0##"), saved.Y.ToInvariantString("0.0##"), saved.Z.ToInvariantString("0.0##"),
                    now.X.ToInvariantString("0.0##"), now.Y.ToInvariantString("0.0##"), now.Z.ToInvariantString("0.0##")),
                "Test position", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes,
                yesText: "Use current", noText: "Use saved");

            if (answer != MessageBoxResult.Yes && answer != MessageBoxResult.No)
                return false;

            if (answer == MessageBoxResult.Yes)
            {
                fx.Coords = current;
                // Same reasoning as btnSetPosition_Click: a corner offset measured from the OLD reference is
                // meaningless once that reference moves, and this is a genuine re-jog.
                fx.CornerOffsetX = 0d;
                fx.CornerOffsetY = 0d;
                UpdatePositionDisplay();
                UpdateTestPositionEnabled();
            }

            return true;
        }

        // The actual macro build-and-kickoff, split out from the button click so the auto-recovery path
        // (RecoverFromFailedSpoilboardSearch/PromptRetryCloserToSpoilboard below) can re-run it from Coords'
        // new value without going through the button.
        private void RunTestPositionMacro(Fixture fx)
        {
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

            // Bounded search depth below the SAVED Z (fx.Coords.Z - wherever the operator jogged to and clicked
            // Set), not a blind dive toward the machine's own Z floor. Retracting to machine Z0 first and
            // searching almost the full travel (the prior design here) threw away the one piece of real
            // information we have - the operator's own careful jog height above the plate/stock - forcing an
            // unbounded worst-case search that isn't actually safer, just less informed. This is the same
            // ~12mm cap the pre-redesign version used (10mm expected jog clearance + 2mm buffer); it doesn't
            // reintroduce a cached Zspoil dependency since the anchor is a FRESH live jog every time, never a
            // stored value.
            const double searchDepthMm = 12d;

            // Machine's own Z soft-limit floor - only used below as pcorner.macro's internal fail-safe cap
            // (_ls_zfloor/_bottom), never as this method's own search target anymore.
            // +10d margin, not +1d: hardware run 2026-07-30 tripped Alarm:2 (soft limit) with only 1mm of
            // margin - $132 travel apparently doesn't leave a full 1mm of headroom at the reported figure
            // (homing pulloff/backlash eats into it).
            double zFloor = GrblInfo.MaxTravel.Z > 0d ? -(GrblInfo.MaxTravel.Z) + 10.0d : -9999d;

            bool edgeProbing = FixtureKinds.ProbesEdges(fx.Kind) && fx.Implemented;

            var b = new StringBuilder();
            b.AppendLine(edgeProbing
                ? "(Test position - locate the true corner)"
                : "(Test position - probe down from the saved Z)");
            // EXPR (grblHAL NGC expressions) is only actually exercised by the corner-locate O<pcorner> CALL
            // below (edge-probing kinds), but requiring it up front for every kind is harmless - keeps this in
            // sync with Start Job's own PREREQ for the same macro.
            b.AppendLine("(PREREQ, connected, homed, noalarm, EXPR)");
            b.AppendLine("G21 G90 G94 G17");
            b.AppendLine("G49");
            b.AppendLine("G10 L2 P1 X0 Y0 Z0");   // clear G54 - absolute Z probe below runs in machine coords, same as pcorner.macro
            // Explicit main-probe-input select (Q0 - both 3D probe and Touch Plate use it, only Tool Setter
            // uses Q1, see GrblCommand.ProbeSelect's own callers) - not just relying on whatever was already
            // active. A prior interrupted tool-change leaves Q1 selected (tc.macro's own comment on this
            // exact hazard), which would silently send this Z probe to the toolsetter input instead.
            b.AppendLine(string.Format(GrblCommand.ProbeSelect, 0));
            b.AppendLine(string.Format("G53G0X{0}Y{1}Z{2}", x, y, z));

            if (!edgeProbing)
            {
                // Non-edge-probing kinds never call pcorner below, so THIS is the only Z touch Test position
                // ever makes - probe down searchDepthMm from the saved Z to confirm the probe actually reaches.
                b.AppendLine(string.Format("G38.2 Z[{0}-{1}] F{2}", z, searchDepthMm.ToInvariantString("0.0##"), searchF));
                b.AppendLine("G91 G1 Z2 F1000");
                b.AppendLine(string.Format("G38.2 Z-5 F{0}", latchF));
                b.AppendLine("G91 G1 Z10 F1000");
                b.AppendLine("G90");
                b.AppendLine("(PRINT, Test position OK - found a safe travel height.)");
            }

            // Edge-probing kinds only (CornerFence today): also locate the true stock corner, ONCE, via a real
            // pcorner.macro DISCOVER pass (same wide-clearance search Start Job's own corner-1 probe used to run
            // EVERY job) - then park at a point 5mm INSIDE that corner (the same tight anchor Start Job's old
            // "exact size" re-probe used) so OnTestPositionDone can read the machine's resting XY back and store
            // it as Fixture.CornerOffsetX/Y (relative to Coords). The fence is bolted down, so this only needs
            // doing once - Start Job then points its own single corner-1 probe straight at this stored anchor
            // instead of locating it fresh every run. See the "double probe of corner 1" backlog item.
            if (edgeProbing)
            {
                // pcorner.macro's #<_ls_topx>/#<_ls_topy> exist to move an OUTSIDE-the-stock reference inward
                // over solid material before the top probe (see its own file header). That doesn't apply here:
                // the operator jogged directly to/near the corner, not to a point outside both faces, so the
                // reference is already exactly where the top probe needs to be.
                double topClearance = 0d;
                // pcorner.macro now ALSO uses #<_ls_thickness> to size the pre-probe approach height
                // (#<_bottom> + thickness + plateoffset + 10 - see its own comment), not just the (now-dead)
                // face-search-depth role this comment used to describe. Test position has no real per-job
                // stock thickness to draw from (it may run before any stock is even placed), so this stays a
                // conservative worst-case assumption (matching the approach height's old blind "assume <=1in"
                // constant) rather than the old deliberately-small 6mm - that value would have UNDER-sized
                // the approach clearance the moment #<_ls_thickness> stopped being inert.
                const double thicknessAssumedMm = 25.4d;
                b.AppendLine("(--- locate the true corner ---)");
                b.AppendLine("#<_ls_corner> = 1");   // FrontLeft - the only origin StartJobView.SelectedCorner ever uses
                b.AppendLine(string.Format("#<_ls_refx> = {0}", x));
                b.AppendLine(string.Format("#<_ls_refy> = {0}", y));
                b.AppendLine(string.Format("#<_ls_rad> = {0}", (probe.ProbeDiameter / 2d).ToInvariantString("0.0##")));
                b.AppendLine("#<_ls_spacer> = 0");
                b.AppendLine(string.Format("#<_ls_thickness> = {0}", thicknessAssumedMm.ToInvariantString("0.0##")));
                // Threaded from the ACTUALLY selected probe now, not hardcoded to the 3D probe - a fence's
                // own corner-locate needs the same touch-plate offsets Start Job's real run applies
                // (StartJobView.BuildProgram), or a fence validated with a touch plate reports a corner
                // shifted by the plate's own lip/thickness. #<_ls_mode> itself has no effect on THIS call
                // (REUSE, #<_ls_startz> below - pcorner.macro only branches on mode inside its DISCOVER-only
                // blocks) but is still set correctly in case that ever changes.
                bool touchPlate = probe.ProbeType == ProbeType.TouchPlate;
                b.AppendLine(string.Format("#<_ls_mode> = {0}", touchPlate ? 1 : 0));
                b.AppendLine(string.Format("#<_ls_plateoffset> = {0}", (touchPlate ? probe.PlateThickness : 0d).ToInvariantString("0.0##")));
                b.AppendLine(string.Format("#<_ls_lipoffset> = {0}", (touchPlate ? probe.LipWidth : 0d).ToInvariantString("0.0##")));
                b.AppendLine("#<_ls_edgemargin> = 10");   // see pcorner.macro's own comment - slop against an unconfirmed edge
                b.AppendLine("#<_ls_spoilx> = 0");
                b.AppendLine("#<_ls_spoily> = 0");
                b.AppendLine(string.Format("#<_ls_topx> = {0}", topClearance.ToInvariantString("0.0##")));
                b.AppendLine(string.Format("#<_ls_topy> = {0}", topClearance.ToInvariantString("0.0##")));
                b.AppendLine(string.Format("#<_ls_searchf> = {0}", searchF));
                b.AppendLine(string.Format("#<_ls_latchf> = {0}", latchF));
                b.AppendLine(string.Format("#<_ls_zfloor> = {0}", zFloor.ToInvariantString("0.0##")));
                // REUSE mode (startz < 9000), NOT DISCOVER (9999): pcorner.macro's own internal spoilboard
                // probe (DISCOVER's o20 block) would be redundant. #<_ls_maxz> is fed straight from the
                // OPERATOR'S OWN saved Z - they jogged there deliberately as a safe height (same trust Set
                // position already relies on), so there's no need to re-derive it with a separate probe first -
                // pcorner's own o45 probe below is the only Z touch this call makes. #<_bottom> (its seek-depth
                // cap) is bound to searchDepthMm below that same saved Z, not the machine floor - the same tight
                // bound the old standalone probe used to enforce, just applied to pcorner's probe instead of a
                // redundant one of our own. Confirmed on real hardware 2026-07-30 - probing the same XY twice
                // made no sense once #<_ls_topx>/#<_ls_topy> stopped moving the reference anywhere.
                b.AppendLine(string.Format("#<_bottom> = [{0}-{1}]", z, searchDepthMm.ToInvariantString("0.0##")));
                b.AppendLine("#<_ls_startz> = 0");
                b.AppendLine(string.Format("#<_ls_maxz> = [{0}+2]", z));
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

            var started = new RunStarted();
            var handler = WatchAsyncCompletion(() => OnTestPositionDone(fx), started);
            bool ran = MacroProcessor.Run(model, "Test fixture position", b.ToString(), true);
            if (ran)
                started.Value = true;
            else
            {
                model.PropertyChanged -= handler;
                SetBusy(false);
            }
        }

        // grblHAL alarm substates for a G38.2 probe search that ran its full commanded travel without ever
        // contacting anything (as opposed to a hard/soft limit, comms loss, etc.) - see ProbingView's own
        // (commented-out) handling of the same 2 substates.
        private const int AlarmSubstateProbeFailInitial = 4;
        private const int AlarmSubstateProbeFailContact = 5;

        // Runs once Test position's async streamed probe has genuinely finished.
        private void OnTestPositionDone(Fixture fx)
        {
            bool alarmed = model.GrblState.State == GrblStates.Alarm;
            fx.PositionValidated = !alarmed;
            // Edge-probing kinds: the macro above parked the machine at the tight corner anchor as its very
            // last move, so the CURRENT machine position now IS that anchor - same "read back after the macro
            // parks there" idiom OnViseCornerProbeDone uses for Set position. Store it relative to Coords (the
            // saved reference), not as an absolute XY - Coords is what Start Job re-reads it against later.
            if (fx.PositionValidated && FixtureKinds.ProbesEdges(fx.Kind) && fx.Implemented)
            {
                var refPos = new Position(fx.Coords);
                fx.CornerOffsetX = model.MachinePosition.X - refPos.X;
                fx.CornerOffsetY = model.MachinePosition.Y - refPos.Y;
            }
            UpdatePositionDisplay();

            // The bounded 12mm search below the saved Z (RunTestPositionMacro) came up empty - the operator
            // wasn't actually within reach of the plate/stock from where they jogged and clicked Set. Still
            // recover automatically (unlock +
            // retract to the saved Z) and offer to retry, rather than just reporting failure and leaving the
            // operator to unlock/jog/retry by hand. Stays busy (no SetBusy(false) here) until that whole
            // recovery flow settles - see RecoverFromFailedSpoilboardSearch.
            if (alarmed && (model.GrblState.Substate == AlarmSubstateProbeFailInitial || model.GrblState.Substate == AlarmSubstateProbeFailContact))
            {
                RecoverFromFailedSpoilboardSearch(fx);
                return;
            }

            SetBusy(false);
            // The macro's own (PRINT, Test position OK - ...) message gets clobbered by JobControl's generic
            // "<Program> ready - press Run to run" banner (SetActiveProgramReady) - Test position reuses
            // the same stay-put-program machinery as a wizard tool, and that banner fires synchronously as part
            // of the Idle transition, before this deferred callback runs. Re-assert a clear final message here,
            // same as OnViseCornerProbeDone already does for Set position.
            model.Message = fx.PositionValidated ? "Test position OK - validated." : "Test position failed or alarmed - not validated.";
        }

        // Unlock the probe-fail alarm ($X - no full reset needed, nothing actually faulted) and retract to the
        // saved reference Z, then (once that retract genuinely finishes) prompt the operator to jog closer and
        // retry. A short delay after $X lets the controller's Idle state actually land before the retract macro
        // starts - same reasoning as JobControl.ResetAndUnlock's own post-unlock delay.
        private void RecoverFromFailedSpoilboardSearch(Fixture fx)
        {
            double savedZ = new Position(fx.Coords).Z;
            model.Message = "Test position failed - the search never made contact. Unlocking and retracting...";
            model.ExecuteCommand(GrblConstants.CMD_UNLOCK);

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();

                var b = new StringBuilder();
                b.AppendLine("(Test position recovery - retract to the saved Z after a failed spoilboard search)");
                b.AppendLine("(PREREQ, connected, homed, noalarm)");
                b.AppendLine("G21 G90");
                b.AppendLine(string.Format("G53G0Z{0}", savedZ.ToInvariantString("0.0##")));

                var started = new RunStarted();
                var handler = WatchAsyncCompletion(PromptRetryCloserToSpoilboard, started);
                bool ran = MacroProcessor.Run(model, "Test position recovery", b.ToString(), false);
                if (ran)
                    started.Value = true;
                else
                {
                    model.PropertyChanged -= handler;
                    SetBusy(false);
                    model.Message = "Test position failed - could not retract automatically. Jog clear by hand.";
                }
            };
            timer.Start();
        }

        // The machine is back at the saved Z and unlocked - explain why it failed and what to do about it.
        //
        // This used to ask the operator to "jog closer ... then click OK to retry automatically" and then
        // compare Z on the way out. That could never work: AppDialogs.Show is a blocking ShowDialog, so the
        // on-screen jog pad is dead for as long as the prompt is up, and the compare therefore always saw an
        // unchanged Z and fell through to "jog closer, then click Test position" - advice that was itself
        // wrong, because Test rapids back to the saved position first and undoes the jog. A real hardware log
        // (2026-08-02) caught the whole loop: fail, retract, dismiss, jog 7mm closer, press Test, drive
        // straight back up, fail identically. Both halves are gone; the operator jogs freely once this is
        // dismissed and ResolveTestPosition above offers to adopt the new position on the next Test.
        private void PromptRetryCloserToSpoilboard()
        {
            SetBusy(false);

            AppDialogs.Show(
                "Test position failed - the probe search never made contact within 12mm below the saved Z. " +
                "The alarm has been cleared and the machine returned to the saved Z.\n\n" +
                "Check the probe (or, for Touch Plate, that a conductive object is actually staged at this " +
                "position), then jog to within ~10mm above the surface and press Test position again - it " +
                "will offer to use the new position.",
                "Test position", MessageBoxButton.OK, MessageBoxImage.Warning);

            model.Message = "Test position failed - jog closer to the surface, then press Test position again.";
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

            // Jaw width/Max opening are vise-only drawing dimensions - meaningless for an edge-probing kind.
            bool isVise = kind == FixtureKind.MachinistVise;
            Show(fldJawWidth, isVise);
            Show(fldMaxOpening, isVise);
            // The probe picker used to be vise-only too, on the reasoning that an edge-probing kind's Set
            // position is a raw jog-capture with no probe run - true, but Test position DOES run a probe for
            // every kind (RunTestPositionMacro, gated on FixtureKinds.ProbesSpoilboard which is unconditionally
            // true), so a Corner Fence etc. needs the same choice. A dead 3D probe with no way to switch a
            // fence's own fixture to Touch Plate for revalidation was the real-world case that found this.
            Show(pnlFxProbeType, true);

            txtNotImplemented.Visibility = FixtureKinds.Implemented(kind) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void Show(UIElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Set (not Window.DialogResult - that throws unless the window was shown via ShowDialog) now that
        // this dialog is opened with Show(), not ShowDialog(): non-modal, so Set/Test position can be run
        // while the main window's jog pad and keyboard jogging stay reachable, instead of jogging being
        // impossible for the whole time this dialog is open. Callers check this from the dialog's own
        // Closed event instead of a ShowDialog() return value.
        public bool Saved { get; private set; }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            Saved = true;
            Close();
        }
    }
}
