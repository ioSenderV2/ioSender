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
using System.Windows;
using System.Windows.Controls;
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

        public StretchTabControl()
        {
            // Resize -> queue a recompute that runs AFTER this layout pass. Never recompute synchronously in
            // SizeChanged/LayoutUpdated - that mutates layout from within layout and re-enters it.
            SizeChanged += (s, e) => QueueUpdate();
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
