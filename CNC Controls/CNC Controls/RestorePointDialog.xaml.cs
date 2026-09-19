/*
 * RestorePointDialog.xaml.cs - part of CNC Controls library for Grbl
 *
 * Picker for restore POINTS - moments, not files. See RestorePoint.cs for why the two snapshot kinds are
 * paired back together here rather than the operator being asked to know which one they need.
 *
 * The Browse... escape hatch still picks an arbitrary controller settings file, for the case where the
 * wanted snapshot is not in the backups folder at all (carried from another machine, kept by hand).
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using CNC.Core;

namespace CNC.Controls
{
    public partial class RestorePointDialog : Window
    {
        /// <summary>Controller settings file to restore, or null. Valid when DialogResult is true.</summary>
        public string SelectedFile { get; private set; }

        /// <summary>App configuration file to restore, or null. Valid when DialogResult is true.</summary>
        public string SelectedConfigFile { get; private set; }

        public RestorePointDialog()
        {
            InitializeComponent();

            dgrSnapshots.ItemsSource = RestorePoint.All();
            if (dgrSnapshots.Items.Count > 0)
                dgrSnapshots.SelectedIndex = 0;
            UpdateChoices();
        }

        private RestorePoint Selected { get { return dgrSnapshots.SelectedItem as RestorePoint; } }

        /// <summary>
        /// Offer only what the chosen moment actually holds. A disabled option states plainly that this
        /// restore point has nothing of that kind - which is information - whereas hiding it would leave the
        /// operator wondering whether they had missed a control.
        /// </summary>
        private void UpdateChoices()
        {
            var p = Selected;
            bool hasGrbl = p != null && p.HasGrbl, hasConfig = p != null && p.HasConfig;

            rbBoth.IsEnabled = hasGrbl && hasConfig;
            rbGrbl.IsEnabled = hasGrbl;
            rbConfig.IsEnabled = hasConfig;
            btnRestore.IsEnabled = hasGrbl || hasConfig;

            // Keep the selection on something legal for this point rather than leaving a checked-but-disabled
            // radio, which would restore nothing and look like a dead button.
            if (rbBoth.IsChecked == true && !rbBoth.IsEnabled)
                (hasGrbl ? rbGrbl : rbConfig).IsChecked = true;
            else if (rbGrbl.IsChecked == true && !hasGrbl)
                (hasConfig ? rbConfig : rbGrbl).IsChecked = true;
            else if (rbConfig.IsChecked == true && !hasConfig)
                (hasGrbl ? rbGrbl : rbConfig).IsChecked = true;
            else if (hasGrbl && hasConfig && rbBoth.IsChecked != true && rbGrbl.IsChecked != true && rbConfig.IsChecked != true)
                rbBoth.IsChecked = true;

            txtWhatNote.Text = p == null
                ? string.Empty
                : p.HasBoth
                    ? "This moment has both. Restoring the app configuration restarts ioSender."
                    : p.HasGrbl
                        ? "This moment holds only the machine's settings - there is no app configuration snapshot beside it."
                        : "This moment holds only the app configuration. Restoring it restarts ioSender.";
        }

        private void dgrSnapshots_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateChoices();
        }

        private void Accept(RestorePoint point)
        {
            if (point == null)
                return;

            bool wantGrbl = point.HasGrbl && (rbBoth.IsChecked == true || rbGrbl.IsChecked == true);
            bool wantConfig = point.HasConfig && (rbBoth.IsChecked == true || rbConfig.IsChecked == true);

            if (!wantGrbl && !wantConfig)
                return;

            SelectedFile = wantGrbl ? point.GrblFile : null;
            SelectedConfigFile = wantConfig ? point.ConfigFile : null;
            DialogResult = true;
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            Accept(Selected);
        }

        private void dgrSnapshots_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Accept(Selected);
        }

        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog file = new OpenFileDialog
            {
                InitialDirectory = GrblSettings.SnapshotFolder,
                Title = "Restore machine settings from file",
                Filter = "Text files (*.txt)|*.txt"
            };

            if (file.ShowDialog() == true)
            {
                SelectedFile = file.FileName;
                SelectedConfigFile = null;
                DialogResult = true;
            }
        }
    }
}
