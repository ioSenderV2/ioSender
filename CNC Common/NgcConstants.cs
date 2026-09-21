/*
 * NgcConstants.cs - part of CNC.Common
 *
 * Resolve CONSTANT named parameters in a g-code program, for controllers that cannot evaluate them.
 *
 * Moved here from CNC Core on 2026-09-05, keeping the CNC.Core namespace so no call site changed -
 * the same discipline the rest of CNC.Common follows. It had no dependency on Core in the first
 * place (only System usings, no controller or comms types). The move exists so CNC.Svg can emit the
 * SvgLaser declarations below WITHOUT referencing CNC Core: that assembly is the machine-comms layer,
 * and dragging it onto an appliance whose only job is turning artwork into a file would defeat the
 * point of CNC.Svg being portable. One authority for these parameter names, reachable from both.
 *
 * WHY THIS EXISTS
 * ---------------
 * A generated laser program is far easier to retune when its four exposure numbers sit at the top as
 *
 *     #<s_line> = 200
 *     #<f_line> = 1200
 *
 * than when the same values are repeated on nine hundred cut moves. But named parameters are an
 * NGC/grblHAL feature: stock Grbl 1.1 has no parameter support at all, and ioSender only forwards '#'
 * lines verbatim when the controller reports EXPR. So the file stays canonical with its variables and
 * each READER resolves them - this class is ioSender's reader.
 *
 * WHAT IT DELIBERATELY WILL NOT DO
 * --------------------------------
 * Constant assignments, plain references, and - since 2026-09-06 - bracket arithmetic over those
 * constants: [#<inset> + #<side>], with + - * / and nesting, folded to a number. Nothing else. No
 * functions (SIN, ATAN, ...), no ** MOD AND OR XOR, no numbered parameters (#100), no read-only
 * system parameters (#<_abs_x>), no O-words. Folding exists so a parametric program - a test square
 * whose side is a (PROMPT) field, say - can be RUN on a controller with no expression support, and it
 * folds only what is provably constant: every name inside the brackets already has a value here.
 *
 * Values can also be SEEDED (Resolver.Seed) before the program is read - that is how (PROMPT) fields
 * collected at load reach the substitution - and an assignment of a seeded name in the program does
 * NOT override the seed: the operator's answer wins over the file's default (RewriteAssignment).
 *
 * It REFUSES rather than guesses, and that is the whole design. A resolver that silently passed
 * through what it did not understand would be the same failure as the parser gap that once dropped a
 * G59.3 from a program because it could not model it - the machine then rapid'd 128 mm into a touch
 * plate. Anything with a '#' that this class cannot prove is a constant makes TryResolve return false,
 * and the caller must then leave the program alone rather than send a half-substituted one.
 *
 * A system parameter such as #<_abs_x> could not be resolved here even in principle: its value is
 * whatever the machine reads at PARSE time, which during a streamed program is not where the machine
 * will be when the line executes. Refusing is the only correct answer, not a limitation to lift later.
 *
 * LINE COUNT IS PRESERVED. An assignment becomes a comment rather than disappearing, so resolved line
 * N is always raw line N. Callers keep both forms (the raw one is what gets saved back out), and a
 * shifting index between them would be its own bug.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CNC.Core
{
    /// <summary>
    /// Substitutes constant named parameters (<c>#&lt;name&gt;</c>) into a g-code program for
    /// controllers without NGC expression support. Refuses anything it cannot prove constant.
    /// </summary>
    public static class NgcConstants
    {
        // #<name> = number   or   #<name> = [expression]   - the two assignment forms accepted. Trailing
        // comment allowed. A number is a plain signed decimal: no leading '+', no exponent. A bracket
        // expression is folded by TryFold below and must reduce to a number or the line is refused.
        private static readonly Regex Assignment = new Regex(
            @"^\s*#<(?<name>[A-Za-z_][A-Za-z0-9_]*)>\s*=\s*(?<value>-?\d+(?:\.\d+)?|\[.*\])\s*(?<trail>\(.*\))?\s*$",
            RegexOptions.Compiled);

        // "#<name> =" with anything at all after it. Used ONLY to tell a malformed assignment apart from
        // a reference, so the refusal names the real problem instead of the symptom it causes later.
        private static readonly Regex AssignmentAttempt = new Regex(
            @"^\s*#<[A-Za-z_][A-Za-z0-9_]*>\s*=",
            RegexOptions.Compiled);

        // A reference anywhere in a block.
        private static readonly Regex Reference = new Regex(
            @"#<(?<name>[A-Za-z_][A-Za-z0-9_]*)>",
            RegexOptions.Compiled);

        /// <summary>True if the program contains any '#' at all - i.e. whether resolving is even relevant.</summary>
        public static bool UsesParameters(IEnumerable<string> lines)
        {
            if (lines == null)
                return false;

            foreach (var l in lines)
                if (l != null && l.IndexOf('#') >= 0)
                    return true;

            return false;
        }

        /// <summary>
        /// Line-at-a-time resolution, holding the values assigned so far.
        ///
        /// Both program sources are line-at-a-time - ParseFileLines reads a file block by block, and
        /// AddBlock is fed one block at a time by the converters - so this is the shape they need.
        /// A whole-program pass would mean buffering a 220k-line file twice for no gain.
        ///
        /// Sequential is not a compromise here, it is the correct reading: a reference takes the value
        /// in force ABOVE it, which is also how a controller with EXPR would evaluate the same file.
        /// </summary>
        public sealed class Resolver
        {
            private readonly Dictionary<string, string> values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>How many constants have been declared so far - for logging a resolved load.</summary>
            public int Count { get { return values.Count; } }

            /// <summary>Forget every declared value. Called when a new program starts.</summary>
            public void Reset()
            {
                values.Clear();
            }

            /// <summary>
            /// Give a name a value BEFORE the program is read - a (PROMPT) field's answer, collected at
            /// load. References resolve against it from line 1, and a later assignment of the same name
            /// in the program is rewritten to it rather than overriding it (see RewriteAssignment): the
            /// operator's answer wins over the file's own default. Names beginning with '_' are accepted
            /// here even though an UNDECLARED one is refused as a system parameter - a seeded value is
            /// known, which is the whole difference.
            /// </summary>
            public void Seed(string name, string value)
            {
                if (!string.IsNullOrEmpty(name) && value != null)
                    values[name.TrimStart('#', '<').TrimEnd('>')] = value.Trim();
            }

            /// <summary>The value a name currently holds, or null.</summary>
            public string ValueOf(string name)
            {
                string v;
                return name != null && values.TryGetValue(name, out v) ? v : null;
            }

            /// <summary>
            /// Resolve one block.
            /// </summary>
            /// <param name="lineNumber">1-based, for the refusal text only.</param>
            /// <param name="line">The raw block.</param>
            /// <param name="resolved">
            /// The block with references substituted. An assignment becomes a comment, so the caller
            /// always gets exactly one line out for one line in and raw line N stays resolved line N.
            /// </param>
            /// <param name="reason">Why it was refused. Null on success.</param>
            /// <returns>False if this block carries '#' syntax that cannot be proven constant.</returns>
            public bool TryLine(int lineNumber, string line, out string resolved, out string reason)
            {
                resolved = line ?? string.Empty;
                reason = null;

                // Untouched unless it actually mentions a parameter. Comments included: a '#' inside a
                // comment is not a parameter, and rewriting one would change the file for no reason.
                if (resolved.IndexOf('#') < 0 || IsComment(resolved))
                    return true;

                var assign = Assignment.Match(resolved);
                if (assign.Success)
                {
                    string value = assign.Groups["value"].Value;
                    if (value.StartsWith("[", StringComparison.Ordinal))
                    {
                        // A bracket right-hand side: substitute what it references, then fold. Refused
                        // the same way a reference line is when anything inside is not a known constant.
                        string inner, why;
                        if (!SubstituteReferences(value, lineNumber, out inner, out why) ||
                            !TryFoldBrackets(inner, lineNumber, out value, out why))
                        {
                            reason = why;
                            return false;
                        }
                    }

                    // A later assignment overrides an earlier one, applied in order.
                    values[assign.Groups["name"].Value] = value;

                    // Kept as a comment, not deleted: resolved line N must stay raw line N, and the
                    // resolved listing should still show what the value was.
                    resolved = "(" + resolved.Trim() + ")";
                    return true;
                }

                // Looks like an assignment but did not parse as one - i.e. the right-hand side is neither
                // a plain number nor a [bracket]. Say THAT, rather than letting it fall through and report
                // the name as "undefined" further down: the operator would go looking for a missing
                // declaration instead of at the expression they actually wrote.
                if (AssignmentAttempt.IsMatch(resolved))
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "line {0}: \"{1}\" - only a plain number or a [bracket expression over constants] can be assigned here; anything else needs a controller with EXPR support",
                        lineNumber, resolved.Trim());
                    return false;
                }

                string raw = resolved;
                string substituted;
                if (!SubstituteReferences(raw, lineNumber, out substituted, out reason))
                    return false;

                // Anything still carrying a '#' is a form this class does not model - a numbered
                // parameter, a system parameter. Refuse; do not ship it half-done.
                if (substituted.IndexOf('#') >= 0)
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "line {0}: \"{1}\" uses parameter syntax this can only pass to a controller with EXPR support",
                        lineNumber, raw.Trim());
                    return false;
                }

                // With every reference now a number, any [brackets] left are pure arithmetic - or they
                // are something this cannot fold, in which case the line is refused, not guessed at.
                if (substituted.IndexOf('[') >= 0 && !TryFoldBrackets(substituted, lineNumber, out substituted, out reason))
                    return false;

                resolved = substituted;
                return true;
            }

            /// <summary>
            /// Replace every #&lt;name&gt; in <paramref name="text"/> with its value. False, with the
            /// reason, when any name has none - a system parameter or an undeclared one.
            /// </summary>
            private bool SubstituteReferences(string text, int lineNumber, out string result, out string reason)
            {
                string failure = null;

                result = Reference.Replace(text, m =>
                {
                    string name = m.Groups["name"].Value;
                    string value;
                    if (values.TryGetValue(name, out value))
                        return value;

                    if (failure == null)
                        failure = name.StartsWith("_", StringComparison.Ordinal)
                            // A leading underscore is NGC's namespace for read-only system parameters
                            // (#<_abs_x>, #<_vmajor>, ...). Reporting these as "undefined" would be a lie
                            // - they can never be declared, and their value is whatever the machine reads
                            // at PARSE time, which during a streamed program is not where it will be when
                            // the line runs. Refusing is the only correct answer. A '_' name that WAS
                            // given a value - a seeded (PROMPT) field, or an assignment above - is in
                            // 'values' and never reaches this branch.
                            ? string.Format(CultureInfo.InvariantCulture,
                                "line {0}: #<{1}> is a system parameter - its value depends on machine state and only a controller with EXPR support can read it",
                                lineNumber, name)
                            : string.Format(CultureInfo.InvariantCulture,
                                "line {0}: #<{1}> is used before it is given a value", lineNumber, name);
                    return m.Value;
                });

                reason = failure;
                return failure == null;
            }
        }

        /// <summary>
        /// Rewrite "#&lt;name&gt; = value" when <paramref name="valueFor"/> has a value for that name - a
        /// (PROMPT) field's answer replacing the file's own default. Returns the line unchanged (the same
        /// instance) when it is not such an assignment or the name is not one being overridden, so the
        /// caller can tell by reference whether anything happened. Works for every controller: on one
        /// with EXPR the rewritten declaration is what gets sent, on one without it is what gets
        /// resolved - either way the operator's answer is what the program runs with.
        /// </summary>
        public static string RewriteAssignment(string line, Func<string, string> valueFor)
        {
            if (line == null || valueFor == null || line.IndexOf('#') < 0 || IsComment(line))
                return line;

            var m = Assignment.Match(line);
            if (!m.Success)
                return line;

            string value = valueFor(m.Groups["name"].Value);
            if (value == null)
                return line;

            string trail = m.Groups["trail"].Success ? "   " + m.Groups["trail"].Value : string.Empty;
            return "#<" + m.Groups["name"].Value + "> = " + value.Trim() + trail;
        }

        // ------------------------------------------------------------------ bracket folding

        /// <summary>
        /// Fold every [ ... ] group in <paramref name="text"/> - which must already have no '#' in it -
        /// to a number. Grammar: + - * / with the usual precedence, unary sign, nested brackets, decimal
        /// numbers. Anything else (a function name, **, MOD, a stray letter) refuses the whole line with
        /// a reason naming what it could not fold. NGC evaluates brackets innermost-first; so does this.
        /// </summary>
        private static bool TryFoldBrackets(string text, int lineNumber, out string folded, out string reason)
        {
            folded = text;
            reason = null;

            int open;
            while ((open = folded.IndexOf('[')) >= 0)
            {
                int depth = 0, close = -1;
                for (int i = open; i < folded.Length; i++)
                {
                    if (folded[i] == '[') depth++;
                    else if (folded[i] == ']' && --depth == 0) { close = i; break; }
                }
                if (close < 0)
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "line {0}: \"{1}\" has an unclosed [ bracket", lineNumber, text.Trim());
                    return false;
                }

                double value;
                string inner = folded.Substring(open + 1, close - open - 1);
                if (!TryEvaluate(inner, out value))
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "line {0}: cannot fold [{1}] - only + - * / and nested brackets over constants can be evaluated without a controller with EXPR support",
                        lineNumber, inner.Trim());
                    return false;
                }
                folded = folded.Substring(0, open) + Format(value) + folded.Substring(close + 1);
            }
            return true;
        }

        private static string Format(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return "0";
            string s = v.ToString("0.####", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        // A tiny recursive-descent evaluator: expr := term (('+'|'-') term)* ; term := factor (('*'|'/')
        // factor)* ; factor := number | ('+'|'-') factor | '[' expr ']'. Whitespace ignored. Anything the
        // grammar does not name is a failure, by design.
        private static bool TryEvaluate(string s, out double value)
        {
            int pos = 0;
            if (!ParseExpr(s, ref pos, out value))
                return false;
            SkipWs(s, ref pos);
            return pos == s.Length;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static bool ParseExpr(string s, ref int pos, out double v)
        {
            if (!ParseTerm(s, ref pos, out v)) return false;
            for (;;)
            {
                SkipWs(s, ref pos);
                if (pos >= s.Length || (s[pos] != '+' && s[pos] != '-')) return true;
                char op = s[pos++];
                double r;
                if (!ParseTerm(s, ref pos, out r)) return false;
                v = op == '+' ? v + r : v - r;
            }
        }

        private static bool ParseTerm(string s, ref int pos, out double v)
        {
            if (!ParseFactor(s, ref pos, out v)) return false;
            for (;;)
            {
                SkipWs(s, ref pos);
                if (pos >= s.Length || (s[pos] != '*' && s[pos] != '/')) return true;
                char op = s[pos++];
                double r;
                if (!ParseFactor(s, ref pos, out r)) return false;
                if (op == '/' && r == 0d) return false;     // a division by zero is not a constant
                v = op == '*' ? v * r : v / r;
            }
        }

        private static bool ParseFactor(string s, ref int pos, out double v)
        {
            v = 0d;
            SkipWs(s, ref pos);
            if (pos >= s.Length) return false;

            char c = s[pos];
            if (c == '+' || c == '-')
            {
                pos++;
                if (!ParseFactor(s, ref pos, out v)) return false;
                if (c == '-') v = -v;
                return true;
            }
            if (c == '[')
            {
                pos++;
                if (!ParseExpr(s, ref pos, out v)) return false;
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != ']') return false;
                pos++;
                return true;
            }

            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
            return pos > start && double.TryParse(s.Substring(start, pos - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>
        /// Resolve a whole program at once. A thin loop over <see cref="Resolver"/> so there is one
        /// implementation of the rules; this form exists for callers that already hold every line.
        /// </summary>
        /// <param name="lines">The raw program, one block per entry.</param>
        /// <param name="resolved">
        /// On success, the same number of lines with references replaced and assignment lines turned
        /// into comments. Null on failure.
        /// </param>
        /// <param name="reason">On failure, which line defeated it and why - for the operator, not a log.</param>
        /// <returns>False if ANY '#' construct could not be proven a constant. Nothing partial is returned.</returns>
        public static bool TryResolve(IList<string> lines, out List<string> resolved, out string reason)
        {
            resolved = null;
            reason = null;

            if (lines == null)
            {
                reason = "no program";
                return false;
            }

            var resolver = new Resolver();
            var output = new List<string>(lines.Count);

            for (int i = 0; i < lines.Count; i++)
            {
                string one;
                if (!resolver.TryLine(i + 1, lines[i], out one, out reason))
                    return false;               // resolved stays null - nothing partial escapes

                output.Add(one);
            }

            resolved = output;
            return true;
        }

        /// <summary>A block that is nothing but a comment - '(...)' or a ';' line.</summary>
        private static bool IsComment(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith(";", StringComparison.Ordinal)
                || (t.StartsWith("(", StringComparison.Ordinal) && t.TrimEnd().EndsWith(")", StringComparison.Ordinal));
        }

        /// <summary>
        /// The four exposure parameters a generated SVG laser job declares. Named here rather than in the
        /// converter so the resolver's tests and the emitter cannot drift apart on spelling.
        /// </summary>
        public static class SvgLaser
        {
            public const string LinePower = "s_line";
            public const string FillPower = "s_fill";
            public const string LineFeed = "f_line";
            public const string FillFeed = "f_fill";
            public const string OriginX = "x_org";
            public const string OriginY = "y_org";

            /// <summary>The assignment block a generated program opens with.</summary>
            public static List<string> Declarations(double linePower, double fillPower, double lineFeed, double fillFeed, bool fill)
            {
                var d = new List<string>
                {
                    "(Exposure lives in these four values - retune the job by editing them.)",
                    // Each line closes its own parenthesis. A sentence wrapped across two blocks with only
                    // one ')' at the end leaves an unterminated comment, and that is not cosmetic: the
                    // shipped form of this bug made ParseFileLines REFUSE the whole file after the first
                    // block, so a generated job could be streamed but never re-loaded from disk.
                    // tools/ngc-roundtrip covers it - reintroduce the missing ')' and three checks fail.
                    "(A controller reporting EXPR evaluates them itself; ioSender and)",
                    "(the EngravingBox appliance substitute them on load when it does not.)",
                    Declare(LinePower, linePower),
                    Declare(LineFeed, lineFeed)
                };

                // Only declared when shading is on. An unused parameter is harmless, but one that is
                // declared and never referenced invites someone to edit it and wonder why nothing changed.
                if (fill)
                {
                    d.Add(Declare(FillPower, fillPower));
                    d.Add(Declare(FillFeed, fillFeed));
                }

                return d;
            }

            /// <summary>
            /// Where the artwork sits relative to the corner the head is parked on - the two numbers from
            /// the dialog, unchanged, so what the file says and what the operator typed are the same thing.
            ///
            /// They are DECLARED rather than folded into every coordinate below, which is what lets the
            /// placement be moved by editing two lines. The program reaches the placement by rapiding to
            /// them and zeroing there, not by naming them on a G92: "G92 X16.5" labels the point the head
            /// is standing on as 16.5, which would put the artwork on the wrong side of the park by twice
            /// the offset. A single G92 could only carry these negated, and a negated constant next to a
            /// header comment quoting the positive one is the kind of disagreement that gets a stave burnt
            /// off the edge of the stock.
            /// </summary>
            public static List<string> PlacementDeclarations(double originX, double originY)
            {
                return new List<string>
                {
                    "(Placement: where the artwork sits from the parked corner - edit to move the job.)",
                    Declare(OriginX, originX),
                    Declare(OriginY, originY)
                };
            }

            private static string Declare(string name, double value)
            {
                return string.Format(CultureInfo.InvariantCulture, "#<{0}> = {1}", name, Trim(value));
            }

            /// <summary>A reference, for the emitter to drop straight after an S or F word.</summary>
            public static string Ref(string name)
            {
                return "#<" + name + ">";
            }

            private static string Trim(double v)
            {
                string s = v.ToString("0.###", CultureInfo.InvariantCulture);
                return s.Length == 0 ? "0" : s;
            }
        }
    }
}
