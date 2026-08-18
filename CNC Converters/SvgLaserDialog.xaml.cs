/*
 * SvgLaserDialog.xaml.cs - part of the CNC Converters library
 *
 * Parameters for an SVG-to-laser conversion. Deliberately small: width, power, feed, travel, passes,
 * and which of M4/M3 to use. Everything else the emitter needs it already knows (SvgToLaser).
 *
 * The two grey summary lines under Width and Power exist because neither number means anything on its
 * own - a width without the height it implies, and an S value without $30 to scale it against, are
 * both quantities you have to go and work out somewhere else. They are bound one-way to the settings
 * so they follow as you type.
 */

using System.Windows;
using CNC.Controls;

namespace CNC.Converters
{
    public partial class SvgLaserDialog : Window
    {
        private SvgLaserSettings settings;

        public SvgLaserDialog(SvgLaserSettings settings)
        {
            InitializeComponent();

            this.settings = settings;
            DataContext = settings;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            // Guarded rather than trusted: a zero width scales the artwork to nothing and a zero feed is
            // a G1 that never completes, and both are easy to leave behind while trying values out.
            if (settings.WidthMm <= 0d)
            {
                AppDialogs.Show("Width must be greater than 0.", "SVG to laser", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (settings.Feed <= 0d || settings.TravelFeed <= 0d)
            {
                AppDialogs.Show("Feed and travel must both be greater than 0.", "SVG to laser", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (settings.Power <= 0d)
            {
                AppDialogs.Show("Power must be greater than 0 - at S0 the beam never fires.", "SVG to laser", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (settings.Power > settings.MaxPower)
            {
                AppDialogs.Show(string.Format("Power is above this controller's maximum of S{0:0} ($30). It would simply be clamped there.",
                                              settings.MaxPower),
                                "SVG to laser", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }
    }
}
