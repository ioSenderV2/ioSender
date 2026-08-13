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
using System.Windows.Documents;   // AdornerLayer - see "Highlighting the match ON the page"
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

            // handledEventsToo: the whole point is to see wheel events an inner control already marked
            // handled. See PageScroll_MouseWheel.
            pageScroll.AddHandler(MouseWheelEvent, new MouseWheelEventHandler(PageScroll_MouseWheel), true);
        }

        // Scroll the page when the wheel turns over a child that swallowed the event without moving.
        //
        // Every settings page sits inside pageScroll, which hands its content unlimited height - so a list
        // control on a page (the Key Mappings grid, the placement list) lays out at its FULL height and its
        // own internal ScrollViewer has nothing left to scroll. WPF's ScrollViewer.OnMouseWheel marks the
        // event handled regardless of whether it actually moved anything, so the wheel died over the list and
        // only worked with the pointer directly over pageScroll's scrollbar.
        //
        // So: if the nearest ScrollViewer between the mouse and pageScroll can't scroll vertically (or there
        // isn't one), drive pageScroll instead. A child that CAN scroll is left alone, which is why this
        // walks the tree per event rather than blanket-handling the wheel at the top.
        private void PageScroll_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0 || InnerScrollCanScroll(e.OriginalSource as DependencyObject))
                return;

            // Match what the ScrollViewer would have done itself, rather than treating the raw delta as a
            // pixel count (which scrolls ~2.5x too far per notch): Windows' own wheel setting, in lines of
            // roughly one text row, scaled for a mouse that reports more than one notch per event.
            int lines = SystemParameters.WheelScrollLines;
            double distance = lines < 0 ? pageScroll.ViewportHeight : lines * 16d;   // -1 = "one screen at a time"
            double notches = System.Math.Abs(e.Delta) / 120d;

            pageScroll.ScrollToVerticalOffset(
                pageScroll.VerticalOffset + (e.Delta > 0 ? -1d : 1d) * distance * System.Math.Max(1d, notches));
            e.Handled = true;
        }

        // True when some ScrollViewer between node and pageScroll has vertical room to move - in which case
        // that control owns the wheel and this handler keeps its hands off.
        private bool InnerScrollCanScroll(DependencyObject node)
        {
            while (node != null && node != pageScroll)
            {
                if (node is ScrollViewer sv && sv.ScrollableHeight > 0d)
                    return true;
                node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
            }
            return false;
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

            // A new page is showing, so any marks belong to the old one. Re-run against this page - this is
            // the call that matters when a search narrows the list and you then CHOOSE one of the survivors,
            // which is the whole point of the highlighting. Also covers the host building content lazily and
            // calling back in here afterwards.
            HighlightMatchesOnPage();
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

                // A graded row (Machine Setup) also carries the status dot and its margin ahead of the
                // text - measuring the label alone under-sizes those rows and the scrollbar comes back.
                double dot = node.StatusBrush != null ? StatusDotWidth : 0;
                widest = System.Math.Max(widest, ft.Width + dot + DepthOf(node) * IndentPerLevel);
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
        private const double StatusDotWidth = 14.0;   // the 8px status Ellipse + its 6px right margin

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
                // Prefer a page whose NAME matched - a name hit is what the user most likely meant.
                var best = AllNodes().FirstOrDefault(n => !n.IsCategory && n.IsShown && n.MatchedLabel)
                        ?? FirstPage();
                if (best != null)
                    Select(best);
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
                {
                    node.MatchesSearch = true;
                    node.MatchedLabel = false;
                    node.MatchContext = null;
                    continue;
                }

                bool onLabel = node.Label != null && node.Label.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0;
                int atText = node.SearchText == null ? -1 : node.SearchText.IndexOf(q, System.StringComparison.OrdinalIgnoreCase);
                var tip = MatchingTip(node, q);

                node.MatchedLabel = onLabel;
                node.MatchesSearch = onLabel || atText >= 0 || tip != null;

                // Explain a match the eye cannot find on the page, and say which kind of text it hit -
                // a tooltip hit is on the page but only on hover, which is what made it look wrong.
                if (onLabel)
                    node.MatchContext = null;
                else if (atText >= 0)
                    node.MatchContext = string.Format(Localized("SettingsMatchedText", "Matched text: {0}"),
                                                      Snippet(node.SearchText, atText, q.Length));
                else if (tip != null)
                    node.MatchContext = string.Format(Localized("SettingsMatchedTooltip", "Matched tooltip: {0}"),
                                                      DescribeTip(tip, q));
                else
                    node.MatchContext = null;
            }

            // While filtering, open everything so matches deeper in the tree are actually on screen.
            if (!all)
                foreach (var n in Nodes)
                    n.IsExpanded = true;

            RefreshVisibility();
            suppressSelection = false;

            matchCount.Text = all ? string.Empty
                : string.Format("{0} match{1}", MatchingPages(), MatchingPages() == 1 ? "" : "es");

            searchQuery = q;
            HighlightMatchesOnPage();
        }

        #region Highlighting the match ON the page

        // Narrowing the tree to the right page told you the page and nothing more: you then had to find the
        // word yourself, and a tooltip hit could not be found by looking at all. MatchContext explains the
        // hit in the tree, but explaining is not pointing. These mark it where it actually is.

        private string searchQuery;

        // The LAYER is remembered alongside each adorner, not looked up again at removal time. Re-resolving
        // it via GetAdornerLayer(AdornedElement) fails once the page has been swapped out of pageHost - the
        // element is no longer in a tree, the lookup returns null, and the adorner stays behind on the layer
        // it was added to. That is a highlight that never goes away, on a page you are no longer searching.
        private readonly List<KeyValuePair<AdornerLayer, ElementHighlightAdorner>> pageHighlights =
            new List<KeyValuePair<AdornerLayer, ElementHighlightAdorner>>();

        // Bumped by every clear. A deferred highlight pass captures it and abandons its work if it no longer
        // matches: page changes and keystrokes can both queue a pass, and without this an older pass could
        // land AFTER a newer clear and re-adorn a page the user has already navigated away from.
        private int highlightGeneration;

        private void ClearPageHighlights()
        {
            highlightGeneration++;
            foreach (var pair in pageHighlights)
                pair.Key.Remove(pair.Value);
            pageHighlights.Clear();
        }

        /// <summary>
        /// Mark every control on the current page whose text contains the query, and scroll the first into
        /// view. Safe to call whenever the page or the query changes.
        /// </summary>
        private void HighlightMatchesOnPage()
        {
            ClearPageHighlights();

            if (string.IsNullOrEmpty(searchQuery) || pageHost.Content == null)
                return;

            // A page is built lazily on FIRST show, and selection runs before that content has been through
            // layout - inside a layout pass, in the menu-hosted case. Walking now would measure a tree with
            // no RenderSize and adorn nothing. Loaded priority puts this after the page has arranged.
            int generation = highlightGeneration;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new System.Action(() =>
            {
                // The query or the page may have moved on while this sat in the queue.
                if (generation != highlightGeneration || string.IsNullOrEmpty(searchQuery))
                    return;

                var root = pageHost.Content as DependencyObject;
                if (root == null)
                    return;

                FrameworkElement first = null;
                foreach (var el in MatchingElements(root, searchQuery))
                {
                    var layer = AdornerLayer.GetAdornerLayer(el);
                    if (layer == null)
                        continue;
                    var a = new ElementHighlightAdorner(el);
                    layer.Add(a);
                    pageHighlights.Add(new KeyValuePair<AdornerLayer, ElementHighlightAdorner>(layer, a));
                    if (first == null)
                        first = el;
                }

                first?.BringIntoView();

                if (pageHighlights.Count == 0)
                    CNC.Core.DebugLog.Write("ui", "settings search: \"" + searchQuery +
                                            "\" matched this page but no control on it could be marked");
            }));
        }

        // Controls on the page whose own text contains the query. Deliberately shallow about what counts as
        // "text": the thing being pointed at is what the user can READ, which is these five. A container is
        // skipped when a descendant of it also matches, so a hit marks the label or box itself rather than
        // drawing a box round half the page.
        private static IEnumerable<FrameworkElement> MatchingElements(DependencyObject root, string q)
        {
            var hits = new List<FrameworkElement>();
            Walk(root, q, hits);
            return hits;
        }

        private static void Walk(DependencyObject node, string q, List<FrameworkElement> hits)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);

                int before = hits.Count;
                Walk(child, q, hits);           // depth first, so the innermost match wins
                if (hits.Count > before)
                    continue;                    // something inside already matched - don't box the container too

                var el = child as FrameworkElement;
                if (el != null && el.IsVisible && Matches(child, q))
                    hits.Add(el);
            }
        }

        private static bool Matches(DependencyObject d, string q)
        {
            if (Contains((d as TextBlock)?.Text, q))
                return true;
            if (Contains((d as TextBox)?.Text, q))
                return true;
            if (Contains((d as GroupBox)?.Header as string, q))
                return true;

            // CheckBox / RadioButton / Button / Label all carry their caption as Content. Only a string
            // counts: a Content that is itself a control has already been walked as a child.
            var cc = d as ContentControl;
            if (cc != null && Contains(cc.Content as string, q))
                return true;

            // Tooltip hits matter most of all - that text IS on the page, but only on hover, so it is the
            // one kind of match the eye can never locate unaided.
            var fe = d as FrameworkElement;
            if (fe != null && Contains(ToolTipText(fe.ToolTip), q))
                return true;

            return false;
        }

        private static string ToolTipText(object tip)
        {
            var s = tip as string;
            if (s != null)
                return s;
            var tt = tip as ToolTip;
            return tt?.Content as string;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && needle != null &&
                   haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion

        // FindResource returns string.Empty (not null) for a key it does not have.
        private static string Localized(string key, string fallback)
        {
            var s = CNC.Core.LibStrings.FindResource(key);
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }

        // The first tooltip on the page whose text contains the query.
        private static SettingsSearchIndex.Tip MatchingTip(SettingsNavNode node, string q)
        {
            if (node.SearchTooltips == null)
                return null;

            return node.SearchTooltips.FirstOrDefault(
                t => t.Text != null && t.Text.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Name the control the tooltip belongs to, then quote it - "Send comments - Stream G-code comments
        // (in parentheses)...". Naming it is what turns the explanation from a floating fragment into
        // something you can go and find on the page. Owner is null when the control carries no caption of
        // its own (a bare field next to a separate label), and then this reads exactly as it did before.
        private static string DescribeTip(SettingsSearchIndex.Tip tip, string q)
        {
            const int whole = 130;
            int at = tip.Text.IndexOf(q, System.StringComparison.OrdinalIgnoreCase);
            string text;

            if (tip.Text.Length <= whole)
                text = Flatten(tip.Text);
            else if (at + q.Length <= whole)
            {
                // The hit is near the front, so keep the tooltip's own opening words and drop the tail.
                // A tooltip explains itself from its first word; a window centred on the hit throws that
                // away and starts mid-sentence, which is how this read before.
                text = Flatten(tip.Text.Substring(0, whole)) + "...";
            }
            else
                text = Snippet(tip.Text, at, q.Length);

            return string.IsNullOrEmpty(tip.Owner) ? text : tip.Owner + " - " + text;
        }

        private static string Flatten(string text)
        {
            var s = text.Replace((char)10, ' ').Replace((char)13, ' ').Trim();
            while (s.Contains("  "))
                s = s.Replace("  ", " ");
            return s;
        }

        // A readable fragment of page text around the hit, for the row tooltip.
        private static string Snippet(string text, int at, int len)
        {
            const int pad = 45;
            int from = System.Math.Max(0, at - pad);
            int to = System.Math.Min(text.Length, at + len + pad);
            var s = text.Substring(from, to - from).Replace((char)10, ' ').Replace((char)13, ' ').Trim();
            while (s.Contains("  "))
                s = s.Replace("  ", " ");
            return (from > 0 ? "..." : "") + s + (to < text.Length ? "..." : "");
        }

        private int MatchingPages()
        {
            return AllNodes().Count(n => !n.IsCategory && n.IsShown);
        }

        public void FocusSearch()
        {
            searchBox.Focus();
            searchBox.SelectAll();
        }

        #endregion
    }
}
