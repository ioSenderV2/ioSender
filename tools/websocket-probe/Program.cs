/*
 * Program.cs - loopback exercise for the ported CNC.Core.WebsocketStream.
 *
 * See websocket-probe.csproj for why this exists. Run: dotnet run --project tools/websocket-probe
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CNC.Core;

namespace WebsocketProbe
{
    class Program
    {
        static int failures = 0;
        const int Port = 47812;

        static void Main()
        {
            Console.WriteLine("WebsocketStream loopback probe (ClientWebSocket port)\n");

            var server = new LoopbackServer(Port);
            server.Start();

            var replies = new List<string>();
            var acks = new List<string>();
            int lost = 0, reconnected = 0;

            var stream = new WebsocketStream(string.Format("ws://localhost:{0}/", Port), null);
            stream.DataReceived += data => { lock (replies) replies.Add(data); };
            stream.ReplyClassified += (cls, r) => { if (cls == Comms.ReplyClass.Ack || cls == Comms.ReplyClass.Nak) lock (acks) acks.Add(r); };
            stream.ConnectionLost += () => Interlocked.Increment(ref lost);
            stream.Reconnected += () => Interlocked.Increment(ref reconnected);

            // ---- connect -------------------------------------------------------------------------
            Check("connects", WaitFor(() => stream.IsOpen, 5000), "IsOpen=" + stream.IsOpen);
            Check("server saw the connection", WaitFor(() => server.Connections == 1, 2000),
                  "connections=" + server.Connections);

            // ---- read path -----------------------------------------------------------------------
            // Replies are CRLF-terminated, as a real controller sends them: the extraction below takes
            // (position of '\n') - 1 characters, i.e. it strips the '\r'. Feeding it a bare '\n' silently
            // eats the last real character of every reply. Same code, same assumption, in TelnetStream.
            server.SendText("ok\r\n");
            Check("single reply arrives", WaitFor(() => Count(replies) == 1, 2000), Dump(replies));
            Check("reply content", Count(replies) == 1 && replies[0] == "ok", Dump(replies));
            Check("state is ACK after 'ok'", stream.CommandState == Comms.State.ACK,
                  stream.CommandState.ToString());
            Check("ack tapped via ReplyClassified", Count(acks) == 1 && acks[0] == "ok", Dump(acks));

            // The port's real behavioural risk: websocket-sharp delivered whole messages, ClientWebSocket
            // can hand back a partial one. Split a reply across two frames and it must still rejoin.
            Clear(replies);
            server.SendText("<Idle|MP");
            Thread.Sleep(150);
            Check("no reply from a partial frame", Count(replies) == 0, Dump(replies));
            server.SendText("os:1.000,2.000,3.000>\r\n");
            Check("split reply reassembles", WaitFor(() => Count(replies) == 1, 2000), Dump(replies));
            Check("reassembled content", Count(replies) == 1 && replies[0] == "<Idle|MPos:1.000,2.000,3.000>",
                  Dump(replies));

            // ...and the converse: several replies in one frame, order preserved.
            Clear(replies);
            server.SendText("ok\r\nerror:1\r\nok\r\n");
            Check("three replies from one frame", WaitFor(() => Count(replies) == 3, 2000), Dump(replies));
            Check("order preserved", Dump(replies) == "ok|error:1|ok", Dump(replies));

            // ---- write path ----------------------------------------------------------------------
            server.ClearReceived();
            stream.WriteCommand("$I");
            Check("WriteCommand sends '$I\\r'", WaitFor(() => server.ReceivedText() == "$I\r", 2000),
                  Show(server.ReceivedText()));
            Check("frames are binary (as websocket-sharp sent)", server.LastMessageType == WebSocketMessageType.Binary,
                  server.LastMessageType.ToString());

            server.ClearReceived();
            stream.WriteString("hello");
            Check("WriteString sends exactly its bytes", WaitFor(() => server.ReceivedBytes() == 5, 2000),
                  server.ReceivedBytes() + " bytes: " + Show(server.ReceivedText()));

            // The len fix: the old body ignored 'len' and sent the whole array, so a caller passing a
            // real length into a bigger buffer (YModem) shipped the buffer's trailing garbage too.
            server.ClearReceived();
            var padded = new byte[16];
            Encoding.ASCII.GetBytes("G0X0", 0, 4, padded, 0);
            stream.WriteBytes(padded, 4);
            Check("WriteBytes honours len (YModem's case)", WaitFor(() => server.ReceivedBytes() == 4, 2000),
                  server.ReceivedBytes() + " bytes: " + Show(server.ReceivedText()));

            // ---- single byte ---------------------------------------------------------------------
            server.ClearReceived();
            stream.WriteByte(0x18);   // realtime reset
            Check("WriteByte sends one byte", WaitFor(() => server.ReceivedBytes() == 1, 2000),
                  server.ReceivedBytes().ToString());

            // ---- connection loss + auto reconnect ------------------------------------------------
            server.DropClient();
            Check("connection loss detected", WaitFor(() => lost == 1, 5000), "lost=" + lost);
            Check("IsOpen false while down", !stream.IsOpen, "IsOpen=" + stream.IsOpen);
            Check("reconnects by itself", WaitFor(() => reconnected == 1, 10000), "reconnected=" + reconnected);
            Check("open again after reconnect", WaitFor(() => stream.IsOpen, 3000), "IsOpen=" + stream.IsOpen);

            // reads still work on the fresh socket
            Clear(replies);
            server.SendText("ok\r\n");
            Check("reads work after reconnect", WaitFor(() => Count(replies) == 1, 3000), Dump(replies));

            // ---- explicit close ------------------------------------------------------------------
            int lostBefore = lost;
            stream.Close();
            Check("closed", WaitFor(() => !stream.IsOpen, 2000), "IsOpen=" + stream.IsOpen);
            Thread.Sleep(2500);   // longer than the 1s reconnect interval
            Check("explicit Close does not trigger a reconnect", lost == lostBefore && !stream.IsReconnecting,
                  "lost=" + lost + " reconnecting=" + stream.IsReconnecting);

            server.Stop();

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            Environment.Exit(failures == 0 ? 0 : 1);
        }

        // ---- helpers ---------------------------------------------------------------------------

        static void Check(string what, bool ok, string detail = null)
        {
            if (!ok)
                failures++;
            Console.WriteLine("  {0} {1}{2}", ok ? "PASS" : "FAIL", what,
                              detail == null ? "" : "   [" + detail + "]");
        }

        static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (condition())
                    return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        static int Count(List<string> l) { lock (l) return l.Count; }
        static void Clear(List<string> l) { lock (l) l.Clear(); }
        static string Dump(List<string> l) { lock (l) return string.Join("|", l); }
        static string Show(string s) { return s == null ? "(null)" : s.Replace("\r", "\\r").Replace("\n", "\\n"); }
    }

    /// <summary>
    /// A real websocket server on loopback, standing in for grblHAL's websocket daemon.
    /// </summary>
    class LoopbackServer
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly List<byte> received = new List<byte>();
        private volatile WebSocket client;
        private int connections;

        public WebSocketMessageType LastMessageType { get; private set; }
        public int Connections { get { return connections; } }

        public LoopbackServer(int port)
        {
            listener.Prefixes.Add(string.Format("http://localhost:{0}/", port));
        }

        public void Start()
        {
            listener.Start();
            Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { return; }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }

                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                client = wsCtx.WebSocket;
                Interlocked.Increment(ref connections);
                var socket = wsCtx.WebSocket;
                _ = Task.Run(() => ReadLoop(socket));
            }
        }

        private async Task ReadLoop(WebSocket socket)
        {
            var buffer = new byte[512];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    LastMessageType = r.MessageType;
                    lock (received)
                        received.AddRange(buffer.Take(r.Count));
                }
            }
            catch { }
        }

        public void SendText(string s)
        {
            var socket = client;
            if (socket != null && socket.State == WebSocketState.Open)
                socket.SendAsync(new ArraySegment<byte>(Encoding.ASCII.GetBytes(s)),
                                 WebSocketMessageType.Binary, true, CancellationToken.None).Wait();
        }

        public int ReceivedBytes() { lock (received) return received.Count; }
        public string ReceivedText() { lock (received) return Encoding.ASCII.GetString(received.ToArray()); }
        public void ClearReceived() { lock (received) received.Clear(); }

        /// <summary>Kill the connection without a close handshake - a controller going away.</summary>
        public void DropClient()
        {
            var socket = client;
            client = null;
            if (socket != null)
            {
                try { socket.Abort(); } catch { }
                try { socket.Dispose(); } catch { }
            }
        }

        public void Stop()
        {
            DropClient();
            try { listener.Stop(); } catch { }
        }
    }
}
