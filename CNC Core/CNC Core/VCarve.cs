/*
 * VCarve.cs - part of CNC Core library
 *
 * V-carving: cut a filled shape with a V-bit so that the tool's cone exactly fills it, giving sharp
 * corners and strokes that taper the way carved lettering does. Depth is not a setting here - it is a
 * consequence of the geometry:
 *
 *     at any interior point, depth = (distance to the nearest boundary) / tan(halfAngle)
 *
 * because that is how deep the cone has to go for its width at the surface to reach both edges. Follow
 * the boundary at depth 0 and the shape's spine at its full depth, and everything between is a contour
 * at its own depth.
 *
 * Pure geometry. It takes closed polygons and knows nothing about fonts, WPF or g-code - TrueTypeOutlines
 * supplies glyphs, but a logo or an imported DXF would do just as well.
 *
 * ---- Why a distance field and not a medial axis ----
 *
 * The textbook answer is the medial axis, computed from a Voronoi diagram of the boundary segments. It is
 * exact, and it is genuinely hard to make robust: real glyphs are full of the degeneracies that break it -
 * near-tangent curves, hairline serifs, bowls that almost touch, contours that share a point after
 * flattening. A Voronoi implementation that is 99% right produces a spine with a wrong BRANCH, and a
 * wrong branch is a gouge.
 *
 * A distance field cannot do that. It is approximate, but its error is bounded by the grid resolution and
 * it can never invent topology that is not there - the worst it does is round a corner it should have
 * kept sharp, and that error shrinks predictably as the grid gets finer. For a process whose failure mode
 * is cutting a workpiece wrong, a bounded approximation beats a fragile exact answer.
 *
 * ---- The error budget, stated so it can be checked ----
 *
 * Three approximations, all bounded and all pointing the same way (slightly shallow, never deep):
 *   1. Boundary sampled as points at SampleFraction of a cell - distance error <= half that spacing.
 *   2. Distance field on a grid of Resolution mm - iso-contours interpolate linearly between cells.
 *   3. Curves already flattened to polylines upstream (TrueTypeOutlines' own tolerance).
 * Verified against shapes with exact answers before it was ever pointed at a glyph: a w x h rectangle's
 * contour at distance d is exactly (w-2d) x (h-2d), and a circle of radius r gives r-d.
 */

using System;
using System.Collections.Generic;

namespace CNC.Core
{
    /// <summary>One closed pass of a V-carve: where to go, and how deep to be while doing it.</summary>
    public class VCarvePass
    {
        /// <summary>Depth below the surface, mm, positive downward. 0 is the boundary itself.</summary>
        public double Depth;

        /// <summary>Closed path - last point equals the first.</summary>
        public List<Point2D> Path = new List<Point2D>();
    }

    public static class VCarve
    {
        // Boundary points are sampled this fraction of a grid cell apart. Half a cell keeps the sampling
        // error (half the spacing) at a quarter of a cell - comfortably below the field's own resolution,
        // so it is never the dominant term.
        private const double SampleFraction = 0.5d;

        // Distance reported for points further from the boundary than the search cares about. Anything
        // past maxDist is simply "deeper than the tool goes", so the exact value only has to be safely
        // above every iso level ever asked for.
        private const double FarFactor = 2d;

