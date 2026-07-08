/*
 * ObsBridge.cs - part of Grbl Code Sender
 *
 * Opt-in OBS Studio recording control over obs-websocket (v5). Turned on together
 * with the demo-marker facility (-demomarker) so a demo shoot can auto-record:
 * ioSender starts OBS recording when a program is loaded and stops it when the
 * program ends - no need to touch OBS during the take. See docs/demo-videos.
 *
 * obs-websocket is OBS's built-in WebSocket server (Tools -> WebSocket Server
 * Settings). Auth is optional; if enabled, the password comes from the
 * IOSENDER_OBSWS_PASSWORD env var. Host/port default to localhost:4455.
 *
 * Everything here is best-effort and never throws: if OBS isn't running, the
 * server isn't enabled, or the password is wrong, the bridge simply no-ops.
 * Reuses the websocket-sharp dependency already used by WebsocketStream.
 */

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WebSocketSharp;

namespace CNC.Core
{
    /// <summary>
    /// Static, opt-in OBS recording controller over obs-websocket v5. No-op unless
    /// enabled via <see cref="Init"/> and an OBS WebSocket server is reachable.
    /// </summary>
    public static class ObsBridge
    {
        private static readonly object _sync = new object();
        private static WebSocket _ws;
        private static string _password;
        private static volatile bool _enabled;
        private static volatile bool _identified;   // completed the obs-websocket handshake
        private static bool _recording;             // our view of the record state (guarded by _sync)
        private static int _reqId;

        /// <summary>True when the bridge is armed (does not imply OBS is connected).</summary>
        public static bool Enabled { get { return _enabled; } }

        /// <summary>
        /// Arm the bridge and start connecting (non-blocking). <paramref name="password"/> is only
        /// needed if OBS has authentication enabled; null/empty is fine when auth is off. Safe to call
        /// more than once; never throws.
        /// </summary>
        public static void Init(bool enabled, string host = "localhost", int port = 4455, string password = null)
        {
            _enabled = enabled;
            _password = password;
            if (!enabled)
                return;

            try
            {
                _ws = new WebSocket(string.Format("ws://{0}:{1}", host, port));
                _ws.OnMessage += OnMessage;
                _ws.OnClose += (s, e) => { _identified = false; };
                _ws.ConnectAsync();   // non-blocking: don't stall startup if OBS is down
                DemoMarker.Mark("OBS_CONNECTING");
            }
            catch
            {
                _enabled = false;
            }
        }

        /// <summary>Start OBS recording (idempotent). No-op unless connected and not already recording.</summary>
        public static void StartRecording()
        {
            lock (_sync)
            {
                if (!_enabled || !_identified || _recording)
                    return;
                if (SendRequest("StartRecord"))
                {
                    _recording = true;
                    DemoMarker.Mark("OBS_RECORD_START");
                }
            }
        }

        /// <summary>Stop OBS recording (idempotent). No-op unless connected and currently recording.</summary>
        public static void StopRecording()
        {
            lock (_sync)
            {
                if (!_enabled || !_identified || !_recording)
                    return;
                if (SendRequest("StopRecord"))
                {
                    _recording = false;
                    DemoMarker.Mark("OBS_RECORD_STOP");
                }
            }
        }

        // ---- obs-websocket v5 protocol ----

        private static void OnMessage(object sender, MessageEventArgs e)
        {
            if (!e.IsText)
                return;
            try
            {
                string msg = e.Data;
                int op = ExtractInt(msg, "op");

                if (op == 0)   // Hello -> reply with Identify (with auth if the server challenged us)
                {
                    string challenge = ExtractString(msg, "challenge");
                    string salt = ExtractString(msg, "salt");
                    string identify;
                    if (!string.IsNullOrEmpty(challenge) && !string.IsNullOrEmpty(salt))
                    {
                        string auth = ComputeAuth(_password ?? string.Empty, salt, challenge);
                        identify = "{\"op\":1,\"d\":{\"rpcVersion\":1,\"eventSubscriptions\":0,\"authentication\":\"" + auth + "\"}}";
                    }
                    else
                        identify = "{\"op\":1,\"d\":{\"rpcVersion\":1,\"eventSubscriptions\":0}}";
                    _ws.Send(identify);
                }
                else if (op == 2)   // Identified - handshake complete
                {
                    _identified = true;
                    DemoMarker.Mark("OBS_CONNECTED");
                }
            }
            catch { /* never take the app down over a demo bridge */ }
        }

        private static bool SendRequest(string requestType)
        {
            try
            {
                if (_ws == null || !_ws.IsAlive)
                    return false;
                _reqId++;
                _ws.Send("{\"op\":6,\"d\":{\"requestType\":\"" + requestType + "\",\"requestId\":\"" + _reqId + "\"}}");
                return true;
            }
            catch { return false; }
        }

        // authentication = base64( sha256( base64(sha256(password + salt)) + challenge ) )
        private static string ComputeAuth(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        private static int ExtractInt(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private static string ExtractString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
