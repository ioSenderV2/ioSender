using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CNC.Core;

namespace NgcRoundTrip
{
    /// <summary>
    /// The resolver's own tests prove the RULES. This proves the WIRING, which they cannot:
    /// that a file declaring constants loads with literals in Data, keeps its source form in Raw,
    /// and would therefore be saved back out with its variables intact.
    ///
    /// GrblInfo.ExpressionsSupported is false here (nothing connected), which is exactly the case
    /// that matters - the laser is stock Grbl 1.1h and can never report EXPR.
    /// </summary>
    public static class Program
    {
        static int fail;

        static void Check(bool ok, string what, string detail = "")
        {
            Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + (ok || detail.Length == 0 ? "" : "   [" + detail + "]"));
            if (!ok) fail++;
        }

        static List<GCodeBlock> Load(string text, out string error, out bool ok)
        {
            string path = Path.Combine(Path.GetTempPath(), "ngc-roundtrip.nc");
            File.WriteAllText(path, text);

            var job = new GCodeJob();
            var got = new List<GCodeBlock>();
            job.BlockConsumer = b => got.Add(b);
            error = null;
            ok = false;
            try { ok = job.ParseFileLines(path); }
            catch (Exception ex) { error = ex.Message; }
            job.BlockConsumer = null;
            return got;
        }

        static void Dump(string label, List<GCodeBlock> blocks)
        {
            Console.WriteLine("  --- " + label + " ---");
            foreach (var b in blocks)
                Console.WriteLine("      Data=[" + b.Data + "]  Raw=[" + (b.Raw ?? "null") + "]");
        }

