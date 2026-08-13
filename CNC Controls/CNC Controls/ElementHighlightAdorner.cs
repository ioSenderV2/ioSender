/*
 * ElementHighlightAdorner.cs - part of CNC Controls library
 *
 * Tints and outlines a whole UIElement, to point at the control a search matched.
 *
 * Distinct from SearchHighlightAdorner, which highlights character RANGES inside one TextBox using
 * GetRectFromCharacterIndex. That is the right tool for the console scrollback - one control, many hits
 * inside its text - and the wrong one here, where the hits are separate controls scattered down a
 * settings page and what needs marking is each control as a whole.
 *
 * Adorners are used for both because the alternative - reaching into a page's own styles to set a
 * background - would mean the shell knowing about every settings panel's markup, and would fight any
 * local Background those panels already set (a Style setter cannot override a local value).
 */

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CNC.Controls
{
    public class ElementHighlightAdorner : Adorner
    {
        // Same palette as the console's find highlighting, so "this is a search hit" looks the same
        // wherever it appears.
        private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xE0, 0x4A)));
        private static readonly Brush OutlineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xEE, 0xD8, 0x5A, 0x00)));
        private static readonly Pen Outline = FreezePen(new Pen(OutlineBrush, 1.5d));

        // A DASHED outline means the match is not in anything you can see: it is in this control's tooltip.
        // Without the distinction a tooltip hit marks a row with no matching text anywhere on it, which reads
        // as a wrong result - you have to hover to discover why it is lit at all. Dashes say "the reason is
        // hidden here, hover me" without changing the tooltip itself.
        private static readonly Pen TooltipOutline = FreezePen(new Pen(OutlineBrush, 1.5d)
        {
            DashStyle = new DashStyle(new double[] { 3d, 2d }, 0d)
        });

        private readonly bool tooltipOnly;

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
        private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

        /// <param name="tooltipOnly">
        /// True when the query was found only in the element's tooltip, not in any text it displays.
        /// </param>
        public ElementHighlightAdorner(UIElement adornedElement, bool tooltipOnly = false) : base(adornedElement)
        {
            this.tooltipOnly = tooltipOnly;
            IsHitTestVisible = false;   // the control underneath must stay clickable
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width <= 0d || size.Height <= 0d)
                return;

            // Bleed slightly outside the control: a checkbox or label sized tight to its text reads better
            // with the mark sitting just proud of it than with the outline clipping the glyphs.
            var r = new Rect(-2d, -1d, size.Width + 4d, size.Height + 2d);
            dc.DrawRoundedRectangle(Fill, tooltipOnly ? TooltipOutline : Outline, r, 3d, 3d);
        }
    }
}
