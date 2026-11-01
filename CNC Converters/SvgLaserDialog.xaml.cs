/*
 * SvgLaserDialog.xaml.cs - part of the CNC Converters library
 *
 * Parameters for an SVG-to-laser conversion. Deliberately small: width, power, feed, travel, passes,
 * and which of M4/M3 to use. Everything else the emitter needs it already knows (SvgToLaser).
 *
 * The grey summary lines under Width, Power and the fill fields exist because none of those numbers
 * means anything on its own - a width without the height it implies, an S value without $30 to scale
 * it against, and above all a power without the feed it is divided by. They are bound one-way to the
 * settings so they follow as you type.
 *
 * The exposure lines are the important ones: S150 at F1200 and S400 at F3000 look like a large power
 * increase and are the same burn. See SvgLaserSettings.Exposure.
 *
 * OK persists the settings - they are the shared config-store instance, so the values found for a
 * material survive both the next import and the next session.
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

            // SizeToContent grows the window to whatever the content asks for, with no upper bound. On a
            // short screen that put OK and Cancel below the bottom edge - on a window that could not be
            // resized, with nothing to scroll, so the dialog was simply unusable and Escape was the only
            // way out of it.
            //
            // Clamping lets SizeToContent do its job where there is room and hands the overflow to the
            // per-tab ScrollViewers where there is not. WorkArea rather than screen height, so the taskbar
            // is allowed for; the margin covers the title bar and border, which are outside this measure.
            MaxHeight = SystemParameters.WorkArea.Height - 60d;

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

            // Persist what the operator settled on. Deliberately here rather than in the property
            // setters: XmlSerializer calls those during deserialization, and a save that runs mid-load
            // has already made this config unloadable once (see the NOTE in UiState.cs).
            AppConfig.Settings.Save();

            DialogResult = true;
        }
    }
}