        /// <summary>
        /// Build the carve passes for a filled region.
        /// </summary>
        /// <param name="contours">
        /// Closed contours bounding the region. Nesting is handled by an even-odd rule, so a glyph's
        /// counters simply appear as further contours - their winding does not have to be told apart here.
        /// </param>
        /// <param name="halfAngleRad">Half the tool's included angle - see CustomTool.HalfAngleRad.</param>
        /// <param name="maxDepth">Never go deeper than this, mm. The cut flattens off at the bottom.</param>
        /// <param name="resolutionMm">Grid cell size. Smaller is sharper and slower; 0.1 mm suits lettering.</param>
        /// <param name="depthStepMm">Depth between successive passes, mm.</param>
        public static List<VCarvePass> Build(IList<IList<Point2D>> contours, double halfAngleRad,
                                             double maxDepth, double resolutionMm, double depthStepMm)
        {
            var passes = new List<VCarvePass>();
            if (contours == null || contours.Count == 0)
                return passes;

            double tan = Math.Tan(halfAngleRad);
            if (tan <= 1e-9 || maxDepth <= 0d)
                return passes;

            double cell = Math.Max(0.01d, resolutionMm);
            double step = Math.Max(0.01d, depthStepMm);

            // The deepest the tool will go decides how far in from the boundary we need the field to be
            // accurate - past that everything is simply "at max depth", so there is nothing to gain by
            // measuring it.
            double maxDist = maxDepth * tan;

            var field = DistanceField.Build(contours, cell, maxDist);
            if (field == null)
                return passes;

            // Shallow to deep, outside to inside: each level's contour sits inside the previous one, so
            // cutting in this order means the tool is always stepping down into material it has already
            // opened up rather than plunging into solid stock.
            for (double depth = 0d; depth <= maxDepth + 1e-9; depth += step)
            {
                double dist = depth * tan;
                foreach (var ring in field.IsoContours(dist))
                {
                    if (ring.Count < 4)
                        continue;
                    passes.Add(new VCarvePass { Depth = Math.Min(depth, maxDepth), Path = ring });
                }
            }

            // The stepped levels are not enough. Consecutive passes tile the V-walls exactly - a pass's
            // flank runs through the tip of the pass above it - but the groove's bottom vertex is cut only
            // by a tip travelling AT the spine, and for a (near-)constant-width region (an annulus, the
            // straight strokes letters are full of) the spine sits at one depth that the step sequence can
            // miss by almost a whole step. What the miss leaves is not the usual small cusp: the deepest
            // ring on either side reaches the spine at the surface, so a standing ridge is left whose TIP
            // IS AT z=0, twice the residual tall. (Where the width varies, the regular levels cross the
            // spine on their own and no ridge survives - this is purely the constant-width case.)
            //
            // So: one extra pass per spine, on a ring hugging it half a cell out, cut at the spine's own
            // full depth. Cutting half a cell early at full depth bites half a cell into the finished wall
            // - inside the engine's resolution budget, and what it buys is the crisp bottom line that is
            // the whole point of a V-carve.
            double halfCell = cell * 0.5d;
            var spine = new List<VCarvePass>();
            foreach (var ridge in field.Ridges(maxDist))
            {
                double level = ridge.Dist - halfCell;
                if (level <= 0d)
                    continue;
                double depth = Math.Min(maxDepth, ridge.Dist / tan);
                foreach (var ring in field.IsoContours(level))
                {
                    if (ring.Count < 4)
                        continue;
                    // The level is global but the spine is local: this same level also has rings around
                    // every DEEPER region, which already get their own passes. Keep only rings that
                    // actually enclose one of this ridge's plateau cells.
                    bool ours = false;
                    foreach (var p in ridge.At)
                        if (DistanceField.Contains(ring, p.X, p.Y)) { ours = true; break; }
                    if (ours)
                        spine.Add(new VCarvePass { Depth = depth, Path = ring });
                }
            }
            if (spine.Count > 0)
            {
                passes.AddRange(spine);
                // Restore shallow-to-deep; List.Sort is unstable, so keep it deterministic by depth only.
                passes.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            }

            return passes;
        }

        // ---------------------------------------------------------------------------------------------
        // Distance field
        // ---------------------------------------------------------------------------------------------

        private class DistanceField
        {
            private double[] dist;      // distance to boundary, negative outside the region
            private int nx, ny;
            private double x0, y0, cell;

            public static DistanceField Build(IList<IList<Point2D>> contours, double cell, double maxDist)
            {
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                int total = 0;
                foreach (var c in contours)
                {
                    if (c == null || c.Count < 3)
                        continue;
                    total += c.Count;
                    foreach (var p in c)
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                    }
                }
                if (total == 0 || minX > maxX)
                    return null;

                // One cell of margin so the region never touches the grid edge - marching squares needs a
                // ring of outside cells to close its contours against.
                var f = new DistanceField();
                f.cell = cell;
                f.x0 = minX - cell;
                f.y0 = minY - cell;
                f.nx = (int)Math.Ceiling((maxX - minX) / cell) + 3;
                f.ny = (int)Math.Ceiling((maxY - minY) / cell) + 3;

                // Guard against a pathological request turning into gigabytes. At 0.1 mm this is a
                // 400 x 400 mm area, far beyond any lettering.
                if ((long)f.nx * f.ny > 16000000L)
                    return null;

                var samples = SampleBoundary(contours, cell * SampleFraction);
                var buckets = new PointGrid(samples, f.x0, f.y0, cell, f.nx, f.ny, maxDist);

