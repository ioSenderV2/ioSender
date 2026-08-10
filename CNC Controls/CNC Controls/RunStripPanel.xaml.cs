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
        // Distance, Speed and Continuous all read the SHARED JogViewModel (JogBaseControl.JogData),
        // which is the same selection the Jog tab and the keyboard handler use. Keeping a private
        // copy here is how two surfaces end up disagreeing about how far the next jog goes.
        // The four presets themselves are the ones configured in Settings > Jogging.
        private const int JogPresetCount = 4;

        private static readonly Brush LampOff = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
        private static readonly Brush LampOn = Brushes.Red;

        // The active keyboard-speed choice. Same pale-green highlight the desktop bar already uses
        // for "this is the one in effect".
        private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xC9));
        private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromRgb(0x66, 0xA6, 0x6A));

        // letter -> which Signals bit it reports. Built once; see the spec for the full letter key.
        private readonly List<KeyValuePair<TextBlock, Signals>> lamps = new List<KeyValuePair<TextBlock, Signals>>();

        private GrblViewModel model;

        // True while the radios are being set FROM machine state, so the Checked handler does not
        // turn a display refresh into an M3/M4/M5.
        private bool syncing;

        private TextBlock lampProbe, lampToolSetter;

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
            // H and S are the controller's FEED HOLD and CYCLE START input pins - the physical panel
            // buttons - so they light while the button is pressed, not while the machine is holding.
            AddLamp(rowProbes, "H", Signals.Hold);
            AddLamp(rowProbes, "S", Signals.CycleStart);

            // P and T are the two selectable probe inputs rather than two signals: grblHAL routes ONE
            // at a time, so both show the same Probe bit and the unselected one is struck through.
            lampProbe = AddProbeLetter("P", 0, "Standard probe input");
            lampToolSetter = AddProbeLetter("T", 1, "Tool setter input");

            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>
        /// A probe-input letter. Carries the live Probe signal like any lamp, plus a strikethrough
        /// when it is not the selected input, and a double-click to select it.
        /// </summary>
        private TextBlock AddProbeLetter(string letter, int probeId, string tip)
        {
            var block = new TextBlock
            {
                Text = letter,
                Style = (Style)FindResource("ProbeLetterStyle"),
                ToolTip = tip + " - double-click to route the probe here.",
                Tag = probeId
            };
            block.MouseLeftButtonDown += ProbeLetter_Click;
            rowProbes.Children.Add(block);
            lamps.Add(new KeyValuePair<TextBlock, Signals>(block, Signals.Probe));
            return block;
        }

        private void ProbeLetter_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || model == null)
                return;

            var block = sender as TextBlock;
            if (block == null || !(block.Tag is int))
                return;

            int probeId = (int)block.Tag;
            if (probeId == model.Probe)
                return;                     // already the selected input - nothing to switch

            // GrblCommand.ProbeSelect is the controller-side macro that actually routes the input;
            // the model's own Probe property follows from the parser state it reports back.
            model.ExecuteCommand(string.Format(GrblCommand.ProbeSelect, probeId));
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
                case nameof(GrblViewModel.Probe):          UpdateProbeSelection(); break;
                case nameof(GrblViewModel.FeedOverride):   UpdateOverrides(); break;
                case nameof(GrblViewModel.RapidsOverride): UpdateOverrides(); break;
                case nameof(GrblViewModel.RPMOverride):    UpdateOverrides(); break;
                case nameof(GrblViewModel.SpindleState):  UpdateSpindleDirection(); break;
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
            UpdateSpindleDirection();
        }

        private void UpdateSignals()
        {
            Signals asserted = model == null ? Signals.Off : model.Signals.Value;
            foreach (var lamp in lamps)
                lamp.Key.Foreground = (asserted & lamp.Value) != 0 ? LampOn : LampOff;

            UpdateProbeSelection();
        }

        /// <summary>
        /// Strike through whichever probe input is NOT currently routed. Reading the selection from
        /// the model rather than remembering the last click means a probe change made anywhere else -
        /// a macro, the probing tab, an MDI G65 - shows up here too.
        /// </summary>
        private void UpdateProbeSelection()
        {
            if (model == null || lampProbe == null)
                return;

            int selected;
            try { selected = model.Probe; }
            catch { return; }               // no parser state reported yet

            lampProbe.TextDecorations = selected == 0 ? null : TextDecorations.Strikethrough;
            lampToolSetter.TextDecorations = selected == 1 ? null : TextDecorations.Strikethrough;
        }

        /// <summary>
        /// Feed rate and Spindle show the LIVE value; Rapids shows its percentage, because there is no
        /// "current rapid rate" to report - only the setting. A box the operator is typing into is left
        /// alone, or the machine's next status report would overwrite it mid-keystroke.
        /// </summary>
        private void UpdateOverrides()
        {
            if (model == null)
                return;

            // Read-only: the feed rate is whatever the program asked for, and there is no command to
            // set it absolutely - the override buttons are the only lever, which is exactly what they do.
            txtFeedRate.Text = ((int)model.FeedRate).ToString();

            lblRapidOvr.Text = ((int)model.RapidsOverride) + "%";

            if (!txtSpindleRpm.IsKeyboardFocusWithin)
                txtSpindleRpm.Text = ((int)model.ProgrammedRPM).ToString();
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
            var jog = JogBaseControl.JogData;
            if (jog != null)
            {
                lblJogDistance.Text = jog.SelectedDistanceText;
                lblJogSpeed.Text = jog.SelectedFeedrateText;
                chkContinuous.IsChecked = jog.Continuous;
            }
            UpdateKbdSpeeds();
        }

        /// <summary>
        /// The two default keyboard-jog speeds, with the active one highlighted. Values come from the
        /// controller's own slow/fast jog feedrates rather than being hardcoded, so changing them in
        /// settings changes what this offers.
        /// </summary>
        private void UpdateKbdSpeeds()
        {
            var rates = model == null || model.Keyboard == null ? null : model.Keyboard.JogFeedrates;
            if (rates == null || rates.Length <= (int)JogMode.Fast)
                return;

            btnKbdSlow.Content = ((int)rates[(int)JogMode.Slow]).ToString();
            btnKbdFast.Content = ((int)rates[(int)JogMode.Fast]).ToString();

            bool fast = model.Keyboard.DefaultSpeedFast;
            Highlight(btnKbdSlow, !fast);
            Highlight(btnKbdFast, fast);
        }

        private static void Highlight(Button button, bool active)
        {
            button.Background = active ? SelectedBrush : Brushes.Transparent;
            button.BorderBrush = active ? SelectedBorder : Brushes.Transparent;
            button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        }

        private void KbdSlow_Click(object sender, RoutedEventArgs e) { SetKbdSpeed(false); }
        private void KbdFast_Click(object sender, RoutedEventArgs e) { SetKbdSpeed(true); }

        private void SetKbdSpeed(bool fast)
        {
            if (model != null && model.Keyboard != null)
            {
                model.Keyboard.DefaultSpeedFast = fast;
                UpdateKbdSpeeds();
            }
        }

        // ---------------------------------------------------------------- jogging

        private void JogDistMinus_Click(object sender, RoutedEventArgs e) { StepPreset(true, -1); }
        private void JogDistPlus_Click(object sender, RoutedEventArgs e) { StepPreset(true, 1); }
        private void JogSpeedMinus_Click(object sender, RoutedEventArgs e) { StepPreset(false, -1); }
        private void JogSpeedPlus_Click(object sender, RoutedEventArgs e) { StepPreset(false, 1); }

        /// <summary>
        /// Move the shared jog selection one preset up or down. There are four of each, configured in
        /// Settings > Jogging - the buttons choose among THOSE rather than scaling a number, so this
        /// panel and the Jog tab can never show different values.
        /// </summary>
        private void StepPreset(bool distance, int direction)
        {
            var jog = JogBaseControl.JogData;
            if (jog == null)
                return;

            int index = (distance ? jog.DistanceIndex : jog.FeedIndex) + direction;
            index = Math.Max(0, Math.Min(JogPresetCount - 1, index));

            if (distance)
                jog.DistanceIndex = index;
            else
                jog.FeedIndex = index;

            UpdateJog();
        }

        private void Continuous_Changed(object sender, RoutedEventArgs e)
        {
            var jog = JogBaseControl.JogData;
            if (jog != null && jog.Continuous != (chkContinuous.IsChecked == true))
                jog.Continuous = chkContinuous.IsChecked == true;
        }

        // ---------------------------------------------------------------- overrides

        // Single realtime bytes, straight to the controller - never through the streamer queue.
        private static void Send(byte command)
        {
            if (Comms.com != null)
                Comms.com.WriteByte(command);
        }

        // COARSE is the 10% step in grblHAL (fine is 1%), which is what "- and + go down and up by
        // 10%" asks for on both of these rows.
        private void FeedOvrMinus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_FEED_OVR_COARSE_MINUS); }
        private void FeedOvrPlus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_FEED_OVR_COARSE_PLUS); }

        private void SpindleOvrMinus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_SPINDLE_OVR_COARSE_MINUS); }
        private void SpindleOvrPlus_Click(object sender, RoutedEventArgs e) { Send(GrblConstants.CMD_SPINDLE_OVR_COARSE_PLUS); }

        // ---------------------------------------------------------------- typed targets

        /// <summary>
        /// Reflect the direction the MACHINE reports, not the last button pressed - an M3 typed at the
        /// MDI or a direction change inside the running program has to move these too. `syncing`
        /// suppresses the Checked handler while we do it, or updating the display would send a command.
        /// </summary>
        private void UpdateSpindleDirection()
        {
            if (model == null)
                return;

            // A spindle the firmware says cannot reverse has no CCW to offer, so the option is not
            // shown at all rather than shown-and-refused. Re-evaluated here rather than once at
            // startup because GrblInfo only knows this after the $I handshake, which lands later.
            rbSpindleCCW.Visibility = GrblInfo.HasReversableSpindle ? Visibility.Visible : Visibility.Collapsed;

            SpindleState state = model.SpindleState.Value;
            syncing = true;
            rbSpindleCW.IsChecked = (state & SpindleState.CW) != 0;
            rbSpindleCCW.IsChecked = (state & SpindleState.CCW) != 0;
            rbSpindleOff.IsChecked = (state & (SpindleState.CW | SpindleState.CCW)) == 0;
            syncing = false;
        }

        private void SpindleDir_Checked(object sender, RoutedEventArgs e)
        {
            if (syncing || model == null)
                return;
            var command = (sender as System.Windows.Controls.Primitives.ToggleButton)?.Tag as string;
            if (!string.IsNullOrEmpty(command))
                model.ExecuteCommand(command);
        }

        private void SpindleRpm_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { ApplySpindleRpm(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { UpdateOverrides(); System.Windows.Input.Keyboard.ClearFocus(); }
        }

        private void SpindleRpm_LostFocus(object sender, RoutedEventArgs e) { UpdateOverrides(); }

        /// <summary>
        /// Spindle speed IS directly settable - an S word sets the programmed RPM - so unlike feed
        /// this needs no override arithmetic and works whether or not the machine is moving.
        /// </summary>
        private void ApplySpindleRpm()
        {
            double rpm;
            if (model == null || !double.TryParse(txtSpindleRpm.Text, out rpm) || rpm < 0d)
            {
                UpdateOverrides();
                return;
            }
            model.ExecuteCommand("S" + ((int)rpm));
        }

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
