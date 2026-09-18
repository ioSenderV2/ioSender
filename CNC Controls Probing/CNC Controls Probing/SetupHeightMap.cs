/*
 * SetupHeightMap.cs - part of CNC Controls Probing library
 *
 * The height map Setup probed, kept until a Work Order asks for it.
 *
 * ---- Why this exists ----
 *
 * Setup's "Probe height map" action used to probe a grid and IMMEDIATELY apply it to whatever program
 * happened to be loaded, into a throwaway HeightMapView that was discarded with the map. That forced the
 * workflow backwards: to get a compensated work order you had to Generate FIRST so there was something to
 * apply to, then go back to Setup - the opposite of the established order, which is Setup first (measure
 * the stock, set the origin and skew) and then Work Order, which composes against the measured size.
 *
 * So Setup no longer applies anything. It probes, and the map is kept here. Work Order's Generate is the
 * one and only place a map is applied, which also means there is no second, hidden application path that
 * could double-compensate a program.
 *
 * ---- Staleness is the whole risk ----
 *
 * A stored map outlives the setup it was probed against, and a map applied to the wrong setup is worse
 * than no map: every Z in the job is shifted by a surface that is not under the cutter any more. It cannot
 * be spotted by eye either - the program still looks like the program.
 *
 * So the map is stored with a STAMP of the setup it belongs to: where work zero sat in machine
 * coordinates, and the stock extent that was mapped. Generate compares the stamp against the live machine
 * and REFUSES (the user's explicit choice over a warning) when they no longer agree. Re-measure moves the
 * origin or changes the size; a re-home that lands anywhere different moves the origin too. A re-home that
 * lands in exactly the same place is, by definition, not a problem for the map.
 *
 * The map survives a restart because the Work Order option that consumes it is saved in the work order -
 * a job reopened next week must either find its map or be told plainly why it cannot have it.
 */

using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using CNC.Core;

namespace CNC.Controls.Probing
{
    public static class SetupHeightMap
    {
        private const double Tolerance = 0.01d;   // mm - below what any of this can mean

        /// <summary>The probed map, or null when none has been taken this install.</summary>
        public static HeightMap Map { get; private set; }

        /// <summary>When it was probed. Shown to the operator, because an age is the one thing that makes
        /// "is this still my setup?" answerable at a glance.</summary>
        public static DateTime ProbedUtc { get; private set; }

        /// <summary>Work zero in MACHINE coordinates when the map was probed - the setup it belongs to.</summary>
        public static double OriginX { get; private set; }
        public static double OriginY { get; private set; }
        public static double OriginZ { get; private set; }

        /// <summary>The stock extent that was mapped, in work coordinates.</summary>
        public static double Width { get; private set; }
        public static double Height { get; private set; }

        public static bool HasMap { get { return Map != null; } }

        private static string MapPath { get { return Path.Combine(Resources.ConfigPath, "setup-heightmap.map"); } }
        private static string StampPath { get { return Path.Combine(Resources.ConfigPath, "setup-heightmap.stamp"); } }

        /// <summary>
        /// Keep <paramref name="map"/> as the setup's map, stamped with the setup it was probed against.
        /// </summary>
        /// <param name="origin">Work zero in machine coordinates - the work position offset.</param>
        public static void Store(HeightMap map, Position origin, double width, double height)
        {
            Map = map;
            ProbedUtc = DateTime.UtcNow;
            OriginX = origin != null ? origin.X : double.NaN;
            OriginY = origin != null ? origin.Y : double.NaN;
            OriginZ = origin != null ? origin.Z : double.NaN;
            Width = width;
            Height = height;

            DebugLog.Write("heightmap", string.Format(
                "stored the setup map: {0}x{1} points over {2:0.###} x {3:0.###} mm, work zero at machine {4:0.###},{5:0.###},{6:0.###}",
                map.SizeX, map.SizeY, width, height, OriginX, OriginY, OriginZ));

            Save();
        }

        /// <summary>Forget the map - the setup it belonged to is gone.</summary>
        public static void Clear()
        {
            Map = null;
            try
            {
                if (File.Exists(MapPath)) File.Delete(MapPath);
                if (File.Exists(StampPath)) File.Delete(StampPath);
            }
            catch { /* a map we cannot delete is not worth failing a run over */ }
        }

