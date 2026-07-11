/*
 * ProbeDefinitionsDialog.xaml.cs - part of CNC Controls library
 *
 * Manages the probe library (add/edit/delete). Edits the live ObservableCollection passed in;
 * the caller persists it after the dialog closes.
 *
 */

using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using CNC.Core;
namespace CNC.Controls
{
    public partial class ProbeDefinitionsDialog : Window
    {
        private readonly ObservableCollection<ProbeDefinition> items;

        public ProbeDefinitionsDialog(ObservableCollection<ProbeDefinition> definitions)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            items = definitions;
            grd.ItemsSource = items;
            if (items.Count > 0)
                grd.SelectedIndex = 0;
            UpdateButtons();
        }

        private ProbeDefinition Selected { get { return grd.SelectedItem as ProbeDefinition; } }

        private void UpdateButtons()
        {
            btnEdit.IsEnabled = btnDelete.IsEnabled = Selected != null;
        }

        private void grd_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void grd_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Selected != null)
                EditSelected();
        }

        private void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            var def = new ProbeDefinition();
            var dlg = new ProbeDefinitionEditDialog(def) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                items.Add(def);
                ProbeDefinitions.Renumber(items);   // names derive from type + count
                grd.SelectedItem = def;
            }
        }

        private void btnEdit_Click(object sender, RoutedEventArgs e)
        {
            EditSelected();
        }

        // Edit a clone and copy back on OK so Cancel reverts.
        private void EditSelected()
        {
            var sel = Selected;
            if (sel == null)
                return;

            var edit = sel.Clone();
            var dlg = new ProbeDefinitionEditDialog(edit) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                sel.CopyFrom(edit);
                ProbeDefinitions.Renumber(items);   // type may have changed
            }
        }

        private void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            var sel = Selected;
            if (sel != null && AppDialogs.Show(string.Format("Delete probe \"{0}\"?", sel.Name), "Probe definitions",
                                               MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                items.Remove(sel);
                ProbeDefinitions.Renumber(items);
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private string UniqueName(string baseName)
        {
            string name = baseName;
            int n = 1;
            while (items.Any(d => d.Name == name))
                name = baseName + " " + (++n);
            return name;
        }
    }
}
