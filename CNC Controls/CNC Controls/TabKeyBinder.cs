/*
 * TabKeyBinder.cs - part of CNC Controls library
 *
 * Right-click "Bind to Key" support for the bindable main-page tabs and Settings sub-tabs, plus the live
 * shortcut badge shown in each tab header. Lets a user bind a tab to a keypress straight from the tab itself,
 * without opening the full Key Mappings editor. Shares the editor's storage + dispatch: bindings live in
 * Config.TabShortcuts, are keyed on the stable ids in KeyMapEditor.TabTargets, and re-register live through
 * AppConfig.NotifyTabShortcutsChanged.
 */

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    // Implemented by a top-level view that hosts bindable inner (second-level) tabs, so a "Tab.<Parent>.<Sub>"
    // shortcut can drill into the right inner tab. The host maps the full id to its inner TabItem and selects it.
    // Returns false (changing nothing) when that sub-tab is not currently available - e.g. it was removed for a
    // missing capability - so a stale binding stays a silent no-op instead of half-acting (switching the parent).
    public interface ITabBindingHost
    {
        bool SelectSubTab(string tabId);
    }

    public static class TabKeyBinder
    {
        // Wrap a tab's header with a live shortcut badge and attach the right-click bind/clear menu. Preserves
        // the existing header (string or element, e.g. a colour-coded TextBlock reached by name) as the label.
        public static void AttachTabBinding(TabItem tab, string tabId)
        {
            if (tab == null)
                return;

            object existing = tab.Header;
            tab.Header = null;   // detach any element header before re-parenting it into the wrapper
            tab.Header = new TabHeaderControl(existing, tabId);
            AttachBindMenu(tab, tabId);
        }

        // Attach only the right-click bind/clear menu (used where the header is built separately).
        public static void AttachBindMenu(TabItem tab, string tabId)
        {
            var bind = new MenuItem();
            bind.Click += (s, e) => PromptAndBind(Window.GetWindow(tab), tabId);
            var clear = new MenuItem { Header = "Clear Shortcut" };
            clear.Click += (s, e) => Clear(tabId);

            var menu = new ContextMenu();
            menu.Items.Add(bind);
            menu.Items.Add(clear);
            menu.Opened += (s, e) =>
            {
                string cur = CurrentDisplay(tabId);
                bind.Header = cur == null ? "Bind to Key…" : "Change Key (" + cur + ")…";
                clear.IsEnabled = cur != null;
            };
            tab.ContextMenu = menu;
        }

        // Friendly name for a tab id (from the editor catalog), for dialog prompts.
        public static string FriendlyName(string tabId)
        {
            var t = KeyMapEditor.TabTargets.FirstOrDefault(x => x.Id == tabId);
            return t != null ? t.Label : tabId;
        }

        // The tab's current shortcut as a display string ("Ctrl+J"), or null when unbound.
        public static string CurrentDisplay(string tabId)
        {
            var s = AppConfig.Settings.Base.TabShortcuts?.FirstOrDefault(x => x.Id == tabId);
            Key k;
            ModifierKeys m;
            if (s != null && !string.IsNullOrEmpty(s.Key) && ShortcutKey.TryParse(s.Key, out k, out m) && k != Key.None)
                return ShortcutKey.ToDisplayString(k, m);
            return null;
        }

        // Prompt for a key combination and bind it to tabId. Returns true when a binding was captured.
        public static bool PromptAndBind(Window owner, string tabId)
        {
            var dlg = new BindKeyWindow(FriendlyName(tabId), CurrentDisplay(tabId)) { Owner = owner };
            if (dlg.ShowDialog() != true)
                return false;

            SetBinding(tabId, dlg.CapturedKey, dlg.CapturedModifiers);
            return true;
        }

        public static void Clear(string tabId)
        {
            var list = AppConfig.Settings.Base.TabShortcuts;
            if (list != null && list.RemoveAll(x => x.Id == tabId) > 0)
                Persist();
        }

        // Assign a key to a tab, dropping any prior binding for this tab and any other tab already using the
        // same combo (one key -> one tab, so dispatch is never ambiguous).
        private static void SetBinding(string tabId, Key key, ModifierKeys mods)
        {
            var list = AppConfig.Settings.Base.TabShortcuts ??
                       (AppConfig.Settings.Base.TabShortcuts = new List<TabShortcut>());

            string stored = ShortcutKey.ToStorageString(key, mods);
            list.RemoveAll(x => x.Id == tabId || x.Key == stored);
            list.Add(new TabShortcut { Id = tabId, Key = stored });
            Persist();
        }

        private static void Persist()
        {
            AppConfig.Settings.Save();
            AppConfig.NotifyTabShortcutsChanged();   // re-registers dispatch + refreshes tab-header badges
        }
    }

    // A tiny modal that captures a single key combination. The first non-modifier key accepts (with whatever
    // modifiers are held); Esc cancels. Built in code so it needs no XAML/localization footprint.
    internal class BindKeyWindow : Window
    {
        public Key CapturedKey { get; private set; } = Key.None;
        public ModifierKeys CapturedModifiers { get; private set; } = ModifierKeys.None;

        private static readonly HashSet<Key> modifierKeys = new HashSet<Key>
        {
            Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift,
            Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin, Key.System, Key.None
        };

        public BindKeyWindow(string tabName, string current)
        {
            Title = "Bind to Key";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            MinWidth = 340;

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = "Press a key combination for the " + tabName + ".",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = current == null ? "Press keys…" : "Currently " + current,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Esc to cancel.",
                Foreground = Brushes.Gray,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            Content = panel;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                DialogResult = false;
                return;
            }

            if (modifierKeys.Contains(key))
                return;   // wait for a non-modifier key

            e.Handled = true;
            CapturedKey = key;
            CapturedModifiers = Keyboard.Modifiers;
            DialogResult = true;   // accept and close
        }
    }

    // A tab header that shows the tab label on the left and, when the tab has a keyboard shortcut, a small
    // badge in the upper-right corner. The badge tracks Config.TabShortcuts live via AppConfig.TabShortcutsChanged.
    // ToString() returns the label so existing code that reads TabItem.Header as text (e.g. the Edit Main Page
    // tab list) keeps working.
    public class TabHeaderControl : UserControl
    {
        private readonly string label;
        private readonly string tabId;
        private readonly TextBlock shortcut;

        // label may be a plain string or an existing header element (e.g. a named, colour-coded TextBlock that
        // other code recolours by field reference) - the element is hosted as-is so those references keep working.
        public TabHeaderControl(object label, string tabId)
        {
            this.tabId = tabId;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FrameworkElement text;
            if (label is FrameworkElement fe)
            {
                text = fe;
                this.label = (fe as TextBlock)?.Text ?? fe.ToString();
            }
            else
            {
                this.label = label?.ToString() ?? string.Empty;
                text = new TextBlock { Text = this.label, VerticalAlignment = VerticalAlignment.Center };
            }
            Grid.SetColumn(text, 0);

            shortcut = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontSize = 9,
                Opacity = 0.6,
                Margin = new Thickness(10, -3, 0, 0),
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(shortcut, 1);

            grid.Children.Add(text);
            grid.Children.Add(shortcut);
            Content = grid;

            Loaded += (s, e) => { AppConfig.TabShortcutsChanged += Refresh; Refresh(); };
            Unloaded += (s, e) => { AppConfig.TabShortcutsChanged -= Refresh; };
        }

        private void Refresh()
        {
            string cur = string.IsNullOrEmpty(tabId) ? null : TabKeyBinder.CurrentDisplay(tabId);
            if (string.IsNullOrEmpty(cur))
                shortcut.Visibility = Visibility.Collapsed;
            else
            {
                shortcut.Text = cur;
                shortcut.Visibility = Visibility.Visible;
            }
        }

        public override string ToString()
        {
            return label;
        }
    }
}
