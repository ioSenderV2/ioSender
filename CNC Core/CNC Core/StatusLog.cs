/*
 * StatusLog.cs - part of Grbl Code Sender
 *
 * Always-on mirror of the Status window (GrblViewModel.MessageLog) to
 * %AppData%\ioSender\logs\status_<run-timestamp>.log, with "latest_status.log" hard-linked to the
 * current run's file - the same scheme ConsoleLog, DebugLog and the crash logger use.
 *
 * WHY THIS EXISTS: the status log was memory-only. GrblViewModel.LogMessage appends to a
 * MessageLog capped at 1200 lines and drops the OLDEST 200 when it overflows, so the very lines
 * that explain how a session got into trouble are the first to go - and nothing survived a
 * restart at all. The console log has been durable for a long time; this closes the gap for the
 * OTHER log, the one carrying the application's own narration and the controller's [MSG:] lines.
 *
 * Each entry records the KIND and SOURCE the status funnel already knows (error/info, and
 * app/firmware for a line that arrived as a controller [MSG:...]), because "which of these did
 * the machine say" is exactly the question this log gets read to answer.
 *
 * Fed from the ONE funnel (GrblViewModel.LogMessage) rather than by subscribing to MessageLog:
 * the funnel is where kind and source are known, and a collection subscription would see neither.
 *
 * The write runs on a dedicated background thread fed by a BlockingCollection, for the same
 * reason ConsoleLog's does - LogMessage is called from the UI thread, and file I/O inline there
 * could freeze the UI during a message burst.
 */

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;

namespace CNC.Core
{
    public static class StatusLog
    {
        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
        private static LogFile _log;
        private static Thread _writer;

        // Matches ConsoleLog's: run-timestamped files never overwrite each other, so without
        // pruning they accumulate forever. Per day-of-week folder (see LogFile), not calendar age.
        private const int RetentionCount = 10;

        /// <summary>Full path of the active log file, or empty if logging couldn't start.</summary>
        public static string LogPath { get { return _log == null ? string.Empty : _log.Path; } }

        /// <summary>Start the background writer. Safe to call once at startup; never throws.</summary>
        public static void Init()
        {
            _log = LogFile.Open("status", retentionCount: RetentionCount, latestLinkName: "latest_status.log");
            if (_log == null)
                return;

            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "StatusLogWriter" };
            _writer.Start();

            Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0}\r\n===== ioSender status log - run started {1} =====",
                new string('=', 72),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Record one status line. <paramref name="kind"/> is "error"/"info" (the wire contract also
        /// carries "warning"/"progress"); <paramref name="source"/> is "app", or "firmware" for a
        /// controller [MSG:...]. No-op if Init wasn't called or failed.
        /// </summary>
        public static void Write(string kind, string source, string text)
        {
            if (_log == null || text == null)
                return;

            // Fixed-width tags so the file stays scannable by eye and greppable by tag - the two
            // ways this log actually gets read.
            Enqueue(string.Format(CultureInfo.InvariantCulture, "{0}  {1,-8} {2,-8} {3}",
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(kind) ? "info" : kind,
                string.IsNullOrEmpty(source) ? "app" : source,
                text));
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
