/*
 * MirrorWindow.xaml.cs - part of ioSender
 *
 * The MachineMirror's first real WPF consumer (client/server split, step 6a read-side proof) - see
 * the XAML header for what this window is and why it paints from code-behind. The one rule that
 * makes it a proof: nothing in this file may read GrblViewModel. Its only input is the mirror, and
 * the mirror's only input is the MachineDelta stream.
 */

using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CNC.Client;
using CNC.Contracts;
using CNC.Core;   // beware: CNC.Core.Action shadows System.Action - qualify System.Action explicitly
using CNC.GCode;  // AxisFlags

namespace GCode_Sender
{
    public partial class MirrorWindow : Window
    {
        private readonly MachineMirror mirror;
        private bool repaintPending = false;

        public MirrorWindow(IMachineStateStream stream)
        {
            InitializeComponent();
            mirror = new MachineMirror(stream);
            mirror.PropertyChanged += OnMirrorChanged;
            Repaint();   // Subscribe already delivered a synchronous snapshot - paint it now
        }

        protected override void OnClosed(EventArgs e)
        {
            mirror.PropertyChanged -= OnMirrorChanged;
            mirror.Dispose();   // unsubscribes from the stream
            base.OnClosed(e);
        }

        // A delta bursts one notify per changed field, at status-report cadence - coalesce to one
        // repaint per dispatcher batch, at Background priority so display work never competes with
        // streaming or operator input (the console-scrollback lesson: display is always coalesced,
        // always Background). Also covers the threading contract: deltas arrive on the model's
        // mutating thread, which in-process is the UI thread, but the marshal keeps this correct
        // even when a future out-of-process stream delivers on a socket thread.
        private void OnMirrorChanged(object sender, PropertyChangedEventArgs e)
        {
            if (repaintPending)
                return;
            repaintPending = true;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                repaintPending = false;
                Repaint();
            }), DispatcherPriority.Background);
        }

        // "X 0.000   Y 12.500   Z -1.200" from the wire's parallel arrays; null slots (never reported,
        // the protocol's NaN-never-crosses rule) are skipped rather than shown as fake zeros.
        private string Axes(double?[] values)
        {
            if (values == null)
                return "-";
            string letters = mirror.AxisLetters;
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (!values[i].HasValue)
                    continue;
                if (sb.Length > 0)
                    sb.Append("   ");
                char letter = letters != null && i < letters.Length ? letters[i] : (char)('X' + i);
                sb.Append(letter).Append(' ').Append(values[i].Value.ToString("F3", CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private void Repaint()
        {
            if (!mirror.HasData)
            {
                txtState.Text = "(no data yet)";
                return;
            }

            var state = mirror.GrblState;
            string stateText = state.State.ToString();
            if (state.Substate > 0)
                stateText += ":" + state.Substate;
            if (state.State == GrblStates.Alarm && state.LastAlarm > 0)
                stateText += "  (alarm " + state.LastAlarm + ")";
            if (state.Error > 0)
                stateText += "  (error " + state.Error + ")";
            txtState.Text = stateText;

            txtHomed.Text = mirror.HomedState.ToString()
                + (mirror.AxisHomed != AxisFlags.None ? "  (" + mirror.AxisHomed + ")" : string.Empty);

            txtMPos.Text = Axes(mirror.MachinePosition);
            // Position, NOT WorkPosition: the DRO's work coordinates are the parser-derived Position
            // field (MPos - WCO). The WorkPosition storage is only written when the controller itself
            // reports WPos ($10 report mask) - on an MPos-reporting machine (the default) it stays
            // null forever, which this window duly displayed as "-" on its first live outing.
            txtWPos.Text = Axes(mirror.Position);
            txtWco.Text = Axes(mirror.WorkPositionOffset);

            txtWcsTool.Text = (string.IsNullOrEmpty(mirror.WorkCoordinateSystem) ? "-" : mirror.WorkCoordinateSystem)
                + "  /  T" + (string.IsNullOrEmpty(mirror.Tool) ? "-" : mirror.Tool);

            txtTlo.Text = mirror.IsTloReferenceSet
                ? (mirror.TloReference.HasValue ? mirror.TloReference.Value.ToString("F3", CultureInfo.InvariantCulture) : "set")
                : "not set";

            txtFeed.Text = string.Format(CultureInfo.InvariantCulture, "{0:F0}  ({1:F0}% feed, {2:F0}% rapids)",
                mirror.FeedRate, mirror.FeedOverride, mirror.RapidsOverride);

            txtSpindle.Text = string.Format(CultureInfo.InvariantCulture, "{0}  S{1:F0}{2}  ({3:F0}%)",
                mirror.SpindleState, mirror.ProgrammedRPM,
                mirror.ActualRPM.HasValue ? string.Format(CultureInfo.InvariantCulture, " act {0:F0}", mirror.ActualRPM.Value) : string.Empty,
                mirror.RPMOverride);

            txtSignals.Text = mirror.Signals == Signals.Off && mirror.OptionalSignals == Signals.Off
                ? "none"
                : (mirror.Signals + (mirror.OptionalSignals != Signals.Off ? "  (optional: " + mirror.OptionalSignals + ")" : string.Empty));

            txtProbe.Text = mirror.ProbePosition == null
                ? "-"
                : Axes(mirror.ProbePosition) + (mirror.IsProbeSuccess ? "  (success)" : string.Empty);

            txtReporting.Text = (mirror.AutoReportingEnabled
                    ? "auto @ " + mirror.AutoReportInterval + " ms"
                    : "polled")
                + (mirror.IsMPGActive == true ? "  ·  MPG active" : string.Empty);
        }
    }
}
