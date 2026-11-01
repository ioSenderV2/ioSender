/*
 * SvgLaserFill.cs - part of the CNC.Svg library
 *
 * Turns closed contours into horizontal spans to burn - the shading pass for SvgToLaser.
 *
 * ---- How ----
 *
 * Plain scanline conversion. For each Y at the chosen interval, find where every contour edge crosses
 * that line, sort the crossings by X, and take alternate pairs as the inside of the shape. That is the
 * even-odd rule, and it handles counters (the hole in an 'o', the gap inside a ring) without needing to
 * know which contour is an outer boundary and which is a hole - a point inside a counter is crossed an
 * even number of times, so it falls outside a span and is left unburned. SvgOutlines does record
 * IsOuter/SignedArea, but not needing it is one less thing to be wrong about.
 *
 * ---- The two edge cases that actually bite ----
 *
 * Both produce artefacts you can SEE - a streak of unburned material across the shading, or a doubled
 * span that burns twice as dark - and both are silent in the g-code.
 *
 *   A vertex landing exactly on a scan line. Two edges meet there, so a naive test counts TWO crossings
 *   at one point and the parity flips twice - the span ends where it should have continued. Fixed with
 *   the half-open rule: an edge owns its lower endpoint and not its upper (y0 <= y < y1). Every vertex
 *   is then counted exactly once, whichever way the two edges run.
 *
 *   A horizontal edge lying along a scan line. It has no single crossing - it IS the line - and any
 *   answer is arbitrary. Skipped entirely; the non-horizontal edges either side of it already give the
 *   correct parity.
 *
 * Scan lines are offset half an interval from the artwork's bottom so they land BETWEEN the extremes
 * rather than exactly on them, which keeps the commonest version of the vertex case from arising at
 * all on artwork drawn to round numbers.
 */

using System;
using System.Collections.Generic;
using CNC.Core;

namespace CNC.Svg
{
    /// <summary>One horizontal run of material to burn, at a given height.</summary>
    public struct FillSpan
    {
        public double Y, X0, X1;
    }

    public static class SvgLaserFill
    {
        // Crossings closer together than this are the same point - a contour that doubles back on
        // itself, or flattening noise. Well below anything a beam can express.
        private const double Epsilon = 1e-9;

        // Spans shorter than this are not worth the acceleration to reach: the head would spend longer
        // starting and stopping than burning, and the result is a dot, not a line. Roughly a spot width.
        private const double MinSpanMm = 0.05d;

        /// <summary>
        /// Horizontal spans covering the interior of <paramref name="contours"/>, bottom to top, with
        /// alternate rows reversed so the head sweeps back and forth instead of returning to the left
        /// edge every line.
        /// </summary>
        public static List<FillSpan> Build(IList<OutlineContour> contours, double intervalMm)
        {
            var spans = new List<FillSpan>();

            if (contours == null || contours.Count == 0 || intervalMm <= 0d)
                return spans;

            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (var c in contours)
                foreach (var p in c.Points)
                {
                    if (p.Y < yMin) yMin = p.Y;
                    if (p.Y > yMax) yMax = p.Y;
                }

            if (yMax <= yMin)
                return spans;

            var crossings = new List<double>();
            bool reverse = false;

            // Half an interval in from the bottom - see this file's header.
            for (double y = yMin + intervalMm * 0.5d; y < yMax; y += intervalMm)
            {
                crossings.Clear();

                foreach (var c in contours)
                {
                    var pts = c.Points;
                    if (pts.Count < 2)
                        continue;

                    for (int i = 0; i < pts.Count; i++)
                    {
                        var a = pts[i];
                        var b = pts[(i + 1) % pts.Count];   // wraps - the contour is closed

                        // Horizontal edge: it lies along the scan line rather than crossing it. Skipped.
                        if (a.Y == b.Y)
                            continue;

                        // Half-open: the edge owns its LOWER endpoint only, so a vertex shared by two
                        // edges is counted exactly once however the pair is oriented.
                        double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                        if (y < lo || y >= hi)
                            continue;

                        crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    }
                }

                if (crossings.Count < 2)
                    continue;

                crossings.Sort();

                var row = new List<FillSpan>();
                // Alternate pairs are inside. An odd count would mean a crossing was miscounted, and
                // pairing what is there is the safe reading - it can only ever burn less, never stray
                // outside the artwork.
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                {
                    double x0 = crossings[i], x1 = crossings[i + 1];
                    if (x1 - x0 <= Epsilon || x1 - x0 < MinSpanMm)
                        continue;
                    row.Add(new FillSpan { Y = y, X0 = x0, X1 = x1 });
                }

                if (row.Count == 0)
                    continue;

                // Serpentine: every other row runs right-to-left, so the head carries on from where it
                // finished instead of flying back across the work with the beam off.
                if (reverse)
                {
                    row.Reverse();
                    for (int i = 0; i < row.Count; i++)
                    {
                        var s = row[i];
                        double t = s.X0; s.X0 = s.X1; s.X1 = t;
                        row[i] = s;
                    }
                }

                spans.AddRange(row);
                reverse = !reverse;
            }

            return spans;
        }
    }
}
