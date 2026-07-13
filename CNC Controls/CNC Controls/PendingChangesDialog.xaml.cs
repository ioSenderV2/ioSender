/*
 * PendingChangesDialog.xaml.cs - part of CNC Controls library
 *
 * Shows the Machine Setup Wizard's pending setting changes (opened by its Preview button).
 * Binds to the passed-in collection by property name, so it has no dependency on the item type.
 *
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CNC.Core;

namespace CNC.Controls
{
    public partial class PendingChangesDialog : Window
    {
        // Live-apply mode (Restore-from-file): OK writes each row in place, one at a time, colouring it as
        // it goes, instead of just confirming intent and leaving the caller to do the writes afterward.
        // ApplyOne writes a row's NewValue, returning null on success or an error string on failure.
        // RollbackOne (optional) writes a row's OldValue back - offered automatically if ApplyOne stops
        // partway with at least one row already applied.
        public Func<SettingChange, string> ApplyOne { get; set; }
        public Func<SettingChange, string> RollbackOne { get; set; }

        // confirm: false (default, Preview button use) = informational, single Close button.
        // true (Restore-from-file use) = asks for a decision - "Restore settings" (ShowDialog() == true)
        // or Cancel (== false/null) - caller applies nothing until it sees true back. Set ApplyOne too to
        // have OK perform (and colour) the writes live instead of just confirming intent.
        public PendingChangesDialog(IEnumerable changes, bool confirm = false)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            grd.ItemsSource = changes;

            if (confirm)
            {
                panelClose.Visibility = Visibility.Collapsed;
                panelConfirm.Visibility = Visibility.Visible;
                if (Title == "Pending changes")
                    Title = "Restore settings";
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            if (ApplyOne == null)
            {
                DialogResult = true;
                return;
            }

            var rows = grd.ItemsSource.OfType<SettingChange>().ToList();

            // Disable BOTH buttons for the duration - Cancel staying live would let a click during one of
            // the DoEvents() pumps below close the window out from under a write still in flight.
            panelConfirm.IsEnabled = false;
            var appliedThisRun = new List<SettingChange>();
            SettingChange failed = null;
            string failedError = null;

            foreach (var row in rows)
            {
                if (row.Status != SettingApplyStatus.Pending)
                    continue; // e.g. NotSupported - nothing to write, leave its row as-is

                var error = ApplyOne(row);
                row.Status = error == null ? SettingApplyStatus.Applied : SettingApplyStatus.Failed;
                EventUtils.DoEvents(); // let the row repaint before moving to the next one

                if (error == null)
                    appliedThisRun.Add(row);
                else
                {
                    failed = row;
                    failedError = error;
                    break;
                }
            }

            if (failed != null && appliedThisRun.Count > 0 && RollbackOne != null &&
                AppDialogs.Show(this, string.Format(
                        "{0} failed ({1}). Roll back the {2} setting(s) already applied?",
                        failed.Setting, failedError, appliedThisRun.Count),
                    "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                for (int i = appliedThisRun.Count - 1; i >= 0; i--)
                {
                    var row = appliedThisRun[i];
                    var error = RollbackOne(row);
                    row.Status = error == null ? SettingApplyStatus.RolledBack : SettingApplyStatus.Failed;
                    EventUtils.DoEvents();
                }
            }

            DialogResult = true; // always - caller reads outcome from each row's final Status
        }
    }
}
