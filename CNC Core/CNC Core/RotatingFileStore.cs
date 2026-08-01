/*
 * RotatingFileStore.cs - part of Grbl Code Sender
 *
 * Shared day-of-week rotation scheme used by the debug/console/crash logs (LogFile.cs) AND the
 * App.config/Grbl settings backups (AppConfig.cs / Grbl.cs): fresh, timestamped files land in a
 * "<root>\<DayOfWeek>\" subfolder, each <baseName> pruned to a fixed count per folder (oldest
 * deleted first) whenever a new one is about to be created, with an optional "latest_<name>"
 * (hard-)linked into the top-level <root> folder so the current file is always found there
 * without knowing the timestamp or which weekday it landed on. Same mechanism, different root
 * folder ("logs" vs "Backups") and different base names.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CNC.Core
{
    public static class RotatingFileStore
    {
        public const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

        /// <summary>Current-moment timestamp in the shared naming format, e.g. "2026-07-31_14-23-05".</summary>
        public static string Stamp()
        {
            return DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Today's day-of-week subfolder under <paramref name="root"/> (created if missing), having first
        /// pruned any existing "<paramref name="baseName"/>_*" groups there down to <paramref name="retentionCount"/>
        /// - 1 (oldest deleted), so the caller's about-to-be-created file brings the folder back up to the cap.
        /// </summary>
        public static string PrepareDayDirectory(string root, string baseName, int retentionCount)
        {
            string dayDir = Path.Combine(root, DateTime.Now.DayOfWeek.ToString());
            Directory.CreateDirectory(dayDir);

            if (retentionCount > 0)
                PruneOldest(dayDir, baseName, retentionCount);

            return dayDir;
        }

        /// <summary>
        /// All day-of-week subfolders under <paramref name="root"/> that currently exist - for callers
        /// (e.g. the settings restore-point picker) that need history across the whole rolling week
        /// rather than just today's folder.
        /// </summary>
        public static IEnumerable<string> ExistingDayDirectories(string root)
        {
            foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
            {
                string dir = Path.Combine(root, d.ToString());
                if (Directory.Exists(dir))
                    yield return dir;
            }
        }

        // Keeps only the newest (retentionCount - 1) existing "<baseName>_<timestamp>[.ext[...]]" groups in
        // the day folder, so that after the new file is created the folder holds at most retentionCount.
        // Grouped by the "<baseName>_<timestamp>" key rather than by individual file, so companion files
        // sharing a timestamp (e.g. a size-rotated ".1", or a crash's .txt+.dmp+.zip) are pruned together.
        private static void PruneOldest(string dir, string baseName, int retentionCount)
        {
            try
            {
                string prefix = baseName + "_";
                var groups = new Dictionary<string, DateTime>();

                foreach (string file in Directory.GetFiles(dir, prefix + "*"))
                {
                    string name = Path.GetFileName(file);
                    string rest = name.Substring(prefix.Length);
                    int dot = rest.IndexOf('.');
                    string key = dot >= 0 ? rest.Substring(0, dot) : rest;

                    DateTime dt;
                    if (!groups.ContainsKey(key) &&
                        DateTime.TryParseExact(key, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                        groups[key] = dt;
                }

                if (groups.Count < retentionCount)
                    return;

                foreach (var stale in groups.OrderByDescending(g => g.Value).Skip(retentionCount - 1))
                    foreach (string file in Directory.GetFiles(dir, prefix + stale.Key + "*"))
                        try { File.Delete(file); } catch { }
            }
            catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        // A hard link (not a symlink) is used deliberately: creating an NTFS symlink needs
        // SeCreateSymbolicLinkPrivilege (admin, or Developer Mode enabled) - a hard link needs neither,
        // works for any user on the same volume, and since it's the SAME underlying file (not a copy),
        // it updates live if the target is appended to, with zero extra I/O. Falls back to a plain copy
        // (won't live-update, but at least exists) if the hard link can't be created (e.g. the root is
        // on a different/non-NTFS volume).
        public static void UpdateLatestLink(string root, string linkName, string path)
        {
            try
            {
                string latest = Path.Combine(root, linkName);
                try { File.Delete(latest); } catch { }
                if (!CreateHardLink(latest, path, IntPtr.Zero))
                    File.Copy(path, latest, true);
            }
            catch { /* best-effort - a missing convenience link must never block the write itself */ }
        }
    }
}
