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
                string raw = block.Data.Trim();
                if (block.IsComment || raw.StartsWith("(") || raw.StartsWith(";"))
                    lines.Add("Comment: " + raw.Trim('(', ')', ';', ' '));
                else if (raw.StartsWith("o") || raw.StartsWith("O") || raw.Contains("#"))
                    lines.Add("Controller flow-control / expression (evaluated by the controller)");
                else
                {
                    lines.AddRange(DescribeRaw(raw));   // lex the raw codes (e.g. a bare "G54")
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

                // M-codes
                case Commands.M0: return "Program pause — press Cycle Start to continue (M0)";
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

        private static readonly System.Text.RegularExpressions.Regex Word =
            new System.Text.RegularExpressions.Regex(@"([A-Za-z])\s*([-+]?[0-9]*\.?[0-9]+)?", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Fallback for a line with no parser tokens (a bare modal code like G54, or a view whose program was not
        // parsed here, e.g. a wizard's own program): lex the raw text and explain the G/M codes + F/S/T and axes.
        private static List<string> DescribeRaw(string data)
        {
            var lines = new List<string>();
            var axes = new List<string>();

            int c = data.IndexOfAny(new[] { '(', ';' });   // drop any trailing comment
            string s = c >= 0 ? data.Substring(0, c) : data;

            foreach (System.Text.RegularExpressions.Match m in Word.Matches(s))
            {
                char letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
                string val = m.Groups[2].Value;
                switch (letter)
                {
                    case 'G':
                    case 'M':
                        var cmd = ParseCode(letter, val);
                        lines.Add(cmd.HasValue ? (ExplainByCommand(cmd.Value) ?? (letter + val)) : (letter + val));
                        break;
                    case 'X': case 'Y': case 'Z': case 'A': case 'B': case 'C':
                        if (val.Length > 0) axes.Add(letter + " " + val);
                        break;
                    case 'F': if (val.Length > 0) lines.Add("Set feed rate to " + val + " mm/min"); break;
                    case 'S': if (val.Length > 0) lines.Add("Set spindle speed to " + val + " rpm"); break;
                    case 'T': if (val.Length > 0) lines.Add("Select tool " + val); break;
                }
            }

            if (axes.Count > 0)
                lines.Add("Move to " + string.Join(", ", axes) + " mm");

            return lines;
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
