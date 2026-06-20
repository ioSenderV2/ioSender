/*
 * FusionFolderLoader.cs - part of CNC Controls library for Grbl
 *
 * Loads a folder of per-operation .nc files produced by the SRWCommands
 * Fusion 360 "PostProcess" add-in and combines them, in memory, into one
 * program - the same stitching the add-in does when it writes a combined
 * file, but kept as discrete toolpath sections so ioSender can show them as
 * an expandable outline and start a run at any individual toolpath.
 *
 * The combine rules here are a faithful port of the add-in's entry.py
 * (_restore_rapids / _strip_per_op_wrappers / _combine_nc_files):
 *
 *   - Per-op files are named  <seq#>_<name>_T<tool#>.nc  and ordered by seq#.
 *   - Each file's header (%, G90 G94, G17, G21, comments, the G28 safe-Z) and
 *     program-end footer (M9, G28/G53 cleanup, M5, M30, %) are stripped.
 *   - Each toolpath is preceded by  G53 G0 Z0  (machine-coord safe-Z retract)
 *     and  M6 T<n>  (the M0 swap-pause lives in the controller's tc.macro).
 *   - Optionally, Fusion-Personal-Use feed-rate retracts/repositions are
 *     converted back to G0 rapids.
 *
 */

/*

Copyright (c) 2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CNC.Controls
{
    public class FolderToolpath
    {
        public int Sequence { get; set; }
        public string Name { get; set; }
        public int Tool { get; set; }
        public string FilePath { get; set; }
        public int RapidsRestored { get; set; }
        public List<string> Body { get; set; } = new List<string>();

        // Label shown as the outline group header.
        public string Section { get { return string.Format("{0}: {1} (T{2})", Sequence, Name, Tool); } }
    }

    public static class FusionFolderLoader
    {
        // <seq#>_<displayName>_T<tool#>.nc  e.g. 2_FinishBottom_T2.nc.
        // Naturally excludes the add-in's combined / _outline / _dryrun files
        // (they have no _T<n> suffix).
        private static readonly Regex FileName = new Regex(@"^(\d+)_(.+)_T(\d+)\.nc$", RegexOptions.IgnoreCase);

        // The tool-table file the add-in emits (e.g. 0_tooltable.nc): carries the (TOOL T= D= TYPE=)
        // comments the simulator reads for material removal. It has no _T<n> suffix so MatchFolder skips
        // it; it is loaded verbatim (comments preserved) at the very top of the combined program instead.
        private static readonly Regex ToolTableFile = new Regex(@"tool\s*table", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // --- _restore_rapids regexes (compiled: run per-line over large files) ---
        private static readonly Regex GMode = new Regex(@"^\s*(G0?0|G0?1)\b", RegexOptions.Compiled);
        private static readonly Regex Coord = new Regex(@"\b([XYZIJKF])(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex Feed = new Regex(@"\s*F-?\d+(?:\.\d+)?", RegexOptions.Compiled);

        // --- _strip_per_op_wrappers patterns ---
        private static readonly Regex[] BodyStart = {
            new Regex(@"^\s*G\s*0?[0-3]\b"),     // G0/G1/G2/G3
            new Regex(@"^\s*G\s*5[3-9]\b"),      // G54..G59 work offsets
            new Regex(@"^\s*S\s*\d"),            // spindle speed
            new Regex(@"^\s*M\s*0?[3-5]\b"),     // M3/M4/M5
            new Regex(@"^\s*M\s*0?[78]\b"),      // M7/M8 coolant
            new Regex(@"^\s*T\s*\d"),            // T<n> tool select
        };
        private static readonly Regex[] ProgramEnd = {
            new Regex(@"^\s*M\s*0?2\b"),         // M2/M02
            new Regex(@"^\s*M\s*30\b"),          // M30
            new Regex(@"^\s*%\s*$"),             // % tape end
        };
        private static readonly Regex AxisOrMotion = new Regex(@"\b[XYZABC]-?\d|\bG\s*0?[0-3]\b");
        private static readonly Regex G28orG53 = new Regex(@"\bG\s*(28|53)\b");

        // M6 / M06 as a standalone word (not M16, M60, ...). Used to detect a tool change
        // already present in a posted file so we don't insert a duplicate.
        private static readonly Regex ToolChange = new Regex(@"(?<![0-9A-Za-z])M0*6(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The G90 G94 / G17 / G21 file prolog, sent before a toolpath when starting a run partway through.</summary>
        public static readonly string[] Prolog = { "G90 G94", "G17", "G21" };

        /// <summary>Find and order the per-op toolpath files in a folder. Empty list if none match.</summary>
        public static List<FolderToolpath> MatchFolder(string folder)
        {
            var ops = new List<FolderToolpath>();

            if (!Directory.Exists(folder))
                return ops;

            foreach (var path in Directory.GetFiles(folder, "*.nc"))
            {
                var m = FileName.Match(Path.GetFileName(path));
                if (!m.Success)
                    continue;

                ops.Add(new FolderToolpath {
                    Sequence = int.Parse(m.Groups[1].Value),
                    Name = m.Groups[2].Value,
                    Tool = int.Parse(m.Groups[3].Value),
                    FilePath = path
                });
            }

            ops.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

            return ops;
        }

        /// <summary>Find the tool-table file (e.g. 0_tooltable.nc) in the folder, or null if none.</summary>
        public static string MatchToolTable(string folder)
        {
            if (!Directory.Exists(folder))
                return null;

            return Directory.GetFiles(folder, "*.nc")
                            .Where(p => ToolTableFile.IsMatch(Path.GetFileNameWithoutExtension(p)))
                            .OrderBy(p => Path.GetFileName(p))
                            .FirstOrDefault();
        }

        /// <summary>
        /// Read a file's lines preserving comments (unlike StripWrappers, which drops the header). Only the
        /// % tape-start/end marks and trailing blank lines are removed. Used for the tool-table file so its
        /// (TOOL ...) comments survive into the combined program.
        /// </summary>
        public static List<string> ReadPreservingComments(string content)
        {
            var lines = SplitLines(content);
            var outLines = new List<string>(lines.Length);

            foreach (var raw in lines)
            {
                if (raw.Trim() == "%")          // tape start/end marker - not a g-code line
                    continue;
                outLines.Add(raw);
            }

            while (outLines.Count > 0 && outLines[outLines.Count - 1].Trim().Length == 0)
                outLines.RemoveAt(outLines.Count - 1);

            return outLines;
        }

        private static string[] SplitLines(string content)
        {
            return content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }

        /// <summary>
        /// Convert Fusion-Personal-Use feed-rate retracts and repositions back to G0 rapids.
        /// Port of entry.py:_restore_rapids. Returns converted line count via <paramref name="converted"/>.
        /// </summary>
        public static string RestoreRapids(string content, out int converted)
        {
            converted = 0;

            var lines = SplitLines(content);
            var outLines = new List<string>(lines.Length);

            string currentMode = null;      // "G0" or "G1"
            double? currentZ = null;
            bool inRapidBlock = false;

            foreach (var raw in lines)
            {
                var stripped = raw.Trim();
                if (stripped.Length == 0 || stripped.StartsWith("("))
                {
                    outLines.Add(raw);
                    continue;
                }

                var m = GMode.Match(raw);
                string explicitMode = null;
                if (m.Success)
                {
                    explicitMode = (m.Groups[1].Value == "G0" || m.Groups[1].Value == "G00") ? "G0" : "G1";
                    currentMode = explicitMode;
                }

                bool hasX = false, hasY = false, hasZ = false, hasIJK = false;
                double newZ = 0d;
                foreach (Match c in Coord.Matches(raw))
                {
                    var v = double.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture);
                    switch (c.Groups[1].Value)
                    {
                        case "X": hasX = true; break;
                        case "Y": hasY = true; break;
                        case "Z": hasZ = true; newZ = v; break;
                        case "I": case "J": case "K": hasIJK = true; break;
                    }
                }

                bool shouldRapid = false;
                if (currentMode == "G1" && !hasIJK)
                {
                    if (hasZ && !hasX && !hasY)
                    {
                        // Z-only move
                        if (currentZ == null || newZ > currentZ.Value)
                        {
                            shouldRapid = true;     // upward retract
                            inRapidBlock = true;
                        }
                        else
                            inRapidBlock = false;   // plunge ends the rapid block
                    }
                    else if ((hasX || hasY) && !hasZ)
                    {
                        if (inRapidBlock)           // reposition at safe height
                            shouldRapid = true;
                    }
                    else
                        inRapidBlock = false;       // combined XYZ / modal cmd
                }
                else
                    inRapidBlock = false;

                if (hasZ)
                    currentZ = newZ;

                if (shouldRapid)
                {
                    var line = Feed.Replace(raw, "");               // G0 has no feedrate
                    if (explicitMode == "G1")
                        line = GMode.Replace(line, "G0", 1);
                    else
                        line = "G0 " + line.TrimStart();            // modal continuation
                    line = line.TrimEnd() + "  (rapid restored)";
                    outLines.Add(line);
                    // NOTE: do NOT update currentMode here - we track the INPUT
                    // file's modal G state, so the next (still-modal-G1) line is
                    // still considered for conversion.
                    converted++;
                }
                else
                    outLines.Add(raw);
            }

            return string.Join("\n", outLines);
        }

        /// <summary>True if any (non-comment) line issues an M6/M06 tool change.</summary>
        public static bool ContainsToolChange(IEnumerable<string> lines)
        {
            foreach (var raw in lines)
            {
                if (raw == null)
                    continue;
                var line = raw;
                int c = line.IndexOf('(');     // ignore comments
                if (c >= 0)
                    line = line.Substring(0, c);
                if (ToolChange.IsMatch(line))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Drop the file-level header (%, units, comments, G28 safe-Z) and program-end footer
        /// (M9, G28/G53 cleanup, M5, M30, %) from a per-op file, returning just the cuttable body.
        /// Port of entry.py:_strip_per_op_wrappers.
        /// </summary>
        public static List<string> StripWrappers(string content)
        {
            var lines = SplitLines(content);

            int start = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (BodyStart.Any(p => p.IsMatch(lines[i])))
                {
                    start = i;
                    break;
                }
            }

            int end = lines.Length;
            for (int i = lines.Length - 1; i >= start; i--)
            {
                var s = lines[i].Trim();
                if (s.Length == 0 || s.StartsWith("("))
                {
                    end = i;
                    continue;
                }
                if (ProgramEnd.Any(p => p.IsMatch(lines[i])))
                {
                    end = i;
                    continue;
                }
                var up = s.ToUpperInvariant();
                if (G28orG53.IsMatch(up))
                {
                    end = i;
                    continue;
                }
                if (!AxisOrMotion.IsMatch(up))
                {
                    end = i;
                    continue;
                }
                break;  // first real motion line from the end
            }

            var body = new List<string>();
            for (int i = start; i < end; i++)
                body.Add(lines[i]);

            while (body.Count > 0 && body[body.Count - 1].Trim().Length == 0)
                body.RemoveAt(body.Count - 1);

            return body;
        }
    }
}