                f.dist = new double[f.nx * f.ny];
                for (int j = 0; j < f.ny; j++)
                {
                    double py = f.y0 + j * cell;
                    for (int i = 0; i < f.nx; i++)
                    {
                        double px = f.x0 + i * cell;
                        double d = buckets.NearestDistance(px, py);
                        // Sign carries inside/outside; magnitude is the distance either way. Outside
                        // values are kept (negative) rather than clamped so marching squares can find the
                        // zero crossing - the boundary pass - by interpolation like any other level.
                        f.dist[j * f.nx + i] = Inside(contours, px, py) ? Math.Min(d, maxDist * FarFactor) : -d;
                    }
                }
                return f;
            }

            /// <summary>Closed rings where the field equals <paramref name="level"/>.</summary>
            public List<List<Point2D>> IsoContours(double level)
            {
                // Marching squares: every cell contributes 0, 1 or 2 segments depending on which of its
                // corners are above the level. Segments are then chained end-to-end into rings.
                var segs = new List<Point2D[]>();

                for (int j = 0; j < ny - 1; j++)
                    for (int i = 0; i < nx - 1; i++)
                    {
                        double a = dist[j * nx + i];             // bottom-left
                        double b = dist[j * nx + i + 1];         // bottom-right
                        double c = dist[(j + 1) * nx + i + 1];   // top-right
                        double d = dist[(j + 1) * nx + i];       // top-left

                        int code = (a > level ? 1 : 0) | (b > level ? 2 : 0) | (c > level ? 4 : 0) | (d > level ? 8 : 0);
                        if (code == 0 || code == 15)
                            continue;

                        double px = x0 + i * cell, py = y0 + j * cell;
                        var bottom = new Point2D(px + Frac(a, b, level) * cell, py);
                        var right = new Point2D(px + cell, py + Frac(b, c, level) * cell);
                        var top = new Point2D(px + Frac(d, c, level) * cell, py + cell);
                        var left = new Point2D(px, py + Frac(a, d, level) * cell);

                        // ORIENTATION IS NOT OPTIONAL. Every segment is emitted so the region ABOVE the
                        // level lies on its left, which makes them run head-to-tail and lets Chain walk a
                        // ring by looking up "the segment starting where this one ended".
                        //
                        // Complementary cases (1 and 14, 2 and 13, ...) therefore need REVERSED order, not
                        // the same order. Giving both the same order - which is what this table did first -
                        // produces a bag of correctly-placed but inconsistently-directed segments that
                        // chain into nothing, and the engine silently returns zero passes.
                        switch (code)
                        {
                            case 1:  segs.Add(new[] { bottom, left }); break;
                            case 14: segs.Add(new[] { left, bottom }); break;
                            case 2:  segs.Add(new[] { right, bottom }); break;
                            case 13: segs.Add(new[] { bottom, right }); break;
                            case 3:  segs.Add(new[] { right, left }); break;
                            case 12: segs.Add(new[] { left, right }); break;
                            case 4:  segs.Add(new[] { top, right }); break;
                            case 11: segs.Add(new[] { right, top }); break;
                            case 6:  segs.Add(new[] { top, bottom }); break;
                            case 9:  segs.Add(new[] { bottom, top }); break;
                            case 7:  segs.Add(new[] { top, left }); break;
                            case 8:  segs.Add(new[] { left, top }); break;
                            // The two ambiguous saddles. Resolved the same way every time - an inconsistent
                            // choice between neighbouring cells leaves a ring that never closes.
                            case 5:  segs.Add(new[] { bottom, left }); segs.Add(new[] { top, right }); break;
                            case 10: segs.Add(new[] { right, bottom }); segs.Add(new[] { left, top }); break;
                        }
                    }

                return Chain(segs, cell);
            }

            private static double Frac(double va, double vb, double level)
            {
                double denom = vb - va;
                if (Math.Abs(denom) < 1e-12)
                    return 0.5d;
                double t = (level - va) / denom;
                return t < 0d ? 0d : t > 1d ? 1d : t;
            }

