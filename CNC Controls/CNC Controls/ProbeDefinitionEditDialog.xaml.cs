/*
 * ProbeDefinitionEditDialog.xaml.cs - part of CNC Controls library
 *
 * Edits a single ProbeDefinition. The caller passes a clone and copies it back on OK so Cancel reverts.
 *
 */

using System.Windows;

namespace CNC.Controls
{
    public partial class ProbeDefinitionEditDialog : Window
    {
        public ProbeDefinitionEditDialog(ProbeDefinition definition)
        {
            InitializeComponent();
            DataContext = definition;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace((DataContext as ProbeDefinition)?.Name))
            {
                MessageBox.Show("Please enter a name for the probe.", "Probe definition", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
