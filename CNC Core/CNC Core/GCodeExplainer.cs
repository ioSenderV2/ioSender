/*
 * GCodeExplainer.cs - part of CNC Core
 *
 * Turns parsed g-code (the GCodeParser's semantic tokens) into plain-language explanations, so a novice
 * hovering a line in the program view sees what it does ("Cutting move to X 125.4, Y 0; feed 800 mm/min")
 * instead of raw g-code. Works off the tokens the loader already produced (keyed to a block by LineNumber),
 * so it inherits the parser's modal state - a bare "X10 Y0" continuation line still reads correctly as a
 * rapid or a cut.
 *
 * English only for now, but every phrase is produced here (not scattered), so it can be localized later
 * (LibStrings) without touching the call sites.
 */

using System.Collections.Generic;
using System.Linq;
using System.Text;
using CNC.GCode;

namespace CNC.Core
{
    public static class GCodeExplainer
    {
        // A selection breakdown is user-made and normally small, but guard against Ctrl+A on a 300k-line
        // program: explain at most this many lines, then note the remainder.
        private const int MaxSelectionLines = 200;

        /// <summary>
        /// Explain one block. <paramref name="lineTokens"/> are the parser tokens for THIS block (same
        /// LineNumber); pass null/empty to fall back to describing the raw text.
        /// </summary>
        public static string ExplainBlock(GCodeBlock block, IReadOnlyList<GCodeToken> lineTokens)
        {
            if (block == null)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append(block.Data.Trim());
            sb.Append('\n');
            sb.Append(new string('─', System.Math.Min(40, System.Math.Max(8, block.Data.Trim().Length))));

            foreach (var line in DescribeBlock(block, lineTokens))
            {
                sb.Append('\n');
                sb.Append("• ");
                sb.Append(line);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Explain a multi-row selection: one compact line per selected block (raw g-code + a one-line
        /// summary). <paramref name="tokensByLine"/> maps a block's LineNumber to its tokens.
        /// </summary>
        public static string ExplainSelection(IReadOnlyList<GCodeBlock> blocks, IDictionary<uint, List<GCodeToken>> tokensByLine)
        {
            if (blocks == null || blocks.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append(string.Format("Selected {0} lines:", blocks.Count));

            int shown = 0;
            foreach (var block in blocks)
            {
                if (shown++ >= MaxSelectionLines)
                {
                    sb.Append('\n');
                    sb.Append(string.Format("… and {0} more lines", blocks.Count - MaxSelectionLines));
                    break;
                }

                List<GCodeToken> lt = null;
                if (tokensByLine != null)
                    tokensByLine.TryGetValue(block.LineNum, out lt);

                var parts = DescribeBlock(block, lt);
                string summary = parts.Count == 0 ? "(no motion)" : string.Join("; ", parts);

                sb.Append('\n');
                sb.Append(block.Data.Trim());
                sb.Append("   →   ");
                sb.Append(summary);
            }

            return sb.ToString();
        }

        // --- the core: one block -> zero or more plain-language phrases -----------------------------------

        private static List<string> DescribeBlock(GCodeBlock block, IReadOnlyList<GCodeToken> lineTokens)
        {
            var lines = new List<string>();

            // Comments / no tokens: surface the text, or flag a controller-side expression/macro line.
            if (lineTokens == null || lineTokens.Count == 0)
            {
                // Strip a leading N line number so it can't hide the real first token from the classifier below
                // (e.g. "N160 #<c1x> = ..." must be seen as an assignment, not lexed as g-code).
                string raw = (block.Data ?? string.Empty).Trim();
                var nWord = System.Text.RegularExpressions.Regex.Match(raw, @"^[Nn]\d+\s*");
                if (nWord.Success)
                    raw = raw.Substring(nWord.Length);
                if (block.IsComment || raw.StartsWith("(") || raw.StartsWith(";"))
                    lines.Add("Comment: " + raw.Trim('(', ')', ';', ' '));
                else if (IsOWord(raw))                       // O<name> CALL / IF / WHILE ... (real flow control)
                    lines.Add(ExplainOWord(raw));
                else if (IsAssignment(raw))                  // #<name> = ...  /  #nnn = ...
                    lines.Add(DescribeAssignment(raw));
                else
                {
                    // Anything else is g-code - lex it, even with #parameter / [expression] operands (a #
                    // in a motion word is NOT flow control): e.g. G53 G0 X[#5181] Y[#5182].
                    lines.AddRange(DescribeRaw(raw));
                    if (lines.Count == 0 && raw.Length > 0)
                        lines.Add(raw);
                }
                return lines;
            }

            foreach (var t in lineTokens)
            {
                string phrase = ExplainToken(t);
                if (!string.IsNullOrEmpty(phrase))
                    lines.Add(phrase);
            }

            return lines;
        }

        private static string ExplainToken(GCodeToken t)
        {
            switch (t)
            {
                case GCComment c:
                    return "Comment: " + (c.Comment ?? string.Empty).Trim('(', ')', ';', ' ');

                case GCLinearMotion lm:
                    return (lm.Command == Commands.G0 ? "Rapid (fast, non-cutting) move" : "Cutting move (straight line)") + AxisTarget(lm);

                case GCAbsLinearMotion alm:
                    return (alm.Motion == Commands.G0 ? "Rapid move in MACHINE coordinates (G53)" : "Cutting move in machine coordinates (G53)") + AxisTarget(alm);

                case GCArc arc:
                    return (arc.Command == Commands.G2 ? "Clockwise arc" : "Counter-clockwise arc") + AxisTarget(arc);

                case GCCannedDrill drill:
                    return (ExplainByCommand(drill.Command) ?? "Canned cycle") + AxisTarget(drill);

                case GCCoordinateSystem cs:
                    return (ExplainByCommand(cs.Command) ?? cs.ToString()) + AxisTarget(cs);

                case GCToolSelect ts:
                    return string.Format("Select tool {0} (loaded at the next tool change)", ts.Tool);

                case GCSpindleRPM rpm:
                    return string.Format("Set spindle speed to {0} rpm", Num(rpm.SpindleRPM));

                case GCSpindleState ss:
                    return ss.SpindleState == SpindleState.Off ? "Stop the spindle (M5)"
                         : ss.SpindleState == SpindleState.CW ? "Start the spindle clockwise (M3)"
                         : "Start the spindle counter-clockwise (M4)";

                case GCCoolantState cool:
                    return cool.CoolantState == CoolantState.Off ? "Coolant off (M9)"
                         : cool.CoolantState.HasFlag(CoolantState.Flood) ? "Flood coolant on (M8)"
                         : "Mist coolant on (M7)";

                case GCFeedrate f:
                    return string.Format("Set feed rate to {0} mm/min", Num(f.Feedrate));

                case GCDwell d:
                    return string.Format("Pause (dwell) for {0} seconds", Num(d.Delay));

                case GCPlane p:
                    return "Work in the " + (p.Plane == Plane.XY ? "XY" : p.Plane == Plane.XZ ? "XZ" : "YZ") + " plane";

                case GCUnits u:
                    return u.Metric ? "Units: millimetres (G21)" : "Units: inches (G20)";

                case GCDistanceMode dm:
                    return dm.DistanceMode == DistanceMode.Absolute
                        ? "Absolute positioning (G90) — coordinates are measured from the work origin"
                        : "Incremental positioning (G91) — coordinates are measured from the current point";

                case GCFeedRateMode frm:
                    return frm.FeedRateMode == FeedRateMode.UnitsPerMin ? "Feed rate is in units per minute (G94)"
                         : frm.FeedRateMode == FeedRateMode.InverseTime ? "Feed rate is inverse-time (G93)"
                         : "Feed rate is per spindle revolution (G95)";
            }

            // Codes without a dedicated token type: explain by command, else show the raw token.
            string byCmd = ExplainByCommand(t.Command);
            return byCmd ?? t.ToString();
        }

        // The full G/M-code dictionary. Used by the token path (modal/valueless codes and as a fallback for
        // axis-bearing tokens) AND by the raw-line lexer, so a bare "G54" reads the same however it arrives.
        // Returns null for a code with no entry (caller shows the raw code).
        private static string ExplainByCommand(Commands cmd)
        {
            switch (cmd)
            {
                // Motion
                case Commands.G0: return "Rapid (fast, non-cutting) move (G0)";
                case Commands.G1: return "Cutting move — straight line (G1)";
                case Commands.G2: return "Clockwise arc (G2)";
                case Commands.G3: return "Counter-clockwise arc (G3)";
                case Commands.G4: return "Pause (dwell) (G4)";
                case Commands.G5: case Commands.G5_1: return "Spline move (G5)";
                case Commands.G33: case Commands.G33_1: return "Spindle-synchronised (threading) move (G33)";
                case Commands.G38_2: return "Probe toward the workpiece, stop on contact — error if none (G38.2)";
                case Commands.G38_3: return "Probe toward the workpiece, stop on contact (G38.3)";
                case Commands.G38_4: return "Probe away, stop when contact is lost — error if not (G38.4)";
                case Commands.G38_5: return "Probe away, stop when contact is lost (G38.5)";

                // Drilling / canned cycles
                case Commands.G73: return "High-speed peck drilling cycle (G73)";
                case Commands.G76: return "Threading cycle (G76)";
                case Commands.G81: return "Drilling cycle (G81)";
                case Commands.G82: return "Drilling cycle with dwell (G82)";
                case Commands.G83: return "Peck drilling cycle (G83)";
                case Commands.G84: return "Tapping cycle (G84)";
                case Commands.G85: return "Boring cycle (G85)";
                case Commands.G86: return "Boring cycle, spindle stops at bottom (G86)";
                case Commands.G89: return "Boring cycle with dwell (G89)";
                case Commands.G80: return "Cancel the canned (drilling) cycle (G80)";
                case Commands.G98: return "Canned-cycle retract to the initial Z height (G98)";
                case Commands.G99: return "Canned-cycle retract to the R plane (G99)";

                // Planes / units / distance / feed mode
                case Commands.G17: return "Work in the XY plane (G17)";
                case Commands.G18: return "Work in the XZ plane (G18)";
                case Commands.G19: return "Work in the YZ plane (G19)";
                case Commands.G20: return "Set units to inches (G20)";
                case Commands.G21: return "Set units to millimetres (G21)";
                case Commands.G90: return "Absolute positioning — coordinates from the work origin (G90)";
                case Commands.G91: return "Incremental positioning — coordinates from the current point (G91)";
                case Commands.G90_1: return "Arc centres (I/J/K) are absolute (G90.1)";
                case Commands.G91_1: return "Arc centres (I/J/K) are incremental (G91.1)";
                case Commands.G93: return "Feed rate is inverse-time (G93)";
                case Commands.G94: return "Feed rate is units per minute (G94)";
                case Commands.G95: return "Feed rate is units per spindle revolution (G95)";
                case Commands.G96: return "Constant surface speed mode (G96)";
                case Commands.G97: return "Constant spindle-speed (RPM) mode (G97)";
                case Commands.G7: return "Lathe diameter mode (G7)";
                case Commands.G8: return "Lathe radius mode (G8)";

                // Coordinate systems / offsets
                case Commands.G53: return "Move in MACHINE coordinates (G53)";
                case Commands.G54: return "Select work coordinate system 1 — G54";
                case Commands.G55: return "Select work coordinate system 2 — G55";
                case Commands.G56: return "Select work coordinate system 3 — G56";
                case Commands.G57: return "Select work coordinate system 4 — G57";
                case Commands.G58: return "Select work coordinate system 5 — G58";
                case Commands.G59: return "Select work coordinate system 6 — G59";
                case Commands.G59_1: return "Select work coordinate system 7 — G59.1";
                case Commands.G59_2: return "Select work coordinate system 8 — G59.2";
                case Commands.G59_3: return "Select work coordinate system 9 — G59.3";
                case Commands.G10: return "Set a coordinate-system / offset value (G10)";
                case Commands.G92: return "Set a temporary coordinate offset (G92)";
                case Commands.G92_1: case Commands.G92_2: case Commands.G92_3: return "Clear/restore the G92 offset";

                // Tool / compensation / rotation / scaling / path
                case Commands.G28: return "Go to the primary reference (home) position (G28)";
                case Commands.G30: return "Go to the secondary reference position (G30)";
                case Commands.G28_1: return "Store the current position as the G28 reference";
                case Commands.G30_1: return "Store the current position as the G30 reference";
                case Commands.G40: return "Cancel cutter radius compensation (G40)";
                case Commands.G43: case Commands.G43_1: case Commands.G43_2: return "Apply tool-length offset (G43)";
                case Commands.G49: return "Cancel tool-length offset (G49)";
                case Commands.G50: return "Cancel scaling (G50)";
                case Commands.G51: return "Scale coordinates (G51)";
                case Commands.G68: return "Rotate the coordinate system (G68)";
                case Commands.G69: return "Cancel coordinate-system rotation (G69)";
                case Commands.G61: case Commands.G61_1: return "Exact-path (exact-stop) motion mode (G61)";
                case Commands.G64: return "Path-blending motion mode (G64)";
                case Commands.G65: return "Macro / subroutine call (G65)";
                case Commands.G66: return "Modal macro call (G66)";
                case Commands.G67: return "Cancel modal macro call (G67)";

                // M-codes
                case Commands.M0: return "Program pause — press Run to continue (M0)";
                case Commands.M1: return "Optional stop (M1)";
                case Commands.M2: return "End of program (M2)";
                case Commands.M30: return "End of program and rewind (M30)";
                case Commands.M3: return "Start the spindle clockwise (M3)";
                case Commands.M4: return "Start the spindle counter-clockwise (M4)";
                case Commands.M5: return "Stop the spindle (M5)";
                case Commands.M6: return "Tool change (M6)";
                case Commands.M7: return "Mist coolant on (M7)";
                case Commands.M8: return "Flood coolant on (M8)";
                case Commands.M9: return "Coolant off (M9)";
                case Commands.M48: return "Enable feed/speed override (M48)";
                case Commands.M49: return "Disable feed/speed override (M49)";
                case Commands.M56: return "Parking-motion override control (M56)";
                case Commands.M61: return "Set the current tool number (M61)";
                case Commands.M62: case Commands.M63: case Commands.M64: case Commands.M65: return "Digital output control (M62–M65)";
                case Commands.M66: return "Wait on an input (M66)";
                case Commands.M67: case Commands.M68: return "Analog output control (M67/M68)";
                default: return null;
            }
        }

        // Explain a line with no parser tokens (a bare modal code, or a view whose program was not parsed here -
        // a wizard's own program) by lexing the raw text. Handles #parameter / [expression] operands so a machine
        // move like G53 G0 X[#5181] reads correctly, and annotates the well-known #5xxx parameters.
        private static List<string> DescribeRaw(string data)
        {
            var lines = new List<string>();
            var axes = new List<string>();
            bool axesNumeric = true;

            int c = data.IndexOfAny(new[] { '(', ';' });   // drop any trailing comment
            string s = c >= 0 ? data.Substring(0, c) : data;

            var codes = new List<Commands>();
            double? L = null, P = null, R = null, Q = null;
            string feed = null, speed = null, tool = null;

            foreach (var w in Tokenize(s))
            {
                char letter = w.Key;
                string val = w.Value;
                switch (letter)
                {
                    case 'G':
                    case 'M':
                        var cmd = ParseCode(letter, val);
                        if (cmd.HasValue) codes.Add(cmd.Value);
                        else if (val.Length > 0) lines.Add(letter + val);
                        break;
                    case 'X': case 'Y': case 'Z': case 'A': case 'B': case 'C':
                        if (val.Length > 0)
                        {
                            string operand = Unwrap(val);
                            if (!IsNumber(operand)) axesNumeric = false;
                            string note = ParamNote(operand);
                            axes.Add(letter + " " + operand + (note != null ? " (" + note + ")" : ""));
                        }
                        break;
                    case 'L': L = ParseNum(val); break;
                    case 'P': P = ParseNum(val); break;
                    case 'R': R = ParseNum(val); break;
                    case 'Q': Q = ParseNum(val); break;
                    case 'F': if (val.Length > 0) feed = val; break;
                    case 'S': if (val.Length > 0) speed = val; break;
                    case 'T': if (val.Length > 0) tool = val; break;
                }
            }

            foreach (var cmd in codes)
            {
                if (cmd == Commands.G10) lines.Add(ExplainG10(L, P, R));
                else if (cmd == Commands.G65) lines.Add(ExplainG65(P, Q));
                else lines.Add(ExplainByCommand(cmd) ?? cmd.ToString().Replace('_', '.'));
            }

            if (feed != null) lines.Add("Set feed rate to " + feed + " mm/min");
            if (speed != null) lines.Add("Set spindle speed to " + speed + " rpm");
            if (tool != null) lines.Add("Select tool " + tool);
            if (axes.Count > 0) lines.Add("Move to " + string.Join(", ", axes) + (axesNumeric ? " mm" : ""));

            return lines;
        }

        // Split a g-code line into (address letter, value) words. The value may be a number, a [bracketed
        // expression] (brackets balanced, spaces allowed inside) or a #parameter / #<named> reference - so
        // expression programs (e.g. G53 G0 X[#5181]) tokenize correctly instead of dropping the operand.
        private static List<KeyValuePair<char, string>> Tokenize(string s)
        {
            var words = new List<KeyValuePair<char, string>>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                if (!char.IsLetter(s[i])) { i++; continue; }
                char letter = char.ToUpperInvariant(s[i++]);
                while (i < n && s[i] == ' ') i++;
                int start = i;
                if (i < n && s[i] == '[')                       // balanced [ ... ] expression
                {
                    int depth = 0;
                    do
                    {
                        if (s[i] == '[') depth++;
                        else if (s[i] == ']') depth--;
                        i++;
                    } while (i < n && depth > 0);
                }
                else if (i < n && s[i] == '#')                  // #<named> or #nnn parameter
                {
                    i++;
                    if (i < n && s[i] == '<') { while (i < n && s[i] != '>') i++; if (i < n) i++; }
                    else while (i < n && char.IsDigit(s[i])) i++;
                }
                else                                            // plain number
                {
                    if (i < n && (s[i] == '-' || s[i] == '+')) i++;
                    while (i < n && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                }
                words.Add(new KeyValuePair<char, string>(letter, s.Substring(start, i - start).Trim()));
            }
            return words;
        }

        private static string Unwrap(string v)
        {
            v = v.Trim();
            if (v.Length >= 2 && v[0] == '[' && v[v.Length - 1] == ']')
                v = v.Substring(1, v.Length - 2).Trim();
            return v;
        }

        private static bool IsNumber(string v)
        {
            return double.TryParse(v, System.Globalization.NumberStyles.Any,
                                   System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        // Annotate a lone numbered parameter (grbl / LinuxCNC #5xxx) with what it stores; null for anything else
        // (named #<...> params and expressions have no fixed meaning to name here).
        private static string ParamNote(string expr)
        {
            var m = System.Text.RegularExpressions.Regex.Match(expr.Trim(), @"^#(\d+)$");
            if (!m.Success)
                return null;
            switch (int.Parse(m.Groups[1].Value))
            {
                case 5161: return "G28 home X"; case 5162: return "G28 home Y"; case 5163: return "G28 home Z";
                case 5181: return "G30 park X"; case 5182: return "G30 park Y"; case 5183: return "G30 park Z";
                case 5220: return "active coordinate system #";
                case 5400: return "current tool #";
                default:
                    int p = int.Parse(m.Groups[1].Value);
                    if (p >= 5061 && p <= 5069) return "last probe result";
                    if (p >= 5221 && p <= 5230) return "G54 offset";
                    if (p >= 5420 && p <= 5423) return "current tool position";
                    return null;
            }
        }

        private static bool IsOWord(string raw)
        {
            return raw.Length > 1 && (raw[0] == 'o' || raw[0] == 'O') && (raw[1] == '<' || char.IsDigit(raw[1]));
        }

        // Explain an O-word flow-control line: O<name>/O<n> + a keyword (CALL/SUB/IF/WHILE/...) evaluated by the
        // controller. For CALL, name the subroutine and list its arguments.
        private static string ExplainOWord(string raw)
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^[Oo]\s*(<[^>]*>|\d+)\s*([A-Za-z]+)?(.*)$");
            if (!m.Success)
                return "Controller flow control (evaluated by the controller): " + raw;

            string label = m.Groups[1].Value.Trim('<', '>', ' ');
            string kw = m.Groups[2].Value.ToUpperInvariant();
            string rest = m.Groups[3].Value.Trim();

            switch (kw)
            {
                case "CALL":
                    var args = BracketArgs(rest);
                    // A NAMED subroutine is resolved to a file /<name>.macro on the controller's SD-card / littleFS
                    // root; a numbered O-word (O100 CALL) is an inline subroutine defined earlier in the program.
                    bool named = label.Any(ch => !char.IsDigit(ch));
                    return "Call subroutine \"" + label + "\""
                         + (named ? " — the controller runs the file /" + label + ".macro from its SD-card / littleFS root"
                                  : " (an inline O-word subroutine)")
                         + (args.Count == 0 ? "" : ", passing " + string.Join(", ", args));
                case "SUB": return "Start of subroutine \"" + label + "\"";
                case "ENDSUB": return "End of subroutine \"" + label + "\"";
                case "RETURN": return "Return from subroutine \"" + label + "\"";
                case "IF": return "If " + rest + " — run the block that follows (conditional " + label + ")";
                case "ELSEIF": return "Else if " + rest + " (conditional " + label + ")";
                case "ELSE": return "Otherwise — the else branch (conditional " + label + ")";
                case "ENDIF": return "End of the conditional block (" + label + ")";
                case "WHILE": return "While " + rest + " — repeat the loop (" + label + ")";
                case "ENDWHILE": return "End of the while-loop (" + label + ")";
                case "DO": return "Start of a do-while loop (" + label + "); the condition is tested at WHILE";
                case "REPEAT": return "Repeat the loop (" + label + ")";
                case "ENDREPEAT": return "End of the repeat-loop (" + label + ")";
                case "BREAK": return "Break out of the loop (" + label + ")";
                case "CONTINUE": return "Continue with the next iteration of the loop (" + label + ")";
                case "END": return "End of the block (" + label + ")";
                default:
                    return (kw.Length > 0 ? kw + " — " : "") + "controller flow control (" + label + ")";
            }
        }

        // Top-level [ ... ] argument groups from an O-word CALL's operand text (brackets balanced).
        private static List<string> BracketArgs(string s)
        {
            var args = new List<string>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                while (i < n && s[i] != '[') i++;
                if (i >= n) break;
                int depth = 0, start = i;
                do
                {
                    if (s[i] == '[') depth++;
                    else if (s[i] == ']') depth--;
                    i++;
                } while (i < n && depth > 0);
                args.Add(Unwrap(s.Substring(start, i - start)));
            }
            return args;
        }

        // "#<name> = ..." / "#nnn = ..." - a bare parameter assignment (not a motion word carrying a # operand).
        private static bool IsAssignment(string raw)
        {
            return raw.StartsWith("#") && raw.IndexOf('=') > 0;
        }

        private static string DescribeAssignment(string raw)
        {
            int eq = raw.IndexOf('=');
            string lhs = raw.Substring(0, eq).Trim();
            string rhs = raw.Substring(eq + 1).Trim();
            string lnote = ParamNote(lhs), rnote = ParamNote(Unwrap(rhs));
            return "Set " + lhs + (lnote != null ? " (" + lnote + ")" : "")
                 + " to " + rhs + (rnote != null ? " (" + rnote + ")" : "");
        }

        // G10 sets stored data: L2/L20 P<n> a work coordinate system (origin + optional R rotation); L1/L10/L11
        // a tool-table entry. P1..P6 map to G54..G59.
        private static string ExplainG10(double? L, double? P, double? R)
        {
            int l = L.HasValue ? (int)L.Value : -1;
            string wcs = null;
            if (P.HasValue)
            {
                int p = (int)P.Value;
                wcs = p >= 1 && p <= 6 ? "G5" + (3 + p) : "P" + p;   // P1->G54 ... P6->G59
            }

            if (l == 2 || l == 20)
            {
                string txt = "Set the " + (wcs != null ? wcs + " " : "") + "work-coordinate-system origin"
                           + (l == 20 ? " to the current position (G10 L20)" : " (G10 L2)");
                if (R.HasValue)
                    txt += R.Value == 0d ? "; clear its rotation (R0)" : "; rotate it " + Num(R.Value) + "° (R)";
                return txt;
            }
            if (l == 1 || l == 10 || l == 11)
                return "Set tool-table data (G10 L" + l + ")";
            return "Set a coordinate-system / offset value (G10)";
        }

        // G65 P<n> calls a stored macro/subroutine. P5 is grblHAL's built-in "select probe input": Q0 = the main
        // probe (3D probe / touch plate), Q1 = tool setter, Q2 = a second probe.
        private static string ExplainG65(double? P, double? Q)
        {
            int p = P.HasValue ? (int)P.Value : -1;
            if (p == 5)
            {
                int q = Q.HasValue ? (int)Q.Value : 0;
                string which = q == 0 ? "main probe (3D probe / touch plate)"
                             : q == 1 ? "tool setter"
                             : q == 2 ? "second probe"
                             : "probe " + q;
                return "Select the " + which + " probe input (G65 P5 Q" + q + ")";
            }
            return p >= 0 ? "Call macro / subroutine G65 P" + p : "Macro / subroutine call (G65)";
        }

        private static double? ParseNum(string val)
        {
            return double.TryParse(val, System.Globalization.NumberStyles.Any,
                                   System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        }

        // Map a raw code word to the Commands enum: "G54"->G54, "G38.2"->G38_2, "M06"->M6. Null if unrecognised.
        private static Commands? ParseCode(char letter, string val)
        {
            if (val.Length == 0)
                return null;

            string num;
            if (val.Contains("."))
                num = val.Replace('.', '_');
            else if (int.TryParse(val, out int iv))
                num = iv.ToString();          // strip leading zeros: "06" -> "6", "00" -> "0"
            else
                num = val;

            return System.Enum.TryParse(char.ToUpperInvariant(letter) + num, out Commands cmd) ? cmd : (Commands?)null;
        }

        // " to X 125.4, Y 0 mm" for the axis words present on an axis-bearing token; "" if none.
        private static string AxisTarget(GCodeToken t)
        {
            double[] values;
            AxisFlags flags;

            if (t is GCAxisCommand9 a9) { values = a9.Values; flags = a9.AxisFlags; }
            else if (t is GCAxisCommand3 a3) { values = a3.Values; flags = a3.AxisFlags; }
            else return string.Empty;

            var parts = new List<string>();
            foreach (int i in flags.ToIndices())
                parts.Add(GrblInfo.AxisIndexToLetter(i) + " " + Num(values[i]));

            return parts.Count == 0 ? string.Empty : " to " + string.Join(", ", parts) + " mm";
        }

        private static string Num(double v)
        {
            // Trim trailing zeros for readability (125.400 -> 125.4, 0.000 -> 0).
            return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
