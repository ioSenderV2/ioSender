/*
 * GCodeProgramComments.cs - part of CNC Controls library
 *
 * Parses the structured comment lines the Fusion ioSenderBatchPost add-in post-processor emits into the
 * currently loaded program - (TOOL T=n D=d TYPE=FLAT|BALL|VBIT [A=angle]) and (STOCK X=.. Y=.. Z=..) - so any
 * consumer can look up "what diameter/shape is tool N" or "what stock size did the post declare" without
 * re-scanning the program or duplicating the regex. Rebuilt once per completed Load File/Load Folder (see
 * GCode.cs's Program_FileChanged, the shared completion point both funnel through via GCodeJob.FileChanged).
 * Used by CarveView (CNC GCodeViewer, 3D carve simulation) and touch-plate probing's edge-radius compensation
 * (CNC Controls Probing, ProbingViewModel.SelectedProbe).
 *
 */

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CNC.Controls
{
    public struct GCodeToolInfo
    {
        public double Diameter;
        public string Shape;    // "FLAT" | "BALL" | "VBIT"
        public double Angle;    // V-bit included angle (degrees), 0 for flat/ball
    }

    public struct GCodeStockInfo
    {
        public double X, Y, Z;
    }

    public static class GCodeProgramComments
    {
        private static readonly Regex rxTool =
            new Regex(@"\(\s*TOOL\s+T=(\d+)\s+D=([0-9.]+)\s+TYPE=(\w+)(?:\s+A=([0-9.]+))?", RegexOptions.IgnoreCase);
        private static readonly Regex rxStock =
            new Regex(@"\(\s*STOCK\s+X=([0-9.]+)\s+Y=([0-9.]+)\s+Z=([0-9.]+)", RegexOptions.IgnoreCase);

        private static readonly Dictionary<int, GCodeToolInfo> tools = new Dictionary<int, GCodeToolInfo>();

        // Rebuild the tool-number -> diameter/shape map and the stock-size info from the currently loaded
        // program. Called once per completed Load File/Load Folder (GCode.cs's Program_FileChanged) - not on
        // every lookup.
        public static void Refresh()
        {
            tools.Clear();
            Stock = null;

            var data = GCode.File?.Data;
            if (data == null)
                return;

            foreach (var b in data)
            {
                string line = b.Data ?? string.Empty;

                var ms = rxStock.Match(line);
                if (ms.Success &&
                    double.TryParse(ms.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sx) &&
                    double.TryParse(ms.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sy) &&
                    double.TryParse(ms.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sz))
                    Stock = new GCodeStockInfo { X = sx, Y = sy, Z = sz };

                var m = rxTool.Match(line);
                if (!m.Success)
                    continue;
                if (int.TryParse(m.Groups[1].Value, out int t) &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    double ang = 0d;
                    if (m.Groups[4].Success)
                        double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ang);
                    tools[t] = new GCodeToolInfo { Diameter = d, Shape = m.Groups[3].Value.ToUpperInvariant(), Angle = ang };
                }
            }
        }

        // Null when the loaded program's (TOOL ...) comments never mention this tool number (or no program is
        // loaded) - callers should fall back to a stored/manual value in that case.
        public static double? DiameterFor(int toolNumber)
        {
            return tools.TryGetValue(toolNumber, out var info) ? info.Diameter : (double?)null;
        }

        // Full info (diameter + shape + angle) for one tool number - e.g. CarveView's 3D carve simulation,
        // which needs cutter shape/angle as well as diameter.
        public static GCodeToolInfo? For(int toolNumber)
        {
            return tools.TryGetValue(toolNumber, out var info) ? info : (GCodeToolInfo?)null;
        }

        // Read-only view of every tool the loaded program's comments mention - e.g. to find the lowest-
        // numbered tool as a "before the first tool change" default.
        public static IReadOnlyDictionary<int, GCodeToolInfo> All { get { return tools; } }

        // Declared stock size from the program's own (STOCK X=.. Y=.. Z=..) comment; null when the loaded
        // program has none (or none is loaded).
        public static GCodeStockInfo? Stock { get; private set; }
    }
}
