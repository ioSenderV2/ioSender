/*
 * svg-compare - does the portable SVG importer agree with the WPF one it replaced?
 *
 * CNC.Svg replaced WPF's Geometry.Parse + GetFlattenedPathGeometry with a hand-written parser and
 * flattener (SvgPath.cs) so the SVG-to-laser converter can run on the EngravingBox appliance. A
 * clean compile says nothing about whether the artwork still comes out the same shape, and the SVG
 * work has a history of being proven by RUNNING it - the original import was verified with a fill
 * map, not a build.
 *
 * ---- Why the comparison is geometric and not textual ----
 *
 * A different flattener subdivides curves differently, so the two produce different POINT COUNTS for
 * the same curve and the emitted g-code differs line for line. That is expected and is not a defect.
 * What must agree is the shape:
 *
 *   - contour count            a lost or invented subpath is a lost or invented piece of artwork
 *   - outer / hole split       IsOuter drives cut ORDER (WorkOrderCompiler's pass grouping)
 *   - bounding box             the artwork's size on the material
 *   - total enclosed area      the sensitive one: a curve flattened wrongly, a subpath that closed
 *                              in the wrong place, or an arc with the wrong sweep all move it while
 *                              leaving the contour count untouched
 *
 * LegacySvgOutlines.cs is the ORIGINAL file, taken verbatim out of git at the commit before the
 * extraction and renamed into SvgCompare.Legacy. It is deliberately not tidied: the point is to
 * compare against what actually shipped, not against a rewrite of it.
 *
 * Run:  dotnet run --project tools/svg-compare        (defaults to tests/svg/*.svg)
 *       dotnet run --project tools/svg-compare -- <file.svg> [more.svg ...]
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CNC.Core;

namespace SvgCompare
{
    internal static class Program
    {
        // The width every file is normalised to before comparing. Any non-zero value works - the
        // importer scales from the ink bounding box - but a realistic one keeps the reported areas in
        // the same range as a real job, so a tolerance expressed in mm² means what it looks like.
        private const double TargetWidthMm = 150d;

        // Areas are sums over thousands of flattened segments, and the two flatteners place those
        // segments differently, so exact agreement is not achievable even in principle. What matters
        // is that the disagreement stays at the level of chord-vs-arc error rather than a missing
        // hole: 0.1% of the artwork's own area is far below any feature a laser can express and far
        // above the noise floor of two different subdivisions.
        private const double AreaTolerancePercent = 0.1d;

        // Bounds come from the extreme points, which both flatteners place ON the curve, so these
        // agree far more tightly than the areas do. 0.05 mm is a twentieth of a millimetre.
        private const double BoundsToleranceMm = 0.05d;

        private static int Main(string[] args)
        {
            var files = args.Length > 0 ? args.ToList() : DefaultFiles();

            if (files.Count == 0)
            {
                Console.Error.WriteLine("no .svg files given and none found in tests/svg");
                return 2;
            }

            int failures = 0;

            foreach (var file in files)
            {
                Console.WriteLine();
                Console.WriteLine("=== " + Path.GetFileName(file) + " ===");

                if (!File.Exists(file))
                {
                    Console.WriteLine("  MISSING: " + file);
                    failures++;
                    continue;
                }

                var legacy = Legacy.SvgOutlines.Load(file, TargetWidthMm);
                var ported = CNC.Svg.SvgOutlines.Load(file, TargetWidthMm);

                if (!Compare(legacy, ported))
                    failures++;
            }

            failures += CheckEmitter(files);

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "ALL FILES AGREE"
                : failures + " check(s) FAILED");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Structural check on the emitter, which moved out of SvgToLaser into CNC.Svg at the same
        /// time as the importer.
        ///
        /// This is NOT a diff against the old emitter - the old one wrote into CNC.Controls.GCode and
        /// cannot be run outside the sender. It asserts the invariants the FILE has to hold, which are
        /// the ones with teeth: the frame handling at the start and end is what puts the artwork in the
        /// right place and leaves the machine where it was found, and getting it wrong burns the work.
        /// See SvgLaserProgram's header for why each of these lines is where it is.
        /// </summary>
        private static int CheckEmitter(List<string> files)
        {
            var art = files.Select(f => CNC.Svg.SvgOutlines.Load(f, TargetWidthMm))
                           .FirstOrDefault(r => r.Error == null && r.Contours.Count > 0);

            Console.WriteLine();
            Console.WriteLine("=== emitter ===");

            if (art == null)
            {
                Console.WriteLine("  no usable artwork to emit from - SKIPPED");
                return 0;
            }

            // Shading on, two copies with a power ramp: the path that exercises the most of the
            // emitter, including the per-copy re-declarations.
            var options = new CNC.Svg.SvgLaserOptions
            {
                Fill = true, OutlineAfterFill = true, Interval = 1.0d,
                Copies = 2, PitchY = -60d, PitchPower = 50d,
                OriginX = 16.5d, OriginY = -9.525d, MaxPower = 1000d
            };

            var p = CNC.Svg.SvgLaserProgram.Build(art, options);
            int bad = 0;

            bad += Expect(p.Count > 10, "program has content (" + p.Count + " lines)");
            bad += Expect(p.Any(l => l.StartsWith("#<x_org>")), "placement constant x_org declared");
            bad += Expect(p.Any(l => l.StartsWith("#<s_line>")), "exposure constant s_line declared");
            bad += Expect(p.Count(l => l == "G92 X0 Y0") == 2, "two zeroing G92s (park, then artwork origin)");
            bad += Expect(p.Any(l => l.StartsWith("G92 X#<x_org>")), "frame restored by re-declaring the placement");

            int clear = p.FindIndex(l => l == "G92.1");
            int home = p.FindLastIndex(l => l.StartsWith("G0 X0 Y0"));
            bad += Expect(clear >= 0 && home >= 0 && home < clear,
                          "returns to park BEFORE clearing the offset");

            bad += Expect(p[p.Count - 1] == "M30", "ends with M30 (SvgToLaser flags it Action.End)");
            bad += Expect(!p.Any(l => l.Contains("NaN")), "no NaN reached the file");

            return bad;
        }

        private static int Expect(bool ok, string what)
        {
            Console.WriteLine("  " + (ok ? "ok   " : "FAIL ") + what);
            return ok ? 0 : 1;
        }

        private static bool Compare(Legacy.SvgImportResult a, CNC.Svg.SvgImportResult b)
        {
            // An error on either side is only a failure if the two DISAGREE about it. A file both
            // refuse is a file this tool has nothing to say about.
            if (a.Error != null || b.Error != null)
            {
                Console.WriteLine("  legacy error: " + (a.Error ?? "(none)"));
                Console.WriteLine("  ported error: " + (b.Error ?? "(none)"));
                bool same = a.Error == null == (b.Error == null);
                Console.WriteLine(same ? "  both refused - nothing to compare" : "  MISMATCH: one refused, one did not");
                return same;
            }

            bool ok = true;

            ok &= Report("contours", a.Contours.Count, b.Contours.Count);
            ok &= Report("outer", a.Contours.Count(c => c.IsOuter), b.Contours.Count(c => c.IsOuter));
            ok &= Report("holes", a.Contours.Count(c => !c.IsOuter), b.Contours.Count(c => !c.IsOuter));

            ok &= ReportMm("width mm", a.WidthMm, b.WidthMm);
            ok &= ReportMm("height mm", a.HeightMm, b.HeightMm);

            double areaA = TotalArea(a.Contours), areaB = TotalArea(b.Contours);
            ok &= ReportArea("area mm2", areaA, areaB);

            // Not a pass/fail condition - the two flatteners are EXPECTED to differ here, and a
            // tolerance on it would only encode one implementation's subdivision as the right answer.
            // Printed because a wild ratio is a useful smell when something else has already failed.
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-12} legacy {1,10}   ported {2,10}   (informational: subdivision differs by design)",
                "points", TotalPoints(a.Contours), TotalPoints(b.Contours)));

            // Unsupported elements and parse failures are counted, not compared for equality: the new
            // parser reads the SVG grammar rather than WPF's XAML dialect, so it legitimately accepts
            // path data the old one refused. FEWER failures is an improvement; MORE is a regression.
            Console.WriteLine(string.Format(
                "  {0,-12} legacy {1,10}   ported {2,10}", "parse fails", a.ParseFailures, b.ParseFailures));
            if (b.ParseFailures > a.ParseFailures)
            {
                Console.WriteLine("    REGRESSION: the ported parser refused paths the WPF one accepted");
                ok = false;
            }

            Console.WriteLine("  " + (ok ? "AGREE" : "DISAGREE"));
            return ok;
        }

        private static double TotalArea(List<OutlineContour> contours)
        {
            double a = 0d;
            foreach (var c in contours)
                a += Math.Abs(c.SignedArea);
            return a;
        }

        private static int TotalPoints(List<OutlineContour> contours)
        {
            int n = 0;
            foreach (var c in contours)
                n += c.Points.Count;
            return n;
        }

        private static bool Report(string label, int a, int b)
        {
            bool ok = a == b;
            Console.WriteLine(string.Format("  {0,-12} legacy {1,10}   ported {2,10}   {3}",
                label, a, b, ok ? "ok" : "<-- MISMATCH"));
            return ok;
        }

        private static bool ReportMm(string label, double a, double b)
        {
            bool ok = Math.Abs(a - b) <= BoundsToleranceMm;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-12} legacy {1,10:0.####}   ported {2,10:0.####}   {3}",
                label, a, b, ok ? "ok" : "<-- MISMATCH (>" + BoundsToleranceMm + "mm)"));
            return ok;
        }

        private static bool ReportArea(string label, double a, double b)
        {
            double denom = Math.Max(Math.Abs(a), Math.Abs(b));
            double pct = denom <= 0d ? 0d : Math.Abs(a - b) / denom * 100d;
            bool ok = pct <= AreaTolerancePercent;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-12} legacy {1,10:0.###}   ported {2,10:0.###}   {3:0.####}%  {4}",
                label, a, b, pct, ok ? "ok" : "<-- MISMATCH (>" + AreaTolerancePercent + "%)"));
            return ok;
        }

        /// <summary>
        /// tests/svg relative to the repo root, found by walking up from the binary rather than
        /// assuming a working directory - "dotnet run" and a direct .exe launch start in different
        /// places, and a harness that silently compares nothing is worse than one that will not start.
        /// </summary>
        private static List<string> DefaultFiles()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "svg")))
                dir = dir.Parent;

            if (dir == null)
                return new List<string>();

            return Directory.GetFiles(Path.Combine(dir.FullName, "tests", "svg"), "*.svg")
                            .OrderBy(f => f).ToList();
        }
    }
}
