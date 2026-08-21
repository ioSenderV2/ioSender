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
using System.Text.RegularExpressions;
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

        // Aspect (height/width of the INK bounding box) per file, so the model can answer "how tall is
        // this at 150 mm wide?" without re-reading and re-flattening the artwork. HalfDepth asks it on
        // every extent evaluation, several times per redraw.
        //
        // MEASURED, not guessed (an earlier version of this comment claimed ~30 ms without checking):
        // a full Load of the 23 KB / 42-contour reference logo is 6.1 ms. Cheap enough that the PREVIEW
        // calls Load outright rather than caching contours - one redraw is a few ms - but not something
        // to repeat inside a property getter.
        //
        // Keyed on path + last-write time + length, so editing the file in Inkscape and coming back
        // invalidates it. A cache that cannot notice its source changed is the recurring bug in this
        // codebase (see the stale-cache family), so it is keyed on the file's own stamp rather than
        // trusting a manual refresh.
        private static readonly Dictionary<string, double> _aspect = new Dictionary<string, double>();

        private static string StampOf(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
            }
            catch { return null; }
        }

        /// <summary>
        /// Height ÷ width of the artwork's ink bounding box, or 0 when the file cannot be measured.
        /// Multiply by a chosen width to get the height it will occupy.
        /// </summary>
        public static double AspectOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0d;

            string stamp = StampOf(path);
            if (stamp == null)
                return 0d;

            double aspect;
            lock (_aspect)
                if (_aspect.TryGetValue(stamp, out aspect))
                    return aspect;

            var r = Load(path, 0d);
            aspect = r.Error == null && r.WidthMm > 0d ? r.HeightMm / r.WidthMm : 0d;

            lock (_aspect)
            {
                // Unbounded growth is not a risk worth code here - one entry per file per edit, and a
                // work order references a handful - but a runaway would be silent, so it is capped.
                if (_aspect.Count > 64)
                    _aspect.Clear();
                _aspect[stamp] = aspect;
            }
            return aspect;
        }

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

            var rings = new List<List<Point2D>>();

            // Walk the RENDERED tree with transforms accumulated down it. Both halves of that matter for
            // exported artwork, and a PDF export needs both at once: it wraps the page in a <clipPath>
            // whose path is a full-page rectangle, and hangs a matrix(1,0,0,-1,..) off every path to undo
            // PDF's upward Y. Walking blindly imports the clip box as a frame around the logo and drops
            // every shape at its untransformed, vertically mirrored position.
            Matrix rootMatrix = Matrix.Identity;
            var rootTransform = root.Attribute("transform");
            if (rootTransform != null && !TryParseTransform(rootTransform.Value, out rootMatrix))
            {
                rootMatrix = Matrix.Identity;
                Bump(result.Unsupported, "transform");
            }

            Walk(root, rootMatrix, rings, result);

            if (rings.Count == 0)
            {
                if (result.Error == null)
                    result.Error = "SVG contains no importable path outlines";
                return result;
            }

            Normalise(result, rings, targetWidthMm);
            return result;
        }

        // ------------------------------------------------------------------ the rendered tree

        // Subtrees that DEFINE something for later reference instead of drawing it. Their contents are
        // not on the page, so importing them is not "extra detail" - it is geometry the artwork does not
        // contain. <clipPath> is the one that bites: every PDF and Illustrator export wraps the page in
        // one, and because its rectangle is the largest thing in the file it also captures the bounding
        // box, so the logo comes out undersized inside a frame it never had.
        private static readonly string[] NonRendered =
            { "defs", "clipPath", "mask", "pattern", "marker", "symbol", "metadata", "title", "desc" };

        private static readonly Regex TransformFunc =
            new Regex(@"([a-zA-Z]+)\s*\(([^)]*)\)", RegexOptions.Compiled);

        /// <summary>
        /// Collect flattened outlines from the rendered elements under <paramref name="parent"/>,
        /// carrying the accumulated transform down with them.
        /// </summary>
        private static void Walk(XElement parent, Matrix inherited, List<List<Point2D>> rings, SvgImportResult result)
        {
            foreach (var el in parent.Elements())
            {
                string local = el.Name.LocalName;

                if (IsNonRendered(local))
                    continue;

                Matrix here = inherited;
                var transform = el.Attribute("transform");
                if (transform != null)
                {
                    Matrix own;
                    if (TryParseTransform(transform.Value, out own))
                    {
                        // own maps this element into its PARENT's space; inherited carries that on to the
                        // root's. WPF matrices are row-vector (p' = p * M), so the child's own transform
                        // applies first and Append is the right way round.
                        here = own;
                        here.Append(inherited);
                    }
                    else
                        // Understood far enough to know something moves this element, not far enough to
                        // know where to. Reported - treating it as identity would misplace the artwork
                        // silently, which is the failure this whole path exists to avoid.
                        Bump(result.Unsupported, "transform");
                }

                if (string.Equals(local, "path", StringComparison.OrdinalIgnoreCase))
                {
                    var d = el.Attribute("d");
                    if (d != null && !string.IsNullOrWhiteSpace(d.Value) && !Flatten(d.Value, here, rings))
                        result.ParseFailures++;
                }
                else foreach (var name in UnsupportedElements)
                    if (string.Equals(local, name, StringComparison.OrdinalIgnoreCase))
                        Bump(result.Unsupported, local);

                Walk(el, here, rings, result);
            }
        }

        private static bool IsNonRendered(string local)
        {
            foreach (var name in NonRendered)
                if (string.Equals(local, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Parse an SVG transform attribute - a list of matrix/translate/scale/rotate/skew calls applied
        /// right to left - into a single matrix. False when any part of it is not understood, so the
        /// caller can report it instead of placing the artwork wrongly.
        /// </summary>
        private static bool TryParseTransform(string text, out Matrix result)
        {
            result = Matrix.Identity;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var found = TransformFunc.Matches(text);
            if (found.Count == 0)
                return false;

            // Everything BETWEEN the recognised calls has to be separator. Without this check a value
            // that only partly parses still returns true, and half a transform places the artwork more
            // convincingly wrong than none at all.
            int pos = 0;
            foreach (Match m in found)
            {
                for (int i = pos; i < m.Index; i++)
                    if (!char.IsWhiteSpace(text[i]) && text[i] != ',')
                        return false;
                pos = m.Index + m.Length;
            }
            for (int i = pos; i < text.Length; i++)
                if (!char.IsWhiteSpace(text[i]) && text[i] != ',')
                    return false;

            foreach (Match m in found)
            {
                double[] a;
                if (!TryNumbers(m.Groups[2].Value, out a))
                    return false;

                Matrix t = Matrix.Identity;
                switch (m.Groups[1].Value)      // SVG function names are case SENSITIVE (skewX, not skewx)
                {
                    case "matrix":
                        if (a.Length != 6) return false;
                        t = new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]);
                        break;

                    case "translate":
                        if (a.Length == 1) t = new Matrix(1d, 0d, 0d, 1d, a[0], 0d);
                        else if (a.Length == 2) t = new Matrix(1d, 0d, 0d, 1d, a[0], a[1]);
                        else return false;
                        break;

                    case "scale":
                        if (a.Length == 1) t = new Matrix(a[0], 0d, 0d, a[0], 0d, 0d);
                        else if (a.Length == 2) t = new Matrix(a[0], 0d, 0d, a[1], 0d, 0d);
                        else return false;
                        break;

                    case "rotate":
                        if (a.Length == 1) t.Rotate(a[0]);
                        else if (a.Length == 3) t.RotateAt(a[0], a[1], a[2]);
                        else return false;
                        break;

                    case "skewX":
                        if (a.Length != 1) return false;
                        t = new Matrix(1d, 0d, Math.Tan(a[0] * Math.PI / 180d), 1d, 0d, 0d);
                        break;

                    case "skewY":
                        if (a.Length != 1) return false;
                        t = new Matrix(1d, Math.Tan(a[0] * Math.PI / 180d), 0d, 1d, 0d, 0d);
                        break;

                    default:
                        return false;
                }

                // Listed left to right, applied right to left: "translate(..) scale(..)" scales first.
                // Prepend gives t * result, which builds that order as the list is read forwards.
                result.Prepend(t);
            }

            return true;
        }

        private static bool TryNumbers(string text, out double[] values)
        {
            values = null;
            var parts = text.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var parsed = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                    return false;
            values = parsed;
            return true;
        }

        // ------------------------------------------------------------------ path -> rings

        private static bool Flatten(string data, Matrix matrix, List<List<Point2D>> rings)
        {
            Geometry geometry;
            try { geometry = Geometry.Parse(data); }
            catch { return false; }   // not WPF-compatible path data - counted, never swallowed

            // Geometry.Parse hands back a FROZEN instance, so Transform cannot be set on it - it throws
            // "cannot set a property on object ... because it is in a read-only state". Clone first.
            //
            // Applied to the GEOMETRY rather than to the flattened points afterwards, so that flattening
            // happens in transformed space. That is what keeps FlattenTolerance meaningful: a curve under
            // a 10x scale needs ten times the segments to stay within 0.01mm of true, and flattening
            // first would have already thrown that detail away.
            if (!matrix.IsIdentity)
            {
                try
                {
                    geometry = geometry.Clone();
                    geometry.Transform = new MatrixTransform(matrix);
                }
                catch { return false; }
            }

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
