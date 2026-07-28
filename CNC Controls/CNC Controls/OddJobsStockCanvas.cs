/*
 * OddJobsStockCanvas.cs - part of CNC Controls library
 *
 * Shared stock-outline drawing + click-to-place for the Odd Jobs job wizards that have an X/Y anchor point
 * (Drill/Bore, Pocket, Contour/Slot - Surface Stock has none, it always covers the whole stock). Draws the
 * real stock rectangle (from Setup's own Width/Height - OddJobsSetupConfig.Section) to scale, origin marker
 * at the front-left corner (matches Start Job's own corner convention - G59's origin), and gives callers the
 * mm<->pixel transform so they can draw their own shape and wire up a click/drag placement handler.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CNC.Controls
{
    public static class OddJobsStockCanvas
    {
        public struct Transform
        {
            public double Scale, OriginX, OriginY;
        }

        // Clears the canvas, draws the stock rectangle + origin marker, and returns the mm-to-pixel
        // transform (front-left corner = work X0/Y0, +X right, +Y "up" - screen Y grows down so it's
        // flipped). Falls back to a 100x100 mm placeholder if Setup hasn't set a stock size yet.
        public static Transform DrawStock(Canvas canvas)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            var t = new Transform { Scale = 1d, OriginX = 0d, OriginY = 0d };
            if (w <= 0 || h <= 0)
                return t;

            var s = OddJobsSetupConfig.Section;
            double stockW = s != null && s.Width > 0d ? s.Width : 100d;
            double stockH = s != null && s.Height > 0d ? s.Height : 100d;

            const double marginPx = 20d;
            double availW = Math.Max(10d, w - marginPx * 2), availH = Math.Max(10d, h - marginPx * 2);
            double scale = Math.Max(0.05d, Math.Min(availW / stockW, availH / stockH));

            double drawW = stockW * scale, drawH = stockH * scale;
            double ox = (w - drawW) / 2d;
            double oy = (h - drawH) / 2d + drawH;   // pixel Y of the front-left (work Y=0) corner

            var rect = new Rectangle
            {
                Width = drawW, Height = drawH, Stroke = Brushes.SaddleBrown, StrokeThickness = 1.5,
                Fill = StockFillBrush(s?.Material)
            };
            Canvas.SetLeft(rect, ox); Canvas.SetTop(rect, oy - drawH);
            canvas.Children.Add(rect);

            var origin = new Ellipse { Width = 7, Height = 7, Fill = Brushes.OrangeRed };
            Canvas.SetLeft(origin, ox - 3.5); Canvas.SetTop(origin, oy - 3.5);
            canvas.Children.Add(origin);

            t.Scale = scale; t.OriginX = ox; t.OriginY = oy;

            // Shared keep-out inset from Setup (same dotted-red look Start Job's own drawing and Surface
            // Stock use) - every wizard drawn via this shared canvas gets it for free, no per-wizard drawing
            // code needed.
            if (TrySafeArea(out double minX, out double minY, out double maxX, out double maxY))
            {
                var k0 = ToPixel(t, minX, maxY);
                var k1 = ToPixel(t, maxX, minY);
                var keepOutRect = new Rectangle
                {
                    Width = Math.Abs(k1.X - k0.X), Height = Math.Abs(k1.Y - k0.Y),
                    Stroke = Brushes.Red, StrokeThickness = 1.5, StrokeDashArray = { 1, 2 }, StrokeDashCap = PenLineCap.Round
                };
                Canvas.SetLeft(keepOutRect, Math.Min(k0.X, k1.X));
                Canvas.SetTop(keepOutRect, Math.Min(k0.Y, k1.Y));
                canvas.Children.Add(keepOutRect);
            }

            return t;
        }

        // Perimeter clearance from Setup - see StartJobSettings.KeepOutInset's own comment. >= 0 always.
        public static double KeepOutInset()
        {
            return Math.Max(0d, OddJobsSetupConfig.Section?.KeepOutInset ?? 15d);
        }

        // The work-space rectangle every wizard's placement should stay inside: the stock footprint (Setup's
        // Width/Height, or the 100x100 placeholder DrawStock falls back to) inset by KeepOutInset() on all 4
        // sides. Returns false when the inset swallows the whole area (nothing safe to place in).
        public static bool TrySafeArea(out double minX, out double minY, out double maxX, out double maxY)
        {
            var s = OddJobsSetupConfig.Section;
            double stockW = s != null && s.Width > 0d ? s.Width : 100d;
            double stockH = s != null && s.Height > 0d ? s.Height : 100d;
            double inset = KeepOutInset();
            minX = inset; minY = inset; maxX = stockW - inset; maxY = stockH - inset;
            return maxX > minX && maxY > minY;
        }

        // Clamp a work-space point (a wizard's own click/drag placement) so it can't land inside the shared
        // keep-out margin - every wizard with an X/Y anchor (DrillBore/Counterbore/Pocket/Contour) shares this
        // same PlaceFromMouse pattern, so one clamp here covers all 4. Only clamps the placement POINT itself,
        // not the shape's full footprint around it (a large pocket centered just inside the margin can still
        // overlap it) - good enough for "don't let a click land ON the clamps", not a full collision check.
        public static Point ClampToKeepOut(double x, double y)
        {
            if (!TrySafeArea(out double minX, out double minY, out double maxX, out double maxY))
                return new Point(x, y);   // inset swallows the whole area - nothing valid to clamp to, leave as-is
            return new Point(Math.Min(Math.Max(x, minX), maxX), Math.Min(Math.Max(y, minY), maxY));
        }

        // Stock fill color tracks the Setup tab's chosen material - a quick visual cue, not a real-world
        // color match. Falls back to the original tan/parchment for an unset or unlisted material.
        private static Brush StockFillBrush(string material)
        {
            switch (material)
            {
                case "MDF": return new SolidColorBrush(Color.FromRgb(0xB8, 0xBA, 0x6E));         // light olive green
                case "Softwood": return new SolidColorBrush(Color.FromRgb(0xCC, 0x9A, 0x5A));     // yellowish brown
                case "Hardwood": return new SolidColorBrush(Color.FromRgb(0xE2, 0xCC, 0xA1));     // darker tan than the default
                case "Aluminum": return new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xC9));     // light grey
                case "Steel": return new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));        // darker grey
                default: return new SolidColorBrush(Color.FromRgb(0xF5, 0xE9, 0xD7));             // original tan/parchment
            }
        }

        public static Point ToPixel(Transform t, double x, double y)
        {
            return new Point(t.OriginX + x * t.Scale, t.OriginY - y * t.Scale);
        }

        public static Point ToWork(Transform t, Point p)
        {
            return new Point((p.X - t.OriginX) / t.Scale, (t.OriginY - p.Y) / t.Scale);
        }
    }
}
