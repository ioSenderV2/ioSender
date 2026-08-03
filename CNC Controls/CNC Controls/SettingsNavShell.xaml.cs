/*
 * SettingsNavShell.xaml.cs - part of CNC Controls library
 *
 * The navigable index that replaces the Settings / Machine Setup tab strips: a searchable tree of
 * categories and pages on the left, the selected page on the right.
 * See docs/Architecture-Settings-Nav-Overhaul.md.
 *
 * The shell owns navigation only. It deliberately knows nothing about grbl, config panels or the
 * footer - the host wires those through SelectedNodeChanged, exactly as it used to react to a
 * TabControl's SelectionChanged.
 */

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CNC.Controls
{
    public class SettingsNavEventArgs : RoutedEventArgs
    {
        public SettingsNavNode From { get; set; }
        public SettingsNavNode To { get; set; }
    }

    public partial class SettingsNavShell : UserControl
    {
        private SettingsNavNode selectedNode;
        private bool suppressSelection;

        public SettingsNavShell()
        {
            InitializeComponent();
            Nodes = new ObservableCollection<SettingsNavNode>();
            navTree.ItemsSource = Nodes;
        }

        public ObservableCollection<SettingsNavNode> Nodes { get; private set; }

        // Raised after the selection changes, with both sides, so the host can run the same
        // leave/enter lifecycle it used to run on tab switches.
        public event System.EventHandler<SettingsNavEventArgs> SelectedNodeChanged;

        // Raised synchronously when a page is selected whose content has not been built yet, so the host
        // can materialize it before the pane is shown. Pages built on first show (the key-map / macros /
        // main-page editors) would otherwise flash the "nothing selected" hint.
        public event System.EventHandler<SettingsNavEventArgs> ContentRequested;

        public SettingsNavNode SelectedNode
        {
            get { return selectedNode; }
            private set
            {
                if (ReferenceEquals(selectedNode, value))
                    return;
                var from = selectedNode;
                selectedNode = value;

                if (selectedNode != null && selectedNode.Content == null)
                    ContentRequested?.Invoke(this, new SettingsNavEventArgs { From = from, To = selectedNode });

                RefreshContent();
                SelectedNodeChanged?.Invoke(this, new SettingsNavEventArgs { From = from, To = selectedNode });
            }
        }

        public IEnumerable<SettingsNavNode> AllNodes()
        {
            return Nodes.SelectMany(n => n.SelfAndDescendants());
        }

        public SettingsNavNode FindByKey(string key)
        {
            return string.IsNullOrEmpty(key) ? null : AllNodes().FirstOrDefault(n => n.Key == key);
        }

        // Select a page by key. Returns false when the key is unknown or its node is hidden, so the
        // caller (e.g. a tab-switch shortcut) can fall through rather than blank the pane.
        public bool SelectByKey(string key)
        {
            var node = FindByKey(key);
            if (node == null || node.IsCategory || !node.IsShown)
                return false;
            Select(node);
            return true;
        }

        // Push the selected node's content into the pane again. Needed when the host builds a page's
        // content lazily, after the node was already selected.
        public void RefreshContent()
        {
            pageHost.Content = selectedNode?.Content;
            emptyHint.Visibility = selectedNode?.Content == null ? Visibility.Visible : Visibility.Collapsed;
        }

        // Re-announce computed visibility after the host has run its capability gates.
        public void RefreshVisibility()
        {
            foreach (var n in Nodes)
                n.RefreshShown();
            AutoSizeNav();
        }

        // Width the tree to its widest label so nothing truncates and no horizontal scrollbar appears.
        // Measured rather than hardcoded: the labels come from each panel's own localized GroupBox header,
        // so the longest one differs per locale (German runs far wider than English) and a magic number
        // that fits today's English text would quietly truncate somewhere else.
        private bool navWidthInitialized;

        public void AutoSizeNav()
        {
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }

            double widest = 0;
            foreach (var node in AllNodes())
            {
                if (!node.IsShown || string.IsNullOrEmpty(node.Label))
                    continue;

                var weight = node.IsCategory ? FontWeights.SemiBold : navTree.FontWeight;
                var tf = new Typeface(navTree.FontFamily, navTree.FontStyle, weight, navTree.FontStretch);
                var ft = new FormattedText(node.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                           tf, navTree.FontSize, Brushes.Black, dpi);
                widest = System.Math.Max(widest, ft.Width + DepthOf(node) * IndentPerLevel);
            }

            if (widest <= 0)
                return;

            // expander column + item padding + TreeView border + the vertical scrollbar the list will need
            double chrome = IndentPerLevel + 8 + 2 + SystemParameters.VerticalScrollBarWidth;
            double want = System.Math.Ceiling(widest + chrome);

            navColumn.MinWidth = want;
            if (!navWidthInitialized || navColumn.Width.Value < want)
            {
                navColumn.Width = new GridLength(want);
                navWidthInitialized = true;
            }
        }

        private const double IndentPerLevel = 19.0;   // WPF's default TreeViewItem indent

        private int DepthOf(SettingsNavNode node)
        {
            foreach (var top in Nodes)
            {
                if (ReferenceEquals(top, node))
                    return 0;
                if (top.Children.Contains(node))
                    return 1;
                foreach (var child in top.Children)
                    if (child.SelfAndDescendants().Contains(node))
                        return 2;
            }
            return 1;
        }

        public void Select(SettingsNavNode node)
        {
            if (node == null)
                return;
            ExpandTo(node);
            node.IsSelected = true;
            SelectedNode = node;
        }

        // Pick the first selectable page - used on entry and whenever the current page is hidden by a
        // capability gate (the Simulator page while connected TO the simulator, say).
        // A node is a PAGE when it has no children. Content may legitimately still be null - the editor
        // pages are built on first show - so content must never be the test for what is selectable.
        public SettingsNavNode FirstPage()
        {
            return AllNodes().FirstOrDefault(n => !n.IsCategory && n.IsShown);
        }

        public void EnsureSelection()
        {
            if (SelectedNode == null || !SelectedNode.IsShown || SelectedNode.IsCategory)
                Select(FirstPage());
        }

        private void ExpandTo(SettingsNavNode node)
        {
            foreach (var top in Nodes)
                if (top.SelfAndDescendants().Contains(node))
                    top.IsExpanded = true;
        }

        private void navTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (suppressSelection)
                return;

            var node = e.NewValue as SettingsNavNode;
            if (node == null)
                return;

            // A category is a heading, not a destination - expand it instead of selecting it. This tests
            // for CHILDREN, not for content: the editor pages have no content until first shown, and
            // testing content here made every node under Interface unclickable.
            if (node.IsCategory)
            {
                node.IsExpanded = !node.IsExpanded;
                return;
            }

            SelectedNode = node;
        }

        #region Search

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplySearch(searchBox.Text);
        }

        private void searchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                searchBox.Text = string.Empty;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                var first = FirstPage();
                if (first != null)
                    Select(first);
                e.Handled = true;
            }
        }

        // Phase 0 matches the node label (and any harvested SearchText, which is empty until phase 3).
        public void ApplySearch(string query)
        {
            bool all = string.IsNullOrWhiteSpace(query);
            var q = all ? null : query.Trim();

            suppressSelection = true;
            foreach (var node in AllNodes())
            {
                if (all)
                    node.MatchesSearch = true;
                else
                    node.MatchesSearch =
                        (node.Label != null && node.Label.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (node.SearchText != null && node.SearchText.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // While filtering, open everything so matches deeper in the tree are actually on screen.
            if (!all)
                foreach (var n in Nodes)
                    n.IsExpanded = true;

            RefreshVisibility();
            suppressSelection = false;
        }

        public void FocusSearch()
        {
            searchBox.Focus();
            searchBox.SelectAll();
        }

        #endregion
    }
}
