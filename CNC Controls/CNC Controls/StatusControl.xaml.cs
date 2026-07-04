/*
 * StatusControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.36 / 2021-11-01 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2021, Io Engineering (Terje Io)
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{

    public partial class StatusControl : UserControl
    {
        private Brush HomeButtonColor;
        private enum StatusButton
        {
            Home,
            Unlock,
            Reset,
            Check
        }

        public StatusControl()
        {
            InitializeComponent();

            HomeButtonColor = btnHome.Background;

            btnHome.Tag = StatusButton.Home;
            btnReset.Tag = StatusButton.Reset;
            btnUnlock.Tag = StatusButton.Unlock;
            chkCheckMode.Tag = StatusButton.Check;
        }

        // The control has two parts - the state/check row and the Home/Unlock/Reset button row. These can be
        // shown independently so a host can place them separately (e.g. the fixed bottom run-control bar puts
        // State/Check up by the MDI line and the buttons down on the button row). Both default true = the
        // normal stacked control used in the side panels (non-XL), unchanged.
        public static readonly DependencyProperty ShowStateProperty = DependencyProperty.Register(
            nameof(ShowState), typeof(bool), typeof(StatusControl), new PropertyMetadata(true, OnPartsChanged));

        public bool ShowState
        {
            get { return (bool)GetValue(ShowStateProperty); }
            set { SetValue(ShowStateProperty, value); }
        }

        public static readonly DependencyProperty ShowButtonsProperty = DependencyProperty.Register(
            nameof(ShowButtons), typeof(bool), typeof(StatusControl), new PropertyMetadata(true, OnPartsChanged));

        public bool ShowButtons
        {
            get { return (bool)GetValue(ShowButtonsProperty); }
            set { SetValue(ShowButtonsProperty, value); }
        }

        private static void OnPartsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (StatusControl)d;
            c.stateRow.Visibility = c.ShowState ? Visibility.Visible : Visibility.Collapsed;
            c.buttonRow.Visibility = c.ShowButtons ? Visibility.Visible : Visibility.Collapsed;
            // no top gap above the button row when the state row above it is hidden
            c.buttonRow.Margin = c.ShowState ? new Thickness(0, 4, 0, 0) : new Thickness(0);
        }

        void btn_Click(object sender, RoutedEventArgs e)
        {
            switch ((StatusButton)((Control)sender).Tag)
            {
                case StatusButton.Reset:
                    var model = (DataContext as GrblViewModel);
                    if (model.GrblState.State == GrblStates.Alarm && model.GrblState.Substate == 10 && model.Signals.Value.HasFlag(Signals.EStop))
                        MessageBox.Show((string)FindResource("ClearEStop"), "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    else
                        Grbl.Reset();
                    break;

                case StatusButton.Unlock:
                    (DataContext as GrblViewModel).ExecuteCommand(GrblConstants.CMD_UNLOCK);
                    break;

                case StatusButton.Home:
                    // ((Control)sender).Background = Brushes.LightSkyBlue;
                    (DataContext as GrblViewModel).ExecuteCommand(GrblConstants.CMD_HOMING);
                    break;

                case StatusButton.Check:
                    GrblStates state = (DataContext as GrblViewModel).GrblState.State;
                    if(state == GrblStates.Check && (sender as CheckBox).IsChecked == false)
                        Grbl.Reset();
                    else if (state == GrblStates.Idle && (sender as CheckBox).IsChecked == true)
                        (DataContext as GrblViewModel).ExecuteCommand(GrblConstants.CMD_CHECK);
                    break;
            }
        }

        // Right-click the State field -> escalating recovery. Unlock lights up only in Alarm (Reset/Home
        // are valid from any state). Reset always available when connected.
        void stateMenu_Opened(object sender, RoutedEventArgs e)
        {
            var model = DataContext as GrblViewModel;
            bool connected = model != null;
            miReset.IsEnabled = connected;
            miUnlock.IsEnabled = connected && model.GrblState.State == GrblStates.Alarm;
            miHome.IsEnabled = connected;
        }

        void stateMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender == miReset)
                RecoverTo(0);       // Reset
            else if (sender == miUnlock)
                RecoverTo(1);       // Reset + Unlock
            else if (sender == miHome)
                RecoverTo(2);       // Reset + Unlock + Home
        }

        // Escalating recovery: each level performs the lower ones first. The soft reset warm-restarts the
        // controller, so Unlock/Home are deferred until it is back and the prior step has settled.
        private void RecoverTo(int level)
        {
            var model = DataContext as GrblViewModel;
            if (model == null)
                return;

            if (model.GrblState.State == GrblStates.Alarm && model.GrblState.Substate == 10 && model.Signals.Value.HasFlag(Signals.EStop))
            {
                MessageBox.Show((string)FindResource("ClearEStop"), "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            Grbl.Reset();
            if (level < 1)
                return;

            RunAfter(1000, () => {
                model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
                if (level >= 2)
                    RunAfter(300, () => model.ExecuteCommand(GrblConstants.CMD_HOMING));
            });
        }

        private static void RunAfter(int ms, System.Action action)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); action(); };
            t.Start();
        }
    }
}