            // Join loose segments into closed rings by matching endpoints on a tolerance grid. Marching
            // squares emits each segment independently, so this is what turns a bag of them into paths a
            // machine can follow.
            private static List<List<Point2D>> Chain(List<Point2D[]> segs, double cell)
            {
                var rings = new List<List<Point2D>>();
                if (segs.Count == 0)
                    return rings;

                double tol = cell * 1e-3;
                var starts = new Dictionary<long, List<int>>();
                var used = new bool[segs.Count];

                Func<Point2D, long> key = p => ((long)Math.Round(p.X / tol) * 73856093L) ^ ((long)Math.Round(p.Y / tol) * 19349663L);

                for (int i = 0; i < segs.Count; i++)
                {
                    long k = key(segs[i][0]);
                    List<int> list;
                    if (!starts.TryGetValue(k, out list))
                        starts[k] = list = new List<int>();
                    list.Add(i);
                }

                for (int i = 0; i < segs.Count; i++)
                {
                    if (used[i])
                        continue;

                    var ring = new List<Point2D> { segs[i][0], segs[i][1] };
                    used[i] = true;
                    var head = segs[i][1];

                    // Walk forward until we return to the start or run out of neighbours.
                    for (int guard = 0; guard < segs.Count + 2; guard++)
                    {
                        List<int> cand;
                        if (!starts.TryGetValue(key(head), out cand))
                            break;

                        int next = -1;
                        foreach (int idx in cand)
                            if (!used[idx]) { next = idx; break; }
                        if (next < 0)
                            break;

                        used[next] = true;
                        head = segs[next][1];
                        ring.Add(head);

                        if (Math.Abs(head.X - ring[0].X) < tol && Math.Abs(head.Y - ring[0].Y) < tol)
                            break;
                    }

                    // Only closed rings are useful: an open chain means the contour ran off the grid, and
                    // cutting one would leave the tool travelling at depth along a path that is not a real
                    // boundary. Dropping it is the safe answer.
                    if (ring.Count >= 4 &&
                        Math.Abs(ring[ring.Count - 1].X - ring[0].X) < tol &&
                        Math.Abs(ring[ring.Count - 1].Y - ring[0].Y) < tol)
                        rings.Add(ring);
                }

                return rings;
            }

            /// <summary>One spine of the region: its distance from the boundary, and cells that lie on it.</summary>
            public class Ridge
            {
                public double Dist;
                public List<Point2D> At = new List<Point2D>();
            }

            /// <summary>
            /// Spines of (near-)constant-width parts of the region: plateau cells at least as far from the
            /// boundary as all eight neighbours, grouped by distance. Spines out of the tool's reach
            /// (>= <paramref name="maxUseful"/>) are not returned - past there the bottom is flat, not a vertex.
            /// </summary>
            public List<Ridge> Ridges(double maxUseful)
            {
                // The tolerance matters in both directions. Plateau cells along a straight stroke differ
                // only by boundary-sampling noise (far below a thousandth of a cell), so a tight tolerance
                // still groups them - while a loose one would promote every gentle slope to a "plateau".
                double tol = cell * 1e-3;
                var cand = new List<KeyValuePair<double, Point2D>>();
                for (int j = 1; j < ny - 1; j++)
                    for (int i = 1; i < nx - 1; i++)
                    {
                        double v = dist[j * nx + i];
                        if (v <= 0d || v >= maxUseful)
                            continue;
                        bool isMax = true;
                        for (int dj = -1; dj <= 1 && isMax; dj++)
                            for (int di = -1; di <= 1; di++)
                                if ((di != 0 || dj != 0) && dist[(j + dj) * nx + i + di] > v + tol)
                                {
                                    isMax = false;
                                    break;
                                }
                        if (isMax)
                            cand.Add(new KeyValuePair<double, Point2D>(v, new Point2D(x0 + i * cell, y0 + j * cell)));
                    }

                // Group candidates whose distances agree to within half a cell - the same spine seen in
                // many cells. Distinct spines closer than that in WIDTH collapse into one group, which is
                // fine: the group's deepest value wins and the others are within the resolution budget.
                cand.Sort((a, b) => a.Key.CompareTo(b.Key));
                var ridges = new List<Ridge>();
                double groupStart = 0d;
                Ridge cur = null;
                foreach (var c in cand)
                {
                    if (cur == null || c.Key - groupStart > cell * 0.5d)
                    {
                        ridges.Add(cur = new Ridge { Dist = c.Key });
                        groupStart = c.Key;
                    }
                    if (c.Key > cur.Dist)
                        cur.Dist = c.Key;
                    cur.At.Add(c.Value);
                }
                return ridges;
            }

            /// <summary>Even-odd test against a single closed ring.</summary>
            public static bool Contains(List<Point2D> ring, double px, double py)
            {
                return Inside(new IList<Point2D>[] { ring }, px, py);
            }

