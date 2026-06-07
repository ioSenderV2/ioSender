/*
 * KeyboardJoggingControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Assignable main-page / flyout panel: the keyboard jog Step/Slow/Fast distance
 * and rate rendered as two sliders (read-only readouts) that live-track the active
 * keyboard-jog mode, plus the "Link step distance to UI jogging" option. Values are
 * edited on Settings:App.
 *
 */

using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class KeyboardJoggingControl : UserControl
    {
        private KeyboardJogViewModel kbdJog;

        public KeyboardJoggingControl()
        {
            InitializeComponent();
        }

        private void KeyboardJoggingControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (kbdJog == null && DataContext is GrblViewModel gvm)
                kbdJog = new KeyboardJogViewModel(gvm.Keyboard);
            // content inherits the GrblViewModel DataContext, so set it explicitly.
            content.DataContext = kbdJog;
        }
    }
}
