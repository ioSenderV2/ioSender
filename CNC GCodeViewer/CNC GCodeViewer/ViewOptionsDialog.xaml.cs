/*
 * ViewOptionsDialog.xaml.cs - part of CNC GCodeViewer
 *
 * The 3D view's own options, reached from a button on the view itself. It replaced a "Reset view" button,
 * which now lives inside here: resetting is a thing you do occasionally, and the view cube already handles
 * orientation, so it did not earn a permanent place on the picture.
 *
 * Binds straight to the live CarveViewConfig and applies as you toggle - there is no OK/Cancel, because the
 * result of every one of these is visible behind the dialog the moment it changes. Settings are saved on
 * close so a session's preference survives a restart.
 *
 * SINCE 2026-10-30 THIS IS A THIN WINDOW. The options themselves live in CarveViewOptionsControl, which
 * Settings > User Interface > 3D View hosts as well - one definition, two entrances, so the page and this
 * dialog cannot come to disagree about what the 3D view's settings are. What stayed here is what belongs
 * to a dialog rather than to the settings: Reset view, which acts on the view this was opened from, and
 * Close. The DataContext set below flows down to the shared control.
 */

using System.Windows;

namespace CNC.Controls.Viewer
{
    public partial class ViewOptionsDialog : Window
    {
        private readonly System.Action resetView;

        public ViewOptionsDialog(CarveViewConfig config, System.Action resetView)
        {
            InitializeComponent();

            this.resetView = resetView;
            DataContext = config;
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            resetView?.Invoke();
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Persist on close rather than in the Close button's handler: Escape and the title-bar X close the
        /// window without going near that button, and a preference that survives only the polite exit is
        /// the kind of thing nobody notices until they have set it three times.
        /// </summary>
        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            AppConfig.Settings.Save();
        }
    }
}
