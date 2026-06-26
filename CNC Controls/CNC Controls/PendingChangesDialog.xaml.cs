/*
 * PendingChangesDialog.xaml.cs - part of CNC Controls library
 *
 * Shows the Machine Setup Wizard's pending setting changes (opened by its Preview button).
 * Binds to the passed-in collection by property name, so it has no dependency on the item type.
 *
 */

using System.Collections;
using System.Windows;

namespace CNC.Controls
{
    public partial class PendingChangesDialog : Window
    {
        public PendingChangesDialog(IEnumerable changes)
        {
            InitializeComponent();
            grd.ItemsSource = changes;
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
