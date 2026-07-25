/*
 * ObsBridge.cs - part of Grbl Code Sender
 *
 * Opt-in OBS Studio recording control over obs-websocket (v5). Turned on together
 * with the demo-marker facility (-demomarker) so a demo shoot can auto-record:
 * ioSender starts OBS recording when a program is loaded and stops it when the
 * program ends - no need to touch OBS during the take. See docs/demo-videos.
 *
 * obs-websocket is OBS's built-in WebSocket server (Tools -> WebSocket Server
 * Settings). Auth is optional; if enabled, the password/host/port/camera source
 * names all come from Settings > App > Camera > "Demo recording (OBS)" (pushed
 * in via Init/ConfigureCameras at startup - this class itself reads no env vars).
 *
 * Everything here is best-effort and never throws: if OBS isn't running, the
 * server isn't enabled, or the password is wrong, the bridge simply no-ops.
 * Reuses the websocket-sharp dependency already used by WebsocketStream.
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
        /// SourceName/FilterName come from AppConfig.Settings.Base.Camera's Obs* fields (Settings > App >
        /// Camera > "Demo recording (OBS)") via <see cref="ConfigureCameras"/> - CNC Core can't reference
        /// AppConfig directly (CNC Controls sits above it), so App.xaml.cs reads the settings and pushes
        /// them in during startup, the same pattern Init's host/port/password already use. An entry with
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

        public static CameraInfo[] Cameras { get; private set; } = new CameraInfo[]
        {
            new CameraInfo { Label = "Front Left" },
            new CameraInfo { Label = "Front Right" },
            new CameraInfo { Label = "App (screen)" },
        };

        /// <summary>Populate the three cameras' source/filter names - call once during startup before any
        /// UI reads <see cref="Cameras"/>. Never throws; a null/empty source just leaves that camera
        /// inert (existing behavior, matches the old unset-env-var case).</summary>
        public static void ConfigureCameras(string camASource, string camAFilter, string camBSource, string camBFilter, string appSource, string appFilter)
        {
            Cameras = new CameraInfo[]
            {
                new CameraInfo { Label = "Front Left", SourceName = camASource, FilterName = string.IsNullOrEmpty(camAFilter) ? "Source Record" : camAFilter },
                new CameraInfo { Label = "Front Right", SourceName = camBSource, FilterName = string.IsNullOrEmpty(camBFilter) ? "Source Record" : camBFilter },
                new CameraInfo { Label = "App (screen)", SourceName = appSource, FilterName = string.IsNullOrEmpty(appFilter) ? "Source Record" : appFilter },
            };
        }

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

        /// <summary>
        /// Synchronous, one-shot connectivity check for the Settings > App > Camera panel's "Validate"
        /// button - opens its own short-lived connection (does NOT touch the shared <see cref="Init"/>
        /// connection, so validating doesn't disturb an already-armed demo-recording bridge), authenticates
        /// if a password is given, and asks OBS for both its current source (input) list AND its scene
        /// list - "App (screen)" is commonly a whole Scene (containing the window/display capture), not a
        /// plain input, and a Source Record filter can be applied to either, so both name spaces need to be
        /// checked or a scene-based App capture always reports "NOT FOUND". Blocks the calling thread up to
        /// ~4s per step; callers should wrap this in a WaitCursor.
        /// </summary>
        public static bool ValidateConnection(string host, int port, string password, out string error, out List<string> sourceNames)
        {
            var names = new List<string>();
            string localError = null;
            bool inputsOk = false;
            string inputsReqId = Guid.NewGuid().ToString("N");
            string scenesReqId = Guid.NewGuid().ToString("N");
            var identified = new ManualResetEventSlim(false);
            var gotInputs = new ManualResetEventSlim(false);
            var gotScenes = new ManualResetEventSlim(false);
            WebSocket ws = null;

            try
            {
                ws = new WebSocket(string.Format("ws://{0}:{1}", host, port));
                ws.OnMessage += (s, e) =>
                {
                    if (!e.IsText)
                        return;
                    try
                    {
                        string msg = e.Data;
                        int op = ExtractInt(msg, "op");
                        if (op == 0)
                        {
                            string challenge = ExtractString(msg, "challenge");
                            string salt = ExtractString(msg, "salt");
                            string identify;
                            if (!string.IsNullOrEmpty(challenge) && !string.IsNullOrEmpty(salt))
                            {
                                string auth = ComputeAuth(password ?? string.Empty, salt, challenge);
                                identify = "{\"op\":1,\"d\":{\"rpcVersion\":1,\"eventSubscriptions\":0,\"authentication\":\"" + auth + "\"}}";
                            }
                            else
                                identify = "{\"op\":1,\"d\":{\"rpcVersion\":1,\"eventSubscriptions\":0}}";
                            ws.Send(identify);
                        }
                        else if (op == 2)
                        {
                            identified.Set();
                            ws.Send("{\"op\":6,\"d\":{\"requestType\":\"GetInputList\",\"requestId\":\"" + inputsReqId + "\"}}");
                        }
                        else if (op == 7)
                        {
                            string rid = ExtractString(msg, "requestId");
                            if (rid == inputsReqId)
                            {
                                var resultMatch = Regex.Match(msg, "\"result\"\\s*:\\s*(true|false)");
                                inputsOk = resultMatch.Success && resultMatch.Groups[1].Value == "true";
                                if (inputsOk)
                                {
                                    foreach (Match m in Regex.Matches(msg, "\"inputName\"\\s*:\\s*\"([^\"]*)\""))
                                        names.Add(m.Groups[1].Value);
                                }
                                else
                                    localError = ExtractString(msg, "comment") ?? "OBS rejected the request.";
                                gotInputs.Set();
                                // Ask for scenes too, regardless of whether the inputs request succeeded -
                                // best-effort, doesn't gate overall success/failure on its own.
                                ws.Send("{\"op\":6,\"d\":{\"requestType\":\"GetSceneList\",\"requestId\":\"" + scenesReqId + "\"}}");
                            }
                            else if (rid == scenesReqId)
                            {
                                foreach (Match m in Regex.Matches(msg, "\"sceneName\"\\s*:\\s*\"([^\"]*)\""))
                                {
                                    if (!names.Contains(m.Groups[1].Value))
                                        names.Add(m.Groups[1].Value);
                                }
                                gotScenes.Set();
                            }
                        }
                    }
                    catch { /* leave localError/inputsOk at their current values */ }
                };
                ws.OnError += (s, e) => { localError = localError ?? e.Message; identified.Set(); gotInputs.Set(); gotScenes.Set(); };
                ws.OnClose += (s, e) => { identified.Set(); gotInputs.Set(); gotScenes.Set(); };

                ws.Connect();   // blocking - throws on refused/unreachable connection

                if (!identified.Wait(4000))
                {
                    error = localError ?? string.Format("Could not connect/authenticate to OBS at ws://{0}:{1} within 4s - check the WebSocket server is running and the password (if any) is correct.", host, port);
                    sourceNames = names;
                    return false;
                }
                if (!gotInputs.Wait(4000) || !inputsOk)
                {
                    error = localError ?? "Connected, but OBS did not return its source list in time.";
                    sourceNames = names;
                    return false;
                }
                gotScenes.Wait(4000);   // best-effort - a missed/slow scene list still leaves the input names we already have

                error = null;
                sourceNames = names;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                sourceNames = names;
                return false;
            }
            finally
            {
                try { ws?.Close(); } catch { /* best-effort teardown of a one-shot validation socket */ }
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
