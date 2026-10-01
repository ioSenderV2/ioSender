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

        /// <summary>Work offset snapshot to restore, or null. Valid when DialogResult is true.</summary>
        public string SelectedOffsetsFile { get; private set; }

        /// <summary>
        /// True when the operator asked for the G28/G30 positions as well. Those cannot be written - the
        /// firmware only teaches them from where the machine is standing - so the caller opens the snapshot
        /// as a program instead of applying it. Never set without SelectedOffsetsFile.
        /// </summary>
        public bool RestoreOffsetPositions { get; private set; }

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
            bool hasOffsets = p != null && p.HasOffsets;

            rbBoth.IsEnabled = hasGrbl && hasConfig;
            rbGrbl.IsEnabled = hasGrbl;
            rbConfig.IsEnabled = hasConfig;

            // Uncheck on the way out, not just disable: a tick left over from a point that HAD offsets would
            // otherwise sit there looking chosen while doing nothing, and would come back the moment the
            // operator reselected a point that has them.
            chkOffsets.IsEnabled = hasOffsets;
            if (!hasOffsets)
                chkOffsets.IsChecked = false;
            chkOffsetPositions.IsEnabled = hasOffsets;
            if (!hasOffsets)
                chkOffsetPositions.IsChecked = false;

            btnRestore.IsEnabled = hasGrbl || hasConfig || hasOffsets;

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

            // Say what this point does and does not hold, and name the two consequences worth knowing before
            // pressing Restore: the restart, and that the offsets are there but will not be touched unless
            // asked for. The second is why it is said at all - the checkbox starts clear, so a point holding
            // the very thing the operator came for would otherwise sit silent.
            if (p == null)
                txtWhatNote.Text = string.Empty;
            else
            {
                var note = new System.Text.StringBuilder();

                if (!p.HasGrbl)
                    note.Append("No machine settings in this moment. ");
                if (!p.HasConfig)
                    note.Append("No app configuration in this moment. ");
                if (p.HasConfig)
                    note.Append("Restoring the app configuration restarts ioSender. ");
                if (p.HasOffsets)
                    note.Append("This moment also holds the work offsets - tick the box above to put them back.");
                else
                    note.Append("No work offsets in this moment.");

                txtWhatNote.Text = note.ToString().Trim();
            }
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
            bool wantOffsets = point.HasOffsets && (chkOffsets.IsChecked == true || chkOffsetPositions.IsChecked == true);

            if (!wantGrbl && !wantConfig && !wantOffsets)
                return;

            SelectedFile = wantGrbl ? point.GrblFile : null;
            SelectedConfigFile = wantConfig ? point.ConfigFile : null;
            SelectedOffsetsFile = wantOffsets ? point.OffsetsFile : null;
            RestoreOffsetPositions = wantOffsets && chkOffsetPositions.IsChecked == true;
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
                SelectedOffsetsFile = null;   // browsing picks a settings file only - never leave a stale pick behind it
                RestoreOffsetPositions = false;
                DialogResult = true;
            }
        }
    }
}