        public static int Main()
        {
            Console.WriteLine("EXPR supported: " + GrblInfo.ExpressionsSupported + "  (false is the case under test)");
            Console.WriteLine();

            string err;
            bool ok;

            var blocks = Load(
                "(svg laser)\n" +
                "#<s_line> = 200\n" +
                "#<f_line> = 1200\n" +
                "G21 G90 G17\n" +
                "G1 X10 Y-10 S#<s_line> F#<f_line>\n" +
                "M30\n", out err, out ok);

            Dump("loaded", blocks);

            Check(err == null, "a file declaring constants loads without throwing", err ?? "");
            Check(ok, "ParseFileLines reports success");
            Check(blocks.Count > 0, "blocks were produced", blocks.Count.ToString());

            // The parser strips spaces, so Data reads "G1X10Y-10S200F1200" - match what it produces,
            // not what the source file said.
            var cut = blocks.FirstOrDefault(b => b.Data != null && b.Data.Contains("G1X10"));
            Check(cut != null, "the cut move is present");
            if (cut != null)
            {
                Check(cut.Data.Contains("S200") && cut.Data.Contains("F1200"),
                      "Data is RESOLVED - literals reach the parser and the wire", cut.Data);
                Check(!cut.Data.Contains("#"), "no '#' survives into Data", cut.Data);
                Check(cut.Raw != null && cut.Raw.Contains("#<s_line>"),
                      "Raw keeps the SOURCE form", cut.Raw ?? "(null)");
                Check(cut.Source == cut.Raw, "Source returns Raw, so Save writes the variables back out", cut.Source);
            }

            var decl = blocks.FirstOrDefault(b => b.Source != null && b.Source.Contains("#<s_line> ="));
            Check(decl != null, "the declaration line survived as a block");
            if (decl != null)
            {
                Check(decl.Data.Contains("(") && decl.Data.Contains(")"),
                      "a declaration becomes a COMMENT in Data, so line count is preserved", decl.Data);
                Check(decl.Source.Contains("#<s_line> = 200"), "and its Source is still the assignment", decl.Source);
            }

            // What Save would write - the whole point of carrying two forms.
            var saved = string.Join("\n", blocks.Select(b => b.Source));
            Check(saved.Contains("#<s_line> = 200") && saved.Contains("S#<s_line>"),
                  "a SAVED file still contains the variables, not flattened literals");

            // An unchanged line must not carry a redundant second copy of itself.
            var plain = blocks.FirstOrDefault(b => b.Data != null && b.Data.Contains("G21"));
            Check(plain != null, "the plain line is present");
            if (plain != null)
                Check(plain.Raw == null, "a line with no substitution has Raw == null (no second copy)", plain.Raw);

            // ---- the per-copy power ramp -------------------------------------------------------------
            // A test strip re-declares the constant before each copy. The whole feature rests on a
            // redeclaration governing the references BELOW it and nothing above, so prove that through
            // the real loader rather than trusting the resolver's own unit test for it.
            Console.WriteLine();

            var ramp = Load(
                "#<s_line> = 200\n" +
                "#<f_line> = 1200\n" +
                "(copy 1)\n" +
                "G1 X1 S#<s_line> F#<f_line>\n" +
                "#<s_line> = 250\n" +
                "(copy 2)\n" +
                "G1 X2 S#<s_line> F#<f_line>\n" +
                "#<s_line> = 300\n" +
                "(copy 3)\n" +
                "G1 X3 S#<s_line> F#<f_line>\n" +
                "M30\n", out err, out ok);

            Dump("power ramp", ramp);

            var cuts = ramp.Where(b => b.Data != null && b.Data.StartsWith("G1X")).Select(b => b.Data).ToList();
            Check(cuts.Count == 3, "three cut moves", string.Join(" | ", cuts));
            Check(cuts.Count == 3 && cuts[0].Contains("S200") && cuts[1].Contains("S250") && cuts[2].Contains("S300"),
                  "each copy takes the power declared ABOVE it, not the first or the last",
                  string.Join(" | ", cuts));
            Check(cuts.All(c => c.Contains("F1200")),
                  "a constant that was never re-declared keeps its original value throughout",
                  string.Join(" | ", cuts));

            var rampSaved = string.Join("\n", ramp.Select(b => b.Source));
            Check(rampSaved.Contains("#<s_line> = 200") && rampSaved.Contains("#<s_line> = 250") && rampSaved.Contains("#<s_line> = 300"),
                  "all three declarations survive into what Save would write");

            // ---- the header the emitter actually writes ----------------------------------------------
            // Declarations() is the block every generated laser job opens with, so check the TEXT it
            // produces and then load it. A comment line that opens '(' and never closes it is not a
            // comment to anything downstream: IsComment rejects it, the resolver hands it on as code,
            // and it reaches a controller that has to decide what to do with an unbalanced block.
            Console.WriteLine();

            var header = NgcConstants.SvgLaser.Declarations(200, 850, 1200, 4000, true);
            foreach (var line in header)
                Console.WriteLine("      " + line);

            Func<string, bool> balanced = l =>
                l.StartsWith("#<") || (l.StartsWith("(") && l.EndsWith(")"));

            Check(header.All(l => balanced(l)),
                  "every line of the header is a BALANCED comment or an assignment",
                  string.Join(" | ", header.Where(l => !balanced(l))));

            var hdr = Load(string.Join("\n", header) +
                           "\nG1 X1 S#<s_fill> F#<f_fill>\nM30\n", out err, out ok);
            Dump("emitted header", hdr);

            Check(ok, "the emitted header loads", err ?? "");
            // A declaration legitimately becomes "(#<s_line> = 200)" - a comment that contains '#'. What
            // must never happen is a '#' surviving into an executable block.
            Check(!hdr.Any(b => b.Data != null && !b.Data.StartsWith("(") && b.Data.Contains("#")),
                  "no unresolved reference survives into an executable block",
                  string.Join(" | ", hdr.Where(b => b.Data != null && !b.Data.StartsWith("(") && b.Data.Contains("#")).Select(b => b.Data)));

            var fillCut = hdr.FirstOrDefault(b => b.Data != null && b.Data.StartsWith("G1X1"));
            Check(fillCut != null && fillCut.Data.Contains("S850") && fillCut.Data.Contains("F4000"),
                  "the shading constants resolve to what the header declared",
                  fillCut == null ? "(no cut move)" : fillCut.Data);

            // ---- the placement constants -------------------------------------------------------------
            // The job reaches its placement by rapiding to #<x_org>/#<y_org> and zeroing there, and comes
            // home by re-labelling that point with the same two. Substitution is TEXTUAL, so the case that
            // matters is a NEGATIVE value: "Y#<y_org>" has to become "Y-9.525" and not "Y--9.525" or a
            // refusal. That is also why the offsets are not carried negated on a single G92 - "Y-#<y_org>"
            // would be exactly the broken form.
            Console.WriteLine();

            var place = NgcConstants.SvgLaser.PlacementDeclarations(16.5, -9.525);
            foreach (var line in place)
                Console.WriteLine("      " + line);

            Check(place.All(l => balanced(l)),
                  "the placement block is balanced comments and assignments",
                  string.Join(" | ", place.Where(l => !balanced(l))));
            Check(place.Any(l => l.Contains("#<x_org> = 16.5")) && place.Any(l => l.Contains("#<y_org> = -9.525")),
                  "the constants hold the DIALOG's values, unchanged and un-negated",
                  string.Join(" | ", place));

            var placed = Load(string.Join("\n", place) +
                              "\nG92 X0 Y0\n" +
                              "G0 X#<x_org> Y#<y_org> S0 F3000\n" +
                              "G92 X0 Y0\n" +
                              "G1 X10 Y-10 S200 F1200\n" +
                              "G0 X0 Y0 F3000\n" +
                              "G92 X#<x_org> Y#<y_org>\n" +
                              "G0 X0 Y0 F3000\n" +
                              "G92.1\nM30\n", out err, out ok);
            Dump("placement", placed);

            Check(ok, "the placed program loads", err ?? "");

            var hop = placed.FirstOrDefault(b => b.Data != null && b.Data.StartsWith("G0X16.5"));
            Check(hop != null && hop.Data.Contains("Y-9.525"),
                  "the rapid to the placement resolves, negative Y intact",
                  hop == null ? "(no hop)" : hop.Data);

            var relabel = placed.FirstOrDefault(b => b.Data != null && b.Data.StartsWith("G92X16.5"));
            Check(relabel != null && relabel.Data.Contains("Y-9.525"),
                  "the closing G92 re-label resolves to the same pair",
                  relabel == null ? "(no re-label)" : relabel.Data);

            Check(!placed.Any(b => b.Data != null && b.Data.Contains("--")),
                  "no double sign anywhere - textual substitution stayed well formed",
                  string.Join(" | ", placed.Where(b => b.Data != null && b.Data.Contains("--")).Select(b => b.Data)));

            // ---- refusals ---------------------------------------------------------------------------
            // ParseFileLines catches per line and routes the failure through the operator's load-error
            // prompt, so the exception does NOT escape. What must hold either way is that the offending
            // line never becomes part of the program.
            Console.WriteLine();

            var bad1 = Load("G0 X#<_abs_x>\nM30\n", out err, out ok);
            Dump("system parameter", bad1);
            Check(!bad1.Any(b => b.Data != null && b.Data.Contains("_abs_x")),
                  "a system parameter never reaches the program", "err=" + (err ?? "none") + " ok=" + ok);

            var bad2 = Load("#<a> = 1\nG1 X#<b>\nM30\n", out err, out ok);
            Dump("undefined reference", bad2);
            Check(!bad2.Any(b => b.Data != null && b.Data.Contains("#<b>")),
                  "an undefined reference never reaches the program", "err=" + (err ?? "none") + " ok=" + ok);

            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "ALL CHECKS PASSED" : fail + " CHECK(S) FAILED");
            return fail == 0 ? 0 : 1;
        }
    }
}
