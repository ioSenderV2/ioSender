/*
 * StretchTabControl.cs - part of CNC Controls library
 *
 * A TabControl that spreads its tab headers to fill the strip width: each tab keeps its natural (content)
 * width - plus one space character on each side - scaled up proportionally so a wider label gets a wider
 * tab and the row fills the strip.
 *
 * Timing (this is the important part): the widths are recomputed ONLY on the discrete data event
 * (tabs added/removed) and, for a resize, on a DEFERRED dispatcher callback that runs AFTER the layout
 * pass. It never runs from inside a layout pass (no LayoutUpdated, no synchronous work in SizeChanged):
 * doing so would mutate layout from within layout and re-enter it. Assigning the widths simply causes one
 * ordinary follow-up pass, which nothing here subscribes to, so it cannot loop. The cost is that during a
 * drag-resize the tabs briefly show old widths and snap to new ones a frame later - an accepted trade.
 *
 * Why per-tab widths and not an ItemsPanel: the default TabControl template hosts its headers in a fixed
 * TabPanel (IsItemsHost), so a replacement ItemsPanel is ignored; assigning each TabItem.Width is honoured.
 */

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CNC.Controls
{
    public class StretchTabControl : TabControl
    {
        // Slack left under the control width so the assigned widths sum just below the header area and the
        // row never wraps to a second line (a wrapped TabPanel justifies each row - scattered-looking tabs).
        private const double HeaderInset = 8d;

        private bool updateQueued;

        // Drag-to-reorder state. draggedTab is the header grabbed on mouse-down; dragging flips true once the
        // pointer has moved past the system drag threshold (so a plain click still just selects the tab).
        // movedThisDrag records whether the order actually changed, so we only persist / notify on a real move.
        private TabItem draggedTab;
        private Point dragStart;
        private bool dragging;
        private bool movedThisDrag;
        private bool orderApplied;

        // When true, a header can be dragged left/right to change tab order. Only works for tabs held directly
        // in Items (ItemsSource-bound tabs would need the source reordered, so reorder is skipped for those).
        public static readonly DependencyProperty AllowReorderProperty = DependencyProperty.Register(
            nameof(AllowReorder), typeof(bool), typeof(StretchTabControl), new PropertyMetadata(true));

        public bool AllowReorder
        {
            get { return (bool)GetValue(AllowReorderProperty); }
            set { SetValue(AllowReorderProperty, value); }
        }

        // When set, this control owns persistence of its own tab order: on load it restores the saved order for
        // this key, and after each drag-reorder it writes the new order back (to the TabOrder config section).
        // Leave unset for bars whose order lives in another store (the main bar and Tools sub-tabs persist via
        // the layout tree instead - those hosts listen to TabsReordered and write there).
        public static readonly DependencyProperty PersistKeyProperty = DependencyProperty.Register(
            nameof(PersistKey), typeof(string), typeof(StretchTabControl), new PropertyMetadata(null));

        public string PersistKey
        {
            get { return (string)GetValue(PersistKeyProperty); }
            set { SetValue(PersistKeyProperty, value); }
        }

        // Raised after a drag-reorder that actually changed the tab order. Hosts whose order lives in another
        // store (main bar, Tools) subscribe to persist the new Items order there.
        public event EventHandler TabsReordered;

        public StretchTabControl()
        {
            // Resize -> queue a recompute that runs AFTER this layout pass. Never recompute synchronously in
            // SizeChanged/LayoutUpdated - that mutates layout from within layout and re-enters it.
            SizeChanged += (s, e) => QueueUpdate();

            // Restore the saved order once the tabs exist (static XAML items are present by Loaded; code-built
            // bars add their items before this fires during their own construction).
            Loaded += (s, e) => ApplySavedOrder();

            // Drag-reorder. Preview handlers so we see the gestures before TabItem consumes them; we never mark
            // the events handled, so normal click-to-select still works.
            PreviewMouseLeftButtonDown += OnReorderButtonDown;
            PreviewMouseMove += OnReorderMouseMove;
            PreviewMouseLeftButtonUp += (s, e) => EndReorder();
            LostMouseCapture += (s, e) => EndReorder();
        }

        // A tab's stable identity for persistence: x:Name, else Tag string, else header text. Hosts that persist
        // (via PersistKey or TabsReordered) give each tab a Name or a Tag so the key survives localization.
        private static string IdOf(TabItem tab)
        {
            if (tab == null)
                return string.Empty;
            if (!string.IsNullOrEmpty(tab.Name))
                return tab.Name;
            return (tab.Tag as string) ?? tab.Header?.ToString() ?? string.Empty;
        }

        private void ApplySavedOrder()
        {
            if (orderApplied || string.IsNullOrEmpty(PersistKey) || ItemsSource != null)
                return;
            orderApplied = true;

            var saved = ConfigStore.Get<TabOrderConfig>()?.Get(PersistKey);
            if (saved == null || saved.Count == 0)
                return;

            var items = Items.Cast<TabItem>().ToList();
            // Present tabs sorted by their position in the saved order; tabs the save doesn't mention keep their
            // relative order and fall after the known ones (OrderBy is stable). Then apply as minimal moves.
            var ordered = items.OrderBy(ti =>
            {
                int i = saved.IndexOf(IdOf(ti));
                return i < 0 ? int.MaxValue : i;
            }).ToList();

            for (int target = 0; target < ordered.Count; target++)
            {
                var ti = ordered[target];
                int cur = Items.IndexOf(ti);
                if (cur != target)
                {
                    Items.Remove(ti);
                    Items.Insert(target, ti);
                }
            }
            QueueUpdate();
        }

        private void OnReorderButtonDown(object sender, MouseButtonEventArgs e)
        {
            draggedTab = null;
            dragging = false;
            // Only tabs we own (no ItemsSource) can be reordered in place.
            if (!AllowReorder || ItemsSource != null)
                return;
            // FindTabItem only reaches a TabItem when the hit is on a header - the selected content is hosted by
            // the TabControl template's own ContentPresenter, outside any TabItem - so this is header-only.
            var tab = FindTabItem(e.OriginalSource as DependencyObject);
            if (tab != null && Items.Contains(tab))
            {
                draggedTab = tab;
                dragStart = e.GetPosition(this);
                movedThisDrag = false;
            }
        }

        private void OnReorderMouseMove(object sender, MouseEventArgs e)
        {
            if (draggedTab == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(this);
            if (!dragging)
            {
                if (Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                dragging = true;
                Mouse.OverrideCursor = Cursors.SizeWE;
            }

            // Tab header under the pointer (null over content or empty strip -> no move this tick).
            var over = FindTabItem(VisualTreeHelper.HitTest(this, pos)?.VisualHit);
            if (over == null || over == draggedTab)
                return;

            int to = Items.IndexOf(over);
            if (to < 0 || Items.IndexOf(draggedTab) < 0)
                return;

            Items.Remove(draggedTab);
            Items.Insert(to, draggedTab);
            draggedTab.IsSelected = true;
            movedThisDrag = true;
            QueueUpdate();
        }

        private void EndReorder()
        {
            if (dragging)
                Mouse.OverrideCursor = null;
            if (movedThisDrag)
            {
                // Self-persist when we own the order; otherwise let the host store it (main bar / Tools).
                if (!string.IsNullOrEmpty(PersistKey) && ItemsSource == null)
                {
                    var store = ConfigStore.Get<TabOrderConfig>();
                    if (store != null)
                    {
                        store.Set(PersistKey, Items.Cast<TabItem>().Select(IdOf));
                        AppConfig.Settings?.Save();
                    }
                }
                TabsReordered?.Invoke(this, EventArgs.Empty);
            }
            draggedTab = null;
            dragging = false;
            movedThisDrag = false;
        }

        // Walk the tree from a hit visual up to the enclosing TabItem, if any (visual parents, with a logical
        // fallback for content elements such as a Run inside the header text).
        private TabItem FindTabItem(DependencyObject d)
        {
            while (d != null && !(d is TabItem))
            {
                DependencyObject parent = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    ? VisualTreeHelper.GetParent(d) : null;
                d = parent ?? LogicalTreeHelper.GetParent(d);
            }
            return d as TabItem;
        }

        protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(e);
            QueueUpdate();
        }

        private void QueueUpdate()
        {
            if (updateQueued)
                return;
            updateQueued = true;
            // Background priority = after the current measure/arrange/render. Setting the widths then causes
            // one more ordinary layout pass; nothing here is subscribed to it, so it does not loop. Coalesces
            // the flurry of SizeChanged events during a drag into a single recompute.
            Dispatcher.BeginInvoke(new Action(() => { updateQueued = false; UpdateTabWidths(); }), DispatcherPriority.Background);
        }

        // Width of a single space character in this control's font - the per-side minimum padding.
        private double SpaceWidth()
        {
            try
            {
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var ft = new FormattedText(" ", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black, dpi);
                double w = ft.WidthIncludingTrailingWhitespace;
                return w > 0d ? w : FontSize * 0.28d;
            }
            catch { return FontSize * 0.28d; }
        }

        private void UpdateTabWidths()
        {
            if (ActualWidth <= 0d)
                return;

            var tabs = new List<TabItem>();
            foreach (var item in Items)
            {
                var ti = item as TabItem ?? ItemContainerGenerator.ContainerFromItem(item) as TabItem;
                if (ti != null && ti.Visibility != Visibility.Collapsed)
                    tabs.Add(ti);
            }
            if (tabs.Count == 0)
                return;

            // Baseline width of every tab: natural width (DesiredSize includes each tab's own margin) plus one
            // space each side, so a tab never sits tighter than "label + a space either side".
            double pad = 2d * SpaceWidth();
            double totalMin = 0d;
            var minWidth = new double[tabs.Count];
            for (int i = 0; i < tabs.Count; i++)
            {
                tabs[i].ClearValue(WidthProperty);
                tabs[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                minWidth[i] = tabs[i].DesiredSize.Width + pad;
                totalMin += minWidth[i];
            }
            if (totalMin <= 0d)
                return;

            // Fill the strip when there is room (scale up proportionally); else fall back to the padded minimum.
            double available = ActualWidth
                             - BorderThickness.Left - BorderThickness.Right
                             - Padding.Left - Padding.Right
                             - HeaderInset;
            double scale = available > totalMin ? available / totalMin : 1d;
            for (int i = 0; i < tabs.Count; i++)
            {
                double margin = tabs[i].Margin.Left + tabs[i].Margin.Right;
                tabs[i].Width = Math.Max(0d, minWidth[i] * scale - margin);
            }
        }
    }
}
