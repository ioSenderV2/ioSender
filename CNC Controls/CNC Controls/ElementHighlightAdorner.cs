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
        private const double Thickness = 1.5d;   // const, so declaration order against Outline does not matter

        private static readonly Pen Outline = FreezePen(new Pen(OutlineBrush, Thickness));

        private readonly bool tooltipOnly;

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
        private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

        // A DASHED outline means the match is not in anything you can see: it is in this control's tooltip.
        // Without the distinction a tooltip hit marks a row with no matching text anywhere on it, which reads
        // as a wrong result - you have to hover to discover why it is lit at all. Dashes say "the reason is
        // hidden here, hover me" without changing the tooltip itself.
        //
        // TWO things made the gap look nearly closed, and neither was UiScale. An earlier attempt scaled the
        // gap by UiScale and changed NOTHING, because UiScale is the app's own zoom and sits at 1.0 unless the
        // operator has zoomed - a high-DPI SCREEN is a Windows display setting, which WPF already accounts for
        // in DIP. Multiplying by 1.0 is not a fix. Recorded so it is not tried a third time.
        //
        //  1. Pen.DashCap defaults to PenLineCap.SQUARE, which extends every dash by thickness/2 at BOTH ends.
        //     At 1.5 thickness that is 1.5 DIP added to each dash, taken straight out of the gap - a nominal
        //     3 DIP gap rendered as about 1.5. Flat caps give the gap back.
        //  2. The pattern was simply small in absolute terms.
        //
        // So the pattern is stated in DIP here and converted to what DashStyle actually wants, which is
        // multiples of PEN THICKNESS - the units mismatch is what made the original numbers so easy to
        // misjudge. UiScale still multiplies, which is right if the operator zooms (the adorner is inside the
        // scaled tree, so this makes the gap grow faster than the stroke), but it is not what fixed this.
        private const double DashDip = 4d;
        private const double GapDip = 5d;

        private static Pen cachedTooltipPen;
        private static double cachedScale = double.NaN;

        private static Pen TooltipOutlineFor(double scale)
        {
            if (cachedTooltipPen != null && cachedScale == scale)
                return cachedTooltipPen;

            cachedScale = scale;
            cachedTooltipPen = FreezePen(new Pen(OutlineBrush, Thickness)
            {
                DashCap = PenLineCap.Flat,   // see 1. above - Square silently eats half the gap
                DashStyle = new DashStyle(new double[] { DashDip / Thickness, GapDip * scale / Thickness }, 0d)
            });
            return cachedTooltipPen;
        }

        // Clamped: a wild UiScale must not turn the outline into four corner ticks, and a null config
        // (designer, or a host that never loaded settings) must not throw.
        private static double UiScale()
        {
            double s = AppConfig.Settings?.Base?.UiScale ?? 1d;
            return double.IsNaN(s) || s < 1d ? 1d : System.Math.Min(s, 3d);
        }

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
            dc.DrawRoundedRectangle(Fill, tooltipOnly ? TooltipOutlineFor(UiScale()) : Outline, r, 3d, 3d);
        }
    }
}
