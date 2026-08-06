/*
 * WireLog.cs - part of CNC Core library
 *
 * An UNFILTERED trace of what actually crossed the link, in its own file.
 *
 * Why this exists, specifically: console.log is not a wire trace and reading it as one produces
 * confident, wrong answers. It is a mirror of the on-screen Console tab (GrblViewModel's ResponseLog),
 * so it inherits that view's filter - and realtime status reports only reach it when the parsed state
 * CHANGED. Measured 2026-08-06: 33-91 status lines per minute in console.log against ~290 polls actually
 * sent and answered. Two separate diagnoses were built on that gap and both were wrong: a fictional
 * "poll latency degrades over 11 hours of uptime" (it was really measuring how much the machine was
 * doing), and a claim that a jog's "ok" was arriving but being misrouted (no "ok" was being produced at
 * all). The real fault - grblHAL withholding the ack for a $J sent during Jog state - was only visible
 * once the wire itself was read.
 *
 * The on-screen console SHOULD keep filtering; a line every 200ms makes it unreadable, which is why the
 * filter is there. This just stops the FILE from inheriting a decision made for the window.
 *
 * Off by default in Release, on in Debug - see Init.
 *
 * ---- What it covers, and what it does not ----
 *
 * RX: every reply, tapped in Comms.PostTo on the read thread. That is upstream of SuspendProcessing,
 *     Silent, and the ResponseLog filter, so a reply the app decides to ignore still appears here -
 *     which is exactly the case worth having.
 *
 * TX: commands issued through GrblViewModel (ExecuteCommand / ExecuteMDI). That covers MDI, macros, the
 *     on-screen jog buttons and the gamepad D-pad (ControllerMapper.JogStep deliberately routes through
 *     ExecuteCommand rather than writing to the stream directly).
 *
 * TX gaps, stated plainly rather than discovered later: raw Comms.WriteByte realtime bytes ('?', feed
 * hold, cycle start, jog cancel 0x85) and StreamPump's own writes during a job do NOT appear. Both go
 * straight to the stream. Closing that means tapping WriteString/WriteByte in each stream class - a
 * follow-up, deliberately not done in the same change as this, because it edits the write path of the
 * code that talks to the machine.
 *
 * Also note the tap point bounds the truth: a reply mangled during the stream's own reply extraction is
 * already mangled by the time it reaches PostTo. Only a raw-byte tap would show that, and this is not one.
 */

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;

namespace CNC.Core
{
    public static class WireLog
    {
        // Status reports alone run ~290/minute, roughly 1.5 MB/hour. LogFile's 8 MB default would rotate
        // every ~5 hours and keep only one previous file, so a long session would lose its own beginning -
        // and the beginning is where a fault's first occurrence lives. 64 MB holds a full day.
        private const long MaxBytes = 64 * 1024 * 1024;
        private const int RetentionCount = 5;

        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
        private static LogFile _log;
        private static Thread _writer;

        /// <summary>True when tracing is on. Callers test this to skip building a line.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Full path of the active log file, or empty when disabled.</summary>
        public static string LogPath { get { return _log?.Path ?? string.Empty; } }

        /// <summary>
        /// Start the trace. <paramref name="enabled"/> is normally the "-wirelog" flag. Safe to call once at
        /// startup; never throws.
        /// </summary>
        public static void Init(bool enabled)
        {
#if DEBUG
            // On for every development session, no flag needed - same reasoning as PollDiag: a fault you
            // only notice occasionally is one you will not have had tracing armed for. Release stays opt-in,
            // so an end user's normal run writes nothing and console.log is unaffected either way.
            enabled = true;
#endif
            Enabled = enabled;
            if (!enabled)
                return;

            _log = LogFile.Open("wire", maxBytes: MaxBytes, retentionCount: RetentionCount,
                                latestLinkName: "latest_wire.log");
            if (_log == null)
            {
                Enabled = false;
                return;
            }

            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "WireLogWriter" };
            _writer.Start();

            Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0}\r\n===== ioSender wire log - run started {1} =====\r\n" +
                "===== '<' = received, '>' = sent. UNFILTERED on the RX side; see WireLog.cs for TX gaps. =====",
                new string('=', 72),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        /// <summary>A reply came off the link. Called on the read thread - keep it cheap.</summary>
        public static void Rx(string reply)
        {
            if (Enabled && reply != null)
                Write('<', reply);
        }

        /// <summary>A command was issued to the controller.</summary>
        public static void Tx(string command)
        {
            if (Enabled && !string.IsNullOrEmpty(command))
                Write('>', command);
        }

        /// <summary>
        /// One byte went out - a realtime command. Named where the name is known, because "0x85" in a log
        /// is a lookup and "JOG_CANCEL" is an answer.
        /// </summary>
        public static void TxByte(byte data)
        {
            if (!Enabled)
                return;

            string name = RealtimeName(data);
            Write('>', name != null
                        ? string.Format(CultureInfo.InvariantCulture, "[0x{0:X2} {1}]", data, name)
                        : string.Format(CultureInfo.InvariantCulture, "[0x{0:X2} '{1}']", data,
                                        data >= 0x20 && data < 0x7F ? ((char)data).ToString() : "."));
        }

        /// <summary>A byte range went out - g-code lines, $ commands, YModem payload.</summary>
        public static void TxBytes(byte[] bytes, int len)
        {
            if (!Enabled || bytes == null || len <= 0)
                return;

            string text;
            try { text = System.Text.Encoding.Default.GetString(bytes, 0, System.Math.Min(len, bytes.Length)); }
            catch { text = "<" + len + " bytes>"; }

            Write('>', text.Replace("\r", "\\r").Replace("\n", "\\n"));
        }

        private static string RealtimeName(byte data)
        {
            switch (data)
            {
                case 0x18: return "RESET";
                case 0x21: return "FEED_HOLD";
                case 0x3F: return "STATUS";
                case 0x7E: return "CYCLE_START";
                case 0x84: return "SAFETY_DOOR";
                case 0x85: return "JOG_CANCEL";
                case 0x87: return "STATUS_ALL";
                case 0x88: return "OPTIONAL_STOP";
                case 0x8A: return "TOOL_ACK";
                default: return null;
            }
        }

        private static void Write(char direction, string text)
        {
            Enqueue(string.Format(CultureInfo.InvariantCulture, "{0}  {1} {2}",
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                direction,
                text.TrimEnd('\r', '\n')));
        }

        // Enqueue only - the file write happens on the writer thread below, so neither the comms read
        // thread nor the UI thread ever waits on disk. Same shape as ConsoleLog, and the reason the volume
        // this produces is affordable at all.
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
