/*
 * ConsoleLog.cs - part of Grbl Code Sender
 *
 * Always-on mirror of the on-screen Console tab (GrblViewModel.ResponseLog) to
 * %AppData%\ioSender\logs\console.log - a durable record that survives the 2000-line
 * in-memory trim and, more importantly, survives a UI freeze (the very failure mode this was
 * built to diagnose: a firmware debug flood once locked the WPF UI thread for the length of a
 * job, hiding the console from the user exactly when it mattered most).
 *
 * The write itself runs on a dedicated background thread, fed by a BlockingCollection queue -
 * ResponseLog changes are appended to the UI thread (Dispatcher-marshalled), so the file I/O
 * must never happen inline there, or a burst of traffic could freeze the UI a second way.
 */

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CNC.Core
{
    public static class ConsoleLog
    {
        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
        private static string _path;
        private static Thread _writer;

        // Guard against a single unbounded file across long sessions: when the log passes this
        // size it is rolled to ".1" (previous ".1" discarded) and a fresh file started.
        private const long MaxBytes = 8 * 1024 * 1024;

        /// <summary>Full path of the active log file, or empty if logging couldn't start.</summary>
        public static string LogPath { get { return _path ?? string.Empty; } }

        /// <summary>Start the background writer. Safe to call once at startup; never throws.</summary>
        public static void Init()
        {
            try
            {
                _path = Path.Combine(Resources.ResolveLogsDirectory(), "console.log");
            }
            catch { _path = null; return; }

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
            if (_path == null || line == null)
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
            {
                try
                {
                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string bak = _path + ".1";
                        if (File.Exists(bak))
                            File.Delete(bak);
                        File.Move(_path, bak);
                    }
                    File.AppendAllText(_path, line + "\r\n", Encoding.UTF8);
                }
                catch { /* logging must never take the app down */ }
            }
        }
    }
}
