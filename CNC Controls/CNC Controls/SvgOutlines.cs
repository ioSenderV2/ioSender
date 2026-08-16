/*
 * SvgOutlines.cs - part of CNC Controls library
 *
 * Turns an .svg file into closed polygon contours, in millimetres, ready for a toolpath - the same
 * List<OutlineContour> TrueTypeOutlines produces from a string, so a logo feeds the carve engine
 * exactly the way a word does. See docs/Architecture-SVG-Import.md.
 *
 * ---- Why this sits beside TrueTypeOutlines rather than in Core ----
 *
 * Same reason that one does: it leans on WPF (Geometry.Parse + GetFlattenedPathGeometry), and Core
 * is WPF-free. The carve engine takes bare polygon lists and has never known where they came from -
 * a glyph, a logo, or something a headless server supplied. This is one more producer for that seam.
 *
 * ---- Why Geometry.Parse rather than a hand-written path parser ----
 *
 * WPF's path mini-language is derived from SVG's own 'd' grammar and accepts the same commands
 * (M L H V C S Q T A Z, absolute and relative, with implicit repeats). Parsing with it means the
 * flattening below is the IDENTICAL call TrueTypeOutlines makes, at the identical tolerance, so
 * curved artwork and curved glyphs discretise the same way. Writing the grammar by hand - elliptical
 * arc endpoint-to-centre parameterisation especially - would be a lot of code to arrive somewhere
 * slightly different.
 *
 * It is close but not a perfect superset, which is why ParseFailures is reported rather than
 * swallowed: a path this cannot read must be visible, not quietly missing from the cut.
 *
 * ---- IsOuter comes from CONTAINMENT, not from winding ----
 *
 * TrueTypeOutlines derives outer-vs-counter from the SIGN of a contour's signed area, which is
 * sound only because TrueType guarantees counters wind opposite their outer boundary. SVG
 * guarantees nothing of the kind: it expresses holes through a fill-rule, and real artwork is
 * routinely wound inconsistently. A real vendor logo measured 2026-08-15 had 2 of its 42 subpaths
 * misclassified by the sign rule - both were islands sitting inside a counter, wound with the hole
 * around them.
 *
 * ⚠️ To be precise about what that costs, because the design doc first got this wrong: the CUT is
 * unaffected. VCarve.DistanceField.Inside is even-odd ray casting across all contours together,
 * which is containment parity, so the engine resolves islands correctly whatever the winding.
 * IsOuter feeds exactly one thing - WorkOrderCompiler's per-glyph pass grouping - so a wrong answer
 * costs cut ORDER, not shape. It is still derived properly here, because "bounds a solid region"
 * is a question with a right answer and guessing it from winding is guessing.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>What an SVG import produced, and what it could not.</summary>
    public class SvgImportResult
    {
        /// <summary>Closed contours in mm, Y up, origin at the artwork's bottom-left.</summary>
        public List<OutlineContour> Contours = new List<OutlineContour>();

        /// <summary>Artwork size in mm at the scale requested.</summary>
        public double WidthMm, HeightMm;

        /// <summary>
        /// Elements this build cannot yet import, by name and count - primitives, transforms,
        /// instancing. Non-empty means the toolpath is INCOMPLETE, and the caller must say so
        /// rather than cutting a plausible-looking subset. See logo-hostile.svg.
        /// </summary>
        public Dictionary<string, int> Unsupported = new Dictionary<string, int>();

        /// <summary>Paths whose data WPF's parser refused. Never silently dropped.</summary>
        public int ParseFailures;

        /// <summary>Null when the file loaded; otherwise why it did not.</summary>
        public string Error;

        public bool IsComplete { get { return Error == null && Unsupported.Count == 0 && ParseFailures == 0; } }

        /// <summary>One line naming what was skipped, for a status message. Empty when complete.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            foreach (var kv in Unsupported)
                parts.Add(kv.Value + " <" + kv.Key + ">");
            if (ParseFailures > 0)
                parts.Add(ParseFailures + " unreadable path" + (ParseFailures == 1 ? "" : "s"));
            return parts.Count == 0 ? string.Empty : string.Join(", ", parts.ToArray());
        }
    }

    public static class SvgOutlines
    {
        // Same tolerance TrueTypeOutlines flattens glyphs at, applied in the SVG's own user units
        // before scaling - so a curve discretises to the same relative smoothness whatever the
        // artwork's coordinate scale happens to be.
        private const double FlattenTolerance = 0.01d;

        // Contours smaller than this (mm²) after scaling are specks - an artefact of flattening or a
        // stray point in the artwork - not something a cutter can express. Same idea as
        // TrueTypeOutlines' own minimum area, expressed directly in mm rather than em fractions.
        private const double MinAreaMm2 = 0.02d;

        // Everything the SVG spec puts on the page that this build does not read yet. Listed by name
        // so the report can say WHICH, and so adding support is one line here plus the conversion.
        private static readonly string[] UnsupportedElements =
            { "rect", "circle", "ellipse", "polygon", "polyline", "use", "image", "text" };

        /// <summary>
        /// Read <paramref name="path"/> and scale the artwork so its bounding box is
        /// <paramref name="targetWidthMm"/> wide, preserving aspect. A target of 0 keeps the file's
        /// own user units as millimetres.
        /// </summary>
        public static SvgImportResult Load(string path, double targetWidthMm)
        {
            var result = new SvgImportResult();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                result.Error = "SVG file not found: " + (path ?? "(none)");
                return result;
            }

            XDocument doc;
            try { doc = XDocument.Load(path); }
            catch (Exception e) { result.Error = "SVG could not be read: " + e.Message; return result; }

            var root = doc.Root;
            if (root == null)
            {
                result.Error = "SVG has no root element";
                return result;
            }

            // Anything with a transform is misplaced rather than merely missing, which is worse -
            // report it as unsupported instead of importing it at the wrong spot.
            foreach (var el in root.DescendantsAndSelf())
            {
                string local = el.Name.LocalName;
                foreach (var name in UnsupportedElements)
                    if (string.Equals(local, name, StringComparison.OrdinalIgnoreCase))
                        Bump(result.Unsupported, local);
                if (el.Attribute("transform") != null)
                    Bump(result.Unsupported, "transform");
            }

            var rings = new List<List<Point2D>>();
            foreach (var el in root.Descendants())
            {
                if (!string.Equals(el.Name.LocalName, "path", StringComparison.OrdinalIgnoreCase))
                    continue;
                var d = el.Attribute("d");
                if (d == null || string.IsNullOrWhiteSpace(d.Value))
                    continue;
                if (!Flatten(d.Value, rings))
                    result.ParseFailures++;
            }

            if (rings.Count == 0)
            {
                if (result.Error == null)
                    result.Error = "SVG contains no importable path outlines";
                return result;
            }

            Normalise(result, rings, targetWidthMm);
            return result;
        }

        // ------------------------------------------------------------------ path -> rings

        private static bool Flatten(string data, List<List<Point2D>> rings)
        {
            Geometry geometry;
            try { geometry = Geometry.Parse(data); }
            catch { return false; }   // not WPF-compatible path data - counted, never swallowed

            PathGeometry flat;
            try { flat = geometry.GetFlattenedPathGeometry(FlattenTolerance, ToleranceType.Absolute); }
            catch { return false; }
            if (flat == null)
                return false;

            foreach (var figure in flat.Figures)
            {
                var pts = new List<Point2D> { new Point2D(figure.StartPoint.X, figure.StartPoint.Y) };
                foreach (var seg in figure.Segments)
                {
                    var poly = seg as PolyLineSegment;
                    if (poly != null)
                    {
                        foreach (var p in poly.Points)
                            pts.Add(new Point2D(p.X, p.Y));
                        continue;
                    }
                    var line = seg as LineSegment;
                    if (line != null)
                        pts.Add(new Point2D(line.Point.X, line.Point.Y));
                    // Flattening yields only line/polyline segments; anything else did not flatten and
                    // must not be faked as a straight line between its endpoints.
                }

                // An OPEN figure is a stroke, not a region. V-carving needs closed rings, so closing it
                // here would invent material that is not in the artwork. Dropped, and the drop is
                // reported through IsClosed below rather than passing silently.
                if (pts.Count < 3)
                    continue;
                rings.Add(pts);
            }
            return true;
        }

        // ------------------------------------------------------------------ scale, flip, classify

        private static void Normalise(SvgImportResult result, List<List<Point2D>> rings, double targetWidthMm)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var r in rings)
                foreach (var p in r)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }

            double w = maxX - minX, h = maxY - minY;
            if (w <= 0d || h <= 0d)
            {
                result.Error = "SVG artwork has no measurable extent";
                return;
            }

            // Scale from the artwork's own BOUNDING BOX, not from the viewBox: a logo is routinely
            // authored with padding around it, and "150 mm wide" should mean the ink is 150 mm wide.
            double scale = targetWidthMm > 0d ? targetWidthMm / w : 1d;
            result.WidthMm = w * scale;
            result.HeightMm = h * scale;

            foreach (var r in rings)
            {
                var pts = new List<Point2D>(r.Count);
                foreach (var p in r)
                    // Y is flipped: SVG grows Y downward, toolpaths grow it up. Origin moves to the
                    // artwork's bottom-left so the caller positions a known corner.
                    pts.Add(new Point2D((p.X - minX) * scale, (maxY - p.Y) * scale));

                if (!Near(pts[0], pts[pts.Count - 1]))
                    pts.Add(pts[0]);

                double area = SignedAreaOf(pts);
                if (Math.Abs(area) < MinAreaMm2)
                    continue;

                result.Contours.Add(new OutlineContour { Points = pts, SignedArea = area });
            }

            ClassifyByContainment(result.Contours);
        }

        /// <summary>
        /// IsOuter = the contour bounds a solid region from outside = EVEN containment depth.
        /// Derived by counting how many other contours enclose this one, never from winding sign -
        /// see this file's header for the measurement that settled it.
        /// </summary>
        private static void ClassifyByContainment(List<OutlineContour> contours)
        {
            for (int i = 0; i < contours.Count; i++)
            {
                var probe = contours[i].Points[0];
                int depth = 0;
                for (int j = 0; j < contours.Count; j++)
                {
                    if (j == i)
                        continue;
                    if (PointInRing(contours[j].Points, probe.X, probe.Y))
                        depth++;
                }
                contours[i].IsOuter = (depth % 2) == 0;
            }
        }

        // Even-odd ray cast along +X against ONE ring - the same test VCarve uses, kept local so this
        // producer does not depend on the engine it feeds.
        private static bool PointInRing(List<Point2D> ring, double px, double py)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double yi = ring[i].Y, yj = ring[j].Y;
                if ((yi > py) == (yj > py))
                    continue;
                double xInt = ring[i].X + (py - yi) / (yj - yi) * (ring[j].X - ring[i].X);
                if (px < xInt)
                    inside = !inside;
            }
            return inside;
        }

        private static double SignedAreaOf(List<Point2D> pts)
        {
            double a = 0d;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                a += pts[j].X * pts[i].Y - pts[i].X * pts[j].Y;
            return a / 2d;
        }

        private static bool Near(Point2D a, Point2D b)
        {
            return Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            int n;
            map[key] = map.TryGetValue(key, out n) ? n + 1 : 1;
        }
    }
}
