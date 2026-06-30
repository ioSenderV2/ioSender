/*
 * ProbeHeaderControl.xaml.cs - part of CNC Probing library
 *
 * Standardized shared header (probe selector + feed overrides + action) placed at the top-left of
 * every probing tab.
 *
 */

using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls.Probing
{
    public partial class ProbeHeaderControl : UserControl
    {
        public ProbeHeaderControl()
        {
            InitializeComponent();
        }

        private void cbxProbe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 1 && ((ComboBox)sender).IsDropDownOpen && DataContext is ProbingViewModel m)
                m.Grbl.ExecuteCommand(string.Format(GrblCommand.ProbeSelect, ((Probe)e.AddedItems[0]).Id));
        }
    }
}
