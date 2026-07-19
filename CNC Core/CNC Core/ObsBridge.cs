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

        /// <summary>
        /// One row per OBS Source Record filter that's remote-controllable - the two RTSP cameras (Front
        /// Left / Front Right) plus the App/screen-capture source, all three sources on this rig (see
        /// docs/demo-videos). Addressed by (sourceName, filterName) via obs-websocket's
        /// SetSourceFilterEnabled, NOT by hotkey: Source Record registers the SAME hotkey name
        /// ("source_record.enable"/".disable") for every filter instance, so triggering by name would
        /// fire all three cameras at once - proven live via "-debuglog=obs" 2026-07-18, not a guess.
        /// SourceName/FilterName come from IOSENDER_OBS_{CAMA,CAMB,APP}_{SOURCE,FILTER} env vars
        /// (FILTER defaults to "Source Record", OBS's own default filter name, if unset); an entry with
        /// no configured source name is still listed (so the panel row exists) but toggling it is a no-op.
        /// Each filter's Record Mode must be "Always" for enable/disable to mean anything independent of
        /// the main Record button.
        /// </summary>
        public class CameraInfo
        {
            public string Label;
            public string SourceName;
            public string FilterName;
        }

        public static readonly CameraInfo[] Cameras = new CameraInfo[]
        {
            new CameraInfo { Label = "Front Left", SourceName = Environment.GetEnvironmentVariable("IOSENDER_OBS_CAMA_SOURCE"), FilterName = Environment.GetEnvironmentVariable("IOSENDER_OBS_CAMA_FILTER") ?? "Source Record" },
            new CameraInfo { Label = "Front Right", SourceName = Environment.GetEnvironmentVariable("IOSENDER_OBS_CAMB_SOURCE"), FilterName = Environment.GetEnvironmentVariable("IOSENDER_OBS_CAMB_FILTER") ?? "Source Record" },
            new CameraInfo { Label = "App (screen)", SourceName = Environment.GetEnvironmentVariable("IOSENDER_OBS_APP_SOURCE"), FilterName = Environment.GetEnvironmentVariable("IOSENDER_OBS_APP_FILTER") ?? "Source Record" },
        };

        private static readonly bool[] _cameraRecording = new bool[Cameras.Length];

        /// <summary>Raised after <see cref="SetCameraRecording"/> changes a camera's state - UI panels
        /// (and any other trigger source, e.g. a keyboard hotkey) resync from this, not from each other.</summary>
        public static event System.Action CamerasChanged;

        public static bool IsCameraRecording(int camera)
        {
            return camera >= 0 && camera < _cameraRecording.Length && _cameraRecording[camera];
        }

        /// <summary>Set one camera's recording state - enables/disables its configured Source Record
        /// filter and notifies <see cref="CamerasChanged"/>. The single entry point for both the RTSP
        /// Cameras panel's toggle click and the ObsCam*Start/Stop keyboard shortcuts, so either can drive
        /// the other's on-screen state. No-op if the index is out of range or already at that state.</summary>
        public static void SetCameraRecording(int camera, bool recording)
        {
            if (camera < 0 || camera >= Cameras.Length || _cameraRecording[camera] == recording)
                return;
            var cam = Cameras[camera];
            if (!string.IsNullOrEmpty(cam.SourceName) && !string.IsNullOrEmpty(cam.FilterName))
            {
                string data = "{\"sourceName\":\"" + JsonEscape(cam.SourceName) + "\",\"filterName\":\"" + JsonEscape(cam.FilterName) + "\",\"filterEnabled\":" + (recording ? "true" : "false") + "}";
                SendRequest("SetSourceFilterEnabled", data);
            }
            _cameraRecording[camera] = recording;
            CamerasChanged?.Invoke();
        }

        private static string JsonEscape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
                else if (op == 7 && DebugLog.Enabled)   // RequestResponse - trace so a rejected request is visible
                {
                    DebugLog.Write("obs", "response: " + msg);
                }
            }
            catch { /* never take the app down over a demo bridge */ }
        }

        private static bool SendRequest(string requestType, string requestDataJson = null)
        {
            try
            {
                if (_ws == null || !_ws.IsAlive)
                    return false;
                _reqId++;
                string d = requestDataJson == null
                    ? "{\"op\":6,\"d\":{\"requestType\":\"" + requestType + "\",\"requestId\":\"" + _reqId + "\"}}"
                    : "{\"op\":6,\"d\":{\"requestType\":\"" + requestType + "\",\"requestId\":\"" + _reqId + "\",\"requestData\":" + requestDataJson + "}}";
                _ws.Send(d);
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
