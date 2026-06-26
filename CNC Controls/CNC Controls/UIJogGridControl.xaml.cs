/*
 * UIJogGridControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Side-panel rendering of the UI jog selectors (the flyout uses UIJoggingControl's
 * sliders): a 2-column grid of distance / feed-rate value cells. Click a cell to
 * select it (highlighted green), plus a Continuous checkbox. Backed by the shared
 * JogViewModel (JogBaseControl.JogData); values are edited on Settings:App.
 *
 */

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CNC.Controls
{
    public partial class UIJogGridControl : UserControl
    {
        private static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0x9F, 0xE0, 0x9F)); // green

        private JogViewModel jog;
        private Button[] dist, feed;

        public UIJogGridControl()
        {
            InitializeComponent();
        }

        private void UIJogGridControl_Loaded(object sender, RoutedEventArgs e)
        {
            dist = new[] { d0, d1, d2, d3 };
            feed = new[] { f0, f1, f2, f3 };

            if (jog == null)
            {
                jog = JogBaseControl.JogData;
                if (jog != null)
                    jog.PropertyChanged += Jog_PropertyChanged;
            }
            content.DataContext = jog;
            UpdateHighlight();
        }

        private void Jog_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Refresh on any selection-related change (keyboard jogging can flip StepSize to Continuous and back,
            // which previously dropped the highlight even though the chosen distance was retained).
            UpdateHighlight();
        }

        private void Distance_Click(object sender, RoutedEventArgs e)
        {
            if (jog != null)
                // Go through DistanceIndex (not StepSize) so picking a distance updates the finite preset
                // without dropping out of Continuous - distance and Continuous are independent controls here.
                jog.DistanceIndex = int.Parse((string)((Button)sender).Tag);
        }

        private void Feed_Click(object sender, RoutedEventArgs e)
        {
            if (jog != null)
                jog.Feed = (JogViewModel.JogFeed)int.Parse((string)((Button)sender).Tag);
        }

        private void UpdateHighlight()
        {
            if (jog == null || dist == null)
                return;

            // Use the retained discrete index (DistanceIndex stays valid even while in Continuous mode), so the
            // chosen distance keeps its green highlight during keyboard/continuous jogging.
            int d = jog.DistanceIndex;
            int f = jog.FeedIndex;
            for (int i = 0; i < 4; i++)
            {
                dist[i].Background = d == i ? Selected : Brushes.Transparent;
                feed[i].Background = f == i ? Selected : Brushes.Transparent;
            }
        }
    }
}
