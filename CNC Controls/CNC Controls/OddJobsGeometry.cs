/*
 * OddJobsGeometry.cs - part of CNC Controls library
 *
 * Shared wall-point builders + tab-emission walk for the Odd Jobs tab's Pocket and Contour/Slot wizards -
 * both need the same round/rect outline geometry and the same "leave uncut tabs on a through cut" logic,
 * factored here once instead of duplicated between them.
 */

using System;
using System.Collections.Generic;

namespace CNC.Controls
{
    public static class OddJobsGeometry
    {
        // N-gon approximation of a circle of radius r centered at (cx,cy), starting at angle 0 (+X), CCW,
        // closed (last point == first). segments should be enough that no chord matters at machining scale.
        public static List<double[]> CirclePoints(double cx, double cy, double r, int segments)
        {
            var pts = new List<double[]>();
            for (int i = 0; i <= segments; i++)
            {
                double a = 2d * Math.PI * i / segments;
                pts.Add(new[] { cx + r * Math.Cos(a), cy + r * Math.Sin(a) });
            }
            return pts;
        }

        // Ellipse (oval) approximation centered at (cx,cy) with semi-axes rx/ry, starting at angle 0 (+X),
        // CCW, closed (last point == first). A circle is just the rx == ry case, but CirclePoints stays as its
        // own entry point since most callers only have one radius to give.
        public static List<double[]> EllipsePoints(double cx, double cy, double rx, double ry, int segments)
        {
            var pts = new List<double[]>();
            for (int i = 0; i <= segments; i++)
            {
                double a = 2d * Math.PI * i / segments;
                pts.Add(new[] { cx + rx * Math.Cos(a), cy + ry * Math.Sin(a) });
            }
            return pts;
        }

        // An OPEN path: a straight line of the given length centered on (cx,cy), rotated angleDeg from +X.
        // Subdivided so a caller walking it gets intermediate points rather than one long move.
        public static List<double[]> LinePoints(double cx, double cy, double length, double angleDeg, double maxSegmentLength)
        {
            double a = angleDeg * Math.PI / 180d;
            double dx = Math.Cos(a) * length / 2d, dy = Math.Sin(a) * length / 2d;
            var pts = new List<double[]>();
            Subdivide(pts, new[] { cx - dx, cy - dy }, new[] { cx + dx, cy + dy }, maxSegmentLength);
            pts.Add(new[] { cx + dx, cy + dy });
            return pts;
        }

        // Rectangle perimeter centered at (cx,cy), half-width hw, half-height hh, starting at the
        // front-left corner, CCW, closed (last point == first). Edges are subdivided so no segment exceeds
        // maxSegmentLength - keeps a tab window's boundary landing close to where it's meant to.
        //
        // dogboneReach > 0 adds a CORNER RELIEF at each corner: on arriving at the corner the path pokes
        // OUTWARD along that corner's diagonal by dogboneReach on each axis, then comes straight back
        // before turning down the next edge. See DogboneReachFor for what to pass.
        public static List<double[]> RectPoints(double cx, double cy, double hw, double hh, double maxSegmentLength, double dogboneReach = 0d)
        {
            var corners = new[] {
                new[] { cx - hw, cy - hh }, new[] { cx + hw, cy - hh },
                new[] { cx + hw, cy + hh }, new[] { cx - hw, cy + hh },
                new[] { cx - hw, cy - hh }
            };
            var pts = new List<double[]>();
            for (int i = 0; i < corners.Length - 1; i++)
            {
                Subdivide(pts, corners[i], corners[i + 1], maxSegmentLength);

                // The relief belongs to the corner this edge ENDS at, and is emitted before turning onto
                // the next edge. Subdivide leaves its end point off, so the corner itself is added here -
                // out to the relief and back - rather than by the next Subdivide's start.
                if (dogboneReach > 0d)
                {
                    var c = corners[i + 1];
                    // Outward = away from the rectangle's centre on both axes, which for an inset wall
                    // path is the direction of the material still standing in the corner.
                    double ox = c[0] > cx ? dogboneReach : -dogboneReach;
                    double oy = c[1] > cy ? dogboneReach : -dogboneReach;
                    pts.Add(new[] { c[0], c[1] });
                    pts.Add(new[] { c[0] + ox, c[1] + oy });
                }
            }
            pts.Add(corners[corners.Length - 1]);
            return pts;
        }