            // Even-odd ray casting along +X. Counters fall out for free: a point inside the hole of an O
            // crosses the outer contour once and the inner contour once, so it is correctly outside.
            private static bool Inside(IList<IList<Point2D>> contours, double px, double py)
            {
                bool inside = false;
                foreach (var c in contours)
                {
                    if (c == null || c.Count < 3)
                        continue;
                    for (int i = 0, j = c.Count - 1; i < c.Count; j = i++)
                    {
                        double yi = c[i].Y, yj = c[j].Y;
                        if ((yi > py) == (yj > py))
                            continue;
                        double xInt = c[i].X + (py - yi) / (yj - yi) * (c[j].X - c[i].X);
                        if (px < xInt)
                            inside = !inside;
                    }
                }
                return inside;
            }

            private static List<Point2D> SampleBoundary(IList<IList<Point2D>> contours, double spacing)
            {
                var pts = new List<Point2D>();
                foreach (var c in contours)
                {
                    if (c == null || c.Count < 2)
                        continue;
                    for (int i = 0; i < c.Count - 1; i++)
                    {
                        var a = c[i];
                        var b = c[i + 1];
                        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                        int n = (int)Math.Ceiling(len / spacing);
                        if (n < 1) n = 1;
                        for (int k = 0; k < n; k++)
                        {
                            double t = (double)k / n;
                            pts.Add(new Point2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                        }
                    }
                    pts.Add(c[c.Count - 1]);
                }
                return pts;
            }
        }

        // Uniform bucket grid over the boundary samples. The nearest-point query walks outward a ring of
        // buckets at a time. Ring number is a CHEBYSHEV distance, so the first hit is not necessarily the
        // nearest point: a sample found diagonally in ring r can be sqrt(2) times as far as a sample
        // sitting axis-aligned in a much later ring. The search therefore keeps walking until the nearest
        // possible point in the next ring - (r-1) cells, since the query sits somewhere inside its own
        // bucket - is already further than the best hit. Terminating "one ring after the first hit"
        // instead is what made every circle's contours drift outward: the nearest boundary of a curved
        // edge is often axis-aligned from the query while the first Chebyshev hit is diagonal.
        private class PointGrid
        {
            private readonly List<int>[] cells;
            private readonly List<Point2D> pts;
            private readonly double x0, y0, cell, far;
            private readonly int nx, ny, maxRings;

            public PointGrid(List<Point2D> points, double x0, double y0, double cell, int nx, int ny, double maxDist)
            {
                this.pts = points; this.x0 = x0; this.y0 = y0; this.cell = cell; this.nx = nx; this.ny = ny;
                // Past maxDist every value means the same thing - "deeper than the tool goes" - so the
                // walk gives up there rather than crossing the whole grid for deep interior points.
                maxRings = (int)Math.Ceiling(maxDist / cell) + 2;
                far = maxDist * FarFactor;
                cells = new List<int>[nx * ny];
                for (int i = 0; i < points.Count; i++)
                {
                    int cx = (int)((points[i].X - x0) / cell);
                    int cy = (int)((points[i].Y - y0) / cell);
                    if (cx < 0 || cy < 0 || cx >= nx || cy >= ny)
                        continue;
                    int k = cy * nx + cx;
                    if (cells[k] == null)
                        cells[k] = new List<int>();
                    cells[k].Add(i);
                }
            }

            public double NearestDistance(double px, double py)
            {
                int cx = (int)((px - x0) / cell);
                int cy = (int)((py - y0) / cell);
                double best = double.MaxValue;

                for (int r = 0; r <= maxRings; r++)
                {
                    // No point in ring r can be nearer than (r-1) cells; once that exceeds the best hit,
                    // no later ring can improve it either.
                    double ringMin = (r - 1) * cell;
                    if (ringMin > 0d && ringMin * ringMin >= best)
                        break;

                    for (int j = cy - r; j <= cy + r; j++)
                    {
                        if (j < 0 || j >= ny) continue;
                        for (int i = cx - r; i <= cx + r; i++)
                        {
                            if (i < 0 || i >= nx) continue;
                            // Only the ring's edge - the interior was covered by smaller r.
                            if (r > 0 && i != cx - r && i != cx + r && j != cy - r && j != cy + r)
                                continue;
                            var list = cells[j * nx + i];
                            if (list == null) continue;
                            foreach (int idx in list)
                            {
                                double dx = pts[idx].X - px, dy = pts[idx].Y - py;
                                double d2 = dx * dx + dy * dy;
                                if (d2 < best) best = d2;
                            }
                        }
                    }
                }

                return best == double.MaxValue ? far : Math.Sqrt(best);
            }
        }
    }
}
