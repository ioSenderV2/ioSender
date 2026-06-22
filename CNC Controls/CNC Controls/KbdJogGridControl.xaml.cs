/*
 * KbdJogGridControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Side-panel rendering of the keyboard-jog default speed (the flyout uses
 * KeyboardJoggingControl's slider): Slow / Fast value cells, click to select
 * (highlighted green). Backed by KeyboardJogViewModel; values edited on Settings:App.
 *
 */

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    public partial class KbdJogGridControl : UserControl
    {
        private static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0x9F, 0xE0, 0x9F)); // green

        private KeyboardJogViewModel kbd;
        private Button[] speed;

        public KbdJogGridControl()
        {
            InitializeComponent();
        }

        private void KbdJogGridControl_Loaded(object sender, RoutedEventArgs e)
        {
            speed = new[] { s0, s1 };

            if (kbd == null && DataContext is GrblViewModel gvm)
            {
                kbd = new KeyboardJogViewModel(gvm.Keyboard);
                kbd.PropertyChanged += Kbd_PropertyChanged;
            }
            content.DataContext = kbd;
            UpdateHighlight();
        }

        private void Kbd_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(KeyboardJogViewModel.SpeedIndex))
                UpdateHighlight();
        }

        private void Speed_Click(object sender, RoutedEventArgs e)
        {
            if (kbd != null)
                kbd.SpeedIndex = int.Parse((string)((Button)sender).Tag);
        }

        private void UpdateHighlight()
        {
            if (kbd == null || speed == null)
                return;

            for (int i = 0; i < speed.Length; i++)
                speed[i].Background = kbd.SpeedIndex == i ? Selected : Brushes.Transparent;
        }
    }
}
