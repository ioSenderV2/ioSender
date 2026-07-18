/*
 * ConsoleLog.cs - part of Grbl Code Sender
 *
 * Always-on mirror of the on-screen Console tab (GrblViewModel.ResponseLog) to
 * %AppData%\ioSender\logs\console_<run-timestamp>.log - a durable record that survives the
 * 2000-line in-memory trim and, more importantly, survives a UI freeze (the very failure mode
 * this was built to diagnose: a firmware debug flood once locked the WPF UI thread for the
 * length of a job, hiding the console from the user exactly when it mattered most).
 *
 * A fresh, timestamped file per run (rather than one ever-growing appended file) keeps each
 * ioSender launch's log independently identifiable - useful when correlating a specific repro
 * attempt against its own log rather than scrolling through prior runs first. "latest_console.log"
 * is (hard-)linked to the current run's file so it never has to be hunted down by timestamp.
 * File creation, size-based rotation and age-based pruning are all handled by LogFile - the same
 * primitive DebugLog and the crash logger use.
 *
 * The write itself runs on a dedicated background thread, fed by a BlockingCollection queue -
 * ResponseLog changes are appended to the UI thread (Dispatcher-marshalled), so the file I/O
 * must never happen inline there, or a burst of traffic could freeze the UI a second way.
 */

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;

namespace CNC.Core
{
    public static class ConsoleLog
    {
        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
        private static LogFile _log;
        private static Thread _writer;

        // Best-effort retention: run-timestamped files never get overwritten (unlike the old single
        // appended console.log), so without pruning they'd accumulate forever.
        private const int RetentionDays = 10;

        /// <summary>Full path of the active log file, or empty if logging couldn't start.</summary>
        public static string LogPath { get { return _log?.Path ?? string.Empty; } }

        /// <summary>Start the background writer. Safe to call once at startup; never throws.</summary>
        public static void Init()
        {
            _log = LogFile.Open("console", perRun: true, retentionDays: RetentionDays, latestLinkName: "latest_console.log");
            if (_log == null)
                return;

            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "ConsoleLogWriter" };
            _writer.Start();

            Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0}\r\n===== ioSender console log - run started {1} =====",
                new string('=', 72),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        /// <summary>Mirror one console line to the log. No-op if Init wasn't called or failed.</summary>
        public static void Write(string line)
        {
            if (_log == null || line == null)
                return;
            Enqueue(string.Format(CultureInfo.InvariantCulture, "{0}  {1}",
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), line));
        }

        private static void Enqueue(string line)
        {
            try { _queue.Add(line); } catch { /* completed/disposed - never on the happy path */ }
        }

        private static void WriterLoop()
        {
            foreach (var line in _queue.GetConsumingEnumerable())
                _log.Write(line + "\r\n");
        }
    }
}
