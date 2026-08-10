/*
 * RunStripPanel.xaml.cs - the run strip's right half (Jogging | Signals | Feeds and Speeds).
 *
 * Spec: docs/RunStrip-Layout-Spec.md.
 *
 * Two deliberate implementation choices:
 *
 * 1. The readouts are driven from the model's PropertyChanged in code-behind rather than by bindings
 *    with converters. Each signal letter needs "is THIS bit set" colouring, which as bindings would
 *    mean a converter instance per letter and a resource entry for each - and a missing x:Key is a
 *    runtime-only crash in this codebase's experience. One handler is less machinery and fails at
 *    compile time.
 *
 * 2. The override nudges write single realtime BYTES straight to the controller
 *    (Comms.com.WriteByte), exactly as the existing FeedControl does. They are never routed through
 *    the MDI/streamer path: an override must not queue behind streamed g-code.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public partial class RunStripPanel : UserControl
    {
        // Jog distance ladder. Matches the step buttons the web client offers and the values an
        // operator actually uses; the "-"/"+" buttons walk it rather than scaling arbitrarily.
        private static readonly double[] JogDistances = { 0.01, 0.1, 1.0, 10.0, 100.0 };
        private const double JogSpeedStep = 100d;
        private const double JogSpeedMin = 10d;

        private static readonly Brush LampOff = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
        private static readonly Brush LampOn = Brushes.Red;

        // letter -> which Signals bit it reports. Built once; see the spec for the full letter key.
        private readonly List<KeyValuePair<TextBlock, Signals>> lamps = new List<KeyValuePair<TextBlock, Signals>>();

        private GrblViewModel model;

        public RunStripPanel()
        {
            InitializeComponent();

            // Limit row: the three real axes. (A 4th+ axis machine would want more here - driven off
            // GrblInfo.AxisLetters when that matters.)
            AddLamp(rowLimit, "X", Signals.LimitX);
            AddLamp(rowLimit, "Y", Signals.LimitY);
            AddLamp(rowLimit, "Z", Signals.LimitZ);

            // Steppers row. W is read as WARNING here, not the W-axis limit: under a "Steppers"
            // heading, motor warning + motor fault are the pair that mean something, and a 3-axis
            // machine has no W axis for a W limit to ever assert on. Flagged with the user.
            AddLamp(rowSteppers, "W", Signals.MotorWarning);
            AddLamp(rowSteppers, "F", Signals.MotorFault);

            // Probes row, per the spec's letters. Note H and S are the HOLD and CYCLE START inputs
            // (physical panel buttons), not probe signals - kept here because the spec asked for them.
            AddLamp(rowProbes, "H", Signals.Hold);
            AddLamp(rowProbes, "S", Signals.CycleStart);
            AddLamp(rowProbes, "P", Signals.Probe);

            DataContextChanged += OnDataContextChanged;
        }

        private void AddLamp(StackPanel row, string letter, Signals signal)
        {
            var block = new TextBlock
            {
                Text = letter,
                Style = (Style)FindResource("SignalLetterStyle"),
                ToolTip = signal.ToString()
            };
            row.Children.Add(block);
            lamps.Add(new KeyValuePair<TextBlock, Signals>(block, signal));
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (model != null)
                model.PropertyChanged -= OnModelPropertyChanged;

            model = e.NewValue as GrblViewModel;

            if (model != null)
            {
                model.PropertyChanged += OnModelPropertyChanged;
                RefreshAll();
            }
        }

        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(GrblViewModel.Signals):        UpdateSignals(); break;
                case nameof(GrblViewModel.FeedOverride):   UpdateOverrides(); break;
                case nameof(GrblViewModel.RapidsOverride): UpdateOverrides(); break;
                case nameof(GrblViewModel.RPMOverride):    UpdateOverrides(); break;
                case nameof(GrblViewModel.TloReference):
                case nameof(GrblViewModel.IsTloReferenceSet): UpdateTlo(); break;
                case nameof(GrblViewModel.JogStep):        UpdateJog(); break;
            }
        }

        private void RefreshAll()
        {
            UpdateSignals();
            UpdateOverrides();
            UpdateTlo();
            UpdateJog();
            chkContinuous.IsChecked = model != null && model.Keyboard != null && model.Keyboard.IsContinuousJoggingEnabled;
        }

        private void UpdateSignals()
        {
            Signals asserted = model == null ? Signals.Off : model.Signals.Value;
            foreach (var lamp in lamps)
                lamp.Key.Foreground = (asserted & lamp.Value) != 0 ? LampOn : LampOff;
        }

        private void UpdateOverrides()
        {
            if (model == null)
                return;
            lblFeedOvr.Text = ((int)model.FeedOverride) + "%";
            lblRapidOvr.Text = ((int)model.RapidsOverride) + "%";
            lblSpindleOvr.Text = ((int)model.RPMOverride) + "%";
        }

        private void UpdateTlo()
        {
            // Blank, not 0.000, when no reference has been set: a confident zero is the one reading an
            // operator must not be shown when the truth is "unknown".
            lblTloRef.Text = model != null && model.IsTloReferenceSet
                ? model.TloReference.ToString(model.Format)
                : "—";
        }

        private void UpdateJog()
        {
            if (model == null || model.Keyboard == null)
                return;
            lblJogDistance.Text = model.Keyboard.JogStepDistance.ToString("0.###");
            lblJogSpeed.Text = ((int)JogFeedrate) + "";
        }

        // The step-jog feedrate. Kept on the controller so the jog pad, keyboard and this panel all
        // read one value rather than three copies that drift.
        private double JogFeedrate
        {
            get
            {
                var rates = model == null || model.Keyboard == null ? null : model.Keyboard.JogFeedrates;
                return rates == null || rates.Length == 0 ? 0d : rates[(int)JogMode.Step];
            }
            set
            {
                var rates = model == null || model.Keyboard == null ? null : model.Keyboard.JogFeedrates;
                if (rates != null && rates.Length > 0)
                    rates[(int)JogMode.Step] = value;
            }
        }

        // ---------------------------------------------------------------- jogging

        private void JogDistMinus_Click(object sender, RoutedEventArgs e) { StepJogDistance(-1); }
        private void JogDistPlus_Click(object sender, RoutedEventArgs e) { StepJogDistance(1); }

        private void StepJogDistance(int direction)
        {
            if (model == null || model.Keyboard == null)
                return;

            // Walk the ladder from whichever entry is nearest the current value, so an odd value set
            // elsewhere still moves sensibly instead of jumping to an end.
            double current = model.Keyboard.JogStepDistance;
            int index = 0;
            for (int i = 1; i < JogDistances.Length; i++)
                if (Math.Abs(JogDistances[i] - current) < Math.Abs(JogDistances[index] - current))
                    index = i;

            index = Math.Max(0, Math.Min(JogDistances.Length - 1, index + direction));
            model.Keyboard.JogStepDistance = JogDistances[index];
            UpdateJog();
        }

        private void JogSpeedMinus_Click(object sender, RoutedEventArgs e) { StepJogSpeed(-JogSpeedStep); }
        private void JogSpeedPlus_Click(object sender, RoutedEventArgs e) { StepJogSpeed(JogSpeedStep); }

        private void StepJogSpeed(double delta)
        {
            JogFeedrate = Math.Max(JogSpeedMin, JogFeedrate + delta);
            UpdateJog();
        }

        private void Continuous_Changed(object sender, RoutedEventArgs e)
        {
            if (model != null && model.Keyboard != null)
                model.Keyboard.IsContinuousJoggingEnabled = chkContinuous.IsChecked == true;
        }

        // ---------------------------------------------------------------- overrides

        // Single realtime bytes, straight to the controller - never through the streamer queue.
        private static void Send(byte command)
        {
            if (Comms.com != null)
                Comms.com.WriteByte(command);
        }

        private void FeedOvrMinus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_FEED_OVR_COARSE_MINUS); }
        private void FeedOvrPlus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_FEED_OVR_COARSE_PLUS); }
        private void FeedOvrReset_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_FEED_OVR_RESET); }

        private void SpindleOvrMinus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_SPINDLE_OVR_COARSE_MINUS); }
        private void SpindleOvrPlus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_SPINDLE_OVR_COARSE_PLUS); }
        private void SpindleOvrReset_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_SPINDLE_OVR_RESET); }

        /// <summary>
        /// Rapids has NO fine/coarse pair - grblHAL offers exactly three settings (100 / 50 / 25) as
        /// distinct commands. So "-" and "+" walk that ladder rather than nudging a percentage, which
        /// is why this row behaves differently from the other two. That is the firmware, not the UI.
        /// </summary>
        private void RapidOvrMinus_Click(object sender, RoutedEventArgs e)
        {
            double current = model == null ? 100d : model.RapidsOverride;
            Send(current > 50d ? GrblConstants.CMD_RAPID_OVR_MEDIUM : GrblConstants.CMD_RAPID_OVR_LOW);
        }

        private void RapidOvrPlus_Click(object sender, RoutedEventArgs e)
        {
            double current = model == null ? 100d : model.RapidsOverride;
            Send(current < 50d ? GrblConstants.CMD_RAPID_OVR_MEDIUM : GrblConstants.CMD_RAPID_OVR_RESET);
        }

        private void RapidOvrReset_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_RAPID_OVR_RESET); }
    }
}
