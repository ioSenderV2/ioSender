/*
 * JogBaseControl.xaml.cs - part of CNC Controls library
 *
 * v0.47 / 2026-03-26 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020-2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for JogControl.xaml
    /// </summary>
    public partial class JogBaseControl : UserControl
    {
        private string mode = "G21"; // Metric
        private bool softLimits = false;
        private int distance = 2, feedrate = 2;
        private double limitSwitchesClearance = .5d, position = 0d;
        private bool jogIsContinuous = false;   // last jog was continuous (selected Continuous, or a Shift/Ctrl+Shift speed tier) - so cancel on release
        private System.Windows.Threading.DispatcherTimer holdTimer;  // tap-vs-hold timer for a UI arrow while Continuous is selected
        private string holdCmd;                 // the axis command captured on press, decided on release/timeout
        private bool holdFired;                 // the hold threshold elapsed -> a continuous jog was started
        private const int HoldThresholdMs = 250;
        private KeypressHandler keyboard;
        private static bool keyboardMappingsOk = false;
        private static volatile int jogAxis = -1;
        private static bool _uiSelectionRestored = false;   // restore the saved jog selection only once per run

        private const Key xplus = Key.J, xminus = Key.H, yplus = Key.K, yminus = Key.L, zplus = Key.I, zminus = Key.M, aplus = Key.U, aminus = Key.N;

        public JogBaseControl()
        {
            InitializeComponent();

            // JogData is a shared singleton: keep the existing instance so the distance/speed/Continuous
            // selection survives a second jog panel being built (flyout, view switch, reconnect). Recreating
            // it here reset the selection to defaults, and the once-per-run RestoreUiSelection guard meant it
            // was never restored - so Continuous (and the chosen step/feed) silently reverted.
            if (JogData == null)
                JogData = new JogViewModel();

            Focusable = true;
        }

        public static JogViewModel JogData { get; private set; }
        public string MenuLabel { get { return (string)FindResource("MenuLabel"); } }

        // Arrows-only mode for embedding (e.g. the run bar): hide the Distance/Feed selector column and pull the
        // arrow pad flush left, so only the jog buttons show. The distance/feed selection then comes from the Jog
        // tab / UI Jogging panel via the shared JogData.
        public static readonly DependencyProperty ArrowsOnlyProperty =
            DependencyProperty.Register(nameof(ArrowsOnly), typeof(bool), typeof(JogBaseControl), new PropertyMetadata(false));
        public bool ArrowsOnly
        {
            get { return (bool)GetValue(ArrowsOnlyProperty); }
            set { SetValue(ArrowsOnlyProperty, value); }
        }

        private void Model_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GrblViewModel.MachinePosition) || e.PropertyName == nameof(GrblViewModel.GrblState))
            {
                if ((sender as GrblViewModel).GrblState.State != GrblStates.Jog)
                    jogAxis = -1;
            }
        }

        private void JogControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is GrblViewModel)
            {
                // Mirror the DRO/model rule (Grbl.cs: IsMetric = $13 != 1): only an explicit $13=1 means
                // inches. GetInteger returns -1 when $13 is absent (or not yet loaded) - default that to
                // metric, otherwise the jog panel silently falls back to G20 + imperial step presets while
                // the DRO stays in mm.
                mode = GrblSettings.GetInteger(GrblSetting.ReportInches) == 1 ? "G20" : "G21";
                softLimits = !(GrblInfo.IsGrblHAL && GrblSettings.GetInteger(grblHALSetting.SoftLimitJogging) == 1) && GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) == 1;
                limitSwitchesClearance = GrblSettings.GetDouble(GrblSetting.HomingPulloff);

                JogData.SetMetric(mode == "G21");

                // Restore last session's distance/speed selection (once per run), then start persisting changes.
                if (!_uiSelectionRestored)
                {
                    JogData.RestoreUiSelection();
                    _uiSelectionRestored = true;
                }

                // If the user placed the "UI Jogging" slider panel (main page or flyout), it now
                // provides the distance/feed selection - hide the in-panel radio selectors.
                if (ArrowsOnly || (MainPanelRegistry.LayoutEnabled &&
                     (AppConfig.Settings.Base.MainPanels.Contains("UIJogging") || AppConfig.Settings.Base.LeftPanels.Contains("UIJogging") || AppConfig.Settings.Base.FlyoutItems.Contains("UIJogging"))))
                {
                    selectorPanel.Visibility = Visibility.Collapsed;
                    arrowPanel.Margin = ArrowsOnly ? new Thickness(0) : new Thickness(5, 10, 5, 0);
                }

                // Controller (Xbox) jogging mirrors the on-screen UI jog panel's selected distance/feed (the 2x4
                // grid / sliders). Set unconditionally on every load so the live view-model always has these,
                // regardless of which jog control happened to trip the one-shot keyboardMappingsOk guard first -
                // otherwise ControllerMapper falls back to the keyboard step distance and slow feed.
                var gvm = DataContext as GrblViewModel;
                // Use the retained discrete selection (SelectedDistance/SelectedFeedrate via DistanceIndex/FeedIndex)
                // - the same value the 2x4 grid highlights - NOT Distance/FeedRate, whose Distance is -1 while
                // StepSize is Continuous (keyboard jogging flips it there), which made the controller fall back to
                // the keyboard step size instead of the chosen UI jog distance.
                gvm.JogDistanceProvider = () => JogData.SelectedDistance;
                gvm.JogFeedProvider = () => JogData.SelectedFeedrate;
                gvm.CycleJogFeed = dir => JogData.FeedIndex = JogData.FeedIndex + dir;   // controller bumpers
                gvm.CycleJogDistance = dir => JogData.DistanceIndex = JogData.DistanceIndex + dir;   // assignable controller action

                if (!keyboardMappingsOk)
                {
                    if (softLimits)
                        (DataContext as GrblViewModel).PropertyChanged += Model_PropertyChanged;

                    keyboard = (DataContext as GrblViewModel).Keyboard;

                    keyboardMappingsOk = true;

                    // Keyboard arrows jog through the keyboard's own continuous path (its Slow/Fast/Step config),
                    // independent of the on-screen UI selection - so no cursor-key handlers are registered here.

                    keyboard.AddHandler(xplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogXplus, false);
                    keyboard.AddHandler(xplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);
                    keyboard.AddHandler(xminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogXminus, false);
                    keyboard.AddHandler(xminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);
                    keyboard.AddHandler(yplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogYplus, false);
                    keyboard.AddHandler(yplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);
                    keyboard.AddHandler(yminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogYminus, false);
                    keyboard.AddHandler(yminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);
                    keyboard.AddHandler(zplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogZplus, false);
                    keyboard.AddHandler(zplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);
                    keyboard.AddHandler(zminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogZminus, false);
                    keyboard.AddHandler(zminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);

                    if (GrblInfo.AxisLetterToIndex('A') >= 0)
                    {
                        keyboard.AddHandler(aplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogAplus, false);
                        keyboard.AddHandler(aplus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);
                        keyboard.AddHandler(aminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogAminus, false);
                        keyboard.AddHandler(aminus, ModifierKeys.Control | ModifierKeys.Shift, KeyJogCancel, true);
                    }
                    if (GrblInfo.AxisLetterToIndex('B') >= 0)
                    {
                        keyboard.AddFunction(KeyJogBplus, null);
                        keyboard.AddFunction(KeyJogBminus, null);
                    }
                    if (GrblInfo.AxisLetterToIndex('C') >= 0)
                    {
                        keyboard.AddFunction(KeyJogCplus, null);
                        keyboard.AddFunction(KeyJogCminus, null);
                    }
                    if (GrblInfo.AxisLetterToIndex('U') >= 0)
                    {
                        keyboard.AddFunction(KeyJogUplus, null);
                        keyboard.AddFunction(KeyJogUminus, null);
                    }
                    if (GrblInfo.AxisLetterToIndex('V') >= 0)
                    {
                        keyboard.AddFunction(KeyJogVplus, null);
                        keyboard.AddFunction(KeyJogVminus, null);
                    }
                    if (GrblInfo.AxisLetterToIndex('W') >= 0)
                    {
                        keyboard.AddFunction(KeyJogWplus, null);
                        keyboard.AddFunction(KeyJogWminus, null);
                    }
                    
                    // UI-jog selection shortcuts (NumPad picks the on-screen distance/feed preset; End cancels) -
                    // always registered: UI jogging is always available.
                    keyboard.AddHandler(Key.End, ModifierKeys.None, EndJog, false);

                    keyboard.AddHandler(Key.NumPad0, ModifierKeys.Control, JogStep0);
                    keyboard.AddHandler(Key.NumPad1, ModifierKeys.Control, JogStep1);
                    keyboard.AddHandler(Key.NumPad2, ModifierKeys.Control, JogStep2);
                    keyboard.AddHandler(Key.NumPad3, ModifierKeys.Control, JogStep3);
                    keyboard.AddHandler(Key.NumPad4, ModifierKeys.Control, JogFeed0);
                    keyboard.AddHandler(Key.NumPad5, ModifierKeys.Control, JogFeed1);
                    keyboard.AddHandler(Key.NumPad6, ModifierKeys.Control, JogFeed2);
                    keyboard.AddHandler(Key.NumPad7, ModifierKeys.Control, JogFeed3);

                    keyboard.AddHandler(Key.NumPad2, ModifierKeys.None, FeedDec);
                    keyboard.AddHandler(Key.NumPad4, ModifierKeys.None, StepDec);
                    keyboard.AddHandler(Key.NumPad6, ModifierKeys.None, StepInc);
                    keyboard.AddHandler(Key.NumPad8, ModifierKeys.None, FeedInc);
                }
            }
        }

        private bool KeyJogCancel(Key key)
        {
            if (JogData.StepSize == JogViewModel.JogStep.Continuous || jogIsContinuous)
            {
                while (Comms.com.OutCount != 0) ;
                Comms.com.WriteByte(GrblConstants.CMD_JOG_CANCEL);
            }
            return true;
        }

        private bool KeyJogXplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "Z+" : "X+");

            return true;
        }

        private bool KeyJogXminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "Z-" : "X-");

            return true;
        }

        private bool KeyJogYplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "X-" : "Y+");

            return true;
        }

        private bool KeyJogYminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "X+" : "Y-");

            return true;
        }

        private bool KeyJogZplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating && !GrblInfo.LatheModeEnabled)
                JogCommand("Z+");

            return true;
        }

        private bool KeyJogZminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating && !GrblInfo.LatheModeEnabled)
                JogCommand("Z-");

            return true;
        }

        private bool KeyJogAplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("A+");

            return true;
        }

        private bool KeyJogAminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("A-");

            return true;
        }

        private bool KeyJogBplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("B+");

            return true;
        }

        private bool KeyJogBminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("B-");

            return true;
        }

        private bool KeyJogCplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("C+");

            return true;
        }

        private bool KeyJogCminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("C-");

            return true;
        }

        private bool KeyJogUplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("U+");

            return true;
        }

        private bool KeyJogUminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("U-");

            return true;
        }

        private bool KeyJogVplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("V+");

            return true;
        }

        private bool KeyJogVminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("V-");

            return true;
        }

        private bool KeyJogWplus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("W+");

            return true;
        }

        private bool KeyJogWminus(Key key)
        {
            if (keyboard.CanJog2 && !keyboard.IsRepeating)
                JogCommand("W-");

            return true;
        }

        private void distance_Click(object sender, RoutedEventArgs e)
        {
            distance = int.Parse((string)(sender as RadioButton).Tag);
        }

        private void feedrate_Click(object sender, RoutedEventArgs e)
        {
            feedrate = int.Parse((string)(sender as RadioButton).Tag);
        }

        private bool EndJog(Key key)
        {
            if (!keyboard.IsRepeating && keyboard.IsJogging)
                JogCommand("stop");

            return keyboard.IsJogging;
        }

        private bool JogStep0(Key key)
        {
            JogData.StepSize = JogViewModel.JogStep.Step0;

            return true;
        }

        private bool JogStep1(Key key)
        {
            JogData.StepSize = JogViewModel.JogStep.Step1;

            return true;
        }

        private bool JogStep2(Key key)
        {
            JogData.StepSize = JogViewModel.JogStep.Step2;

            return true;
        }

        private bool JogStep3(Key key)
        {
            JogData.StepSize = JogViewModel.JogStep.Step3;

            return true;
        }

        private bool JogFeed0(Key key)
        {
            JogData.Feed = JogViewModel.JogFeed.Feed0;

            return true;
        }

        private bool JogFeed1(Key key)
        {
            JogData.Feed = JogViewModel.JogFeed.Feed1;

            return true;
        }

        private bool JogFeed2(Key key)
        {
            JogData.Feed = JogViewModel.JogFeed.Feed2;

            return true;
        }

        private bool JogFeed3(Key key)
        {
            JogData.Feed = JogViewModel.JogFeed.Feed3;

            return true;
        }

        private bool FeedDec(Key key)
        {
            JogData.FeedDec();

            return true;
        }
        private bool FeedInc(Key key)
        {
            JogData.FeedInc();

            return true;
        }

        private bool StepDec(Key key)
        {
            JogData.StepDec();

            return true;
        }

        private bool StepInc(Key key)
        {
            JogData.StepInc();

            return true;
        }

        private bool canJog(GrblStates state)
        {
            return state == GrblStates.Idle || state == GrblStates.Jog || state == GrblStates.Tool;
        }

        // How the no-modifier UI-jog path resolves its distance: Auto honours the Continuous checkbox (the
        // default for keyboard/controller callers), Step forces one discrete increment (a tap), Continuous
        // forces a continuous jog (a hold) regardless of the checkbox.
        private enum JogKind { Auto, Step, Continuous }

        private void JogCommand(string cmd)
        {
            JogCommand(cmd, JogKind.Auto);
        }

        private void JogCommand(string cmd, JogKind kind)
        {
            GrblViewModel model = DataContext as GrblViewModel;

            if (cmd == "stop") {
                jogAxis = -1;
                cmd = ((char)GrblConstants.CMD_JOG_CANCEL).ToString();
            }
            else
            {
                int axis = GrblInfo.AxisLetterToIndex(cmd[0]);

                // Unified modifier tiers (panel buttons + keyboard arrows, sharing the keyboard's own Slow/Fast/Step
                // config): Shift = Fast, Ctrl = a single Step increment, Ctrl+Shift = Slow, no modifier = the
                // on-screen selected distance/feed. Fast/Slow are continuous (jog while held); Step is one finite
                // increment. A -1 distance means continuous.
                JogConfig jogcfg = AppConfig.Settings.Jog;
                ModifierKeys mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
                double jogDistance, jogFeed;
                if (mods == ModifierKeys.Control)                               // Ctrl -> single step
                { jogDistance = jogcfg.StepDistance; jogFeed = jogcfg.StepFeedrate; }
                else if (mods == ModifierKeys.Shift)                            // Shift -> fast continuous
                { jogDistance = -1d; jogFeed = jogcfg.FastFeedrate; }
                else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))   // Ctrl+Shift -> slow continuous
                { jogDistance = -1d; jogFeed = jogcfg.SlowFeedrate; }
                else                                                            // no modifier -> selected preset
                {
                    jogFeed = JogData.FeedRate;
                    if (kind == JogKind.Step)                                    // tap while Continuous -> one discrete step
                        jogDistance = JogData.SelectedDistance;
                    else if (kind == JogKind.Continuous)                         // hold while Continuous -> continuous jog
                        jogDistance = -1d;
                    else                                                         // Auto -> honour the Continuous checkbox
                        jogDistance = JogData.Distance;
                }
                jogIsContinuous = jogDistance == -1d;

                var distance = (jogDistance == -1 ? GrblInfo.MaxTravel.Values[axis] : jogDistance) * (cmd[1] == '-' ? -1d : 1d);

                if (softLimits)
                {
                    if (!canJog(model.GrblState.State) || (jogAxis != -1 && axis != jogAxis))
                        return;

                    if (axis != jogAxis || model.GrblState.State != GrblStates.Jog)
                        position = distance + model.MachinePosition.Values[axis];
                    else
                        position += distance;

                    if (GrblInfo.ForceSetOrigin)
                    {
                        if (!GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(axis)))
                        {
                            if (position > 0d)
                                position = 0d;
                            else if (position < (-GrblInfo.MaxTravel.Values[axis] + limitSwitchesClearance))
                                position = (-GrblInfo.MaxTravel.Values[axis] + limitSwitchesClearance);
                        }
                        else
                        {
                            if (position < 0d)
                                position = 0d;
                            else if (position > (GrblInfo.MaxTravel.Values[axis] - limitSwitchesClearance))
                                position = GrblInfo.MaxTravel.Values[axis] - limitSwitchesClearance;
                        }
                    }
                    else
                    {
                        if (position > -limitSwitchesClearance)
                            position = -limitSwitchesClearance;
                        else if (position < -(GrblInfo.MaxTravel.Values[axis] - limitSwitchesClearance))
                            position = -(GrblInfo.MaxTravel.Values[axis] - limitSwitchesClearance);
                    }

                    if (position == 0d)
                        return;

                    jogAxis = axis;

                    cmd = string.Format("$J=G53{0}{1}{2}F{3}", mode, cmd.Substring(0, 1), position.ToInvariantString(), Math.Ceiling(jogFeed).ToInvariantString());
                }
                else
                    cmd = string.Format("$J=G91{0}{1}{2}F{3}", mode, cmd.Substring(0, 1), distance.ToInvariantString(), Math.Ceiling(jogFeed).ToInvariantString());
            }

            model.ExecuteCommand(cmd);
        }

        private void JogButton_JogStart(object sender, EventArgs e)
        {
            string cmd = (string)(sender as JogButton).Tag == "stop" ? "stop" : (string)(sender as JogButton).Content;

            if (cmd == "stop") {
                JogCommand("stop");
                return;
            }

            // "Continuous" is timer-based: a tap (press + quick release) moves one discrete step at the selected
            // distance; pressing and holding past the threshold starts a true continuous jog (cancelled on
            // release). So when Continuous is selected and no modifier is held, defer the move - JogButton_JogEnd
            // turns a quick release into a single step, HoldTimer_Tick turns a held press into continuous motion.
            // Discrete-step mode and the Shift/Ctrl modifier tiers keep their immediate behaviour.
            ModifierKeys mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
            if (mods == ModifierKeys.None && JogData.StepSize == JogViewModel.JogStep.Continuous)
            {
                holdCmd = cmd;
                holdFired = false;
                if (holdTimer == null) {
                    holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldThresholdMs) };
                    holdTimer.Tick += HoldTimer_Tick;
                }
                holdTimer.Start();
            }
            else
                JogCommand(cmd);
        }

        private void HoldTimer_Tick(object sender, EventArgs e)
        {
            holdTimer.Stop();
            holdFired = true;
            JogCommand(holdCmd, JogKind.Continuous);   // held past the threshold -> start the continuous jog
        }

        private void JogButton_JogEnd(object sender, EventArgs e)
        {
            if (holdTimer != null && holdTimer.IsEnabled)   // released before the threshold -> tap -> one discrete step
            {
                holdTimer.Stop();
                JogCommand(holdCmd, JogKind.Step);
                return;
            }

            if (holdFired)   // a hold-started continuous jog -> stop on release
            {
                holdFired = false;
                JogCommand("stop");
                return;
            }

            if (jogIsContinuous)   // modifier-tier continuous (Shift / Ctrl+Shift) - stop on release
                JogCommand("stop");
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            JogCommand((string)(sender as Button).Tag == "stop" ? "stop" : (string)(sender as Button).Content);
        }

        // Centre button: while a jog is running it cancels the jog (the red stop sign shown then); when idle it
        // rapids to a centre point at safe Z (bullseye). Target = the loaded program's XY-bounding-box centre
        // ("stock") if it has moves, else the machine-envelope centre. The move always retracts Z to the top
        // first and stays there (never plunges) - the same safe G53 jog pattern the 3D click-to-jog uses.
        private void CenterButton_Click(object sender, RoutedEventArgs e)
        {
            GrblViewModel model = DataContext as GrblViewModel;

            if (model != null && model.GrblState.State == GrblStates.Jog)
                JogCommand("stop");
            else
                GoToCenter();
        }

        private void GoToCenter()
        {
            GrblViewModel model = DataContext as GrblViewModel;

            if (model == null)
                return;

            if (model.HomedState != HomedState.Homed) {
                model.Message = "Go to centre: home the machine first.";
                return;
            }

            if (model.IsJobRunning ||
                 !(model.GrblState.State == GrblStates.Idle || model.GrblState.State == GrblStates.Jog || model.GrblState.State == GrblStates.Tool)) {
                model.Message = "Go to centre: the machine must be idle.";
                return;
            }

            if (GrblInfo.MaxTravel.X <= 0d || GrblInfo.MaxTravel.Y <= 0d || GrblInfo.MaxTravel.Z <= 0d) {
                model.Message = "Go to centre: set max travel ($130-$132) first.";
                return;
            }

            if (GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) != 1) {
                model.Message = "Go to centre: enable soft limits ($20=1) first.";
                return;
            }

            double mx, my;

            if (ProgramHasMoves(model)) {
                Position wco = new Position(model.WorkPositionOffset, model.UnitFactor);
                ProgramLimits pl = model.ProgramLimits;
                mx = ClampMachine(0, (pl.MinX + pl.MaxX) / 2d + wco.X);   // stock (work) centre -> machine
                my = ClampMachine(1, (pl.MinY + pl.MaxY) / 2d + wco.Y);
                model.Message = "Go to centre of stock at safe Z.";
            } else {
                mx = (ClampMachine(0, double.MaxValue) + ClampMachine(0, double.MinValue)) / 2d;   // envelope mid
                my = (ClampMachine(1, double.MaxValue) + ClampMachine(1, double.MinValue)) / 2d;
                model.Message = "Go to centre of machine at safe Z.";
            }

            double mtop = ClampMachine(2, double.MaxValue);   // fully retracted toward the home/top end (safe Z)
            double zFeed = RapidFeed(2), xyFeed = Math.Max(RapidFeed(0), RapidFeed(1));

            // Retract Z to the top first, then rapid to the centre XY and stay there. grblHAL runs queued $J=
            // jogs strictly FIFO, so the Z retract always completes before the XY traverse - never at depth.
            model.ExecuteCommand(string.Format("$J=G53G21Z{0}F{1}", mtop.ToInvariantString(), Math.Ceiling(zFeed).ToInvariantString()));
            model.ExecuteCommand(string.Format("$J=G53G21X{0}Y{1}F{2}", mx.ToInvariantString(), my.ToInvariantString(), Math.Ceiling(xyFeed).ToInvariantString()));
        }

        // Per-axis max feed ($110-$112), used so the jog moves at rapid speed; falls back to the fast-jog feed.
        private double RapidFeed(int axis)
        {
            double rate = GrblSettings.GetDouble(GrblSetting.MaxFeedRateBase + axis);
            return rate > 0d ? rate : AppConfig.Settings.Jog.FastFeedrate;
        }

        // Clamp an absolute machine-axis target to the safe travel range (mirrors the jog limiter above).
        private double ClampMachine(int axis, double pos)
        {
            double maxTravel = GrblInfo.MaxTravel.Values[axis];
            double clearance = GrblSettings.GetDouble(GrblSetting.HomingPulloff);

            if (GrblInfo.ForceSetOrigin) {
                if (!GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(axis))) {
                    if (pos > 0d) pos = 0d;
                    else if (pos < -maxTravel + clearance) pos = -maxTravel + clearance;
                } else {
                    if (pos < 0d) pos = 0d;
                    else if (pos > maxTravel - clearance) pos = maxTravel - clearance;
                }
            } else {
                if (pos > -clearance) pos = -clearance;
                else if (pos < -(maxTravel - clearance)) pos = -(maxTravel - clearance);
            }

            return pos;
        }

        // A loaded program "has moves" only when its bounding box has a real extent on some axis (an unloaded
        // program is all-NaN, a moveless one all-zero). Mirrors LimitsControl's "Program limits" test.
        private static bool ProgramHasMoves(GrblViewModel model)
        {
            if (!GCode.File.IsLoaded)
                return false;

            ProgramLimits pl = model.ProgramLimits;
            for (int i = 0; i < GrblInfo.NumAxes; i++) {
                double min = pl.MinValues[i], max = pl.MaxValues[i];
                if (!double.IsNaN(min) && !double.IsNaN(max) && max != min)
                    return true;
            }
            return false;
        }
    }

    internal class ArrayValues<T> : ViewModelBase
    {
        private T[] arr = new T[4];

        public int Length { get { return arr.Length; } }

        public T this[int i]
        {
            get { return arr[i]; }
            set
            {
                if (!value.Equals(arr[i]))
                {
                    arr[i] = value;
                    OnPropertyChanged(i.ToString());
                }
            }
        }
    }

    public class JogViewModel : ViewModelBase
    {
        public enum JogStep
        {
            Step0 = 0,
            Step1,
            Step2,
            Step3,
            Continuous
        }
        public enum JogFeed
        {
            Feed0 = 0,
            Feed1,
            Feed2,
            Feed3
        }

        JogStep _jogStep = JogStep.Step1;
        JogFeed _jogFeed = JogFeed.Feed1;
        JogStep _lastStep = JogStep.Step1;      // last discrete step, retained while in Continuous mode
        private bool _metric = true;
        private double[] _distance = new double[5];
        private int[] _feedRate = new int[4];
        private bool _persistSelection = false;   // set after the initial restore so we don't save defaults

        // Restore the distance/speed selection saved at the end of the previous session (if the option is on),
        // then start persisting subsequent changes. Called once from JogControl_Loaded after SetMetric.
        public void RestoreUiSelection()
        {
            if (AppConfig.Settings.Base != null && AppConfig.Settings.Base.Jog.KeepUiJogSelection)
            {
                _lastStep = (JogStep)System.Math.Max(0, System.Math.Min(3, Properties.Settings.Default.UiJogStep));
                Feed = (JogFeed)System.Math.Max(0, System.Math.Min(3, Properties.Settings.Default.UiJogFeed));
                StepSize = Properties.Settings.Default.UiJogContinuous ? JogStep.Continuous : _lastStep;
            }
            _persistSelection = true;

            // Enabling the option in Settings:App after a selection was already made should capture the current
            // selection right away - otherwise nothing is saved until the next change (too late on restart).
            if (AppConfig.Settings.Base != null)
                AppConfig.Settings.Base.Jog.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(JogConfig.KeepUiJogSelection) && AppConfig.Settings.Base.Jog.KeepUiJogSelection)
                        PersistSelection();
                };
        }

        // Persist the current selection immediately (user settings store - isolated from the Base config file).
        private void PersistSelection()
        {
            if (!_persistSelection || AppConfig.Settings.Base == null || !AppConfig.Settings.Base.Jog.KeepUiJogSelection)
                return;
            Properties.Settings.Default.UiJogStep = DistanceIndex;
            Properties.Settings.Default.UiJogContinuous = Continuous;
            Properties.Settings.Default.UiJogFeed = (int)_jogFeed;
            Properties.Settings.Default.Save();
        }

        public void SetMetric(bool on)
        {
            _metric = on;
            for (int i = 0; i < _feedRate.Length; i++)
            {
                _distance[i] = on ? AppConfig.Settings.JogUiMetric.Distance[i] : AppConfig.Settings.JogUiImperial.Distance[i];
                _feedRate[i] = on ? AppConfig.Settings.JogUiMetric.Feedrate[i] : AppConfig.Settings.JogUiImperial.Feedrate[i];
                OnPropertyChanged("Feedrate" + i.ToString());
                OnPropertyChanged("Distance" + i.ToString());
            }
            _distance[(int)JogStep.Continuous] = -1d;
            OnPropertyChanged(nameof(SelectedDistance));
            OnPropertyChanged(nameof(SelectedFeedrate));
            OnPropertyChanged(nameof(SelectedDistanceText));
            OnPropertyChanged(nameof(SelectedFeedrateText));
            OnPropertyChanged(nameof(DistanceHeader));
            OnPropertyChanged(nameof(SpeedHeader));
        }

        public JogStep StepSize { get { return _jogStep; } set { _jogStep = value; OnPropertyChanged(); OnPropertyChanged(nameof(Distance)); OnPropertyChanged(nameof(DistanceIndex)); OnPropertyChanged(nameof(Continuous)); OnPropertyChanged(nameof(SelectedDistance)); OnPropertyChanged(nameof(SelectedDistanceText)); PersistSelection(); } }
        public double Distance { get { return _distance[(int)_jogStep]; } }
        public JogFeed Feed { get { return _jogFeed; } set { _jogFeed = value; OnPropertyChanged(); OnPropertyChanged(nameof(FeedRate)); OnPropertyChanged(nameof(FeedIndex)); OnPropertyChanged(nameof(SelectedFeedrate)); OnPropertyChanged(nameof(SelectedFeedrateText)); PersistSelection(); } }
        public double FeedRate { get { return _feedRate[(int)_jogFeed]; } }

        public int Feedrate0 { get { return _feedRate[0]; } }
        public int Feedrate1 { get { return _feedRate[1]; } }
        public int Feedrate2 { get { return _feedRate[2]; } }
        public int Feedrate3 { get { return _feedRate[3]; } }

        public double Distance0 { get { return _distance[0]; } }
        public double Distance1 { get { return _distance[1]; } }
        public double Distance2 { get { return _distance[2]; } }
        public double Distance3 { get { return _distance[3]; } }

        // Slider-friendly accessors: a 0-3 selector over the 4 presets, a Continuous toggle for distance,
        // and read-only display values for the selected level (values are edited on the Settings:App tab).
        public int DistanceIndex
        {
            get { return _jogStep == JogStep.Continuous ? (int)_lastStep : (int)_jogStep; }
            set
            {
                _lastStep = (JogStep)System.Math.Max(0, System.Math.Min(3, value));
                // The UI jog panel presents distance and Continuous as separate controls, so changing the
                // distance must not silently turn Continuous off. While Continuous is on, just update the
                // stored finite preset (the distance used once Continuous is unchecked) and refresh the
                // readouts; only leave Continuous when the user explicitly unchecks it.
                if (_jogStep == JogStep.Continuous)
                {
                    OnPropertyChanged(nameof(DistanceIndex));
                    OnPropertyChanged(nameof(SelectedDistance));
                    OnPropertyChanged(nameof(SelectedDistanceText));
                    PersistSelection();
                }
                else
                    StepSize = _lastStep;
            }
        }
        public bool Continuous
        {
            get { return _jogStep == JogStep.Continuous; }
            set { StepSize = value ? JogStep.Continuous : _lastStep; }
        }
        public int FeedIndex
        {
            get { return (int)_jogFeed; }
            set { Feed = (JogFeed)System.Math.Max(0, System.Math.Min(3, value)); }
        }
        public double SelectedDistance { get { return _distance[DistanceIndex]; } }
        public int SelectedFeedrate { get { return _feedRate[FeedIndex]; } }
        // Read-only readouts shown between the slider buttons - bare values; the unit lives in the header.
        public string SelectedDistanceText { get { return SelectedDistance.ToString("0.0###"); } }
        public string SelectedFeedrateText { get { return SelectedFeedrate.ToString(); } }
        public bool IsMetric { get { return _metric; } }
        public string DistanceHeader { get { return "Distance (" + (_metric ? "mm" : "in") + ")"; } }
        public string SpeedHeader { get { return "Speed (" + (_metric ? "mm/min" : "in/min") + ")"; } }

        public void StepInc()
        {
            if (StepSize != JogStep.Continuous)
                StepSize += 1;
        }
        public void StepDec()
        {
            if (StepSize != JogStep.Step0)
                StepSize -= 1;
        }

        public void FeedInc()
        {
            if (Feed != JogFeed.Feed3)
                Feed += 1;
        }

        public void FeedDec()
        {
            if (Feed != JogFeed.Feed0)
                Feed -= 1;
        }
    }

    // Backs the "Keyboard Jogging" panel: a single selector that sets the DEFAULT continuous keyboard-jog
    // speed (Slow or Fast) used while an arrow key is held with no modifier; Shift then jogs at the other
    // speed. The slider position IS the setting (Config.Jog.DefaultSpeedFast), pushed live into the handler.
    // Bare value (the feed rate) is shown; the unit lives in the header and follows the UI jog panel.
    public class KeyboardJogViewModel : ViewModelBase
    {
        private readonly KeypressHandler keyboard;

        public KeyboardJogViewModel(KeypressHandler keyboard)
        {
            this.keyboard = keyboard;
        }

        public int SpeedIndex
        {
            get { return AppConfig.Settings.Jog.DefaultSpeedFast ? 1 : 0; }    // 0 = Slow, 1 = Fast
            set
            {
                bool fast = value >= 1;
                AppConfig.Settings.Jog.DefaultSpeedFast = fast;
                if (keyboard != null)
                    keyboard.DefaultSpeedFast = fast;       // take effect immediately
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedFeedrateText));
            }
        }

        private double SelectedFeedrate
        {
            get { var j = AppConfig.Settings.Jog; return j.DefaultSpeedFast ? j.FastFeedrate : j.SlowFeedrate; }
        }

        public string SelectedFeedrateText { get { return SelectedFeedrate.ToString("0.###"); } }

        // The two selectable speeds, for the side-panel grid (the flyout uses the slider + SelectedFeedrateText).
        public string SlowText { get { return AppConfig.Settings.Jog.SlowFeedrate.ToString("0.###"); } }
        public string FastText { get { return AppConfig.Settings.Jog.FastFeedrate.ToString("0.###"); } }

        public string SpeedHeader
        {
            get
            {
                bool metric = JogBaseControl.JogData == null || JogBaseControl.JogData.IsMetric;
                return "Default speed (" + (metric ? "mm/min" : "in/min") + ")";
            }
        }
    }
}
