/*
 * JogPresetSelector.xaml.cs - part of CNC Controls library
 *
 * Two read-only up/down spinners (jog distance and jog feed) for the run bar. Each steps through the four
 * jog presets held by the shared JogData singleton, wrapping around at either end. The presets themselves
 * (and the "live" selection shared with the jog pad / keyboard / controller) live in JogViewModel, so this
 * is purely a compact selector - no state of its own.
 */

using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public partial class JogPresetSelector : UserControl
    {
        public JogPresetSelector()
        {
            InitializeComponent();
        }

        // Bind to the shared jog model on load: JogData is created by JogBaseControl's constructor, which
        // in the run bar is built alongside this control, so it is guaranteed to exist by the time Loaded fires.
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            DataContext = JogBaseControl.JogData;
        }

        // 0..3 with wrap. DistanceIndex/FeedIndex clamp to [0,3] on set, so the modulo keeps us in range and
        // lets the ends roll over (0 -> 3 on down, 3 -> 0 on up).
        private void DistDown_Click(object sender, RoutedEventArgs e)
        {
            if (JogBaseControl.JogData != null)
                JogBaseControl.JogData.DistanceIndex = (JogBaseControl.JogData.DistanceIndex + 3) % 4;
        }

        private void DistUp_Click(object sender, RoutedEventArgs e)
        {
            if (JogBaseControl.JogData != null)
                JogBaseControl.JogData.DistanceIndex = (JogBaseControl.JogData.DistanceIndex + 1) % 4;
        }

        private void FeedDown_Click(object sender, RoutedEventArgs e)
        {
            if (JogBaseControl.JogData != null)
                JogBaseControl.JogData.FeedIndex = (JogBaseControl.JogData.FeedIndex + 3) % 4;
        }

        private void FeedUp_Click(object sender, RoutedEventArgs e)
        {
            if (JogBaseControl.JogData != null)
                JogBaseControl.JogData.FeedIndex = (JogBaseControl.JogData.FeedIndex + 1) % 4;
        }
    }
}