        // How far a dogbone must poke out of a corner, per axis, for a cutter of radius toolRadius running
        // an outline inset by `inset` from the true wall.
        //
        // The relief is done when the cutter's CIRCLE passes through the true corner point C. That puts the
        // cutter centre at C + (toolRadius/sqrt2) on each axis towards the interior - distance toolRadius
        // from C, along the diagonal. The path corner sits at C + inset on each axis, so the poke is the
        // difference. With the usual inset == toolRadius that is toolRadius*(1 - 1/sqrt2) per axis
        // (~0.293r), a diagonal travel of r*(sqrt2 - 1) (~0.414r), and it overcuts each wall by ~0.293r.
        //
        // Note this comes out right for ANY inset, not just inset == toolRadius: the reach is measured from
        // wherever the path corner actually is, so the cutter still lands the same distance from the true
        // corner. Returns 0 when the path is already at or inside that point, i.e. nothing to relieve.
        public static double DogboneReachFor(double toolRadius, double inset)
        {
            return Math.Max(0d, inset - toolRadius / Math.Sqrt(2d));
        }

        private static void Subdivide(List<double[]> pts, double[] a, double[] b, double maxSegmentLength)
        {
            double len = Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2));
            int n = Math.Max(1, (int)Math.Ceiling(len / Math.Max(0.1d, maxSegmentLength)));
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                pts.Add(new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t });
            }
        }

        // Walks a CLOSED path (points[0] == points[last]) once, returning (x,y,z) samples: z is floorZ
        // except inside numTabs evenly-spaced windows (by cumulative arc length around the path) of width
        // tabWidth, where z is held at floorZ+tabHeight - a bridge of uncut material. A boundary-crossing
        // sample is inserted exactly at each window edge so the Z step lands at the right XY (no ramp - a
        // short vertical face at each tab edge, same as a typical no-ramp CAM tab). numTabs <= 0 or
        // tabHeight <= 0 returns the path unchanged at floorZ (no tabs).
        public static List<double[]> ApplyTabs(List<double[]> path, double floorZ, double tabHeight, int numTabs, double tabWidth)
        {
            var outPts = new List<double[]>();
            if (numTabs <= 0 || tabHeight <= 0d || tabWidth <= 0d || path.Count < 2)
            {
                foreach (var p in path)
                    outPts.Add(new[] { p[0], p[1], floorZ });
                return outPts;
            }

            // Cumulative arc length at each vertex; total = perimeter (path is closed).
            var cum = new double[path.Count];
            for (int i = 1; i < path.Count; i++)
            {
                double dx = path[i][0] - path[i - 1][0], dy = path[i][1] - path[i - 1][1];
                cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            double perim = cum[cum.Length - 1];
            double halfW = Math.Min(tabWidth, perim / Math.Max(1, numTabs)) / 2d;

            bool InWindow(double d)
            {
                for (int t = 0; t < numTabs; t++)
                {
                    double center = perim * t / numTabs;
                    double delta = Math.Abs(((d - center + perim / 2d) % perim + perim) % perim - perim / 2d);
                    if (delta <= halfW)
                        return true;
                }
                return false;
            }

            double PrevBoundaryCrossing(double d0, double d1, out bool enteringTab)
            {
                // Bisect between d0 (outside) and d1 (inside), or vice versa, for the exact crossing distance.
                bool startIn = InWindow(d0);
                double lo = d0, hi = d1;
                for (int iter = 0; iter < 24; iter++)
                {
                    double mid = (lo + hi) / 2d;
                    if (InWindow(mid) == startIn) lo = mid; else hi = mid;
                }
                enteringTab = !startIn;
                return (lo + hi) / 2d;
            }

            outPts.Add(new[] { path[0][0], path[0][1], InWindow(0d) ? floorZ + tabHeight : floorZ });
            for (int i = 1; i < path.Count; i++)
            {
                bool prevIn = InWindow(cum[i - 1]), curIn = InWindow(cum[i]);
                if (prevIn != curIn)
                {
                    double dCross = PrevBoundaryCrossing(cum[i - 1], cum[i], out bool enteringTab);
                    double segLen = cum[i] - cum[i - 1];
                    double t = segLen > 1e-9 ? (dCross - cum[i - 1]) / segLen : 0d;
                    double bx = path[i - 1][0] + (path[i][0] - path[i - 1][0]) * t;
                    double by = path[i - 1][1] + (path[i][1] - path[i - 1][1]) * t;
                    outPts.Add(new[] { bx, by, enteringTab ? floorZ + tabHeight : floorZ });
                }
                outPts.Add(new[] { path[i][0], path[i][1], curIn ? floorZ + tabHeight : floorZ });
            }
            return outPts;
        }
    }
}
