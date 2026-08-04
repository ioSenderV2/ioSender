/*
 * KbdDefaultSpeedControl.xaml.cs - part of CNC Controls library
 *
 * Run-bar readout + toggle for the keyboard jog panel's default continuous-jog speed. Wraps the SAME
 * KeyboardJogViewModel the Keyboard Jogging panel (KbdJogGridControl) uses, rather than reading/writing
 * Config.Jog directly - its SpeedIndex setter both persists the choice and pushes it into the live
 * KeypressHandler.DefaultSpeedFast so a click here takes effect immediately, same as the real panel.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public partial class KbdDefaultSpeedControl : UserControl
    {
        private KeyboardJogViewModel kbd;

        public KbdDefaultSpeedControl()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (kbd == null && DataContext is GrblViewModel gvm)
                kbd = new KeyboardJogViewModel(gvm.Keyboard);
            pnlRoot.DataContext = kbd;
        }

        private void pnlRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (kbd != null)
                kbd.SpeedIndex = kbd.SpeedIndex == 0 ? 1 : 0;
        }
    }
}
