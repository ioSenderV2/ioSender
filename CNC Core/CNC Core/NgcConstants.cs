/*
 * NgcConstants.cs - part of CNC Core
 *
 * Resolve CONSTANT named parameters in a g-code program, for controllers that cannot evaluate them.
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
 * Constant assignments and plain references. Nothing else. No arithmetic, no [expressions], no
 * numbered parameters (#100), no read-only system parameters (#<_abs_x>), no O-words.
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
        // #<name> = number   - the only assignment form accepted. Trailing comment allowed.
        // The value is a plain signed decimal: no leading '+', no exponent, nothing to evaluate.
        private static readonly Regex Assignment = new Regex(
            @"^\s*#<(?<name>[A-Za-z_][A-Za-z0-9_]*)>\s*=\s*(?<value>-?\d+(?:\.\d+)?)\s*(?<trail>\(.*\))?\s*$",
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
                    // A later assignment overrides an earlier one, applied in order.
                    values[assign.Groups["name"].Value] = assign.Groups["value"].Value;

                    // Kept as a comment, not deleted: resolved line N must stay raw line N, and the
                    // resolved listing should still show what the value was.
                    resolved = "(" + resolved.Trim() + ")";
                    return true;
                }

                // Looks like an assignment but did not parse as one - i.e. the right-hand side is not a
                // plain number. Say THAT, rather than letting it fall through and report the name as
                // "undefined" further down: the operator would go looking for a missing declaration
                // instead of at the expression they actually wrote.
                if (AssignmentAttempt.IsMatch(resolved))
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "line {0}: \"{1}\" - only a plain number can be assigned here; anything to evaluate needs a controller with EXPR support",
                        lineNumber, resolved.Trim());
                    return false;
                }

                string raw = resolved;
                string failure = null;

                string substituted = Reference.Replace(raw, m =>
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
                            // the line runs. Refusing is the only correct answer.
                            ? string.Format(CultureInfo.InvariantCulture,
                                "line {0}: #<{1}> is a system parameter - its value depends on machine state and only a controller with EXPR support can read it",
                                lineNumber, name)
                            : string.Format(CultureInfo.InvariantCulture,
                                "line {0}: #<{1}> is used before it is given a value", lineNumber, name);
                    return m.Value;
                });

                if (failure != null)
                {
                    reason = failure;
                    return false;
                }

                // Anything still carrying a '#' is a form this class does not model - a numbered
                // parameter, an expression, a system parameter. Refuse; do not ship it half-done.
                if (substituted.IndexOf('#') >= 0)
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "line {0}: \"{1}\" uses parameter syntax this can only pass to a controller with EXPR support",
                        lineNumber, raw.Trim());
                    return false;
                }

                resolved = substituted;
                return true;
            }
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
