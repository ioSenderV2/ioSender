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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class MainPageEditor : UserControl, IRestartRequired, ISettingsEditorTab, ISettingsPageProvider
    {
        // Fires (once) when Commit() applied a layout/tab change, so the settings host lights up its Restart button.
        public event EventHandler<RestartRequiredEventArgs> RestartRequired;

        // The Panels / Tabs tabs become two nav pages; "Unavailable" does not - it is a question
        // ("why can't I see X?"), not a place you configure something, so it moved to a button.
        public IEnumerable<SettingsSubPage> GetPages()
        {
            return new List<SettingsSubPage>
            {
                new SettingsSubPage("Tab.Settings.MainPage", Localized("SettingsPageJobLayout", "Job tab layout"), this)
                    { IndexRoot = (tabs.Items[0] as TabItem)?.Content as FrameworkElement },
                new SettingsSubPage("Tab.Settings.Tabs", Localized("SettingsPageTopTabs", "Top-level tabs"), this)
                    { IndexRoot = (tabs.Items[1] as TabItem)?.Content as FrameworkElement }
            };
        }

        public void ShowPage(string key)
        {
            tabs.SelectedIndex = key == "Tab.Settings.Tabs" ? 1 : 0;
        }

        // LibStrings.FindResource hands back string.Empty (not null) for a key it doesn't have, so these
        // fall back to English until the two new labels get their locale rows.
        private static string Localized(string key, string fallback)
        {
            var s = LibStrings.FindResource(key);
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }

        // The Unavailable list, detached from the tab strip on first use and shown in its own window so it
        // is reachable from either page.
        private void btnUnavailable_Click(object sender, RoutedEventArgs e)
        {
            var body = unavailableTab.Content as FrameworkElement;
            if (body == null)
                return;

            unavailableTab.Content = null;
            var host = new Window {
                Title = "Unavailable components",
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 640,
                Height = 420,
                ShowInTaskbar = false,
                Content = body
            };
            // Hand the list back to its holder, or the button only ever works once.
            host.Closed += (s, ev) => { host.Content = null; unavailableTab.Content = body; };
            host.ShowDialog();
        }

        private const int MaxMainPanels = 8;
        private const int MaxLeftPanels = 6;
        private const string ProtectedTab = "AppConfig";   // Settings:App must always stay reachable

        private readonly ObservableCollection<AssignableItem> Available = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Main = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Left = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<AssignableItem> Flyouts = new ObservableCollection<AssignableItem>();
        private readonly ObservableCollection<PlacementRow> Placements = new ObservableCollection<PlacementRow>();
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

            LoadPlacements(cfg);
            lstPlacement.ItemsSource = Placements;

            _origMain = Main.Select(i => i.Name).ToArray();
            _origLeft = Left.Select(i => i.Name).ToArray();
            _origFlyouts = Flyouts.Select(i => i.Name).ToArray();
            _origTabs = PlacementBaseline();

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

            btnTabUp.IsEnabled = btnTabDown.IsEnabled = lstPlacement.SelectedItem != null;
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

        // Build one placement row per placeable view, seeded from where it currently sits in the layout tree.
        //
        // Sources, deliberately unioned rather than taken from the tree alone: every REGISTERED view
        // (TabRegistry.AllTabs, not TabRegistry.Available - a view that is hidden is never built and so never
        // re-appears in Available, which would make hiding it a one-way door), plus any bare component the
        // tree currently places (the tool table / Trinamic / PID tuners, which are ComponentRegistry entries
        // with no view contract). The union is what keeps a newly-registered view visible here on day one.
        private void LoadPlacements(CNC.Controls.Config cfg)
        {
            var root = AppConfig.Settings.Layout;
            var placed = new Dictionary<string, ViewPlacement>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var slot in new[] { LayoutKeys.SlotTabs, LayoutKeys.SlotMenuFile, LayoutKeys.SlotMenuTools })
            {
                var s = root?.Slot(slot);
                if (s == null)
                    continue;
                var where = slot == LayoutKeys.SlotTabs ? ViewPlacement.TabBar
                          : slot == LayoutKeys.SlotMenuFile ? ViewPlacement.FileMenu : ViewPlacement.ToolsMenu;
                foreach (var n in s.Items)
                    if (!string.IsNullOrEmpty(n.Component) && !placed.ContainsKey(n.Component))
                    {
                        placed[n.Component] = where;
                        order.Add(n.Component);
                    }
            }

            // Registered views the tree doesn't place are hidden; append them so they can be brought back.
            foreach (var t in TabRegistry.AllTabs)
                if (!placed.ContainsKey(t.Name))
                {
                    placed[t.Name] = ViewPlacement.Hidden;
                    order.Add(t.Name);
                }

            var labels = TabRegistry.AllTabs.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First().Label);
            foreach (var key in order)
            {
                string label;
                if (!labels.TryGetValue(key, out label))
                    label = ComponentRegistry.Get(key)?.Label;
                if (string.IsNullOrEmpty(label))
                    continue;   // a foreign key from another build - nothing to show, and nothing we can place

                Placements.Add(new PlacementRow(key, label, placed[key], CanHide(key), CanMenu(key)));
            }
        }

        // Settings, Machine Setup and the Job view must stay reachable: LayoutTree.EnsureEssentials puts any
        // of them back on the tab bar if the tree places them nowhere, so offering "Hidden" would be a lie -
        // the choice would silently undo itself on the next load. Settings and Machine Setup can still move
        // between bar and menu; the Job view cannot - see CanMenu.
        private static bool CanHide(string key)
        {
            return key != ProtectedTab && !LayoutKeys.Essential.Contains(key);
        }

        // The Job view is the one view that is not merely a place to put things: it boots the controller
        // (Activate -> InitSystem -> $I and the settings load), owns the status poller, and hosts the run
        // controls. A dozen call sites resolve it with getTab(ViewType.GRBL) and do nothing when that
        // returns null - which is exactly what a menu-hosted Job view returns - so menuing it silently
        // costs you the connect handshake. Rather than teach every one of those sites to open a window on
        // connect, the invariant they already assume is made true here: Job lives on the tab bar.
        // AppConfig.EnforceMenuPlacement repairs a profile that menued it under an earlier build.
        private static bool CanMenu(string key)
        {
            return key != LayoutKeys.Grbl;
        }

        // Row identity + placement, flattened for change detection ("Job=TabBar|GRBLConfig=FileMenu|...").
        private string[] PlacementBaseline()
        {
            return Placements.Select(p => p.Name + "=" + p.Placement).ToArray();
        }

        private void btnTabUp_Click(object sender, RoutedEventArgs e) { Reorder(Placements, lstPlacement, -1); }
        private void btnTabDown_Click(object sender, RoutedEventArgs e) { Reorder(Placements, lstPlacement, 1); }

        // Write the buckets back to config. Applied on next layout build (restart).
        private void ApplyChanges()
        {
            var cfg = AppConfig.Settings.Base;
            cfg.MainPanels = Main.Select(i => i.Name).ToList();
            cfg.LeftPanels = Left.Select(i => i.Name).ToList();
            cfg.FlyoutItems = Flyouts.Select(i => i.Name).ToList();

            // Only persist tab/placement config when the host published its tabs (ioSender XL) - otherwise
            // there is nothing to place and the saved arrangement must be left alone.
            if (TabRegistry.Enabled)
                ApplyPlacements(cfg);
        }

        // Write the placement rows into the layout tree (the authority BuildTabs/BuildViewMenus read) and the
        // legacy flat tab list. Both, because TabOrder.Apply rebuilds the tabs slot from Config.Tabs on every
        // load - writing only the tree would have the flat list overwrite it right back.
        private void ApplyPlacements(CNC.Controls.Config cfg)
        {
            // Nothing to place means the registries were empty when this editor was built, NOT that the user
            // asked for an empty app - writing the slots from it would wipe a perfectly good saved layout.
            // Commit() runs on every tab-leave, so this would fire on merely opening and leaving the page.
            if (Placements.Count == 0)
                return;

            cfg.Tabs = Placements.Where(p => p.Placement == ViewPlacement.TabBar).Select(p => p.Name).ToList();
            cfg.HiddenViews = Placements.Where(p => p.Placement == ViewPlacement.Hidden).Select(p => p.Name).ToList();

            var root = AppConfig.Settings.Layout;
            if (root == null)
                return;

            // Reuse the existing node for a component rather than making a fresh one: a container's nested
            // slots (the Job tab's centre arrangement) hang off it, and rebuilding the node would drop them.
            var existing = LayoutTree.Flatten(root)
                                     .Where(n => !string.IsNullOrEmpty(n.Component))
                                     .GroupBy(n => n.Component)
                                     .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            SetSlot(root, LayoutKeys.SlotTabs, ViewPlacement.TabBar, existing);
            SetSlot(root, LayoutKeys.SlotMenuFile, ViewPlacement.FileMenu, existing);
            SetSlot(root, LayoutKeys.SlotMenuTools, ViewPlacement.ToolsMenu, existing);
        }

        private void SetSlot(LayoutNode root, string slotName, ViewPlacement placement, Dictionary<string, LayoutNode> existing)
        {
            var slot = root.Slot(slotName);
            if (slot == null)
            {
                slot = new LayoutSlot(slotName);
                root.Slots.Add(slot);
            }

            slot.Items = Placements
                .Where(p => p.Placement == placement)
                .Select(p => existing.TryGetValue(p.Name, out var n) ? n : new LayoutNode(p.Name))
                .ToList();
        }

        // Save-on-leave: write the buckets back to config and persist. If anything changed since the tab was
        // entered, signal the host to surface its Restart button (the new layout applies on next launch). Called
        // by the settings host on tab-switch / view-leave. Baselines reset after so a second leave won't re-fire.
        public void Commit()
        {
            Changed = !Main.Select(i => i.Name).SequenceEqual(_origMain)
                   || !Left.Select(i => i.Name).SequenceEqual(_origLeft)
                   || !Flyouts.Select(i => i.Name).SequenceEqual(_origFlyouts)
                   || !PlacementBaseline().SequenceEqual(_origTabs);

            ApplyChanges();
            AppConfig.Settings.Save();

            if (Changed)
            {
                RestartRequired?.Invoke(this, new RestartRequiredEventArgs("Restart required to apply main page / tab layout changes."));
                _origMain = Main.Select(i => i.Name).ToArray();
                _origLeft = Left.Select(i => i.Name).ToArray();
                _origFlyouts = Flyouts.Select(i => i.Name).ToArray();
                _origTabs = PlacementBaseline();
            }
        }
    }

    /// <summary>Where a top-level view appears. The layout tree's root slots, named for the operator.</summary>
    public enum ViewPlacement
    {
        TabBar,
        FileMenu,
        ToolsMenu,
        Hidden
    }

    /// <summary>One view in the placement editor: what it is called, and where the user wants it.</summary>
    public class PlacementRow : INotifyPropertyChanged
    {
        public string Name { get; }        // stable component/registry key
        public string Label { get; }       // what the operator sees on the tab or menu entry
        public List<PlacementChoice> Choices { get; }

        private ViewPlacement placement;
        public ViewPlacement Placement
        {
            get { return placement; }
            set { if (placement != value) { placement = value; Notify(nameof(Placement)); } }
        }

        public string PlacementTooltip
        {
            get { return "Where " + Label + " appears. Applied on restart."; }
        }

        // canHide/canMenu == false drop those choices entirely rather than offering one that would be undone
        // on the next load (see MainPageEditor.CanHide / CanMenu) - an unavailable option is better absent
        // than a trap. Each still keeps a choice the profile is CURRENTLY sitting on, so a row can always
        // display its own state; the every-load invariant is what moves it back.
        public PlacementRow(string name, string label, ViewPlacement placement, bool canHide, bool canMenu = true)
        {
            Name = name;
            Label = label;
            this.placement = placement;

            Choices = new List<PlacementChoice> { new PlacementChoice(ViewPlacement.TabBar, "Tab bar") };
            if (canMenu || placement == ViewPlacement.FileMenu)
                Choices.Add(new PlacementChoice(ViewPlacement.FileMenu, "File menu"));
            if (canMenu || placement == ViewPlacement.ToolsMenu)
                Choices.Add(new PlacementChoice(ViewPlacement.ToolsMenu, "Tools menu"));
            if (canHide || placement == ViewPlacement.Hidden)
                Choices.Add(new PlacementChoice(ViewPlacement.Hidden, "Not shown"));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>A placement option in the per-row dropdown.</summary>
    public class PlacementChoice
    {
        public ViewPlacement Placement { get; }
        public string Label { get; }
        public PlacementChoice(ViewPlacement placement, string label) { Placement = placement; Label = label; }
    }
}
