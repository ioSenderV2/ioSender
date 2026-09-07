/*
 * StrokeFont.cs - part of CNC Core library
 *
 * A SINGLE-STROKE font for engraving: each glyph is a set of pen strokes along the letter's centreline,
 * not an outline. A V-bit follows those strokes at a constant depth and the stroke width falls out of the
 * geometry (width = 2 * depth * tan(halfAngle)), which is what "engrave text with a V-bit" means on a
 * router. It is legible down to a few millimetres, where an outline-traced TrueType glyph is not.
 *
 * ---- Why the font is defined here in code ----
 *
 * The obvious source is the Hershey set (public domain, and the origin of most CNC stick fonts), but it
 * is thousands of coordinates that would have to be fetched, and this machine builds offline. Rather than
 * ship a half-remembered transcription of someone else's data - which would be wrong in ways nobody could
 * spot without the original to diff against - the glyphs are authored here. They are readable, editable,
 * and every one of them can be checked by eye against the drawing commands.
 *
 * ---- The path mini-language ----
 *
 * Each glyph is one string. Coordinates are on a normalised em box: baseline y=0, cap height y=1, pen
 * starts at x=0, and the glyph's own advance width is declared separately (this is a proportional font -
 * an "I" must not occupy the same width as a "W").
 *
 *   M x,y            lift the pen and move - starts a new stroke
 *   L x,y            draw to
 *   A cx,cy r a0 a1  arc about (cx,cy), radius r, from a0 to a1 DEGREES, signed: a1>a0 sweeps CCW
 *
 * The arc command earns its place: O, C, G, S and the digits are most of the alphabet, and writing them
 * as polylines would be both bulky and visibly faceted at large sizes. Arcs are flattened at render time
 * against the requested size, so a 5 mm letter and a 50 mm letter each get an appropriate segment count
 * instead of one baked-in compromise.
 *
 * ---- Deliberately not here ----
 *
 * No kerning pairs and no ligatures: at engraving sizes on a router the difference is far below what the
 * cutter itself resolves, and every pair is another thing to get wrong. Lowercase currently folds to
 * uppercase - see Glyphs - which is stated rather than silently rendering blanks.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CNC.Core
{
    /// <summary>
    /// Turns text into pen strokes for engraving. Pure geometry - no I/O, no UI, no g-code.
    /// </summary>
    public static class StrokeFont
    {
        // Advance width of a space, and the gap left between glyphs, both in em units (cap heights).
        private const double SpaceWidth = 0.55d;
        private const double LetterGap = 0.14d;

        // Baseline-to-baseline distance for multi-line text, in em units. 1.0 is the cap height itself, so
        // this leaves half a cap height of clear air between lines - tight enough to read as a block,
        // loose enough that a descender never touches the line below.
        public const double DefaultLineSpacing = 1.5d;

        // Arc flattening: one segment per this many degrees, then clamped by ArcSagitta below. Two limits
        // rather than one because they fail in opposite directions - a fixed angle over-segments a tiny
        // letter and under-segments a huge one.
        private const double DegreesPerSegment = 15d;
        private const double MinSegments = 3d;

        /// <summary>
        /// One glyph: its strokes, and how far the pen advances afterwards. Width is NOT derived from the
        /// drawn extent - a comma is drawn narrow but must still advance a sensible amount.
        /// </summary>
        private struct Glyph
        {
            public readonly string Path;
            public readonly double Width;
            public Glyph(double width, string path) { Width = width; Path = path; }
        }

        // The glyph table. Uppercase, digits and the punctuation an engraved label actually uses.
        //
        // Lowercase is deliberately absent and FOLDS to uppercase (see StrokesFor): a single-stroke
        // lowercase set is another 26 glyphs of careful authoring, and rendering nothing at all for "abc"
        // would be a worse answer than rendering "ABC". Worth adding, but not worth blocking on.
        private static readonly Dictionary<char, Glyph> Glyphs = new Dictionary<char, Glyph>
        {
            // ---- uppercase ----
            { 'A', new Glyph(0.72, "M0,0 L0.36,1 L0.72,0 M0.13,0.36 L0.59,0.36") },
            { 'B', new Glyph(0.68, "M0,0 L0,1 L0.44,1 A0.44,0.75 0.25 90 -90 L0,0.5 L0.46,0.5 A0.46,0.25 0.25 90 -90 L0,0") },
            // C and G share a skeleton with O - two arcs of radius 0.38 whose centres sit at y 0.65 and
            // 0.35, joined down the left by a straight run - so the round letters all read as one family.
            // Every piece begins exactly where the last ended, which is what keeps them a single stroke:
            // the old C had 0.156 and 0.450 em of stray straight line where its arcs failed to meet.
            { 'C', new Glyph(0.70, "A0.38,0.65 0.38 45 180 L0,0.35 A0.38,0.35 0.38 180 315") },
            { 'D', new Glyph(0.70, "M0,0 L0,1 L0.35,1 A0.35,0.65 0.35 90 -90 L0,0") },
            { 'E', new Glyph(0.62, "M0.62,1 L0,1 L0,0 L0.62,0 M0,0.5 L0.50,0.5") },
            { 'F', new Glyph(0.60, "M0.60,1 L0,1 L0,0 M0,0.52 L0.48,0.52") },
            // G is C carried round to the right at mid-height, then the bar back in - still one stroke.
            { 'G', new Glyph(0.74, "A0.38,0.65 0.38 45 180 L0,0.35 A0.38,0.35 0.38 180 360 L0.44,0.35") },
            { 'H', new Glyph(0.70, "M0,0 L0,1 M0.70,0 L0.70,1 M0,0.5 L0.70,0.5") },
            { 'I', new Glyph(0.20, "M0.10,0 L0.10,1 M0,1 L0.20,1 M0,0 L0.20,0") },
            { 'J', new Glyph(0.56, "M0.56,1 L0.56,0.25 A0.28,0.25 0.28 0 -180") },
            { 'K', new Glyph(0.68, "M0,0 L0,1 M0.66,1 L0.05,0.45 M0.22,0.61 L0.68,0") },
            { 'L', new Glyph(0.58, "M0,1 L0,0 L0.58,0") },
            { 'M', new Glyph(0.84, "M0,0 L0,1 L0.42,0.42 L0.84,1 L0.84,0") },
            { 'N', new Glyph(0.72, "M0,0 L0,1 L0.72,0 L0.72,1") },
            { 'O', new Glyph(0.76, "M0,0.65 L0,0.35 A0.38,0.35 0.38 180 360 L0.76,0.65 A0.38,0.65 0.38 0 180") },
            { 'P', new Glyph(0.66, "M0,0 L0,1 L0.40,1 A0.40,0.75 0.25 90 -90 L0,0.5") },
            { 'Q', new Glyph(0.76, "M0,0.65 L0,0.35 A0.38,0.35 0.38 180 360 L0.76,0.65 A0.38,0.65 0.38 0 180 M0.48,0.22 L0.78,-0.06") },
            { 'R', new Glyph(0.68, "M0,0 L0,1 L0.40,1 A0.40,0.75 0.25 90 -90 L0,0.5 M0.34,0.5 L0.68,0") },
            // ONE stroke, two arcs meeting at the waist. It was three strokes with two M commands and arc
            // endpoints that did not meet, so the bit lifted and re-plunged twice inside the letter and cut
            // something closer to a 5 (reported from a real carve, 2026-08-07).
            //
            // The join is the whole design: two circles only meet at one point if they are tangent, which
            // needs their centres 2r apart. r = 0.25 with centres at y = 0.75 and 0.25 puts that point at
            // mid-height and makes the letter span the full em. Change one of those three numbers and the
            // arcs stop meeting - the stroke will still be continuous, but it will have a kink where the
            // second arc jumps to its own start.
            { 'S', new Glyph(0.66, "A0.33,0.75 0.25 20 270 A0.33,0.25 0.25 90 -160") },
            { 'T', new Glyph(0.64, "M0,1 L0.64,1 M0.32,1 L0.32,0") },
            { 'U', new Glyph(0.70, "M0,1 L0,0.32 A0.35,0.32 0.35 180 360 L0.70,1") },
            { 'V', new Glyph(0.70, "M0,1 L0.35,0 L0.70,1") },
            { 'W', new Glyph(0.94, "M0,1 L0.20,0 L0.47,0.62 L0.74,0 L0.94,1") },
            { 'X', new Glyph(0.68, "M0,0 L0.68,1 M0,1 L0.68,0") },
            { 'Y', new Glyph(0.68, "M0,1 L0.34,0.5 L0.68,1 M0.34,0.5 L0.34,0") },
            { 'Z', new Glyph(0.64, "M0,1 L0.64,1 L0,0 L0.64,0") },

            // ---- digits ----
            { '0', new Glyph(0.66, "M0,0.66 L0,0.34 A0.33,0.34 0.33 180 360 L0.66,0.66 A0.33,0.66 0.33 0 180 M0.14,0.20 L0.52,0.80") },
            { '1', new Glyph(0.40, "M0.06,0.80 L0.22,1 L0.22,0 M0,0 L0.44,0") },
            { '2', new Glyph(0.62, "M0.02,0.78 A0.31,0.72 0.31 160 20 L0.62,0.64 L0,0 L0.62,0") },
            { '3', new Glyph(0.62, "M0.02,0.80 A0.31,0.74 0.28 160 -20 L0.31,0.52 A0.31,0.26 0.28 90 -160") },
            { '4', new Glyph(0.66, "M0.48,0 L0.48,1 L0,0.30 L0.66,0.30") },
            // The stem lands exactly on the bowl: at 150 deg a circle of r 0.30 about (0.32,0.30) passes
            // through (0.060,0.450), so the vertical can stop there and the arc continue from it. Picking
            // the stem's x first and hoping the arc met it is what left the old 5 with a 0.195 em spur.
            { '5', new Glyph(0.62, "M0.62,1 L0.06,1 L0.06,0.45 A0.32,0.30 0.30 150 -140") },
            // 6 and 9 are one shape and its 180-deg rotation: a bowl, plus a tail arc that starts exactly
            // on the bowl so the two join without a visible step. Built as a pair on purpose - drawing
            // them independently is how you end up with a 6 and a 9 that do not match.
            { '6', new Glyph(0.64, "A0.60,0.30 0.56 90 180 A0.32,0.28 0.28 180 540") },
            { '7', new Glyph(0.60, "M0,1 L0.60,1 L0.22,0") },
            { '8', new Glyph(0.64, "M0.32,0.52 A0.32,0.76 0.24 -90 270 M0.32,0.52 A0.32,0.26 0.26 90 450") },
            { '9', new Glyph(0.64, "A0.32,0.72 0.28 0 360 A0.04,0.70 0.56 0 -90") },

            // ---- punctuation ----
            { '.', new Glyph(0.24, "M0.10,0 L0.14,0") },
            { ',', new Glyph(0.24, "M0.14,0.04 L0.06,-0.14") },
            { '-', new Glyph(0.50, "M0.08,0.46 L0.42,0.46") },
            { '_', new Glyph(0.60, "M0,-0.14 L0.60,-0.14") },
            { ':', new Glyph(0.24, "M0.10,0 L0.14,0 M0.10,0.62 L0.14,0.62") },
            { ';', new Glyph(0.24, "M0.14,0.04 L0.06,-0.14 M0.10,0.62 L0.14,0.62") },
            { '/', new Glyph(0.54, "M0,-0.08 L0.54,1.02") },
            { '\\', new Glyph(0.54, "M0,1.02 L0.54,-0.08") },
            // A mirrored pair, both drawn as a single arc with the centre OUTSIDE the glyph: "(" has its
            // centre to the right and bulges left through 180 deg, ")" has its centre at negative x and
            // bulges right through 0 deg. Getting ")" the other way round put it at x = -0.24, i.e. biting
            // into the previous character - which is exactly what the bounds check caught.
            { '(', new Glyph(0.40, "A0.64,0.48 0.6 120 240") },
            { ')', new Glyph(0.40, "A-0.26,0.48 0.6 -60 60") },
            { '#', new Glyph(0.74, "M0.20,0 L0.30,1 M0.46,0 L0.56,1 M0.04,0.32 L0.68,0.32 M0.08,0.68 L0.72,0.68") },
            { '+', new Glyph(0.62, "M0.31,0.16 L0.31,0.76 M0.01,0.46 L0.61,0.46") },
            { '=', new Glyph(0.62, "M0.01,0.32 L0.61,0.32 M0.01,0.60 L0.61,0.60") },
            { '\'', new Glyph(0.20, "M0.10,1 L0.10,0.74") },
            { '"', new Glyph(0.34, "M0.10,1 L0.10,0.74 M0.24,1 L0.24,0.74") },
            { '!', new Glyph(0.22, "M0.11,1 L0.11,0.28 M0.11,0 L0.11,0.04") },
            { '?', new Glyph(0.58, "M0.02,0.78 A0.29,0.72 0.29 165 15 L0.29,0.36 M0.29,0 L0.29,0.04") },
            { '%', new Glyph(0.80, "M0,0 L0.80,1 M0.15,0.85 A0.15,0.85 0.15 0 360 M0.65,0.15 A0.65,0.15 0.15 0 360") },
            { '&', new Glyph(0.78, "M0.78,0 L0.24,0.72 A0.38,0.72 0.14 -20 200 L0.16,0.44 A0.32,0.28 0.30 130 -60 L0.70,0.52") },
            { '*', new Glyph(0.52, "M0.26,0.42 L0.26,0.94 M0.03,0.55 L0.49,0.81 M0.03,0.81 L0.49,0.55") },
            { '@', new Glyph(0.86, "M0.55,0.32 A0.43,0.46 0.16 0 360 M0.71,0.46 L0.71,0.28 A0.43,0.46 0.43 300 60") },
        };

        /// <summary>
        /// Lay <paramref name="text"/> out as engraving strokes, in millimetres, with the text's baseline
        /// starting at (0,0) and running along +X. Newlines start a new line below the previous one.
        /// </summary>
        /// <param name="capHeight">Height of a capital letter in mm - the size an operator actually means.</param>
        /// <param name="lineSpacing">Baseline-to-baseline distance as a multiple of capHeight.</param>
        /// <returns>One list of points per pen stroke. Never null; unknown characters are skipped.</returns>
        public static List<List<Point2D>> Render(string text, double capHeight, double lineSpacing = DefaultLineSpacing)
        {
            var strokes = new List<List<Point2D>>();
            if (string.IsNullOrEmpty(text) || capHeight <= 0d)
                return strokes;

            double penX = 0d, penY = 0d;
            foreach (char raw in text)
            {
                if (raw == '\n')
                {
                    penX = 0d;
                    penY -= Math.Abs(lineSpacing) * capHeight;
                    continue;
                }
                if (raw == '\r')
                    continue;
                if (raw == ' ' || raw == '\t')
                {
                    penX += (SpaceWidth + LetterGap) * capHeight;
                    continue;
                }

                Glyph g;
                if (!TryGetGlyph(raw, out g))
                    continue;   // nothing sensible to draw - skip rather than substitute a wrong character

                foreach (var stroke in ParsePath(g.Path, capHeight))
                {
                    for (int i = 0; i < stroke.Count; i++)
                        stroke[i] = new Point2D(stroke[i].X + penX, stroke[i].Y + penY);
                    strokes.Add(stroke);
                }
                penX += (g.Width + LetterGap) * capHeight;
            }

            return strokes;
        }

        /// <summary>
        /// Width and height the rendered text occupies, in mm, without building the strokes. For laying a
        /// label out against stock before any g-code exists.
        /// </summary>
        public static Point2D Measure(string text, double capHeight, double lineSpacing = DefaultLineSpacing)
        {
            if (string.IsNullOrEmpty(text) || capHeight <= 0d)
                return new Point2D(0d, 0d);

            double widest = 0d, run = 0d;
            int lines = 1;
            foreach (char raw in text)
            {
                if (raw == '\n') { widest = Math.Max(widest, run); run = 0d; lines++; continue; }
                if (raw == '\r') continue;
                if (raw == ' ' || raw == '\t') { run += (SpaceWidth + LetterGap) * capHeight; continue; }
                Glyph g;
                if (TryGetGlyph(raw, out g))
                    run += (g.Width + LetterGap) * capHeight;
            }
            widest = Math.Max(widest, run);

            // Trim the trailing inter-letter gap - it is spacing to the NEXT glyph, and there isn't one.
            if (widest > 0d)
                widest -= LetterGap * capHeight;

            double height = capHeight + (lines - 1) * Math.Abs(lineSpacing) * capHeight;
            return new Point2D(Math.Max(0d, widest), height);
        }

        /// <summary>Characters this font can draw - for a UI that wants to warn before cutting.</summary>
        public static bool CanRender(char c)
        {
            Glyph g;
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || TryGetGlyph(c, out g);
        }

        /// <summary>The characters in <paramref name="text"/> that would be silently skipped.</summary>
        public static string UnsupportedCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var seen = new List<char>();
            foreach (char c in text)
                if (!CanRender(c) && !seen.Contains(c))
                    seen.Add(c);
            return new string(seen.ToArray());
        }

        // Lowercase folds to uppercase - see the Glyphs comment. Done here, in one place, so every entry
        // point (Render, Measure, CanRender) agrees about what is renderable.
        private static bool TryGetGlyph(char c, out Glyph g)
        {
            if (Glyphs.TryGetValue(c, out g))
                return true;
            char up = char.ToUpperInvariant(c);
            return up != c && Glyphs.TryGetValue(up, out g);
        }

        // ---- path parsing ----

        private static List<List<Point2D>> ParsePath(string path, double scale)
        {
            var strokes = new List<List<Point2D>>();
            List<Point2D> cur = null;

            var tokens = path.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                char cmd = t[0];

                if (cmd == 'M' || cmd == 'L')
                {
                    var p = Pair(t.Substring(1), scale);
                    if (cmd == 'M')
                    {
                        cur = new List<Point2D>();
                        strokes.Add(cur);
                    }
                    if (cur == null)   // a path that opens with L is malformed; treat it as a move
                    {
                        cur = new List<Point2D>();
                        strokes.Add(cur);
                    }
                    cur.Add(p);
                }
                else if (cmd == 'A')
                {
                    // A cx,cy r a0 a1  - four whitespace-separated tokens, the first carrying the command.
                    var c = Pair(t.Substring(1), scale);
                    double r = Num(tokens[++i]) * scale;
                    double a0 = Num(tokens[++i]);
                    double a1 = Num(tokens[++i]);

                    if (cur == null)
                    {
                        cur = new List<Point2D>();
                        strokes.Add(cur);
                    }
                    AppendArc(cur, c, r, a0, a1);
                }
            }

            // A stroke of one point draws nothing and would emit a zero-length move; drop it.
            strokes.RemoveAll(s => s.Count < 2);
            return strokes;
        }

        // Flatten an arc into the current stroke. The first sample is skipped when the pen is already
        // there (an arc continuing a line), so a duplicate point never reaches the g-code.
        private static void AppendArc(List<Point2D> stroke, Point2D c, double r, double a0Deg, double a1Deg)
        {
            double sweep = a1Deg - a0Deg;
            int n = (int)Math.Ceiling(Math.Abs(sweep) / DegreesPerSegment);
            if (n < MinSegments)
                n = (int)MinSegments;

            for (int i = 0; i <= n; i++)
            {
                double a = (a0Deg + sweep * i / n) * Math.PI / 180d;
                var p = new Point2D(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
                if (i == 0 && stroke.Count > 0 && Near(stroke[stroke.Count - 1], p))
                    continue;
                stroke.Add(p);
            }
        }

        private static bool Near(Point2D a, Point2D b)
        {
            return Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
        }

        private static Point2D Pair(string s, double scale)
        {
            int comma = s.IndexOf(',');
            return new Point2D(Num(s.Substring(0, comma)) * scale, Num(s.Substring(comma + 1)) * scale);
        }

        private static double Num(string s)
        {
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