        /// <summary>
        /// Why this map must not be applied to the current setup, or null when it may be.
        /// </summary>
        /// <remarks>
        /// Returns the REASON rather than a bool: "no" on its own, in front of an operator who has just
        /// probed a grid and ticked a box, is the kind of refusal that gets worked around rather than
        /// understood. The caller puts this text in front of them.
        /// </remarks>
        public static string WhyNotApplicable(Position liveOrigin, double width, double height)
        {
            if (Map == null)
                return "No height map has been probed. Run Setup with 'Probe height map' ticked first.";

            if (liveOrigin == null || double.IsNaN(OriginX))
                return "The work origin is unknown, so there is no way to tell whether the stored map belongs to this setup.";

            if (Math.Abs(liveOrigin.X - OriginX) > Tolerance ||
                Math.Abs(liveOrigin.Y - OriginY) > Tolerance ||
                Math.Abs(liveOrigin.Z - OriginZ) > Tolerance)
                return string.Format(CultureInfo.CurrentCulture,
                    "The work origin has moved since the height map was probed - it was at machine {0:0.###}, {1:0.###}, {2:0.###} and is now at {3:0.###}, {4:0.###}, {5:0.###}.\n\n"
                    + "The map describes the surface under the OLD origin, so applying it would shift every Z by a surface that is no longer under the cutter. Re-run Setup's height map.",
                    OriginX, OriginY, OriginZ, liveOrigin.X, liveOrigin.Y, liveOrigin.Z);

            if (width > 0d && height > 0d &&
                (Math.Abs(width - Width) > Tolerance || Math.Abs(height - Height) > Tolerance))
                return string.Format(CultureInfo.CurrentCulture,
                    "The stock has been re-measured since the height map was probed - the map covers {0:0.###} x {1:0.###} mm and the stock is now {2:0.###} x {3:0.###} mm.\n\n"
                    + "Re-run Setup's height map so the grid covers the stock you are about to cut.",
                    Width, Height, width, height);

            return null;
        }

        /// <summary>How old the map is, for the Work Order's own summary line.</summary>
        public static string Describe()
        {
            if (Map == null)
                return "no height map probed";

            var age = DateTime.UtcNow - ProbedUtc;
            string when = age.TotalMinutes < 1d ? "just now"
                        : age.TotalHours < 1d ? string.Format("{0:0} min ago", age.TotalMinutes)
                        : age.TotalDays < 1d ? string.Format("{0:0} h ago", age.TotalHours)
                        : ProbedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

            return string.Format(CultureInfo.CurrentCulture, "{0} x {1} points over {2:0.#} x {3:0.#} mm, probed {4}",
                Map.SizeX, Map.SizeY, Width, Height, when);
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Resources.ConfigPath);
                Map.Save(MapPath);
                new XElement("setupheightmap",
                    new XAttribute("ProbedUtc", ProbedUtc.ToString("o", CultureInfo.InvariantCulture)),
                    new XAttribute("OriginX", OriginX.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("OriginY", OriginY.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("OriginZ", OriginZ.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("Width", Width.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("Height", Height.ToString("R", CultureInfo.InvariantCulture))
                ).Save(StampPath);
            }
            catch (Exception ex)
            {
                // The map is still usable this session - it is only the restart that is lost.
                DebugLog.Write("heightmap", "could not save the setup map: " + ex.Message);
            }
        }

        /// <summary>
        /// Read back the stored map at startup. A map without its stamp is DISCARDED rather than loaded
        /// unstamped: an unstamped map cannot be checked against the setup, and the check is the only thing
        /// standing between a stale map and the work.
        /// </summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(MapPath) || !File.Exists(StampPath))
                    return;

                var stamp = XElement.Load(StampPath);
                ProbedUtc = DateTime.Parse(stamp.Attribute("ProbedUtc").Value, CultureInfo.InvariantCulture,
                                           DateTimeStyles.RoundtripKind);
                OriginX = double.Parse(stamp.Attribute("OriginX").Value, CultureInfo.InvariantCulture);
                OriginY = double.Parse(stamp.Attribute("OriginY").Value, CultureInfo.InvariantCulture);
                OriginZ = double.Parse(stamp.Attribute("OriginZ").Value, CultureInfo.InvariantCulture);
                Width = double.Parse(stamp.Attribute("Width").Value, CultureInfo.InvariantCulture);
                Height = double.Parse(stamp.Attribute("Height").Value, CultureInfo.InvariantCulture);

                Map = HeightMap.Load(MapPath);
                DebugLog.Write("heightmap", "loaded the stored setup map: " + Describe());
            }
            catch (Exception ex)
            {
                Map = null;
                DebugLog.Write("heightmap", "could not load the stored setup map: " + ex.Message);
            }
        }
    }
}
