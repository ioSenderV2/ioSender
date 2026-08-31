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
        /// Resolve every <c>#&lt;name&gt;</c> reference to the literal it was assigned.
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

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var output = new List<string>(lines.Count);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i] ?? string.Empty;

                // Untouched unless it actually mentions a parameter. Comments included: a '#' inside a
                // comment is not a parameter, and rewriting one would change the file for no reason.
                if (line.IndexOf('#') < 0)
                {
                    output.Add(line);
                    continue;
                }

                if (IsComment(line))
                {
                    output.Add(line);
                    continue;
                }

                var assign = Assignment.Match(line);
                if (assign.Success)
                {
                    string name = assign.Groups["name"].Value;
                    string value = assign.Groups["value"].Value;

                    // A later assignment overrides an earlier one, applied in order, so a reference always
                    // takes the value in force ABOVE it. That is the only reading that matches how the
                    // controller would evaluate the same file.
                    values[name] = value;

                    // Kept as a comment, not deleted: resolved line N must stay raw line N (see header),
                    // and the resolved listing should still show what the value was.
                    output.Add("(" + line.Trim() + ")");
                    continue;
                }

                // Looks like an assignment but did not parse as one - i.e. the right-hand side is not a
                // plain number. Say THAT, rather than letting it fall through and report the name as
                // "undefined" further down: the operator would go looking for a missing declaration
                // instead of at the expression they actually wrote.
                var attempted = AssignmentAttempt.Match(line);
                if (attempted.Success)
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "line {0}: \"{1}\" - only a plain number can be assigned here; anything to evaluate needs a controller with EXPR support",
                        i + 1, line.Trim());
                    return false;
                }

                // Not an assignment. Every '#' left in it must be a reference to something already
                // assigned - anything else and we stop.
                string failure = null;
                string substituted = Reference.Replace(line, m =>
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
                                i + 1, name)
                            : string.Format(CultureInfo.InvariantCulture,
                                "line {0}: #<{1}> is used before it is given a value", i + 1, name);
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
                        i + 1, line.Trim());
                    return false;
                }

                output.Add(substituted);
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

            /// <summary>The assignment block a generated program opens with.</summary>
            public static List<string> Declarations(double linePower, double fillPower, double lineFeed, double fillFeed, bool fill)
            {
                var d = new List<string>
                {
                    "(Exposure lives in these four values - retune the job by editing them.)",
                    "(A controller reporting EXPR evaluates them itself; ioSender and the",
                    "(EngravingBox appliance substitute them on load when it does not.)",
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
