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

        /// <summary>
        /// Work zero in MACHINE coordinates when the map was probed - the setup it belongs to. WITHOUT the
        /// tool length offset; see WorkOrigin.
        /// </summary>
        public static double OriginX { get; private set; }
        public static double OriginY { get; private set; }
        public static double OriginZ { get; private set; }

        /// <summary>
        /// Where work zero sits in machine coordinates, with the TOOL taken out of it. False when the
        /// controller has not reported enough to say.
        /// </summary>
        /// <remarks>
        /// NOT the work position offset on its own, which is what this used to compare and what produced a
        /// false refusal on real hardware 2026-09-18: grblHAL's WCO is the WCS offset PLUS G92 PLUS THE TOOL
        /// LENGTH OFFSET, so fitting a different tool moves it. The operator had changed nothing - G54 read
        /// 150.304, -635.370, -77.936 before and after - but the TLO went from -25.766 to -16.540 and the
        /// map was refused for a work origin that had "moved" by exactly that 9.226 mm.
        ///
        /// Taking the tool out is not a loosening: work Z0 means the same physical plane whatever is in the
        /// spindle - that is what a tool length offset IS - so a map probed with one tool describes the same
        /// surface for the next. G92 stays IN, because a G92 shift genuinely does move the origin out from
        /// under the map, as does re-zeroing Z, and both must still refuse.
        /// </remarks>
        /// <param name="fresh">
        /// Ask the controller for its offsets first. Not optional politeness: WCO arrives with EVERY status
        /// report and the tool length offset ONLY in a $# report, so the two are read from sources that
        /// update at wildly different rates, and subtracting one from the other is only meaningful when
        /// both are current. Measured 2026-09-18: a map was stamped with a TEN-MINUTE-OLD tool offset
        /// (-19.902) against a WCO that already carried the new one (-22.227), putting the stamp 2.325 mm
        /// out and refusing the map on the app's own arithmetic, with nothing having moved.
        ///
        /// True at the two points that DECIDE something - storing a map, and the check before applying one.
        /// False on a UI refresh, where a blocking round trip to the controller does not belong, and the
        /// answer only colours a summary line. Skipped anyway while a job is streaming: $# in the middle of
        /// a run is the collision this codebase has been bitten by before.
        /// </param>
        private static bool WorkOrigin(GrblViewModel model, bool fresh, out double x, out double y, out double z)
        {
            x = y = z = double.NaN;

            if (fresh && model != null && !model.IsJobRunning && model.GrblState.State != GrblStates.Unknown)
                GrblWorkParameters.Get(model);   // ends by writing the freshly-read TLO into model.ToolOffset

            var wco = model?.WorkPositionOffset;
            if (wco == null || double.IsNaN(wco.X) || double.IsNaN(wco.Y) || double.IsNaN(wco.Z))
                return false;

            var tlo = model.ToolOffset;
            // No tool offset reported at all is the ordinary no-TLO case, not a failure - it is zero.
            double tx = tlo == null || double.IsNaN(tlo.X) ? 0d : tlo.X;
            double ty = tlo == null || double.IsNaN(tlo.Y) ? 0d : tlo.Y;
            double tz = tlo == null || double.IsNaN(tlo.Z) ? 0d : tlo.Z;

            x = wco.X - tx;
            y = wco.Y - ty;
            z = wco.Z - tz;
            return true;
        }

        /// <summary>The stock extent that was mapped, in work coordinates.</summary>
        public static double Width { get; private set; }
        public static double Height { get; private set; }

        public static bool HasMap { get { EnsureLoaded(); return Map != null; } }

        private static bool _loadAttempted;

        /// <summary>
        /// Read the stored map in on first use.
        /// </summary>
        /// <remarks>
        /// NOT at startup, which is where this was called from first and why it never worked:
        /// Resources.ConfigPath is "./" until AppConfig.Load resolves it, and the registration ran before
        /// that - so it looked for the map beside the executable, found nothing, and returned without a
        /// word. The stored files were sitting in %AppData% the whole time and the work order's option
        /// stayed disabled with nothing to explain it (2026-09-18).
        ///
        /// First use is always long after the config is up, and it costs one file check.
        /// </remarks>
        private static void EnsureLoaded()
        {
            if (_loadAttempted || Map != null)
                return;
            _loadAttempted = true;
            Load();
        }

        private static string MapPath { get { return Path.Combine(Resources.ConfigPath, "setup-heightmap.map"); } }
        private static string StampPath { get { return Path.Combine(Resources.ConfigPath, "setup-heightmap.stamp"); } }

        /// <summary>
        /// Keep <paramref name="map"/> as the setup's map, stamped with the setup it was probed against.
        /// </summary>
        /// <param name="model">The live controller state - work zero is taken from it, see WorkOrigin.</param>
        public static void Store(HeightMap map, GrblViewModel model, double width, double height)
        {
            Map = map;
            ProbedUtc = DateTime.UtcNow;
            WorkOrigin(model, true, out double ox, out double oy, out double oz);
            OriginX = ox;
            OriginY = oy;
            OriginZ = oz;
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
        public static string WhyNotApplicable(GrblViewModel model, double width, double height, bool fresh)
        {
            EnsureLoaded();
            if (Map == null)
                return "No height map has been probed. Run Setup with 'Probe height map' ticked first.";

            if (!WorkOrigin(model, fresh, out double lx, out double ly, out double lz) || double.IsNaN(OriginX))
                return "The work origin is unknown, so there is no way to tell whether the stored map belongs to this setup.";

            if (Math.Abs(lx - OriginX) > Tolerance ||
                Math.Abs(ly - OriginY) > Tolerance ||
                Math.Abs(lz - OriginZ) > Tolerance)
                return string.Format(CultureInfo.CurrentCulture,
                    "The work origin has moved since the height map was probed - it was at machine {0:0.###}, {1:0.###}, {2:0.###} and is now at {3:0.###}, {4:0.###}, {5:0.###}.\n\n"
                    + "The map describes the surface under the OLD origin, so applying it would shift every Z by a surface that is no longer under the cutter. Re-run Setup's height map.\n\n"
                    + "(A tool change does not move the work origin - the tool length offset is not part of these numbers.)",
                    OriginX, OriginY, OriginZ, lx, ly, lz);

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
            EnsureLoaded();
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

        // ---- a .map file that carries the setup it belongs to -------------------------------------
        //
        // HeightMap.Save writes <heightmap ...> with its own attributes and HeightMap.Load reads the ones it
        // knows by name, ignoring the rest - so the stamp rides along in the same file as extra attributes,
        // and a stamped map still opens in anything that reads the old format.
        //
        // Why it has to ride along at all: a saved map is the thing an operator keeps NEXT TO A JOB and
        // opens weeks later. Re-stamping it with the live origin at load time - which is what happens
        // without this - makes every map look like it belongs to whatever setup is in front of you, which
        // is precisely the state the staleness refusal exists to catch. A map that cannot say which setup
        // it was probed against is not trustworthy, and now it says.

        private const string StampProbed = "SetupProbedUtc";
        private const string StampOx = "SetupOriginX", StampOy = "SetupOriginY", StampOz = "SetupOriginZ";
        private const string StampW = "SetupWidth", StampH = "SetupHeight";

        /// <summary>Write <paramref name="map"/> to <paramref name="path"/> with the stamp it is held under.</summary>
        public static void SaveMapWithStamp(HeightMap map, string path, GrblViewModel model, double width, double height)
        {
            if (map == null)
                return;

            map.Save(path);

            // The stamp this map is KNOWN by, when it is the one in hand - so saving does not quietly
            // relabel a map with wherever the machine happens to be standing now. Only a map with no stamp
            // of its own falls back to the live position.
            bool known = Map == map && !double.IsNaN(OriginX);
            WorkOrigin(model, true, out double lx, out double ly, out double lz);
            double ox = known ? OriginX : lx;
            double oy = known ? OriginY : ly;
            double oz = known ? OriginZ : lz;
            double w = known && Width > 0d ? Width : width;
            double h = known && Height > 0d ? Height : height;
            DateTime when = known ? ProbedUtc : DateTime.UtcNow;

            if (double.IsNaN(ox))
                return;    // nothing truthful to stamp it with; leave the file as a plain map

            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                root.SetAttributeValue(StampProbed, when.ToString("o", CultureInfo.InvariantCulture));
                root.SetAttributeValue(StampOx, ox.ToString("R", CultureInfo.InvariantCulture));
                root.SetAttributeValue(StampOy, oy.ToString("R", CultureInfo.InvariantCulture));
                root.SetAttributeValue(StampOz, oz.ToString("R", CultureInfo.InvariantCulture));
                root.SetAttributeValue(StampW, w.ToString("R", CultureInfo.InvariantCulture));
                root.SetAttributeValue(StampH, h.ToString("R", CultureInfo.InvariantCulture));
                doc.Save(path);
                DebugLog.Write("heightmap", "saved " + path + " stamped " + Describe());
            }
            catch (Exception ex)
            {
                DebugLog.Write("heightmap", "saved the map but could not stamp it: " + ex.Message);
            }
        }

        /// <summary>
        /// Load a .map and adopt it as the setup's map, WITH the stamp the file carries.
        /// </summary>
        /// <returns>null on success, or operator-facing text explaining why it was not adopted.</returns>
        public static string LoadMapWithStamp(string path)
        {
            try
            {
                var map = HeightMap.Load(path);
                if (map == null)
                    return "That file could not be read as a height map.";

                var root = XDocument.Load(path).Root;
                var probed = root.Attribute(StampProbed);
                var ox = root.Attribute(StampOx);

                Map = map;

                if (probed == null || ox == null)
                {
                    // An unstamped map - an older file, or one saved before stamping existed. Adopted, but
                    // with the origin marked unknown, so the work order refuses it rather than compensating
                    // against a surface nobody can tie to this setup.
                    ProbedUtc = DateTime.UtcNow;
                    OriginX = OriginY = OriginZ = double.NaN;
                    Width = map.Max.X - map.Min.X;
                    Height = map.Max.Y - map.Min.Y;
                    DebugLog.Write("heightmap", "loaded " + path + " - NO STAMP, so the setup it belongs to is unknown");
                    return null;
                }

                ProbedUtc = DateTime.Parse(probed.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                OriginX = double.Parse(ox.Value, CultureInfo.InvariantCulture);
                OriginY = double.Parse(root.Attribute(StampOy).Value, CultureInfo.InvariantCulture);
                OriginZ = double.Parse(root.Attribute(StampOz).Value, CultureInfo.InvariantCulture);
                Width = double.Parse(root.Attribute(StampW).Value, CultureInfo.InvariantCulture);
                Height = double.Parse(root.Attribute(StampH).Value, CultureInfo.InvariantCulture);
                _loadAttempted = true;

                DebugLog.Write("heightmap", "loaded " + path + " stamped " + Describe());
                Save();     // becomes the current stored map, stamp and all
                return null;
            }
            catch (Exception ex)
            {
                DebugLog.Write("heightmap", "could not load " + path + ": " + ex.Message);
                return "That height map could not be loaded: " + ex.Message;
            }
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
                {
                    // Logged, because "no map stored" and "looked in the wrong place" are the same silence.
                    DebugLog.Write("heightmap", "no stored setup map at " + MapPath);
                    return;
                }

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
