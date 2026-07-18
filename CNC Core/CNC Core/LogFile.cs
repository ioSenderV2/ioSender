/*
 * LogFile.cs - part of Grbl Code Sender
 *
 * Shared low-level file-logging primitive: resolves the logs directory (with the same fallback
 * everyone needs when that isn't available yet, e.g. a crash during early startup), enforces an
 * 8MB rotate-to-".1" size cap, and - for a fresh-file-per-run log - prunes old files by age and
 * can maintain a hard-linked "latest" alias. ConsoleLog, DebugLog and the crash logger each used
 * to reimplement this (three near-identical copies); they now all call through this one instead.
 *
 * Deliberately just the file mechanics - callers keep their own enable flags, category filters,
 * banners, and (for ConsoleLog) the background writer thread/queue, since those differ per log.
 */

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CNC.Core
{
    public sealed class LogFile
    {
        private readonly object _sync = new object();
        private readonly long _maxBytes;

        /// <summary>Full path of this log's active file.</summary>
        public string Path { get; private set; }

        private LogFile(string path, long maxBytes)
        {
            Path = path;
            _maxBytes = maxBytes;
        }

        /// <summary>
        /// Open (and, for a per-run log, create) a log file. Never throws - returns null on failure,
        /// which callers treat as "logging disabled" the same way a caught exception used to.
        /// </summary>
        /// <param name="baseName">File name with no extension/timestamp, e.g. "console" or "ioSender.debug".</param>
        /// <param name="maxBytes">Size cap before rotating the current file to ".1" (previous ".1" discarded). 0 = no cap.</param>
        /// <param name="perRun">True: a fresh "&lt;baseName&gt;_&lt;timestamp&gt;.log" file is created now (e.g. ConsoleLog).
        /// False: the fixed "&lt;baseName&gt;.log" is reused/appended-to across the life of the install (e.g. DebugLog, crash log).</param>
        /// <param name="retentionDays">Only meaningful with perRun=true: prior "&lt;baseName&gt;_*.log*" files older than this are
        /// deleted now. 0 = don't prune.</param>
        /// <param name="latestLinkName">Only meaningful with perRun=true: if set, this name is (hard-)linked to the fresh file
        /// so it always resolves to the current run's log without needing to know the timestamp.</param>
        public static LogFile Open(string baseName, long maxBytes = 8 * 1024 * 1024, bool perRun = false,
                                    int retentionDays = 0, string latestLinkName = null)
        {
            string dir;
            try { dir = Resources.ResolveLogsDirectory(); }
            catch { dir = AppDomain.CurrentDomain.BaseDirectory; }

            try
            {
                if (perRun && retentionDays > 0)
                    PruneOld(dir, baseName, retentionDays);

                string path = System.IO.Path.Combine(dir, perRun
                    ? baseName + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".log"
                    : baseName + ".log");

                if (perRun)
                {
                    File.WriteAllText(path, string.Empty, Encoding.UTF8);   // create it now so a latest-link below has a real target
                    if (latestLinkName != null)
                        UpdateLatestLink(dir, latestLinkName, path);
                }

                return new LogFile(path, maxBytes);
            }
            catch { return null; }
        }

        /// <summary>Append text (caller supplies its own line ending), rotating first if oversize. Never throws.</summary>
        public void Write(string text)
        {
            lock (_sync)
            {
                try
                {
                    if (_maxBytes > 0)
                    {
                        var fi = new FileInfo(Path);
                        if (fi.Exists && fi.Length > _maxBytes)
                        {
                            string bak = Path + ".1";
                            if (File.Exists(bak))
                                File.Delete(bak);
                            File.Move(Path, bak);
                        }
                    }
                    File.AppendAllText(Path, text, Encoding.UTF8);
                }
                catch { /* logging must never take the app down */ }
            }
        }

        // Best-effort retention for a per-run log: fresh timestamped files never get overwritten, so
        // without pruning they'd accumulate forever.
        private static void PruneOld(string dir, string baseName, int retentionDays)
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (string file in Directory.GetFiles(dir, baseName + "_*.log*"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        // A hard link (not a symlink) is used deliberately: creating an NTFS symlink needs
        // SeCreateSymbolicLinkPrivilege (admin, or Developer Mode enabled) - a hard link needs neither,
        // works for any user on the same volume, and since it's the SAME underlying file (not a copy),
        // it updates live as the target is appended to, with zero extra I/O. Falls back to a plain copy
        // (won't live-update, but at least exists) if the hard link can't be created (e.g. the logs dir
        // is on a different/non-NTFS volume).
        private static void UpdateLatestLink(string dir, string linkName, string path)
        {
            try
            {
                string latest = System.IO.Path.Combine(dir, linkName);
                try { File.Delete(latest); } catch { }
                if (!CreateHardLink(latest, path, IntPtr.Zero))
                    File.Copy(path, latest, true);
            }
            catch { /* best-effort - a missing convenience link must never block logging itself */ }
        }
    }
}
