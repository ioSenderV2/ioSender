/*
 * DemoMarker.cs - part of Grbl Code Sender
 *
 * Opt-in demo-video sync marker. OFF by default; turned on with the "-demomarker"
 * command-line flag (or the IOSENDER_DEMOMARKER env var). When on, it timestamps a
 * few job-state transitions to a small CSV so a video compositor (ffmpeg) can:
 *   - SYNC the app screen-recording track to the machine-camera track, and
 *   - SHRINK the app overlay to the run-control strip at the RUN instant,
 * without any hand-keyframing. See docs/demo-videos/README.md.
 *
 * The file is (re)started fresh on each enabled launch, so one launch == one video's
 * markers. Sibling of DebugLog (app-internal flow trace) and the crash log; this one
 * is a tiny, purpose-built event stream, not a diagnostic log.
 */

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CNC.Core
{
    /// <summary>
    /// Static, thread-safe, opt-in demo-video sync marker. No-op (early bool return)
    /// when disabled. Enable once at startup via <see cref="Init"/>, then call
    /// <see cref="Mark"/> at the transitions of interest.
    /// </summary>
    public static class DemoMarker
    {
        private static readonly object _sync = new object();
        private static string _path;

        /// <summary>True when marking is on. Callers may test this to skip work.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Full path of the active marker file, or empty when disabled.</summary>
        public static string LogPath { get { return _path ?? string.Empty; } }

        /// <summary>
        /// Turn marking on. <paramref name="enabled"/> is normally the result of the "-demomarker"
        /// flag / IOSENDER_DEMOMARKER env test done at startup. Starts a fresh file (header +
        /// SESSION_START) so one launch is one video's markers. Safe to call more than once; never throws.
        /// </summary>
        public static void Init(bool enabled)
        {
            lock (_sync)
            {
                Enabled = enabled;
                if (!enabled)
                    return;

                try
                {
                    string dir = Resources.ConfigPath;
                    if (string.IsNullOrEmpty(dir) || dir == "./" || !Path.IsPathRooted(dir))
                        dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ioSender");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, "ioSender.demo-markers.csv");
                }
                catch
                {
                    try { _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ioSender.demo-markers.csv"); }
                    catch { _path = null; Enabled = false; return; }
                }

                try { File.WriteAllText(_path, "timestamp,event\r\n", Encoding.UTF8); }
                catch { _path = null; Enabled = false; return; }

                Mark("SESSION_START");
            }
        }

        /// <summary>Record a marker row (ISO-8601 timestamp + event name). No-op when disabled.</summary>
        public static void Mark(string @event)
        {
            if (!Enabled)
                return;

            lock (_sync)
            {
                if (_path == null)
                    return;
                try
                {
                    string line = string.Format(CultureInfo.InvariantCulture, "{0},{1}\r\n",
                        DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                        @event);
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
                catch { /* marking must never take the app down */ }
            }
        }
    }
}
