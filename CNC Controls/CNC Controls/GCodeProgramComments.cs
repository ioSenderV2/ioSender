/*
 * GCodeProgramComments.cs - part of CNC Controls library
 *
 * Parses the structured comment lines the Fusion ioSenderBatchPost add-in post-processor emits into the
 * currently loaded program - (TOOL T=n D=d TYPE=FLAT|BALL|VBIT [A=angle] [L=length]) and
 * (STOCK X=.. Y=.. Z=..) - so any consumer can look up "what diameter/shape/length is tool N" or "what
 * stock size did the post declare" without re-scanning the program or duplicating the regex. Rebuilt once
 * per completed Load File/Load Folder (see GCode.cs's Program_FileChanged, the shared completion point
 * both funnel through via GCodeJob.FileChanged). Used by CarveView (CNC GCodeViewer, 3D carve simulation)
 * and touch-plate probing's edge-radius compensation (CNC Controls Probing, ProbingViewModel.SelectedProbe).
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
        public double Length;   // tool length (mm) - DefaultLengthMm when the comment omits L=
    }

    public struct GCodeStockInfo
    {
        public double X, Y, Z;
    }

    public static class GCodeProgramComments
    {
        // Used whenever a (TOOL ...) comment omits L= (older post output, or a post that can't determine
        // the tool's actual length) - a plausible generic stickout, not a measured value.
        public const double DefaultLengthMm = 40d;

        private static readonly Regex rxTool =
            new Regex(@"\(\s*TOOL\s+T=(\d+)\s+D=([0-9.]+)\s+TYPE=(\w+)(?:\s+A=([0-9.]+))?(?:\s+L=([0-9.]+))?", RegexOptions.IgnoreCase);
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

                var stock = TryParseStockLine(line);
                if (stock.HasValue)
                    Stock = stock;

                if (TryParseToolLine(line, out int t, out GCodeToolInfo info))
                    tools[t] = info;
            }
        }

        // Scan an arbitrary set of program lines for (TOOL ...) comments - the last one per tool number
        // wins, same as Refresh()'s loop. Used both by Refresh() (the global loaded-job scan) and by
        // ProgramView.DeclaredTools (per-instance, for a program that carries its own Blocks rather than
        // deferring to GCode.File.Data).
        public static IReadOnlyDictionary<int, GCodeToolInfo> ParseTools(IEnumerable<string> lines)
        {
            var found = new Dictionary<int, GCodeToolInfo>();
            foreach (var line in lines)
            {
                if (TryParseToolLine(line, out int t, out GCodeToolInfo info))
                    found[t] = info;
            }
            return found;
        }

        private static bool TryParseToolLine(string line, out int toolNumber, out GCodeToolInfo info)
        {
            toolNumber = 0;
            info = default;

            var m = rxTool.Match(line ?? string.Empty);
            if (!m.Success)
                return false;
            if (!int.TryParse(m.Groups[1].Value, out toolNumber) ||
                !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return false;

            double ang = 0d;
            if (m.Groups[4].Success)
                double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ang);

            double len = DefaultLengthMm;
            if (m.Groups[5].Success)
                double.TryParse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out len);

            info = new GCodeToolInfo { Diameter = d, Shape = m.Groups[3].Value.ToUpperInvariant(), Angle = ang, Length = len };
            return true;
        }

        // Scan an arbitrary set of program lines for a (STOCK X=.. Y=.. Z=..) comment - the last one wins, same
        // as Refresh()'s loop. Used both by Refresh() (the global loaded-job scan) and by ProgramView.Stock
        // (per-instance, for a program that carries its own Blocks rather than deferring to GCode.File.Data).
        public static GCodeStockInfo? ParseStock(IEnumerable<string> lines)
        {
            GCodeStockInfo? found = null;
            foreach (var line in lines)
            {
                var stock = TryParseStockLine(line);
                if (stock.HasValue)
                    found = stock;
            }
            return found;
        }

        private static GCodeStockInfo? TryParseStockLine(string line)
        {
            var ms = rxStock.Match(line ?? string.Empty);
            if (ms.Success &&
                double.TryParse(ms.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sx) &&
                double.TryParse(ms.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sy) &&
                double.TryParse(ms.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sz))
                return new GCodeStockInfo { X = sx, Y = sy, Z = sz };
            return null;
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
