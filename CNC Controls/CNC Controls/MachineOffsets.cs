/*
 * MachineOffsets.cs - part of CNC Controls library
 *
 * The real machine's offsets are the truth; the simulator mirrors them on every connect.
 *
 */

/*

The simulator is for testing, not for keeping state of its own. So the reference points that make an
ATC job work - where the toolsetter is (G59.3), where the tool-change position is (G30) - are captured
from the real controller whenever ioSender is connected to it, and stamped onto the simulator every
time ioSender connects to one. Sim-side drift is deliberately not preserved: if the two disagree, the
hardware wins, always.

The one thing that makes this more than "replay some numbers": THE SIMULATOR DERIVES ITS OFFSETS FROM
ITS SIMULATED WORLD. driver.c's sim_setup_apply_offsets writes G28/G30/G59.3 into NVS at boot out of
the sim_setup.cfg geometry - G59.3 from toolsetter_x/y, G30 from toolchange_x/y, G28 from the stock
corner and spoilboard. That is not an implementation detail to work around, it is the point: the sim's
G59.3 must be where its SIMULATED toolsetter physically is, or tc.macro drives there and probes empty
air. Writing the hardware's G59.3 straight into the controller's parameters would make the numbers
agree and the world disagree - the worst of both, and it would fail looking like a macro bug.

So the geometry is what gets copied. sim_setup.cfg is rewritten from the captured hardware offsets
before the simulator launches, and the sim derives its own offsets from that - moving the simulated
toolsetter and tool-change point to where the real ones are.

What is NOT copied, and why:
  - Stock size / spoilboard height (and therefore G28): per-job, not machine truth. There is no
    hardware value to mirror, so whatever is already in the cfg is preserved.
  - G92: a temporary offset that ioSender itself clears (G92.1) between runs. Copying it would
    reinstate a shift the operator had already finished with.
  - TLO: tool state, not a machine reference point.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CNC.Core;

namespace CNC.Controls
{
    public static class MachineOffsets
    {
        // Captured hardware truth, kept next to the other per-machine state in %AppData%\ioSender.
        // A plain "CODE=x,y,z" file rather than a settings field: this is a snapshot of what the
        // controller reported, not a user preference, and it must survive independently of the
        // settings schema.
        private const string StoreName = "machine_offsets.cfg";

        private static string StorePath()
        {
            return Path.Combine(Resources.ConfigPath, StoreName);
        }

        // The coordinate systems worth mirroring. G28/G30/G59.3 are machine reference points; G54-G59.2
        // are work offsets, replayed straight into the simulator's parameters (they carry no simulated
        // geometry with them, so G10 L2 is the whole job).
        private static readonly string[] References = { "G30", "G59.3" };
        private static readonly string[] WorkOffsets = { "G54", "G55", "G56", "G57", "G58", "G59", "G59.1", "G59.2" };

        /// <summary>
        /// Snapshot the connected REAL machine's offsets. No-op on a simulator connection - mirroring a
        /// simulator's own offsets back over the hardware truth is exactly the drift this exists to stop.
        /// </summary>
        public static void CaptureFromMachine()
        {
            if (AppConfig.Settings.Base.StartSimulator || !GrblWorkParameters.IsLoaded)
                return;

            var lines = new List<string>();
            foreach (string code in References)
                AppendIfKnown(lines, code);
            foreach (string code in WorkOffsets)
                AppendIfKnown(lines, code);

            if (lines.Count == 0)
                return;

            try
            {
                File.WriteAllText(StorePath(), string.Join(Environment.NewLine, lines) + Environment.NewLine);
                if (DebugLog.Enabled)
                    DebugLog.Write("fs", "MachineOffsets: captured " + lines.Count + " offset(s) from the connected machine");
            }
            catch (Exception ex)
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("fs", "MachineOffsets: capture failed - " + ex.Message);
            }
        }

        private static void AppendIfKnown(List<string> lines, string code)
        {
            var cs = GrblWorkParameters.GetCoordinateSystem(code);
            if (cs == null)
                return;
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1},{2},{3}",
                                    code, cs.X, cs.Y, cs.Z));
        }

        /// <summary>The captured offsets, empty when hardware has never been connected.</summary>
        private static Dictionary<string, double[]> Read()
        {
            var map = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = StorePath();
                if (!File.Exists(path))
                    return map;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string[] parts = line.Substring(eq + 1).Split(',');
                    if (parts.Length < 3)
                        continue;
                    double x, y, z;
                    if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out x) &&
                        double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out y) &&
                        double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out z))
                        map[line.Substring(0, eq).Trim()] = new[] { x, y, z };
                }
            }
            catch { }
            return map;
        }

        /// <summary>
        /// Rewrite the simulator's sim_setup.cfg so its SIMULATED toolsetter and tool-change point sit
        /// where the real machine's are - the sim then derives G59.3/G30 from that at boot. Every other
        /// key in the file is preserved: stock and spoilboard are per-job, with no hardware truth to
        /// mirror.
        /// </summary>
        /// <returns>
        /// True when the file's contents actually CHANGED, which means any simulator already running
        /// booted with the old geometry and has to be restarted to pick this up - the sim reads this
        /// file once, at startup, and nothing re-reads it later.
        /// </returns>
        public static bool WriteSimSetup(string simExePath)
        {
            var offsets = Read();
            if (offsets.Count == 0 || string.IsNullOrEmpty(simExePath))
                return false;

            double[] ts, tc;
            offsets.TryGetValue("G59.3", out ts);
            offsets.TryGetValue("G30", out tc);
            if (ts == null && tc == null)
                return false;

            try
            {
                string dir = Path.GetDirectoryName(simExePath);
                if (string.IsNullOrEmpty(dir))
                    return false;
                string cfg = Path.Combine(dir, SimulatorManager.SimSetupName);

                // Preserve every key we are not authoritative about (stock_*, spoilboard_z,
                // toolsetter_height, resolution_mm, description) - only the two positions we have
                // hardware truth for are replaced.
                var kept = new List<string>();
                var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (ts != null) { replaced.Add("toolsetter_x"); replaced.Add("toolsetter_y"); }
                if (tc != null) { replaced.Add("toolchange_x"); replaced.Add("toolchange_y"); }

                if (File.Exists(cfg))
                    foreach (string raw in File.ReadAllLines(cfg))
                    {
                        int eq = raw.IndexOf('=');
                        string key = eq > 0 ? raw.Substring(0, eq).Trim() : string.Empty;
                        if (!replaced.Contains(key))
                            kept.Add(raw);
                    }

                var sb = new System.Text.StringBuilder();
                foreach (string line in kept)
                    sb.AppendLine(line);
                sb.AppendLine("# positions below mirror the real machine (MachineOffsets) - edits here are overwritten on connect");
                if (ts != null)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "toolsetter_x = {0:F3}", ts[0]));
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "toolsetter_y = {0:F3}", ts[1]));
                }
                if (tc != null)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "toolchange_x = {0:F3}", tc[0]));
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "toolchange_y = {0:F3}", tc[1]));
                }

                string wanted = sb.ToString();
                bool changed = !File.Exists(cfg) || File.ReadAllText(cfg) != wanted;
                if (changed)
                    File.WriteAllText(cfg, wanted);

                if (DebugLog.Enabled)
                    DebugLog.Write("fs", string.Format("MachineOffsets: {0} {1} (toolsetter={2} toolchange={3})",
                        changed ? "wrote" : "already current -", cfg,
                        ts == null ? "kept" : "mirrored", tc == null ? "kept" : "mirrored"));
                return changed;
            }
            catch (Exception ex)
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("fs", "MachineOffsets: sim_setup.cfg write failed - " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Replay the captured WORK offsets (G54-G59.2) into the connected simulator. These carry no
        /// simulated geometry, so G10 L2 says all there is to say. The machine reference points are NOT
        /// sent here - they came from the geometry the sim already booted with (see WriteSimSetup);
        /// sending them as well would put the numbers back out of step with the simulated world.
        /// Call once a simulator connection is fully up.
        /// </summary>
        public static void ApplyToSimulator(GrblViewModel model)
        {
            if (model == null || !AppConfig.Settings.Base.StartSimulator)
                return;

            var offsets = Read();
            if (offsets.Count == 0)
                return;

            for (int i = 0; i < WorkOffsets.Length; i++)
            {
                double[] v;
                if (!offsets.TryGetValue(WorkOffsets[i], out v))
                    continue;

                // P1..P9 map to G54..G59.3 - the same order as WorkOffsets, which stops at G59.2 (P8).
                model.ExecuteCommand(string.Format(CultureInfo.InvariantCulture,
                    "G10L2P{0}X{1:F3}Y{2:F3}Z{3:F3}", i + 1, v[0], v[1], v[2]));
            }

            if (DebugLog.Enabled)
                DebugLog.Write("fs", "MachineOffsets: replayed work offsets into the simulator");
        }
    }
}
