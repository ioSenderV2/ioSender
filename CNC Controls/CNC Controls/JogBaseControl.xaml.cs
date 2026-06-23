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
        private KeypressHandler keyboard;
        private static bool keyboardMappingsOk = false;
        private static volatile int jogAxis = -1;
        private static bool _uiSelectionRestored = false;   // restore the saved jog selection only once per run

        private const Key xplus = Key.J, xminus = Key.H, yplus = Key.K, yminus = Key.L, zplus = Key.I, zminus = Key.M, aplus = Key.U, aminus = Key.N;

        public JogBaseControl()
        {
            InitializeComponent();

            JogData = new JogViewModel();

            Focusable = true;
        }

        public static JogViewModel JogData { get; private set; }
        public string MenuLabel { get { return (string)FindResource("MenuLabel"); } }

        private void JogData_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(JogViewModel.Distance):
                    if (AppConfig.Settings.Jog.Mode == JogConfig.JogMode.UI || (AppConfig.Settings.Jog.LinkStepJogToUI && JogData.StepSize != JogViewModel.JogStep.Step3))
                        (DataContext as GrblViewModel).JogStep = JogData.Distance;
                    break;

                case nameof(JogViewModel.FeedRate):
                    if (AppConfig.Settings.Jog.LinkStepJogToUI)
                        SyncKeyboardFeedToUI();
                    break;
            }
        }

        // "Link distance and speed to UI jogging": keyboard held-arrow jog uses the UI-selected feed too,
        // so all keyboard jog feedrates track the on-screen selection (step distance links via JogStep above).
        private void SyncKeyboardFeedToUI()
        {
            var gvm = DataContext as GrblViewModel;
            if (gvm == null || gvm.Keyboard == null || JogData == null)
                return;

            double fr = JogData.FeedRate;
            for (int i = 0; i < gvm.Keyboard.JogFeedrates.Length; i++)
                gvm.Keyboard.JogFeedrates[i] = fr;
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
                mode = GrblSettings.GetInteger(GrblSetting.ReportInches) == 0 ? "G21" : "G20";
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
                if (MainPanelRegistry.LayoutEnabled &&
                     (AppConfig.Settings.Base.MainPanels.Contains("UIJogging") || AppConfig.Settings.Base.LeftPanels.Contains("UIJogging") || AppConfig.Settings.Base.FlyoutItems.Contains("UIJogging")))
                {
                    selectorPanel.Visibility = Visibility.Collapsed;
                    arrowPanel.Margin = new Thickness(5, 10, 5, 0);
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

                if (!keyboardMappingsOk)
                {
                    if (!GrblInfo.HasFirmwareJog || AppConfig.Settings.Jog.LinkStepJogToUI)
                        JogData.PropertyChanged += JogData_PropertyChanged;

                    if (softLimits)
                        (DataContext as GrblViewModel).PropertyChanged += Model_PropertyChanged;

                    keyboard = (DataContext as GrblViewModel).Keyboard;

                    if (AppConfig.Settings.Jog.LinkStepJogToUI)
                        SyncKeyboardFeedToUI();   // initial sync; kept current by JogData_PropertyChanged

                    keyboardMappingsOk = true;

                    if (AppConfig.Settings.Jog.Mode == JogConfig.JogMode.UI)
                    {
                        keyboard.AddHandler(Key.PageUp, ModifierKeys.None, CursorJogZplus, false);
                        keyboard.AddHandler(Key.PageUp, ModifierKeys.None, KeyJogCancel, true);
                        keyboard.AddHandler(Key.PageDown, ModifierKeys.None, CursorJogZminus, false);
                        keyboard.AddHandler(Key.PageDown, ModifierKeys.None, KeyJogCancel, true);
                        keyboard.AddHandler(Key.Left, ModifierKeys.None, CursorJogXminus, false);
                        keyboard.AddHandler(Key.Left, ModifierKeys.None, KeyJogCancel, true);
                        keyboard.AddHandler(Key.Up, ModifierKeys.None, CursorJogYplus, false);
                        keyboard.AddHandler(Key.Up, ModifierKeys.None, KeyJogCancel, true);
                        keyboard.AddHandler(Key.Right, ModifierKeys.None, CursorJogXplus, false);
                        keyboard.AddHandler(Key.Right, ModifierKeys.None, KeyJogCancel, true);
                        keyboard.AddHandler(Key.Down, ModifierKeys.None, CursorJogYminus, false);
                        keyboard.AddHandler(Key.Down, ModifierKeys.None, KeyJogCancel, true);
                    }

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
                    
                    if (AppConfig.Settings.Jog.Mode != JogConfig.JogMode.Keypad)
                    {
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
        }

        private bool KeyJogCancel(Key key)
        {
            if (JogData.StepSize == JogViewModel.JogStep.Continuous)
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

        private bool CursorJogXplus(Key key)
        {
            if (keyboard.CanJog && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "Z+" : "X+");

            return true;
        }

        private bool CursorJogXminus(Key key)
        {
            if (keyboard.CanJog && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "Z-" : "X-");

            return true;
        }

        private bool CursorJogYplus(Key key)
        {
            if (keyboard.CanJog && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "X-" : "Y+");

            return true;
        }

        private bool CursorJogYminus(Key key)
        {
            if (keyboard.CanJog && !keyboard.IsRepeating)
                JogCommand(GrblInfo.LatheModeEnabled ? "X+" : "Y-");

            return true;
        }

        private bool CursorJogZplus(Key key)
        {
            if (keyboard.CanJog && !keyboard.IsRepeating && !GrblInfo.LatheModeEnabled)
                JogCommand("Z+");

            return true;
        }

        private bool CursorJogZminus(Key key)
        {
            if (keyboard.CanJog && !keyboard.IsRepeating && !GrblInfo.LatheModeEnabled)
                JogCommand("Z-");

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

        private void JogCommand(string cmd)
        {
            GrblViewModel model = DataContext as GrblViewModel;

            if (cmd == "stop") {
                jogAxis = -1;
                cmd = ((char)GrblConstants.CMD_JOG_CANCEL).ToString();
            }
            else
            {
                int axis = GrblInfo.AxisLetterToIndex(cmd[0]);

                var distance = (JogData.Distance == -1 ? GrblInfo.MaxTravel.Values[axis] : JogData.Distance) * (cmd[1] == '-' ? -1d : 1d);

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

                    cmd = string.Format("$J=G53{0}{1}{2}F{3}", mode, cmd.Substring(0, 1), position.ToInvariantString(), Math.Ceiling(JogData.FeedRate).ToInvariantString());
                }
                else
                    cmd = string.Format("$J=G91{0}{1}{2}F{3}", mode, cmd.Substring(0, 1), distance.ToInvariantString(), Math.Ceiling(JogData.FeedRate).ToInvariantString());
            }

            model.ExecuteCommand(cmd);
        }

        private void JogButton_JogStart(object sender, EventArgs e)
        {
            JogCommand((string)(sender as JogButton).Tag == "stop" ? "stop" : (string)(sender as JogButton).Content);
        }

        private void JogButton_JogEnd(object sender, EventArgs e)
        {
            if(JogData.Distance == -1)
                JogCommand("stop");
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            JogCommand((string)(sender as Button).Tag == "stop" ? "stop" : (string)(sender as Button).Content);
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
            set { _lastStep = (JogStep)System.Math.Max(0, System.Math.Min(3, value)); StepSize = _lastStep; }
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
