/*
 * DebugLog.cs - part of Grbl Code Sender
 *
 * Lightweight, opt-in diagnostic trace log. OFF by default; turned on with the
 * "-debuglog" command-line flag (or the IOSENDER_DEBUGLOG env var) so normal runs
 * write nothing and pay next to nothing (a single bool test per call site).
 *
 * The point is to be able to drop CNC.Core.DebugLog.Write(...) anywhere - in
 * CNC.Core, CNC.Controls or the app - and get a timestamped flow trace on the next
 * flagged run, WITHOUT hand-rolling a throwaway logger and tearing it down again.
 *
 * Companion to the crash log (App.xaml.cs) and the serial-wire Console verbose log;
 * this one is for app-internal state/flow, not the wire protocol. File creation and size-based
 * rotation are handled by LogFile - the same primitive ConsoleLog and the crash logger use; this
 * class just owns the enable flag, the category filter, and line formatting.
 */

using System;
using System.Globalization;

namespace CNC.Core
{
    /// <summary>
    /// Static, thread-safe, opt-in diagnostic trace log. No-op (early bool return)
    /// when disabled. Enable once at startup via <see cref="Init"/>.
    /// </summary>
    public static class DebugLog
    {
        private static readonly object _sync = new object();
        private static LogFile _log;
        private static System.Collections.Generic.HashSet<string> _categories; // null = all categories

        /// <summary>True when tracing is on. Callers may test this to skip building an expensive message.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Full path of the active log file, or empty when disabled.</summary>
        public static string LogPath { get { return _log?.Path ?? string.Empty; } }

        /// <summary>
        /// Turn tracing on. <paramref name="enabled"/> is normally the result of the "-debuglog" flag /
        /// IOSENDER_DEBUGLOG env test done at startup. <paramref name="categories"/> is an optional
        /// comma-separated allow-list (e.g. "settings,comms"); null/empty means log every category.
        /// Safe to call more than once (last call wins); never throws.
        /// </summary>
        public static void Init(bool enabled, string categories = null)
        {
            lock (_sync)
            {
                Enabled = enabled;
                if (!enabled)
                    return;

                if (string.IsNullOrWhiteSpace(categories))
                    _categories = null;
                else
                {
                    _categories = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in categories.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        _categories.Add(c.Trim());
                }

                _log = LogFile.Open("ioSender.debug");
                if (_log == null)
                {
                    Enabled = false;
                    return;
                }

                // Run-start banner so multiple runs appended to the same file stay separable.
                string ver = "?";
                try { ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?"; }
                catch { }

                WriteRaw(string.Format(CultureInfo.InvariantCulture,
                    "{0}\r\n===== ioSender debug log - run started {1} (v{2}){3} =====\r\n",
                    new string('=', 72),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ver,
                    _categories == null ? "" : "  categories=[" + string.Join(",", _categories) + "]"));
            }
        }

        /// <summary>Log a line (no category). No-op when disabled.</summary>
        public static void Write(string message)
        {
            if (!Enabled)
                return;
            Emit(null, message);
        }

        /// <summary>
        /// Log a line under a category (e.g. "settings"). No-op when disabled, or when a category
        /// allow-list is set and does not include this one.
        /// </summary>
        public static void Write(string category, string message)
        {
            if (!Enabled)
                return;
            if (_categories != null && category != null && !_categories.Contains(category))
                return;
            Emit(category, message);
        }

        private static void Emit(string category, string message)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "{0}  {1}{2}\r\n",
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(category) ? "" : "[" + category + "]  ",
                message);
            WriteRaw(line);
        }

        private static void WriteRaw(string text)
        {
            lock (_sync)
            {
                _log?.Write(text);
            }
        }
    }
}
