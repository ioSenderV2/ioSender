/*
 * SvgPath.cs - part of the CNC.Svg library
 *
 * Parses an SVG path 'd' attribute and flattens it to polylines, replacing WPF's
 * Geometry.Parse + GetFlattenedPathGeometry so this assembly runs anywhere .NET does.
 *
 * ---- Why this exists at all ----
 *
 * The original leaned on WPF because its path mini-language is derived from SVG's 'd' grammar, which
 * made flattening one call at the same tolerance TrueTypeOutlines uses for glyphs. That is Windows-
 * only: PresentationCore has no Linux build, so the SVG-to-laser converter could not run on the
 * EngravingBox appliance. This is the replacement, and it is deliberately dependency-free - a native
 * graphics library (SkiaSharp and friends) would mean shipping a per-architecture .so with an
 * appliance whose whole job here is arithmetic on a few dozen contours.
 *
 * ---- It is NOT byte-compatible with WPF, on purpose ----
 *
 * WPF's flattener uses its own adaptive subdivision, so the same curve comes out with a different
 * number of points. Emitted g-code therefore differs line-for-line from what the WPF path produced,
 * and any comparison must be GEOMETRIC - contour count, containment classification, bounding box,
 * enclosed area - not textual. tools/svg-compare does exactly that.
 *
 * One difference is a fix rather than a variance: WPF parses the XAML mini-language, which is close
 * to SVG's 'd' but not identical. This parses the SVG grammar, so path data that WPF refused (and
 * that surfaced as a ParseFailure) can now be read.
 *
 * ---- Flattening happens in TRANSFORMED space ----
 *
 * Control points are mapped through the accumulated matrix BEFORE subdivision, matching what the WPF
 * version did by setting Geometry.Transform rather than transforming the flattened output. It is what
 * keeps an absolute tolerance meaningful: a curve under a 10x scale needs ten times the segments to
 * stay within 0.01 units of true, and flattening first has already thrown that detail away.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using CNC.Core;

namespace CNC.Svg
{
    /// <summary>SVG path data to polylines.</summary>
    public static class SvgPath
    {
        // Subdivision cannot run away on degenerate control points (coincident, NaN-free but
        // pathological). 24 levels is 16 million segments - unreachable in practice, and a hard stop
        // if the flatness test ever fails to converge.
        private const int MaxDepth = 24;

        /// <summary>
        /// Flatten <paramref name="data"/> into <paramref name="rings"/>, one entry per subpath, with
        /// <paramref name="matrix"/> applied before subdivision. Returns false when the data is not
        /// valid SVG path syntax - the caller reports that rather than cutting a partial path.
        /// </summary>
        public static bool Flatten(string data, SvgMatrix matrix, double tolerance, List<List<Point2D>> rings)
        {
            if (string.IsNullOrEmpty(data) || rings == null)
                return false;

            var s = new Scanner(data);
            var current = new List<Point2D>();

            // User-space state. Relative commands and the S/T reflection are defined against the
            // untransformed coordinates, so the walk is kept in user space and each segment's control
            // points are mapped only as they are handed to the flattener.
            double cx = 0d, cy = 0d;            // current point
            double startX = 0d, startY = 0d;    // start of the current subpath, where Z returns to
            double lastC1X = 0d, lastC1Y = 0d;  // previous cubic's second control point, for S
            double lastQX = 0d, lastQY = 0d;    // previous quadratic's control point, for T
            bool lastWasCubic = false, lastWasQuad = false;
            bool haveStart = false;

            char command = '\0';

            while (true)
            {
                s.SkipSeparators();
                if (s.AtEnd)
                    break;

                char c = s.Peek();
                if (IsCommand(c))
                {
                    command = c;
                    s.Advance();
                }
                else if (command == '\0')
                    return false;               // data starts with a number - not a path
                else if (command == 'M')
                    command = 'L';              // implicit repeat of moveto is lineto, per the grammar
                else if (command == 'm')
                    command = 'l';

                double x, y, x1, y1, x2, y2;

                switch (command)
                {
                    case 'M':
                    case 'm':
                        if (!s.TryNumber(out x) || !s.TryNumber(out y)) return false;
                        if (command == 'm') { x += cx; y += cy; }
                        Emit(rings, current);
                        current = new List<Point2D>();
                        cx = x; cy = y;
                        startX = x; startY = y;
                        haveStart = true;
                        AddPoint(current, matrix, cx, cy);
                        lastWasCubic = lastWasQuad = false;
                        break;

                    case 'L':
                    case 'l':
                        if (!s.TryNumber(out x) || !s.TryNumber(out y)) return false;
                        if (command == 'l') { x += cx; y += cy; }
                        cx = x; cy = y;
                        AddPoint(current, matrix, cx, cy);
                        lastWasCubic = lastWasQuad = false;
                        break;

                    case 'H':
                    case 'h':
                        if (!s.TryNumber(out x)) return false;
                        if (command == 'h') x += cx;
                        cx = x;
                        AddPoint(current, matrix, cx, cy);
                        lastWasCubic = lastWasQuad = false;
                        break;

                    case 'V':
                    case 'v':
                        if (!s.TryNumber(out y)) return false;
                        if (command == 'v') y += cy;
                        cy = y;
                        AddPoint(current, matrix, cx, cy);
                        lastWasCubic = lastWasQuad = false;
                        break;

                    case 'C':
                    case 'c':
                        if (!s.TryNumber(out x1) || !s.TryNumber(out y1) ||
                            !s.TryNumber(out x2) || !s.TryNumber(out y2) ||
                            !s.TryNumber(out x) || !s.TryNumber(out y)) return false;
                        if (command == 'c')
                        {
                            x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy;
                        }
                        FlattenCubic(current, matrix, tolerance, cx, cy, x1, y1, x2, y2, x, y);
                        lastC1X = x2; lastC1Y = y2;
                        cx = x; cy = y;
                        lastWasCubic = true; lastWasQuad = false;
                        break;

                    case 'S':
                    case 's':
                        if (!s.TryNumber(out x2) || !s.TryNumber(out y2) ||
                            !s.TryNumber(out x) || !s.TryNumber(out y)) return false;
                        if (command == 's') { x2 += cx; y2 += cy; x += cx; y += cy; }
                        // The first control point is the reflection of the previous curve's second one.
                        // With no previous cubic it coincides with the current point, per the spec.
                        if (lastWasCubic) { x1 = 2d * cx - lastC1X; y1 = 2d * cy - lastC1Y; }
                        else { x1 = cx; y1 = cy; }
                        FlattenCubic(current, matrix, tolerance, cx, cy, x1, y1, x2, y2, x, y);
                        lastC1X = x2; lastC1Y = y2;
                        cx = x; cy = y;
                        lastWasCubic = true; lastWasQuad = false;
                        break;

                    case 'Q':
                    case 'q':
                        if (!s.TryNumber(out x1) || !s.TryNumber(out y1) ||
                            !s.TryNumber(out x) || !s.TryNumber(out y)) return false;
                        if (command == 'q') { x1 += cx; y1 += cy; x += cx; y += cy; }
                        FlattenQuadratic(current, matrix, tolerance, cx, cy, x1, y1, x, y);
                        lastQX = x1; lastQY = y1;
                        cx = x; cy = y;
                        lastWasQuad = true; lastWasCubic = false;
                        break;

                    case 'T':
                    case 't':
                        if (!s.TryNumber(out x) || !s.TryNumber(out y)) return false;
                        if (command == 't') { x += cx; y += cy; }
                        if (lastWasQuad) { x1 = 2d * cx - lastQX; y1 = 2d * cy - lastQY; }
                        else { x1 = cx; y1 = cy; }
                        FlattenQuadratic(current, matrix, tolerance, cx, cy, x1, y1, x, y);
                        lastQX = x1; lastQY = y1;
                        cx = x; cy = y;
                        lastWasQuad = true; lastWasCubic = false;
                        break;

                    case 'A':
                    case 'a':
                        {
                            double rx, ry, rot, largeArc, sweep;
                            if (!s.TryNumber(out rx) || !s.TryNumber(out ry) || !s.TryNumber(out rot) ||
                                !s.TryFlag(out largeArc) || !s.TryFlag(out sweep) ||
                                !s.TryNumber(out x) || !s.TryNumber(out y)) return false;
                            if (command == 'a') { x += cx; y += cy; }
                            FlattenArc(current, matrix, tolerance, cx, cy, rx, ry, rot,
                                       largeArc != 0d, sweep != 0d, x, y);
                            cx = x; cy = y;
                            lastWasCubic = lastWasQuad = false;
                        }
                        break;

                    case 'Z':
                    case 'z':
                        if (haveStart)
                        {
                            cx = startX; cy = startY;
                            AddPoint(current, matrix, cx, cy);
                        }
                        lastWasCubic = lastWasQuad = false;
                        break;

                    default:
                        return false;
                }
            }

            Emit(rings, current);
            return true;
        }

        /// <summary>
        /// A subpath is kept when it has enough points to enclose something. Matches what the WPF
        /// version did: it never asked whether the figure was closed, and Normalise closes any ring
        /// whose ends do not already meet.
        /// </summary>
        private static void Emit(List<List<Point2D>> rings, List<Point2D> pts)
        {
            if (pts != null && pts.Count >= 3)
                rings.Add(pts);
        }

        private static void AddPoint(List<Point2D> pts, SvgMatrix m, double x, double y)
        {
            double tx, ty;
            m.Transform(x, y, out tx, out ty);

            // Consecutive duplicates carry no shape and only cost the downstream area and containment
            // tests work. Exact comparison: anything else is a real, if tiny, segment.
            if (pts.Count > 0)
            {
                var last = pts[pts.Count - 1];
                if (last.X == tx && last.Y == ty)
                    return;
            }
            pts.Add(new Point2D(tx, ty));
        }

        // ------------------------------------------------------------------ curve flattening

        private static void FlattenCubic(List<Point2D> pts, SvgMatrix m, double tol,
                                         double x0, double y0, double x1, double y1,
                                         double x2, double y2, double x3, double y3)
        {
            double tx0, ty0, tx1, ty1, tx2, ty2, tx3, ty3;
            m.Transform(x0, y0, out tx0, out ty0);
            m.Transform(x1, y1, out tx1, out ty1);
            m.Transform(x2, y2, out tx2, out ty2);
            m.Transform(x3, y3, out tx3, out ty3);

            if (pts.Count == 0)
                pts.Add(new Point2D(tx0, ty0));

            SubdivideCubic(pts, tol, tx0, ty0, tx1, ty1, tx2, ty2, tx3, ty3, 0);
            AppendTransformed(pts, tx3, ty3);
        }

        private static void SubdivideCubic(List<Point2D> pts, double tol,
                                           double x0, double y0, double x1, double y1,
                                           double x2, double y2, double x3, double y3, int depth)
        {
            if (depth >= MaxDepth || IsFlatCubic(tol, x0, y0, x1, y1, x2, y2, x3, y3))
                return;

            // de Casteljau at t = 0.5
            double x01 = (x0 + x1) * 0.5d, y01 = (y0 + y1) * 0.5d;
            double x12 = (x1 + x2) * 0.5d, y12 = (y1 + y2) * 0.5d;
            double x23 = (x2 + x3) * 0.5d, y23 = (y2 + y3) * 0.5d;
            double x012 = (x01 + x12) * 0.5d, y012 = (y01 + y12) * 0.5d;
            double x123 = (x12 + x23) * 0.5d, y123 = (y12 + y23) * 0.5d;
            double xm = (x012 + x123) * 0.5d, ym = (y012 + y123) * 0.5d;

            SubdivideCubic(pts, tol, x0, y0, x01, y01, x012, y012, xm, ym, depth + 1);
            AppendTransformed(pts, xm, ym);
            SubdivideCubic(pts, tol, xm, ym, x123, y123, x23, y23, x3, y3, depth + 1);
        }

        /// <summary>
        /// Flat when both control points lie within <paramref name="tol"/> of the chord. Distances are
        /// compared squared against the chord length squared to keep a square root out of the inner
        /// loop; the degenerate zero-length chord falls back to comparing the control offsets directly,
        /// because every point is "on" a chord of no length.
        /// </summary>
        private static bool IsFlatCubic(double tol, double x0, double y0, double x1, double y1,
                                        double x2, double y2, double x3, double y3)
        {
            double dx = x3 - x0, dy = y3 - y0;
            double lenSq = dx * dx + dy * dy;
            double tolSq = tol * tol;

            if (lenSq < 1e-24d)
            {
                double a1 = (x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0);
                double a2 = (x2 - x0) * (x2 - x0) + (y2 - y0) * (y2 - y0);
                return a1 <= tolSq && a2 <= tolSq;
            }

            // Perpendicular distance of a control point from the infinite line through the chord,
            // squared: cross^2 / lenSq.
            double c1 = (x1 - x0) * dy - (y1 - y0) * dx;
            double c2 = (x2 - x0) * dy - (y2 - y0) * dx;
            return c1 * c1 <= tolSq * lenSq && c2 * c2 <= tolSq * lenSq;
        }

        private static void FlattenQuadratic(List<Point2D> pts, SvgMatrix m, double tol,
                                             double x0, double y0, double x1, double y1,
                                             double x2, double y2)
        {
            // Degree-elevate to a cubic and reuse one tested subdivision path rather than carrying a
            // second flatness test that has to be kept in step with it.
            double c1x = x0 + 2d / 3d * (x1 - x0), c1y = y0 + 2d / 3d * (y1 - y0);
            double c2x = x2 + 2d / 3d * (x1 - x2), c2y = y2 + 2d / 3d * (y1 - y2);
            FlattenCubic(pts, m, tol, x0, y0, c1x, c1y, c2x, c2y, x2, y2);
        }

        private static void AppendTransformed(List<Point2D> pts, double x, double y)
        {
            if (pts.Count > 0)
            {
                var last = pts[pts.Count - 1];
                if (last.X == x && last.Y == y)
                    return;
            }
            pts.Add(new Point2D(x, y));
        }

        // ------------------------------------------------------------------ elliptical arc

        /// <summary>
        /// Endpoint-to-centre parameterisation (SVG 1.1 implementation notes F.6.5), then the arc is
        /// split into cubic Beziers of at most 90 degrees each and flattened by the same subdivision as
        /// every other curve. Converting to cubics rather than stepping the angle directly means the
        /// tolerance argument keeps one meaning across the whole file.
        /// </summary>
        private static void FlattenArc(List<Point2D> pts, SvgMatrix m, double tol,
                                       double x0, double y0, double rx, double ry, double rotDegrees,
                                       bool largeArc, bool sweep, double x1, double y1)
        {
            rx = Math.Abs(rx);
            ry = Math.Abs(ry);

            // "If the endpoints are identical the arc is omitted; if either radius is zero it is a
            // straight line" - both are spec-mandated, not shortcuts.
            if (rx < 1e-12d || ry < 1e-12d || (Math.Abs(x1 - x0) < 1e-12d && Math.Abs(y1 - y0) < 1e-12d))
            {
                if (pts.Count == 0)
                    AddPoint(pts, m, x0, y0);
                AddPoint(pts, m, x1, y1);
                return;
            }

            double phi = rotDegrees * Math.PI / 180d;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

            double dx2 = (x0 - x1) * 0.5d, dy2 = (y0 - y1) * 0.5d;
            double x1p = cosPhi * dx2 + sinPhi * dy2;
            double y1p = -sinPhi * dx2 + cosPhi * dy2;

            // Radii too small to span the chord are scaled up until they just reach, per F.6.6.
            double lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lambda > 1d)
            {
                double k = Math.Sqrt(lambda);
                rx *= k;
                ry *= k;
            }

            double sign = largeArc == sweep ? -1d : 1d;
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double coef = den <= 0d ? 0d : sign * Math.Sqrt(Math.Max(0d, num / den));

            double cxp = coef * rx * y1p / ry;
            double cyp = -coef * ry * x1p / rx;

            double cxc = cosPhi * cxp - sinPhi * cyp + (x0 + x1) * 0.5d;
            double cyc = sinPhi * cxp + cosPhi * cyp + (y0 + y1) * 0.5d;

            double theta1 = Angle(1d, 0d, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);

            if (!sweep && delta > 0d)
                delta -= 2d * Math.PI;
            else if (sweep && delta < 0d)
                delta += 2d * Math.PI;

            int segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI * 0.5d));
            if (segments < 1)
                segments = 1;

            double step = delta / segments;
            // Control-point distance for a cubic approximating a circular arc of angle 'step'.
            double alpha = 4d / 3d * Math.Tan(step * 0.25d);

            double px = x0, py = y0;
            for (int i = 0; i < segments; i++)
            {
                double a0 = theta1 + i * step;
                double a1 = a0 + step;

                double cos0 = Math.Cos(a0), sin0 = Math.Sin(a0);
                double cos1 = Math.Cos(a1), sin1 = Math.Sin(a1);

                double e1x, e1y, e2x, e2y, d1x, d1y, d2x, d2y;
                EllipsePoint(cxc, cyc, rx, ry, cosPhi, sinPhi, cos1, sin1, out e2x, out e2y);
                EllipseDerivative(rx, ry, cosPhi, sinPhi, cos0, sin0, out d1x, out d1y);
                EllipseDerivative(rx, ry, cosPhi, sinPhi, cos1, sin1, out d2x, out d2y);

                e1x = px + alpha * d1x;
                e1y = py + alpha * d1y;

                FlattenCubic(pts, m, tol, px, py, e1x, e1y, e2x - alpha * d2x, e2y - alpha * d2y, e2x, e2y);

                px = e2x;
                py = e2y;
            }
        }

        private static void EllipsePoint(double cx, double cy, double rx, double ry,
                                         double cosPhi, double sinPhi, double cosT, double sinT,
                                         out double x, out double y)
        {
            x = cx + rx * cosT * cosPhi - ry * sinT * sinPhi;
            y = cy + rx * cosT * sinPhi + ry * sinT * cosPhi;
        }

        private static void EllipseDerivative(double rx, double ry, double cosPhi, double sinPhi,
                                              double cosT, double sinT, out double dx, out double dy)
        {
            dx = -rx * sinT * cosPhi - ry * cosT * sinPhi;
            dy = -rx * sinT * sinPhi + ry * cosT * cosPhi;
        }

        private static double Angle(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            if (len <= 0d)
                return 0d;

            double cos = dot / len;
            if (cos > 1d) cos = 1d;
            else if (cos < -1d) cos = -1d;

            double a = Math.Acos(cos);
            return (ux * vy - uy * vx) < 0d ? -a : a;
        }

        // ------------------------------------------------------------------ tokenising

        private static bool IsCommand(char c)
        {
            switch (c)
            {
                case 'M': case 'm': case 'L': case 'l': case 'H': case 'h': case 'V': case 'v':
                case 'C': case 'c': case 'S': case 's': case 'Q': case 'q': case 'T': case 't':
                case 'A': case 'a': case 'Z': case 'z':
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads the SVG number grammar, which is not what double.Parse accepts on its own: numbers run
        /// together without separators ("1.5.5" is 1.5 then 0.5, ".5-.5" is 0.5 then -0.5), so the scan
        /// has to end a number the moment the next one starts.
        /// </summary>
        private sealed class Scanner
        {
            private readonly string text;
            private int pos;

            public Scanner(string s) { text = s; pos = 0; }

            public bool AtEnd { get { return pos >= text.Length; } }
            public char Peek() { return text[pos]; }
            public void Advance() { pos++; }

            public void SkipSeparators()
            {
                while (pos < text.Length)
                {
                    char c = text[pos];
                    if (c == ',' || c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v')
                        pos++;
                    else
                        break;
                }
            }

            /// <summary>
            /// The large-arc and sweep arguments are single-digit FLAGS, not numbers: "a1 1 0 011 1" is
            /// legal and means flags 0 and 1 with no separator. Parsing them as numbers reads "011" as
            /// eleven and silently shifts every following argument.
            /// </summary>
            public bool TryFlag(out double value)
            {
                value = 0d;
                SkipSeparators();
                if (pos >= text.Length)
                    return false;

                char c = text[pos];
                if (c != '0' && c != '1')
                    return false;

                value = c == '1' ? 1d : 0d;
                pos++;
                return true;
            }

            public bool TryNumber(out double value)
            {
                value = 0d;
                SkipSeparators();

                int start = pos;
                if (pos < text.Length && (text[pos] == '+' || text[pos] == '-'))
                    pos++;

                int digitsBefore = 0;
                while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') { pos++; digitsBefore++; }

                int digitsAfter = 0;
                if (pos < text.Length && text[pos] == '.')
                {
                    pos++;
                    while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') { pos++; digitsAfter++; }
                }

                if (digitsBefore == 0 && digitsAfter == 0)
                {
                    pos = start;
                    return false;
                }

                // An exponent only counts when it actually has digits - "1e" is the number 1 followed by
                // junk, and consuming the 'e' would lose the position to back up to.
                if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E'))
                {
                    int save = pos;
                    pos++;
                    if (pos < text.Length && (text[pos] == '+' || text[pos] == '-'))
                        pos++;
                    int expDigits = 0;
                    while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') { pos++; expDigits++; }
                    if (expDigits == 0)
                        pos = save;
                }

                return double.TryParse(text.Substring(start, pos - start),
                                       NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
        }
    }
}
