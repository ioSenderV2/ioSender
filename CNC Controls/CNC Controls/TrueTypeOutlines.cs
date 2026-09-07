/*
 * TrueTypeOutlines.cs - part of CNC Controls library
 *
 * Turns a string set in any installed font into closed polygon contours, in millimetres, ready for a
 * toolpath. This is the front half of V-carving: the carve engine works on polygons and knows nothing
 * about fonts, so everything font-shaped stops here.
 *
 * ---- Why this is in CNC.Controls and not Core ----
 *
 * Reading a TrueType outline needs a font rasteriser, and the one available is WPF's. Core is WPF-free
 * and staying that way (see CNC Core.csproj's comment where the references used to be), so the split is:
 * this class knows about fonts and produces plain CNC.Core.Point2D polygons; the carve engine in Core
 * consumes those and never learns what a glyph is. A headless server would supply outlines some other
 * way - from a client, or from its own rasteriser - without the engine changing.
 *
 * ---- Why FormattedText rather than per-glyph GlyphTypeface ----
 *
 * FormattedText.BuildGeometry lays the whole string out and returns one Geometry: kerning pairs,
 * ligatures, bidi and glyph substitution all come free and correct. Walking glyphs individually with
 * GlyphTypeface.GetGlyphOutline means reimplementing text layout, badly, in exchange for nothing.
 *
 * ---- What the caller gets, and the one thing that matters downstream ----
 *
 * A list of CLOSED contours with their signed area recorded. Sign is not decoration: a glyph's counters -
 * the hole in an O, the triangle in an A, the two bowls of a B - are contours wound OPPOSITE to the
 * outer boundary, and a carve engine that cannot tell them apart will happily carve the hole solid.
 * Everything about V-carving quality downstream depends on getting inside/outside right, so it is
 * established here, once, where the winding is still authoritative.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    // OutlineContour moved to CNC.Common (Geometry.cs, namespace CNC.Core) on 2026-09-05, beside
    // Point2D. It is the shared type between outline producers and the carve engine, and the producers
    // are no longer all WPF: CNC.Svg reads artwork with no WPF so the same contours can be built on a
    // Linux appliance. Nothing else changed - this file already had "using CNC.Core;".

    public static class TrueTypeOutlines
    {
        // Em size the geometry is built at. Nothing is cut at this scale - everything is scaled to the
        // requested cap height afterwards - it exists purely so the flattening tolerance below is a small
        // FRACTION of the glyph rather than a size comparable to it. Build at em 12 and a 0.01 tolerance
        // would be coarse; at 1000 it is a thousandth of the em.
        private const double BuildEmSize = 1000d;

        // Flattening tolerance in those same build units, so ~0.02% of the em. Curves become polylines
        // here and never get any smoother downstream, so this sets the ceiling on carve quality; it is
        // cheap because the distance field the engine builds is a far coarser grid anyway.
        private const double FlattenTolerance = 0.2d;

        // A contour smaller than this fraction of the em squared is a rasteriser artefact or a stray
        // speck, not a feature anyone asked to cut. Dropping them here keeps the engine from trying to
        // carve something a fraction of a bit-width across.
        private const double MinAreaEmFraction = 1e-5;

        /// <summary>Font families installed on this machine, sorted, for a picker.</summary>
        public static IEnumerable<string> InstalledFamilies()
        {
            var names = new List<string>();
            foreach (var f in Fonts.SystemFontFamilies)
            {
                string n = f.Source;
                if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n))
                    names.Add(n);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Set <paramref name="text"/> in <paramref name="fontFamily"/> and return its outline as closed
        /// contours in millimetres, baseline at Y=0, first glyph starting at X=0, Y increasing UPWARD.
        /// </summary>
        /// <param name="capHeightMm">
        /// Height of a capital letter, in mm - the same thing it means for the stroke font, so switching
        /// between the two does not silently resize the job.
        /// </param>
        /// <returns>Empty when the text is blank or the family cannot be resolved. Never null.</returns>
        public static List<OutlineContour> Render(string text, string fontFamily, double capHeightMm,
                                                  bool bold = false, bool italic = false)
        {
            var result = new List<OutlineContour>();
            if (string.IsNullOrEmpty(text) || capHeightMm <= 0d)
                return result;

            var typeface = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Arial" : fontFamily),
                                        italic ? FontStyles.Italic : FontStyles.Normal,
                                        bold ? FontWeights.Bold : FontWeights.Normal,
                                        FontStretches.Normal);

            // Scale from the typeface's OWN cap height rather than from the em box. Em size is a design
            // unit that varies between fonts - two faces at the same em size have visibly different
            // capital heights - so scaling by em would make "10 mm text" mean different things in
            // different fonts. CapsHeight is a fraction of the em, published by the font itself.
            double capFraction = 0.7d;   // a sane fallback; most Latin faces sit near this
            GlyphTypeface gt;
            if (typeface.TryGetGlyphTypeface(out gt) && gt.CapsHeight > 0d)
                capFraction = gt.CapsHeight;

            double mmPerUnit = capHeightMm / (capFraction * BuildEmSize);

            Geometry geometry;
            double baselineDown;
            try
            {
                var ft = BuildText(text, typeface);
                geometry = ft.BuildGeometry(new Point(0d, 0d));

                // The baseline comes from the FormattedText, NOT from GlyphTypeface.Baseline * emSize.
                // BuildGeometry places the origin at the top-left of the LINE BOX, and the line box is
                // taller than the font's own ascent - so the two differ, and using the wrong one drops
                // every letter below the baseline by that difference (measured: 0.228 mm at a 10 mm cap,
                // which is a visible misplacement against a positioned toolpath).
                baselineDown = ft.Baseline;
            }
            catch
            {
                return result;   // an unresolvable family or a text engine refusal is "nothing to cut"
            }

            if (geometry == null || geometry.IsEmpty())
                return result;

            // Flatten curves to polylines ONCE, here. Everything downstream is polygon arithmetic.
            var flat = geometry.GetFlattenedPathGeometry(FlattenTolerance, ToleranceType.Absolute);
            if (flat == null)
                return result;

            // Y is flipped about that baseline: WPF grows Y downward, toolpaths grow it up. Getting this
            // wrong renders the text upside down and, worse, reverses every contour's winding - which is
            // exactly the signal the carve engine depends on.
            double minArea = MinAreaEmFraction * BuildEmSize * BuildEmSize * mmPerUnit * mmPerUnit;

            foreach (var figure in flat.Figures)
            {
                var pts = new List<Point2D>();
                AddPoint(pts, figure.StartPoint, mmPerUnit, baselineDown);

                foreach (var seg in figure.Segments)
                {
                    var poly = seg as PolyLineSegment;
                    if (poly != null)
                    {
                        foreach (var p in poly.Points)
                            AddPoint(pts, p, mmPerUnit, baselineDown);
                        continue;
                    }
                    var line = seg as LineSegment;
                    if (line != null)
                        AddPoint(pts, line.Point, mmPerUnit, baselineDown);
                    // GetFlattenedPathGeometry yields only line/polyline segments; anything else would be
                    // a curve that did not flatten, which cannot be cut and must not be silently dropped
                    // as a straight line between its endpoints.
                }

                if (pts.Count < 3)
                    continue;

                // Close it explicitly. A figure that reports IsClosed carries no duplicate final point,
                // and the engine's inside/outside test needs the ring to actually meet.
                if (!Near(pts[0], pts[pts.Count - 1]))
                    pts.Add(pts[0]);

                double area = SignedAreaOf(pts);
                if (Math.Abs(area) < minArea)
                    continue;

                result.Add(new OutlineContour { Points = pts, SignedArea = area });
            }

            // Whichever winding the LARGEST contour has is "outer" - true for any sane glyph, since a
            // counter is by construction enclosed by the boundary it sits inside. Comparing against a
            // fixed sign instead would depend on the font's fill rule and on the Y flip above.
            double biggest = 0d;
            foreach (var c in result)
                if (Math.Abs(c.SignedArea) > Math.Abs(biggest))
                    biggest = c.SignedArea;
            foreach (var c in result)
                c.IsOuter = (c.SignedArea > 0d) == (biggest > 0d);

            return result;
        }

        /// <summary>Width and height the rendered text occupies, in mm - for layout before any carving.</summary>
        public static Point2D Measure(string text, string fontFamily, double capHeightMm,
                                      bool bold = false, bool italic = false)
        {
            var contours = Render(text, fontFamily, capHeightMm, bold, italic);
            if (contours.Count == 0)
                return new Point2D(0d, 0d);

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var c in contours)
                foreach (var p in c.Points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
            return new Point2D(maxX - minX, maxY - minY);
        }

        private static FormattedText BuildText(string text, Typeface typeface)
        {
#pragma warning disable 618   // the pixelsPerDip overload is not on net462's FormattedText
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     typeface, BuildEmSize, Brushes.Black);
#pragma warning restore 618
        }

        private static void AddPoint(List<Point2D> pts, Point p, double mmPerUnit, double baselineDown)
        {
            // Flip about the baseline and scale in one step - see the note at the call site on why the
            // flip matters beyond appearance.
            var q = new Point2D(p.X * mmPerUnit, (baselineDown - p.Y) * mmPerUnit);
            if (pts.Count > 0 && Near(pts[pts.Count - 1], q))
                return;   // duplicate points make zero-length moves and upset the area/winding test
            pts.Add(q);
        }

        private static bool Near(Point2D a, Point2D b)
        {
            return Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
        }

        // Shoelace. Sign carries the winding, magnitude the enclosed area.
        private static double SignedAreaOf(List<Point2D> pts)
        {
            double sum = 0d;
            for (int i = 0; i < pts.Count - 1; i++)
                sum += pts[i].X * pts[i + 1].Y - pts[i + 1].X * pts[i].Y;
            return sum / 2d;
        }
    }
}
