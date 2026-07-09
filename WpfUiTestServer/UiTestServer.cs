/*
 * UiTestServer.cs - part of Grbl Code Sender
 *
 * A flag-gated, in-process localhost HTTP server that lets an external script (e.g. Claude Code) drive the
 * WPF UI by x:Uid and read state back, so a change can be self-verified end to end instead of always being
 * handed off for a human to click. It exists ONLY when launched with -testserver / IOSENDER_TESTSERVER, so a
 * normal user run never opens a socket.
 *
 * Addressing: localization (LocBaml) put a unique x:Uid on ~every interactive control, and WPF surfaces that
 * at runtime as UIElement.Uid - a ready-made, stable addressing scheme for the whole UI. Every request is
 * marshalled onto the Dispatcher, the element is found by walking the visual tree of the open windows, and the
 * action is performed through its AutomationPeer (the same machinery real UI-automation uses) so it respects
 * IsEnabled and fires the real handlers regardless of where the window sits on screen.
 *
 * Transport is a raw TcpListener bound to 127.0.0.1 (loopback), NOT HttpListener: a specific-IP HttpListener
 * prefix still needs a netsh urlacl reservation or admin, which would break unattended launches; a loopback
 * TCP socket needs no privilege. We speak just enough HTTP/1.1 (request line + headers + optional body,
 * Connection: close per request) for curl / Invoke-WebRequest style clients.
 *
 * Routes (all return JSON):
 *   GET  /ping             liveness -> {"ok":true,...}
 *   GET  /idle             block until the Dispatcher has drained to background priority, then ok
 *   GET  /tree             every realized element that carries an x:Uid, across all open windows
 *   GET  /state/{uid}      one element: uid, name, type, enabled, visible, value/text
 *   POST /invoke/{uid}     invoke the element (button click) via its Invoke automation pattern
 *   POST /set/{uid}?value= set the element's value: ValuePattern text, or Toggle for checkboxes/toggles
 *
 * KNOWN LIMITATION (the genuinely hard part per the design): only *realized* elements are visible to the
 * visual-tree walk. Content on a not-yet-selected TabItem is created lazily, so /tree won't list it until that
 * tab has been shown once. Synchronization/settle + readback is where the real design work lives; /idle is the
 * seed of the answer, not the whole of it.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfUiTestServer
{
    // App-agnostic, flag-gated in-process HTTP server that drives a WPF UI by x:Uid via AutomationPeers so an
    // external script can exercise it end to end and read state back. It knows nothing about any particular app;
    // the host supplies domain state through IUiTestStatusProvider (see Start) and, optionally, a log sink.
    public static class UiTestServer
    {
        private static TcpListener _listener;
        private static Thread _thread;
        private static Window _main;
        private static int _port;
        private static IUiTestStatusProvider _status;
        private static Action<string> _log = _ => { };

        public const int DefaultPort = 8760;

        // Start the server on the loopback interface. Safe to call once; a second call is ignored. Never throws
        // out to the caller - a bind failure is logged and the app continues normally (the flag is diagnostic).
        //   main           - the window whose visual tree (plus any other open windows) is addressed.
        //   port           - <=0 uses DefaultPort.
        //   statusProvider - optional host/domain state for /status and /waitfor?status= (null => those are empty).
        //   log            - optional diagnostic sink (null => no-op); the host can route it to its own logging.
        public static void Start(Window main, int port, IUiTestStatusProvider statusProvider = null, Action<string> log = null)
        {
            if (_listener != null)
                return;

            _main = main;
            _port = port <= 0 ? DefaultPort : port;
            _status = statusProvider;
            if (log != null) _log = log;

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
            }
            catch (Exception ex)
            {
                _listener = null;
                _log("failed to bind 127.0.0.1:" + _port + " - " + ex.Message);
                return;
            }

            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "UiTestServer" };
            _thread.Start();

            InstallBanner(main);   // visible "hands off" bar so a watching operator knows automation is live

            _log("listening on http://127.0.0.1:" + _port + "/");
        }

        // A persistent, gently-pulsing red bar docked across the very top of the window (above the menu) while
        // the test server is running, so an operator watching the machine knows automated input is live and
        // won't fight the harness for the mouse/keyboard. Docked (not overlaid) so it reflows cleanly and
        // follows move/resize/minimize; best-effort - a layout we don't recognise just skips the bar.
        private static void InstallBanner(Window main)
        {
            try
            {
                var dock = main.Content as DockPanel;
                if (dock == null)
                    return;

                var banner = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                    Padding = new Thickness(8, 3, 8, 3),
                    Child = new TextBlock
                    {
                        Text = "●  UNDER TEST-SERVER CONTROL — automated input is live on 127.0.0.1:" + _port +
                               ".  Please don't use the mouse or keyboard.",
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    }
                };
                DockPanel.SetDock(banner, Dock.Top);
                dock.Children.Insert(0, banner);

                var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.6,
                    new Duration(TimeSpan.FromSeconds(0.9)))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                banner.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            catch { /* the banner is cosmetic - never let it break server startup */ }
        }

        private static void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; }   // listener stopped / app shutting down

                try { HandleConnection(client); }
                catch (Exception ex) { _log("handler error: " + ex.Message); }
                finally { try { client.Close(); } catch { } }
            }
        }

        // ---- minimal HTTP/1.1 ----------------------------------------------------------------------------

        private static void HandleConnection(TcpClient client)
        {
            using (var stream = client.GetStream())
            {
                string method, path, query, body;
                if (!ReadRequest(stream, out method, out path, out query, out body))
                    return;

                string json;
                int status;
                try { json = Route(method, path, query, body, out status); }
                catch (Exception ex) { status = 500; json = Err("route threw: " + ex.Message); }

                WriteResponse(stream, status, json);
            }
        }

        // Read the request line + headers (until CRLFCRLF), then Content-Length bytes of body. Loopback +
        // Connection: close, so we do not support pipelining or chunked bodies - none of our clients need them.
        private static bool ReadRequest(NetworkStream stream, out string method, out string path, out string query, out string body)
        {
            method = path = query = body = "";

            var header = new MemoryStream();
            int b, matched = 0;
            // read byte-by-byte until we see \r\n\r\n (matched==4). Fine for tiny request headers.
            while ((b = stream.ReadByte()) != -1)
            {
                header.WriteByte((byte)b);
                if ((matched == 0 || matched == 2) && b == '\r') matched++;
                else if ((matched == 1 || matched == 3) && b == '\n') matched++;
                else matched = 0;
                if (matched == 4) break;
                if (header.Length > 64 * 1024) return false;   // runaway guard
            }
            if (header.Length == 0) return false;

            string head = Encoding.ASCII.GetString(header.ToArray());
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] req = lines[0].Split(' ');
            if (req.Length < 2) return false;

            method = req[0].ToUpperInvariant();
            string rawUrl = req[1];
            int q = rawUrl.IndexOf('?');
            if (q >= 0) { path = rawUrl.Substring(0, q); query = rawUrl.Substring(q + 1); }
            else path = rawUrl;

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                if (lines[i].Substring(0, c).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(lines[i].Substring(c + 1).Trim(), out contentLength);
            }

            if (contentLength > 0)
            {
                var buf = new byte[contentLength];
                int got = 0;
                while (got < contentLength)
                {
                    int n = stream.Read(buf, got, contentLength - got);
                    if (n <= 0) break;
                    got += n;
                }
                body = Encoding.UTF8.GetString(buf, 0, got);
            }
            return true;
        }

        private static void WriteResponse(NetworkStream stream, int status, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json ?? "");
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : status == 400 ? "Bad Request" : "Error";
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        // ---- routing -------------------------------------------------------------------------------------

        private static string Route(string method, string path, string query, string body, out int status)
        {
            status = 200;
            string[] seg = path.Trim('/').Split('/');
            string head = seg.Length > 0 ? seg[0].ToLowerInvariant() : "";
            string arg = seg.Length > 1 ? Uri.UnescapeDataString(seg[1]) : null;
            int index = ParseInt(QueryValue(query, "index"), 0);   // disambiguate duplicate uids (0 = first)

            switch (head)
            {
                case "":
                case "ping":
                    return "{\"ok\":true,\"server\":\"ioSender UiTestServer\",\"port\":" + _port + "}";

                case "idle":
                    WaitForIdle();
                    return "{\"ok\":true,\"idle\":true}";

                case "status":
                    return OnDispatcher(BuildStatus);

                case "waitfor":
                    return DoWaitFor(query);

                case "tree":
                    return OnDispatcher(() => BuildTree());

                case "uids":
                    return OnDispatcher(BuildUids);

                case "state":
                    if (string.IsNullOrEmpty(arg)) { status = 400; return Err("state requires /state/{uid}"); }
                    return OnDispatcher(() =>
                    {
                        var el = FindByUid(arg, index);
                        if (el == null) return NotFound(arg);
                        return "{\"ok\":true,\"element\":" + DescribeElement(el, arg, index) + "}";
                    });

                case "invoke":
                    if (string.IsNullOrEmpty(arg)) { status = 400; return Err("invoke requires /invoke/{uid}"); }
                    return OnDispatcher(() => DoInvoke(arg, index));

                case "set":
                    if (string.IsNullOrEmpty(arg)) { status = 400; return Err("set requires /set/{uid}?value=..."); }
                    return OnDispatcher(() => DoSet(arg, QueryValue(query, "value"), index));

                default:
                    status = 404;
                    return Err("unknown route: /" + head);
            }
        }

        // Route bodies that touch WPF elements run here, synchronously marshalled onto the UI thread.
        private static string OnDispatcher(Func<string> action)
        {
            return (string)_main.Dispatcher.Invoke(action);
        }

        // Host/domain state for assertions - connection, controller state, streaming/job, loaded file, DRO -
        // supplied by the host's IUiTestStatusProvider. These are the things a test asserts on but that no
        // single x:Uid exposes. Read on the UI thread (the provider may touch view-models). Empty if the host
        // registered no provider.
        private static List<KeyValuePair<string, string>> StatusMap()
        {
            var list = new List<KeyValuePair<string, string>>();
            if (_status == null) return list;
            var pairs = _status.GetStatus();
            if (pairs != null)
                foreach (var kv in pairs)
                    list.Add(kv);
            return list;
        }

        // A single field by name (case-insensitive), or null if the host doesn't expose it - so /waitfor can
        // report a clean error rather than silently never matching.
        private static string StatusField(string name)
        {
            foreach (var kv in StatusMap())
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        private static string BuildStatus()
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"status\":{");
            bool first = true;
            foreach (var kv in StatusMap())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Str(kv.Key)).Append(':').Append(Str(kv.Value));
            }
            sb.Append("}}");
            return sb.ToString();
        }

        // Drain the Dispatcher queue down to Background priority - i.e. layout/render and queued app work have
        // run. This is the seed of "has the UI settled after my action"; it does NOT know about async I/O such
        // as an in-flight connection or a running job (that readback is future work).
        private static void WaitForIdle()
        {
            _main.Dispatcher.Invoke(new System.Action(() => { }), DispatcherPriority.Background);
        }

        // Block until a condition holds or a timeout elapses, POLLING from this background handler thread so the
        // UI thread keeps running (a connect completes, a job streams, a tab realizes) while we wait. This is the
        // real answer to "has the UI settled after my action" that /idle only gestures at. Each poll marshals a
        // tiny read onto the UI thread; between polls we Thread.Sleep here, off the UI thread.
        //
        //   GET /waitfor?uid=btn_reset&enabled=true[&timeout=5000][&poll=100]
        //   GET /waitfor?uid=dlgFoo&exists=true          (element appears)  / exists=false (goes away)
        //   GET /waitfor?uid=lblStatus&value=Idle
        //   GET /waitfor?status=connected&equals=true    (app-level state, see /status field names)
        //   GET /waitfor?status=state&equals=Idle
        private static string DoWaitFor(string query)
        {
            int timeout = ClampInt(ParseInt(QueryValue(query, "timeout"), 5000), 0, 120000);
            int poll = ClampInt(ParseInt(QueryValue(query, "poll"), 100), 20, 5000);

            string statusField = QueryValue(query, "status");
            string uid = QueryValue(query, "uid");

            Func<string> observe;
            string expected, cond;

            if (statusField != null)
            {
                // Validate the field name once (returns null if unknown) so we fail fast rather than never match.
                string probe = (string)_main.Dispatcher.Invoke((Func<string>)(() => StatusField(statusField)));
                if (probe == null) return Err("unknown status field: " + statusField + " (see /status)");
                expected = QueryValue(query, "equals") ?? "";
                cond = "status:" + statusField + "==" + expected;
                observe = () => Dispatch(() => StatusField(statusField));
            }
            else if (!string.IsNullOrEmpty(uid))
            {
                string exists = QueryValue(query, "exists");
                string enabled = QueryValue(query, "enabled");
                string visible = QueryValue(query, "visible");
                string value = QueryValue(query, "value");

                if (exists != null)
                {
                    expected = NormBool(exists); cond = uid + ".exists==" + expected;
                    observe = () => Dispatch(() => FindByUid(uid) != null ? "true" : "false");
                }
                else if (enabled != null)
                {
                    expected = NormBool(enabled); cond = uid + ".enabled==" + expected;
                    observe = () => Dispatch(() => { var el = FindByUid(uid); return el == null ? "(absent)" : (el.IsEnabled ? "true" : "false"); });
                }
                else if (visible != null)
                {
                    expected = NormBool(visible); cond = uid + ".visible==" + expected;
                    observe = () => Dispatch(() => { var el = FindByUid(uid); return el == null ? "(absent)" : (el.Visibility == Visibility.Visible ? "true" : "false"); });
                }
                else if (value != null)
                {
                    expected = value; cond = uid + ".value==" + expected;
                    observe = () => Dispatch(() => { var el = FindByUid(uid); return el == null ? "(absent)" : (ReadValue(el) ?? "(null)"); });
                }
                else
                    return Err("waitfor needs a condition: exists|enabled|visible|value (with uid=)");
            }
            else
                return Err("waitfor needs uid=... (+ exists|enabled|visible|value) or status=... (+ equals=)");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string last;
            while (true)
            {
                last = observe();
                if (string.Equals(last ?? "", expected ?? "", StringComparison.OrdinalIgnoreCase))
                    return "{\"ok\":true,\"matched\":true,\"elapsedMs\":" + sw.ElapsedMilliseconds +
                           ",\"condition\":" + Str(cond) + ",\"value\":" + Str(last) + "}";
                if (sw.ElapsedMilliseconds >= timeout)
                    return "{\"ok\":false,\"matched\":false,\"timeout\":true,\"elapsedMs\":" + sw.ElapsedMilliseconds +
                           ",\"condition\":" + Str(cond) + ",\"last\":" + Str(last) + "}";
                Thread.Sleep(poll);
            }
        }

        private static string Dispatch(Func<string> f) { return (string)_main.Dispatcher.Invoke(f); }

        private static int ParseInt(string s, int dflt) { int v; return int.TryParse(s, out v) ? v : dflt; }
        private static int ClampInt(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static string NormBool(string s)
        {
            s = (s ?? "").Trim();
            return (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("on", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase))
                   ? "true" : "false";
        }

        // ---- element lookup + description ----------------------------------------------------------------

        // All UIElements whose Uid matches, in visual-tree (document) order across every open window. x:Uid is
        // unique per XAML file but repeats across reused templates (e.g. the pinned offset flyouts give three
        // btn_pin), so callers disambiguate by 0-based index - the same index /tree reports for each element.
        private static List<UIElement> FindAllByUid(string uid)
        {
            var list = new List<UIElement>();
            foreach (Window w in EnumerateWindows())
                CollectMatches(w, uid, list);
            return list;
        }

        private static void CollectMatches(DependencyObject root, string uid, List<UIElement> acc)
        {
            if (root is UIElement ue && ue.Uid == uid)
                acc.Add(ue);
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                CollectMatches(VisualTreeHelper.GetChild(root, i), uid, acc);
        }

        private static UIElement FindByUid(string uid) { return FindByUid(uid, 0); }

        private static UIElement FindByUid(string uid, int index)
        {
            var all = FindAllByUid(uid);
            return (index >= 0 && index < all.Count) ? all[index] : null;
        }

        private static IEnumerable<Window> EnumerateWindows()
        {
            // MainWindow first, then any others (flyouts/dialogs), de-duplicated.
            var seen = new HashSet<Window>();
            if (_main != null && seen.Add(_main)) yield return _main;
            foreach (Window w in Application.Current.Windows)
                if (w != null && seen.Add(w)) yield return w;
        }

        private static string BuildTree()
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"elements\":[");
            bool first = true;
            var counter = new Dictionary<string, int>();
            foreach (Window w in EnumerateWindows())
                CollectUids(w, sb, ref first, counter);
            sb.Append("]}");
            return sb.ToString();
        }

        // Lightweight catalog of the DISTINCT x:Uids addressable right now (sorted), each with its occurrence
        // count so a caller knows which need a ?index=N. This is the "what can I address" list; /tree adds the
        // per-element detail. NOTE: only REALIZED elements appear - content on a not-yet-selected tab is created
        // lazily, so select its tab (POST /invoke/tab_...) to realize it first. The full static catalog of every
        // declared x:Uid lives in the app's localization CSVs / the XAML source, not at runtime.
        private static string BuildUids()
        {
            var counts = new Dictionary<string, int>();
            var types = new Dictionary<string, string>();   // control type of the first occurrence (for discovery)
            foreach (Window w in EnumerateWindows())
                CountUids(w, counts, types);

            var keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"count\":").Append(keys.Count).Append(",\"uids\":[");
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"uid\":").Append(Str(keys[i]))
                  .Append(",\"type\":").Append(Str(types[keys[i]]))
                  .Append(",\"count\":").Append(counts[keys[i]]).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void CountUids(DependencyObject root, Dictionary<string, int> counts, Dictionary<string, string> types)
        {
            if (root is UIElement ue && !string.IsNullOrEmpty(ue.Uid))
            {
                int c; counts.TryGetValue(ue.Uid, out c); counts[ue.Uid] = c + 1;
                if (!types.ContainsKey(ue.Uid)) types[ue.Uid] = ue.GetType().Name;
            }
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                CountUids(VisualTreeHelper.GetChild(root, i), counts, types);
        }

        // counter tracks the running per-uid occurrence so each element carries the same 0-based "index" a
        // caller passes back as ?index=N to disambiguate duplicates. The pre-order walk here matches
        // FindAllByUid exactly, so the indices line up.
        private static void CollectUids(DependencyObject root, StringBuilder sb, ref bool first, Dictionary<string, int> counter)
        {
            if (root is UIElement ue && !string.IsNullOrEmpty(ue.Uid))
            {
                int idx; counter.TryGetValue(ue.Uid, out idx); counter[ue.Uid] = idx + 1;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(DescribeElement(ue, ue.Uid, idx));
            }
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                CollectUids(VisualTreeHelper.GetChild(root, i), sb, ref first, counter);
        }

        private static string DescribeElement(UIElement el, string uid, int index = -1)
        {
            string name = "", value = null;
            bool enabled = el.IsEnabled, visible = el.Visibility == Visibility.Visible;
            if (el is FrameworkElement fe) name = fe.Name ?? "";
            value = ReadValue(el);

            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"uid\":").Append(Str(uid));
            if (index >= 0) sb.Append(",\"index\":").Append(index);
            sb.Append(",\"name\":").Append(Str(name));
            sb.Append(",\"type\":").Append(Str(el.GetType().Name));
            sb.Append(",\"enabled\":").Append(enabled ? "true" : "false");
            sb.Append(",\"visible\":").Append(visible ? "true" : "false");
            if (value != null) sb.Append(",\"value\":").Append(Str(value));
            sb.Append('}');
            return sb.ToString();
        }

        // Best-effort readback: the ValuePattern text, else a toggle state, else the control's Content/Text.
        private static string ReadValue(UIElement el)
        {
            try
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(el);
                if (peer != null)
                {
                    if (peer.GetPattern(PatternInterface.Value) is IValueProvider vp)
                        return vp.Value;
                    if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tp)
                        return tp.ToggleState.ToString();
                    if (peer.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider rp)
                        return rp.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { }

            if (el is System.Windows.Controls.TextBox tb) return tb.Text;
            if (el is TextBlock tblk) return tblk.Text;                    // status/DRO/banner labels
            if (el is System.Windows.Controls.ContentControl cc && cc.Content is string s) return s;
            return null;
        }

        // ---- actions -------------------------------------------------------------------------------------

        private static string DoInvoke(string uid, int index)
        {
            var el = FindByUid(uid, index);
            if (el == null) return NotFound(uid);
            if (!el.IsEnabled) return Err("element is disabled: " + uid);

            var peer = UIElementAutomationPeer.CreatePeerForElement(el);
            if (peer == null) return Err("no automation peer for: " + uid);

            if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv)
            {
                inv.Invoke();
                return "{\"ok\":true,\"invoked\":" + Str(uid) + "}";
            }
            if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tog)
            {
                tog.Toggle();
                return "{\"ok\":true,\"toggled\":" + Str(uid) + ",\"state\":" + Str(tog.ToggleState.ToString()) + "}";
            }
            if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel)
            {
                sel.Select();
                return "{\"ok\":true,\"selected\":" + Str(uid) + "}";
            }
            // A peer built directly off a selector item (TabItem/ListBoxItem) usually lacks the SelectionItem
            // pattern - that comes from the parent selector's peer - so fall back to the item's own IsSelected.
            if (el is System.Windows.Controls.TabItem ti)
            {
                ti.IsSelected = true;
                return "{\"ok\":true,\"selected\":" + Str(uid) + "}";
            }
            if (el is System.Windows.Controls.ListBoxItem li)
            {
                li.IsSelected = true;
                return "{\"ok\":true,\"selected\":" + Str(uid) + "}";
            }
            return Err("element does not support Invoke/Toggle/Select: " + uid + " (" + el.GetType().Name + ")");
        }

        private static string DoSet(string uid, string value, int index)
        {
            if (value == null) return Err("set requires ?value=...");
            var el = FindByUid(uid, index);
            if (el == null) return NotFound(uid);
            if (!el.IsEnabled) return Err("element is disabled: " + uid);

            var peer = UIElementAutomationPeer.CreatePeerForElement(el);
            if (peer == null) return Err("no automation peer for: " + uid);

            if (peer.GetPattern(PatternInterface.Value) is IValueProvider vp)
            {
                vp.SetValue(value);
                return "{\"ok\":true,\"set\":" + Str(uid) + ",\"value\":" + Str(value) + "}";
            }
            if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tog)
            {
                // interpret value as a desired boolean; toggle until it matches (max 3 to cover Indeterminate)
                bool want = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase);
                for (int i = 0; i < 3 && (tog.ToggleState == ToggleState.On) != want; i++)
                    tog.Toggle();
                return "{\"ok\":true,\"set\":" + Str(uid) + ",\"state\":" + Str(tog.ToggleState.ToString()) + "}";
            }
            if (peer.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider rp)
            {
                double d;
                if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                    return Err("value is not a number for range element: " + uid);
                rp.SetValue(d);
                return "{\"ok\":true,\"set\":" + Str(uid) + ",\"value\":" + rp.Value.ToString(CultureInfo.InvariantCulture) + "}";
            }
            return Err("element does not support Value/Toggle/Range: " + uid + " (" + el.GetType().Name + ")");
        }

        // ---- helpers -------------------------------------------------------------------------------------

        private static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return eq >= 0 ? Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')) : "";
            }
            return null;
        }

        private static string NotFound(string uid) => Err("no element with uid: " + uid);

        private static string Err(string msg) => "{\"ok\":false,\"error\":" + Str(msg) + "}";

        // Minimal JSON string encoder (quotes + escapes) - avoids a serializer dependency for our simple output.
        private static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
