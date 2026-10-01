/*
 * SoftLimitExplainer.cs - part of CNC Core
 *
 * "Soft limit alarm. G-code motion target exceeds machine travel." is the controller's whole answer, and
 * it names neither the axis nor the number. Four times in one day of setup work that cost a round trip
 * through the wire log to establish something the app already knew every part of: where the machine was,
 * what it had just asked for, and where the travel ends.
 *
 * The worked example this was written from, 2026-09-22:
 *
 *     > N70 G38.2 Z-100.0 F500.0      with the machine at Z -61.254
 *     < ALARM:2
 *
 *     Z: -61.254 - 100.000 = -161.254, which is 15.254 mm below the limit of -146.000
 *
 * Every term of that was already in hand. This turns it into the sentence.
 *
 * ---- what it will not do ----
 *
 * It says nothing rather than guessing. A G90 move is expressed in WORK coordinates, so resolving it to a
 * machine target needs the work offset - and on a ROTATED coordinate system an X/Y target does not
 * resolve by adding an offset at all. Where the frame cannot be settled honestly the axis is skipped, and
 * if that leaves nothing to say the whole explanation is dropped. A confident wrong number here would be
 * worse than the controller's silence, because it would be believed.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CNC.Core
{
    /// <summary>
    /// Remembers the last motion line put on the wire, and the distance mode in force when it went.
    ///
    /// Fed from StreamCommsBase, which is the one place every outgoing write funnels through. NOT from
    /// WireLog: that returns early when wire logging is switched off, and an explanation that only works
    /// on a debug flag is no use to the operator who hit the alarm.
    /// </summary>
    public static class MotionTrace
    {
        private static readonly object sync = new object();
        private static readonly Regex rxMotion = new Regex(@"G\s*0*(?:0|1|2|3|38(?:\.\d)?)(?![\d.])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxAbs = new Regex(@"G\s*0*90(?![\d.])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxInc = new Regex(@"G\s*0*91(?![\d.])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxMachine = new Regex(@"G\s*0*53(?![\d.])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The lookahead, not . A word boundary looks right and is wrong on every compound line ioSender
        // actually sends: "G90" in "N30G21G90G94G17" is followed by a word character, so  fails, and
        // "G0" in "N80G0Z2" likewise. Checked against real wire-log lines rather than reasoned about -
        // six of them matched on  and should not have, or did not and should have.
        private static bool relative;          // modal G90/G91, tracked from what we send
        private static string lastLine;
        private static bool lastRelative, lastMachine;

        /// <summary>The last motion line sent, or null.</summary>
        public static string LastLine { get { lock (sync) return lastLine; } }

        /// <summary>Whether that line was incremental.</summary>
        public static bool LastRelative { get { lock (sync) return lastRelative; } }

        /// <summary>Whether that line carried G53.</summary>
        public static bool LastMachine { get { lock (sync) return lastMachine; } }

        /// <summary>Forget everything - a reset makes the modal state ours to re-learn, not to assume.</summary>
        public static void Reset()
        {
            lock (sync) { relative = false; lastLine = null; lastRelative = false; lastMachine = false; }
        }

        /// <summary>
        /// Note an outgoing line. Cheap by design: this runs on the write path for every line of every
        /// program, so it does no work beyond two regex probes on lines that could be motion.
        /// </summary>
        public static void Record(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            lock (sync)
            {
                // Modal, so track it even on a line that moves nothing.
                if (rxInc.IsMatch(text)) relative = true;
                else if (rxAbs.IsMatch(text)) relative = false;

                if (!rxMotion.IsMatch(text))
                    return;

                lastLine = text.Trim();
                lastRelative = relative;
                lastMachine = rxMachine.IsMatch(text);
            }
        }
    }

    /// <summary>Turns ALARM:2 into the arithmetic behind it. See the file header.</summary>
    public static class SoftLimitExplainer
    {
        /// <summary>
        /// One line per axis that exceeds its travel, or null when nothing can be said honestly.
        /// </summary>
        public static string Explain(Position machinePosition, Position workOffset)
        {
            string line = MotionTrace.LastLine;
            if (line == null || machinePosition == null)
                return null;

            bool relative = MotionTrace.LastRelative, machineFrame = MotionTrace.LastMachine;
            var rows = new List<string>();
            string letters = GrblInfo.AxisLetters;

            for (int i = 0; i < GrblInfo.NumAxes && i < letters.Length; i++)
            {
                string letter = letters.Substring(i, 1);
                double word;
                if (!TryAxisWord(line, letter, out word))
                    continue;

                double start = machinePosition.Values[i];
                double target;

                if (machineFrame)
                    target = word;                       // G53: already machine coordinates
                else if (relative)
                    target = start + word;               // a delta is a delta in either frame
                else if (workOffset != null && i < workOffset.Values.Length && !double.IsNaN(workOffset.Values[i]))
                    target = word + workOffset.Values[i];
                else
                    continue;                            // cannot resolve the frame - say nothing for this axis

                double lo = GrblInfo.ReachableLimit(i, false), hi = GrblInfo.ReachableLimit(i, true);
                if (double.IsNaN(lo) || double.IsNaN(hi))
                    continue;                            // envelope unknown - the same refusal ReachableLimit makes

                if (lo > hi) { double t = lo; lo = hi; hi = t; }

                if (target >= lo && target <= hi)
                    continue;

                double limit = target < lo ? lo : hi;
                string sum = machineFrame
                    ? string.Format(CultureInfo.CurrentCulture, "{0:0.0##}", target)
                    : string.Format(CultureInfo.CurrentCulture, "{0:0.0##} {1} {2:0.0##} = {3:0.0##}",
                        start, word < 0d ? "-" : "+", Math.Abs(word), target);

                rows.Add(string.Format(CultureInfo.CurrentCulture,
                    "{0}: {1}, which is {2:0.0##} mm {3} the limit of {4:0.0##}",
                    letter, sum, Math.Abs(target - limit), target < lo ? "below" : "above", limit));
            }

            if (rows.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.Append(rows.Count == 1 ? "Out of travel - " : "Out of travel - ");
            sb.Append(string.Join("; ", rows));
            sb.Append(".  (from ").Append(Shorten(line)).Append(")");
            return sb.ToString();
        }

        /// <summary>The value of one axis word, ignoring anything inside parentheses.</summary>
        private static bool TryAxisWord(string line, string letter, out double value)
        {
            value = 0d;
            var m = Regex.Match(line, letter + @"\s*(-?\d*\.?\d+)", RegexOptions.IgnoreCase);
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                                CultureInfo.InvariantCulture, out value);
        }

        private static string Shorten(string line)
        {
            line = line.Replace("\r", "").Replace("\n", "").Trim();
            return line.Length <= 48 ? line : line.Substring(0, 45) + "...";
        }
    }
}
