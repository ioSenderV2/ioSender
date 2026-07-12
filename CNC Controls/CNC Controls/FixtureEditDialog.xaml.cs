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
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
            DataContext = fixture;
            this.model = model;

            SelectKind(fixture.Kind);
            UpdateFieldVisibility(fixture.Kind);
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

        // Disable both position buttons while a vise corner probe is running (it moves the machine
        // asynchronously - see RunViseCornerProbe) so a second click can't overlap it.
        private void SetBusy(bool busy)
        {
            _probing = busy;
            btnSetPosition.IsEnabled = !busy;
            UpdateTestPositionEnabled();
        }

        // The clearance circle in the corner-style schematic is sized to the current 3D probe's body diameter
        // (the physical constraint - see FixtureEditDialog.xaml.cs class comment). Labelled once at dialog-open
        // since the probe library rarely changes mid-edit; "no 3D probe defined yet" falls back to a generic hint.
        private void UpdateProbeCircleLabel()
        {
            var probe = ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
            txtProbeCircle.Text = probe != null
                ? string.Format("~{0:0.#} mm", probe.BodyDiameter)
                : "probe diameter";
        }

        private void UpdatePositionDisplay()
        {
            var fx = DataContext as Fixture;
            if (fx == null || !fx.HasPosition)
                txtPosition.Text = "Not set";
            else
                txtPosition.Text = fx.Coords + (fx.PositionValidated ? "  (validated)" : "  (not tested)");
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
                RunViseCornerProbe(fx, coords);
                return;
            }

            fx.Coords = coords;
            UpdatePositionDisplay();
            UpdateTestPositionEnabled();
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

            var probe = ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
            if (probe == null)
            {
                model.Message = "Define a 3D probe first (Machine Setup > Probe definitions).";
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
            const double zMargin = 8d;

            var b = new StringBuilder();
            b.AppendLine("(Set position - vise: probe the fixed jaw's front-left corner via pvisecorner.macro)");
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

            var handler = WatchAsyncCompletion(() => OnViseCornerProbeDone(fx));

            // confirm:false - clicking "Set position" IS the explicit confirmation. A "Run macro?" Yes/No gate
            // here would block between the jogged position being captured (above) and the probe actually
            // running - and it's a genuine gap: a hardware MPG pendant jogs the controller directly over serial,
            // bypassing this (blocked) WPF window, so the machine can move to a NEW position while the dialog
            // sits waiting for a click. The macro would then run its probe from the STALE captured X/Y/Z, not
            // wherever the operator actually ended up - confirmed on real hardware: the printed rx/ry exactly
            // matched an EARLIER jog position, not the later one the operator had settled on before clicking Yes.
            bool ran = MacroProcessor.Run(model, "Set fixture position", b.ToString(), false);
            if (!ran)
            {
                // Aborted before streaming even started (PREREQ failed) - the PropertyChanged handler will
                // never see Send, so unhook it here instead of waiting forever.
                model.PropertyChanged -= handler;
                SetBusy(false);
            }
        }

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
        // arm-on-Send/fire-on-Idle pattern. Returns the subscribed handler so the caller can unhook it if
        // MacroProcessor.Run itself reports the run never started (PREREQ failed) - otherwise it waits forever
        // for a Send/SendMDI transition that will never come.
        private System.ComponentModel.PropertyChangedEventHandler WatchAsyncCompletion(System.Action onDone)
        {
            bool started = false;
            System.ComponentModel.PropertyChangedEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                var st = model.StreamingState;
                if (st == StreamingState.Send || st == StreamingState.SendMDI)
                    started = true;
                else if (started && (st == StreamingState.Idle || st == StreamingState.NoFile))
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
        // capped search to reach) is caught here, before it aborts a real Start Job run.
        private void btnTestPosition_Click(object sender, RoutedEventArgs e)
        {
            var fx = DataContext as Fixture;
            if (fx == null || !fx.HasPosition || model == null)
                return;

            var probe = ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
            if (probe == null)
            {
                model.Message = "Define a 3D probe first (Machine Setup > Probe definitions).";
                return;
            }

            var pos = new Position(fx.Coords);
            string x = pos.X.ToInvariantString("0.0##"), y = pos.Y.ToInvariantString("0.0##"), z = pos.Z.ToInvariantString("0.0##");
            string searchF = probe.ProbeFeedRate.ToInvariantString("0.0##"), latchF = probe.LatchFeedRate.ToInvariantString("0.0##");

            var b = new StringBuilder();
            b.AppendLine("(Test position - spoilboard probe search only, 12 mm cap matching pcorner.macro's DISCOVER phase)");
            b.AppendLine("(PREREQ, connected, homed, noalarm)");
            b.AppendLine("G21 G90 G94 G17");
            b.AppendLine("G49");
            b.AppendLine("G10 L2 P1 X0 Y0 Z0");   // clear G54 - absolute Z probe below runs in machine coords, same as pcorner.macro
            // Controlled feed, not a bare rapid - same "avoid rapids" precaution applied to pcorner.macro/
            // pvisecorner.macro this round, so an unexpected surface gets a gentle contact, not a fast one.
            b.AppendLine("G53 G1 F1000 Z0");
            b.AppendLine(string.Format("G53 G1 F1000 X{0} Y{1}", x, y));
            b.AppendLine(string.Format("G53 G1 F1000 Z{0}", z));
            b.AppendLine(string.Format("G38.2 Z[{0} - 12] F{1}", z, searchF));
            b.AppendLine("G91 G1 Z2 F1000");
            b.AppendLine(string.Format("G38.2 Z-5 F{0}", latchF));
            b.AppendLine("#<_tp_z> = #5063");
            b.AppendLine("G91 G1 Z10 F1000");
            b.AppendLine("G90");
            b.AppendLine(string.Format("#<_tp_margin> = [{0} - #<_tp_z>]", z));
            b.AppendLine("(PRINT, Test position OK - spoilboard is #<_tp_margin> mm below the saved Z (cap is 12 mm).)");

            // The G91 G1 retract lines above are feed moves, so MacroProcessor.Flush routes this through the
            // ASYNC streamed job path, not the synchronous MDI path (see WatchAsyncCompletion's comment) - Run()
            // returns as soon as the stream is kicked off, well before the probe actually happens. Reading
            // GrblState right after Run() returns (the old approach here) saw a STALE state, not the probe's
            // real result - found on real hardware while diagnosing a related staleness bug in Set position.
            fx.PositionValidated = false;
            UpdatePositionDisplay();
            SetBusy(true);

            var handler = WatchAsyncCompletion(() => OnTestPositionDone(fx));
            bool ran = MacroProcessor.Run(model, "Test fixture position", b.ToString(), true);
            if (!ran)
            {
                model.PropertyChanged -= handler;
                SetBusy(false);
            }
        }

        // Runs once Test position's async streamed probe has genuinely finished.
        private void OnTestPositionDone(Fixture fx)
        {
            fx.PositionValidated = model.GrblState.State != GrblStates.Alarm;
            UpdatePositionDisplay();
            SetBusy(false);
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
