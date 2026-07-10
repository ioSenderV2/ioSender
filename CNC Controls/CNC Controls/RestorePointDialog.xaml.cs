/*
 * RestorePointDialog.xaml.cs - part of CNC Controls library for Grbl
 *
 * Picker for settings restore points (auto-snapshots written on each successful connect),
 * newest first, with a Browse... escape hatch for arbitrary backup files.
 *
 */

using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using CNC.Core;

namespace CNC.Controls
{
    public partial class RestorePointDialog : Window
    {
        // Full path of the chosen settings file, valid when DialogResult is true.
        public string SelectedFile { get; private set; }

        public RestorePointDialog()
        {
            InitializeComponent();

            dgrSnapshots.ItemsSource = GrblSettings.GetSnapshots();
            if (dgrSnapshots.Items.Count > 0)
                dgrSnapshots.SelectedIndex = 0;
        }

        private void Accept(SettingsSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            SelectedFile = snapshot.FilePath;
            DialogResult = true;
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            Accept(dgrSnapshots.SelectedItem as SettingsSnapshot);
        }

        private void dgrSnapshots_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Accept(dgrSnapshots.SelectedItem as SettingsSnapshot);
        }

        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog file = new OpenFileDialog
            {
                InitialDirectory = GrblSettings.SnapshotFolder,
                Title = "Restore settings from file",
                Filter = "Text files (*.txt)|*.txt"
            };

            if (file.ShowDialog() == true)
            {
                SelectedFile = file.FileName;
                DialogResult = true;
            }
        }
    }
}
