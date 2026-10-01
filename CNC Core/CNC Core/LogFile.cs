/*
 * LogFile.cs - part of Grbl Code Sender
 *
 * Shared low-level file-logging primitive for the debug, console and crash logs. Each run gets a
 * fresh, timestamped file created via RotatingFileStore (day-of-week subfolder under the logs
 * directory, per-folder retention, "latest_<name>.log" hard-linked into the top-level logs
 * folder) - this class just owns the write-append + size-based rotation on top of that file.
 *
 * ConsoleLog, DebugLog and the crash logger (App.xaml.cs) all call through this one instead of
 * reimplementing file creation and rotation three times over.
 */

using System.IO;
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
        /// Create a fresh, timestamped log file for this run (see RotatingFileStore for the folder/retention
        /// scheme). Never throws - returns null on failure, which callers treat as "logging disabled" the
        /// same way a caught exception used to.
        /// </summary>
        /// <param name="baseName">File name with no extension/timestamp, e.g. "console" or "ioSender.debug".</param>
        /// <param name="maxBytes">Size cap before rotating the current file to ".1" (previous ".1" discarded). 0 = no cap.</param>
        /// <param name="retentionCount">Max files for this baseName kept per day-of-week folder. 0 = don't prune.</param>
        /// <param name="latestLinkName">If set, this name is (hard-)linked - in the top-level logs folder, not the
        /// day subfolder - to the fresh file, so it always resolves to the current run's log.</param>
        public static LogFile Open(string baseName, long maxBytes = 8 * 1024 * 1024, int retentionCount = 10, string latestLinkName = null)
        {
            try
            {
                string logsRoot = Resources.ResolveLogsDirectory();
                string dayDir = RotatingFileStore.PrepareDayDirectory(logsRoot, baseName, retentionCount);

                string path = System.IO.Path.Combine(dayDir, baseName + "_" + RotatingFileStore.Stamp() + ".log");
                File.WriteAllText(path, string.Empty, Encoding.UTF8); // create it now so a latest-link below has a real target

                if (latestLinkName != null)
                    RotatingFileStore.UpdateLatestLink(logsRoot, latestLinkName, path);

                return new LogFile(path, maxBytes);
            }
            catch { return null; }
        }

        /// <summary>
        /// Append text (caller supplies its own line ending), rotating first if oversize. Never throws.
        /// Returns true if the text reached the file.
        /// <para>
        /// The return value exists because a swallowed failure here is indistinguishable from "nothing
        /// happened": Open() creates the file eagerly (so a latest-link has a target), which means a Write
        /// that fails leaves a 3-byte, BOM-only file that reads exactly like a log with nothing to say.
        /// That is precisely what an OutOfMemoryException produced on 2026-08-06 - AppendAllText needs a
        /// StreamWriter and an encoder buffer, and under OOM it cannot have them. Callers who care (the
        /// crash logger) check the result and retry; the high-frequency callers ignore it as before.
        /// </para>
        /// </summary>
        public bool Write(string text)
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
                    return true;
                }
                catch { /* logging must never take the app down */ }
            }
            return false;
        }
    }
}
