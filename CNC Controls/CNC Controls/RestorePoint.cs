/*
 * RestorePoint.cs - part of CNC Controls library
 *
 * A moment you can go back to, rather than a file you have to identify.
 *
 * Two different things get snapshotted into Backups\<DayOfWeek>\: the CONTROLLER's settings
 * (Grbl_<stamp>.txt, its $ values) and the APP's own configuration (App.config_<stamp>.config - profiles,
 * probe and fixture definitions, work surface, layout). They are written at the same moments, into the same
 * folder, with the same timestamp convention, which reads as one backup in two parts.
 *
 * It was not treated as one. Restore listed only the Grbl files, and the App.config snapshots had no
 * user-facing restore at all - they existed solely as a crash fallback inside AppConfig.Load. So an operator
 * who knew only "it was fine an hour ago" had to already know WHICH of the two kinds held the thing that
 * broke before the Restore button was any use to them. That is backwards: knowing what broke is the thing
 * they are trying to find out.
 *
 * So the two are paired back into one restore point by time, and the operator picks a MOMENT. What that
 * moment happens to contain - one file or both - is then shown to them rather than asked of them.
 *
 * Pairing is by proximity, not equality, because the two snapshots are written by different code on
 * different triggers and land a few seconds apart (observed: App.config at 06:31:59, Grbl at 06:32:02).
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>What the operator asked to be put back.</summary>
    [Flags]
    public enum RestoreParts
    {
        None = 0,
        MachineSettings = 1,   // Grbl_*.txt   - the controller's $ settings
        AppConfig = 2          // App.config_* - ioSender's own configuration
    }

    public class RestorePoint
    {
        /// <summary>When this restore point was taken - the earlier of its two files.</summary>
        public DateTime Saved { get; set; }

        /// <summary>Controller settings snapshot, or null if this moment has none.</summary>
        public string GrblFile { get; set; }

        /// <summary>App configuration snapshot, or null if this moment has none.</summary>
        public string ConfigFile { get; set; }

        public bool HasGrbl { get { return !string.IsNullOrEmpty(GrblFile); } }
        public bool HasConfig { get { return !string.IsNullOrEmpty(ConfigFile); } }
        public bool HasBoth { get { return HasGrbl && HasConfig; } }

        public RestoreParts Available
        {
            get
            {
                return (HasGrbl ? RestoreParts.MachineSettings : RestoreParts.None) |
                       (HasConfig ? RestoreParts.AppConfig : RestoreParts.None);
            }
        }

        public string SavedText { get { return Saved.ToString("yyyy-MM-dd HH:mm:ss"); } }

        /// <summary>
        /// Said in terms of what the operator would lose or regain, not in filenames. "Grbl_2026-08-19.txt"
        /// answers a question nobody asked; "Machine settings" is the thing they are actually looking for.
        /// </summary>
        public string ContainsText
        {
            get
            {
                if (HasBoth)
                    return "Machine settings + app configuration";
                return HasGrbl ? "Machine settings only" : "App configuration only";
            }
        }

        /// <summary>How long ago, for picking "before the thing that broke it" without doing date arithmetic.</summary>
        public string AgeText
        {
            get
            {
                var d = DateTime.Now - Saved;
                if (d.TotalMinutes < 1d) return "just now";
                if (d.TotalMinutes < 60d) return string.Format("{0:0} min ago", d.TotalMinutes);
                if (d.TotalHours < 24d) return string.Format("{0:0} hr ago", d.TotalHours);
                return string.Format("{0:0} days ago", d.TotalDays);
            }
        }

        // Two snapshots belong to the same moment if they land within this of each other. Generous on
        // purpose: they are written by separate code paths, and the cost of pairing two that merely happened
        // to be close is that the operator is OFFERED a choice they can decline, while the cost of splitting
        // a genuine pair is the original problem - a restore point that silently omits half of itself.
        private static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(90d);

        /// <summary>Every restore point found in the backups folder, newest first.</summary>
        public static List<RestorePoint> All()
        {
            var grbl = Files("Grbl_", ".txt", "Grbl_".Length);
            var cfg = Files("App.config_", ".config", "App.config_".Length);
            var points = new List<RestorePoint>();

            // Walk the controller snapshots first and claim the nearest unclaimed config snapshot for each,
            // so a config file is never handed to two different moments.
            var takenConfigs = new HashSet<string>();
            foreach (var g in grbl)
            {
                var match = cfg.Where(c => !takenConfigs.Contains(c.Key))
                               .OrderBy(c => Math.Abs((c.Value - g.Value).TotalSeconds))
                               .FirstOrDefault();

                bool paired = match.Key != null && Math.Abs((match.Value - g.Value).TotalSeconds) <= PairWindow.TotalSeconds;
                if (paired)
                    takenConfigs.Add(match.Key);

                points.Add(new RestorePoint
                {
                    GrblFile = g.Key,
                    ConfigFile = paired ? match.Key : null,
                    Saved = paired && match.Value < g.Value ? match.Value : g.Value
                });
            }

            // Config snapshots with no controller snapshot near them are restore points in their own right -
            // an app-config-only moment is still a moment worth going back to.
            foreach (var c in cfg.Where(c => !takenConfigs.Contains(c.Key)))
                points.Add(new RestorePoint { ConfigFile = c.Key, Saved = c.Value });

            return points.OrderByDescending(p => p.Saved).ToList();
        }

        /// <summary>Snapshot files of one kind, as path -> timestamp (from the name, falling back to mtime).</summary>
        private static Dictionary<string, DateTime> Files(string prefix, string extension, int stampAt)
        {
            var found = new Dictionary<string, DateTime>();
            try
            {
                foreach (var dir in RotatingFileStore.ExistingDayDirectories(GrblSettings.SnapshotFolder))
                {
                    foreach (var path in Directory.GetFiles(dir, prefix + "*" + extension))
                    {
                        string stamp = Path.GetFileNameWithoutExtension(path);
                        stamp = stamp.Length > stampAt ? stamp.Substring(stampAt) : string.Empty;

                        DateTime saved;
                        if (!DateTime.TryParseExact(stamp, RotatingFileStore.TimestampFormat,
                                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out saved))
                            saved = File.GetLastWriteTime(path);

                        found[path] = saved;
                    }
                }
            }
            catch { }
            return found;
        }
    }
}
