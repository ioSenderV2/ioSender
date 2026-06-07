/*
 * MainPageEditor.xaml.cs - part of CNC Controls library for Grbl
 *
 * "Edit Main Page" dialog (ioSender XL): a configure-association / shuttle UI that
 * moves assignable items between three buckets - Available (unassigned), Main page
 * (panels filling the slots) and Flyouts (sidebar). Writes Config.MainPanels /
 * Config.FlyoutItems on OK; applied on restart.
 *
 */

using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class MainPageEditor : Window
    {
        private const int MaxMainPanels = 6;

        private readonly ObservableCollection<AssignableItem> Available = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Main = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Flyouts = new ObservableCollection<AssignableItem>();

        public MainPageEditor()
        {
            InitializeComponent();

            var cfg = AppConfig.Settings.Base;
            var all = MainPanelRegistry.AllItems();
            var byName = all.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First());

            foreach (var name in cfg.MainPanels)
            {
                AssignableItem it;
                if (byName.TryGetValue(name, out it) && it.CanBeMainPanel && Main.Count < MaxMainPanels && !Main.Contains(it))
                    Main.Add(it);
            }
            foreach (var name in cfg.FlyoutItems)
            {
                AssignableItem it;
                if (byName.TryGetValue(name, out it) && !Main.Contains(it) && !Flyouts.Contains(it))
                    Flyouts.Add(it);
            }
            foreach (var it in all)
            {
                if (!Main.Contains(it) && !Flyouts.Contains(it))
                    Available.Add(it);
            }

            lstAvailable.ItemsSource = Available;
            lstMain.ItemsSource = Main;
            lstFlyouts.ItemsSource = Flyouts;

            UpdateButtons();
        }

        private void Selection_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var avail = lstAvailable.SelectedItem as AssignableItem;
            btnToMain.IsEnabled = avail != null && avail.CanBeMainPanel && Main.Count < MaxMainPanels;
            btnToFlyout.IsEnabled = avail != null;
            btnFromMain.IsEnabled = lstMain.SelectedItem != null;
            btnFromFlyout.IsEnabled = lstFlyouts.SelectedItem != null;
            btnMainUp.IsEnabled = btnMainDown.IsEnabled = lstMain.SelectedItem != null;
            btnFlyUp.IsEnabled = btnFlyDown.IsEnabled = lstFlyouts.SelectedItem != null;
        }

        private void btnToMain_Click(object sender, RoutedEventArgs e)
        {
            var it = lstAvailable.SelectedItem as AssignableItem;
            if (it != null && it.CanBeMainPanel && Main.Count < MaxMainPanels)
            {
                Available.Remove(it);
                Main.Add(it);
                lstMain.SelectedItem = it;
                UpdateButtons();
            }
        }

        private void btnFromMain_Click(object sender, RoutedEventArgs e)
        {
            var it = lstMain.SelectedItem as AssignableItem;
            if (it != null)
            {
                Main.Remove(it);
                Available.Add(it);
                lstAvailable.SelectedItem = it;
                UpdateButtons();
            }
        }

        private void btnToFlyout_Click(object sender, RoutedEventArgs e)
        {
            var it = lstAvailable.SelectedItem as AssignableItem;
            if (it != null)
            {
                Available.Remove(it);
                Flyouts.Add(it);
                lstFlyouts.SelectedItem = it;
                UpdateButtons();
            }
        }

        private void btnFromFlyout_Click(object sender, RoutedEventArgs e)
        {
            var it = lstFlyouts.SelectedItem as AssignableItem;
            if (it != null)
            {
                Flyouts.Remove(it);
                Available.Add(it);
                lstAvailable.SelectedItem = it;
                UpdateButtons();
            }
        }

        private static void Reorder(ObservableCollection<AssignableItem> list, ListBox lb, int dir)
        {
            int i = lb.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= list.Count)
                return;
            list.Move(i, j);
            lb.SelectedIndex = j;
        }

        private void btnMainUp_Click(object sender, RoutedEventArgs e) { Reorder(Main, lstMain, -1); }
        private void btnMainDown_Click(object sender, RoutedEventArgs e) { Reorder(Main, lstMain, 1); }
        private void btnFlyUp_Click(object sender, RoutedEventArgs e) { Reorder(Flyouts, lstFlyouts, -1); }
        private void btnFlyDown_Click(object sender, RoutedEventArgs e) { Reorder(Flyouts, lstFlyouts, 1); }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            var cfg = AppConfig.Settings.Base;
            cfg.MainPanels = Main.Select(i => i.Name).ToList();
            cfg.FlyoutItems = Flyouts.Select(i => i.Name).ToList();
            DialogResult = true;
        }
    }
}
