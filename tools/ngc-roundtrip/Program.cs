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
