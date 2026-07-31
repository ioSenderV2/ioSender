/*
 * KbdDefaultSpeedControl.xaml.cs - part of CNC Controls library
 *
 * Run-bar readout + toggle for the keyboard jog panel's default continuous-jog speed
 * (Config.Jog.DefaultSpeedFast). Binds directly to the shared JogConfig object, same idiom
 * JogPresetSelector uses for its own shared model (set DataContext on Loaded, not inherited from the
 * ambient run-bar DataContext, which is GrblViewModel, not AppConfig).
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public partial class KbdDefaultSpeedControl : UserControl
    {
        public KbdDefaultSpeedControl()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            DataContext = AppConfig.Settings.Base?.Jog;
        }

        private void pnlRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var jog = AppConfig.Settings.Base?.Jog;
            if (jog == null)
                return;

            jog.DefaultSpeedFast = !jog.DefaultSpeedFast;
            AppConfig.Settings.Save();
        }
    }
}
