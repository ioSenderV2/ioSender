/*
 * MainPageEditor.xaml.cs - part of CNC Controls library for Grbl
 *
 * "Edit Main Page" dialog (ioSender XL): a configure-association / shuttle UI that
 * moves assignable items between three buckets - Available (unassigned), Main page
 * (panels filling the slots) and Flyouts (sidebar). Writes Config.MainPanels /
 * Config.FlyoutItems on OK; applied on restart.
 *
 */

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class MainPageEditor : UserControl, IRestartRequired, ISettingsEditorTab
    {
        // Fires (once) when Commit() applied a layout/tab change, so the settings host lights up its Restart button.
        public event EventHandler<RestartRequiredEventArgs> RestartRequired;

        private const int MaxMainPanels = 8;
        private const int MaxLeftPanels = 6;
        private const string ProtectedTab = "AppConfig";   // Settings:App must always stay reachable

        private readonly ObservableCollection<AssignableItem> Available = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Main = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Left = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Flyouts = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<TabInfo> TabsShown = new ObservableCollection<TabInfo>();
        private readonly ObservableCollection<TabInfo> TabsHidden = new ObservableCollection<TabInfo>();
        private string[] _origMain, _origLeft, _origFlyouts, _origTabs;   // baselines to detect changes on OK

        // True if OK applied any layout/tab change (the host enables its Restart button + status when set).
        public bool Changed { get; private set; }

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
            foreach (var name in cfg.LeftPanels)
            {
                AssignableItem it;
                if (byName.TryGetValue(name, out it) && it.CanBeMainPanel && Left.Count < MaxLeftPanels && !Main.Contains(it) && !Left.Contains(it))
                    Left.Add(it);
            }
            foreach (var name in cfg.FlyoutItems)
            {
                AssignableItem it;
                if (byName.TryGetValue(name, out it) && it.CanBeFlyout && !Main.Contains(it) && !Left.Contains(it) && !Flyouts.Contains(it))
                    Flyouts.Add(it);
            }
            foreach (var it in all)
            {
                if (!Main.Contains(it) && !Left.Contains(it) && !Flyouts.Contains(it))
                    Available.Add(it);
            }

            lstAvailable.ItemsSource = Available;
            lstMain.ItemsSource = Main;
            lstLeft.ItemsSource = Left;
            lstFlyouts.ItemsSource = Flyouts;

            LoadTabs(cfg);
            lstTabsShown.ItemsSource = TabsShown;
            lstTabsAvail.ItemsSource = TabsHidden;

            _origMain = Main.Select(i => i.Name).ToArray();
            _origLeft = Left.Select(i => i.Name).ToArray();
            _origFlyouts = Flyouts.Select(i => i.Name).ToArray();
            _origTabs = TabsShown.Select(t => t.Name).ToArray();

            var unavailable = ComponentAvailability.Unavailable();
            if (unavailable.Count == 0)
                unavailable.Add(new UnavailableComponent { Label = "(none)", Reason = "All capability-gated components are available on this controller." });
            lstUnavailable.ItemsSource = unavailable;

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
            btnToLeft.IsEnabled = avail != null && avail.CanBeMainPanel && Left.Count < MaxLeftPanels;
            btnToFlyout.IsEnabled = avail != null && avail.CanBeFlyout;
            btnFromMain.IsEnabled = lstMain.SelectedItem != null;
            btnFromLeft.IsEnabled = lstLeft.SelectedItem != null;
            btnFromFlyout.IsEnabled = lstFlyouts.SelectedItem != null;
            btnMainUp.IsEnabled = btnMainDown.IsEnabled = lstMain.SelectedItem != null;
            btnLeftUp.IsEnabled = btnLeftDown.IsEnabled = lstLeft.SelectedItem != null;
            btnFlyUp.IsEnabled = btnFlyDown.IsEnabled = lstFlyouts.SelectedItem != null;

            var shown = lstTabsShown.SelectedItem as TabInfo;
            btnTabShow.IsEnabled = lstTabsAvail.SelectedItem != null;
            btnTabHide.IsEnabled = shown != null && shown.Name != ProtectedTab;   // Settings:App can't be hidden
            btnTabUp.IsEnabled = btnTabDown.IsEnabled = shown != null;
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

        private void btnToLeft_Click(object sender, RoutedEventArgs e)
        {
            var it = lstAvailable.SelectedItem as AssignableItem;
            if (it != null && it.CanBeMainPanel && Left.Count < MaxLeftPanels)
            {
                Available.Remove(it);
                Left.Add(it);
                lstLeft.SelectedItem = it;
                UpdateButtons();
            }
        }

        private void btnFromLeft_Click(object sender, RoutedEventArgs e)
        {
            var it = lstLeft.SelectedItem as AssignableItem;
            if (it != null)
            {
                Left.Remove(it);
                Available.Add(it);
                lstAvailable.SelectedItem = it;
                UpdateButtons();
            }
        }

        private void btnToFlyout_Click(object sender, RoutedEventArgs e)
        {
            var it = lstAvailable.SelectedItem as AssignableItem;
            if (it != null && it.CanBeFlyout)
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

        private static void Reorder<T>(ObservableCollection<T> list, ListBox lb, int dir)
        {
            int i = lb.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= list.Count)
                return;
            list.Move(i, j);
            lb.SelectedIndex = j;
        }

        private void btnMainUp_Click(object sender, RoutedEventArgs e) { Reorder(Main, lstMain, -1); }
        private void btnMainDown_Click(object sender, RoutedEventArgs e) { Reorder(Main, lstMain, 1); }
        private void btnLeftUp_Click(object sender, RoutedEventArgs e) { Reorder(Left, lstLeft, -1); }
        private void btnLeftDown_Click(object sender, RoutedEventArgs e) { Reorder(Left, lstLeft, 1); }
        private void btnFlyUp_Click(object sender, RoutedEventArgs e) { Reorder(Flyouts, lstFlyouts, -1); }
        private void btnFlyDown_Click(object sender, RoutedEventArgs e) { Reorder(Flyouts, lstFlyouts, 1); }

        // Populate the Tabs editor from the host-published tab set and the saved order (Config.Tabs).
        private void LoadTabs(CNC.Controls.Config cfg)
        {
            var avail = TabRegistry.Available;
            var byName = avail.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());

            if (cfg.Tabs != null && cfg.Tabs.Count > 0)
            {
                foreach (var name in cfg.Tabs)
                {
                    TabInfo t;
                    if (byName.TryGetValue(name, out t) && !TabsShown.Contains(t))
                        TabsShown.Add(t);
                }
                foreach (var t in avail)
                    if (!TabsShown.Contains(t))
                        TabsHidden.Add(t);
            }
            else
            {
                foreach (var t in avail)   // default (no saved config): all tabs shown in built-in order
                    TabsShown.Add(t);
            }

            // The protected tab must always be shown - if a stale config hid it, restore it.
            var prot = avail.FirstOrDefault(t => t.Name == ProtectedTab);
            if (prot != null && !TabsShown.Contains(prot))
            {
                TabsHidden.Remove(prot);
                TabsShown.Add(prot);
            }
        }

        private void btnTabShow_Click(object sender, RoutedEventArgs e)
        {
            var t = lstTabsAvail.SelectedItem as TabInfo;
            if (t != null)
            {
                TabsHidden.Remove(t);
                TabsShown.Add(t);
                lstTabsShown.SelectedItem = t;
                UpdateButtons();
            }
        }

        private void btnTabHide_Click(object sender, RoutedEventArgs e)
        {
            var t = lstTabsShown.SelectedItem as TabInfo;
            if (t != null && t.Name != ProtectedTab)
            {
                TabsShown.Remove(t);
                TabsHidden.Add(t);
                lstTabsAvail.SelectedItem = t;
                UpdateButtons();
            }
        }

        private void btnTabUp_Click(object sender, RoutedEventArgs e) { Reorder(TabsShown, lstTabsShown, -1); }
        private void btnTabDown_Click(object sender, RoutedEventArgs e) { Reorder(TabsShown, lstTabsShown, 1); }

        // Write the buckets back to config. Applied on next layout build (restart).
        private void ApplyChanges()
        {
            var cfg = AppConfig.Settings.Base;
            cfg.MainPanels = Main.Select(i => i.Name).ToList();
            cfg.LeftPanels = Left.Select(i => i.Name).ToList();
            cfg.FlyoutItems = Flyouts.Select(i => i.Name).ToList();

            // Only persist tab config when the host published its tabs (ioSender XL) - otherwise leave it untouched.
            if (TabRegistry.Enabled)
            {
                var tabs = TabsShown.Select(t => t.Name).ToList();
                if (tabs.Count == 0)                                   // never hide everything
                    tabs = TabRegistry.Available.Select(t => t.Name).ToList();
                else if (!tabs.Contains(ProtectedTab) && TabRegistry.Available.Any(t => t.Name == ProtectedTab))
                    tabs.Add(ProtectedTab);                            // keep Settings:App reachable
                cfg.Tabs = tabs;
            }
        }

        // Save-on-leave: write the buckets back to config and persist. If anything changed since the tab was
        // entered, signal the host to surface its Restart button (the new layout applies on next launch). Called
        // by the settings host on tab-switch / view-leave. Baselines reset after so a second leave won't re-fire.
        public void Commit()
        {
            Changed = !Main.Select(i => i.Name).SequenceEqual(_origMain)
                   || !Left.Select(i => i.Name).SequenceEqual(_origLeft)
                   || !Flyouts.Select(i => i.Name).SequenceEqual(_origFlyouts)
                   || !TabsShown.Select(t => t.Name).SequenceEqual(_origTabs);

            ApplyChanges();
            AppConfig.Settings.Save();

            if (Changed)
            {
                RestartRequired?.Invoke(this, new RestartRequiredEventArgs("Restart required to apply main page / tab layout changes."));
                _origMain = Main.Select(i => i.Name).ToArray();
                _origLeft = Left.Select(i => i.Name).ToArray();
                _origFlyouts = Flyouts.Select(i => i.Name).ToArray();
                _origTabs = TabsShown.Select(t => t.Name).ToArray();
            }
        }
    }
}
