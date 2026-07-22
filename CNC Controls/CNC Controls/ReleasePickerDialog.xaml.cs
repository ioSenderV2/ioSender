/*
 * ReleasePickerDialog.xaml.cs - part of CNC Controls library
 *
 * Pick-a-release dropdown (OK/Cancel) - used by ioSender's "Check for Updates" when running a
 * local dev build: there's no embedded version to compare against a "latest" release, so instead
 * of just refusing, the caller lists every published release that has an installable ioSender.zip
 * asset and lets the operator pick one to install over the dev build. Mirrors ScenarioNameDialog's
 * DialogScaling.Apply pattern so it scales with UiScale like every other dialog.
 */

using System.Collections.Generic;
using System.Windows;

namespace CNC.Controls
{
    public partial class ReleasePickerDialog : Window
    {
        private IList<string> tags;

        /// <summary>The tag of the release the operator picked, or null if cancelled/closed.</summary>
        public string SelectedTag { get; private set; }

        // tags/displayLabels are parallel lists (same index = same release) - displayLabels drives
        // the dropdown text (e.g. "2.21 - 2026-07-22"), tags holds the bare version to pass to
        // install.ps1's -Tag.
        public ReleasePickerDialog(string prompt, string title, IList<string> tags, IList<string> displayLabels)
        {
            InitializeComponent();
            DialogScaling.Apply(this);

            Title = string.IsNullOrEmpty(title) ? "ioSender" : title;
            txtPrompt.Text = prompt;
            this.tags = tags;
            cbxRelease.ItemsSource = displayLabels;
            cbxRelease.SelectedIndex = 0;
        }

        /// <summary>Shows the dialog and returns the picked release's tag, or null if cancelled/closed.</summary>
        public static string Show(Window owner, string prompt, string title, IList<string> tags, IList<string> displayLabels)
        {
            var dlg = new ReleasePickerDialog(prompt, title, tags, displayLabels);
            if (owner != null && owner.IsLoaded)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dlg.ShowDialog() == true ? dlg.SelectedTag : null;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedTag = tags[cbxRelease.SelectedIndex];
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
