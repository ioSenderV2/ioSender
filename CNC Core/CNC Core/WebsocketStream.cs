/*
 * WebsocketStream.cs - part of CNC Controls library
 *
 * v0.41 / 2022-09-03 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2022, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System;
using System.IO;
using System.Text;
using System.Threading;
#if USEWEBSOCKET
using System.Net.WebSockets;    // in the framework on BOTH net462 and net8.0 - replaces websocket-sharp,
using System.Threading.Tasks;   // an unmaintained net462-only binary, with no external reference at all
#endif

namespace CNC.Core
{
#if USEWEBSOCKET
    /* Transport for grblHAL's websocket daemon (ws://host:port).
     *
     * Ported from websocket-sharp to System.Net.WebSockets.ClientWebSocket: that library was the last
     * dependency keeping CNC.Core off modern .NET, and unlike ObsBridge (client functionality, which
     * simply moved to CNC.Controls) this is a genuine server concern - it talks to the machine.
     *
     * The API difference that shapes this file: websocket-sharp was event-driven with a blocking
     * Connect() and an OnMessage callback, ClientWebSocket is task-based. So reads become an explicit
     * receive loop on a background task, and writes block on the send task - StreamComms is a
     * synchronous contract and websocket-sharp's Send was synchronous too, so blocking preserves the
     * existing behaviour rather than introducing new async semantics on the write path.
     */
    public class WebsocketStream : StreamCommsBase, StreamComms
    {
        private const int ConnectTimeout = 3000;

        private ClientWebSocket websocket = null;
        private CancellationTokenSource cancel = null;
        private volatile bool _isOpen = false;
        private volatile Comms.State state = Comms.State.ACK;
        private StringBuilder input = new StringBuilder(1024);
        private SynchronizationContext SyncContext { get; set; }

        // ClientWebSocket allows only ONE send in flight - a concurrent SendAsync throws "There is
        // already one outstanding 'SendAsync' call". websocket-sharp serialised internally; here the
        // streamer thread and the UI thread both write, so serialise them explicitly.
        private readonly object sendLock = new object();

        private readonly string hostUrl;
        private readonly Reconnector reconnector;
        private volatile bool closing = false;

        public event DataReceivedHandler DataReceived;

        public Action<string> AckSink { get; set; }
        public bool BlockingWrites { get; set; }   // websocket Send is already synchronous; no-op here

        public WebsocketStream(string host, SynchronizationContext syncContext)
        {
            Comms.com = this;
            Reply = string.Empty;
            SyncContext = syncContext;

            hostUrl = host;

            // Auto-reconnect: same Reconnector state machine as the serial/network transports.
            reconnector = new Reconnector(() => OpenConnection());

            OpenConnection();
        }

        // Opens (or re-opens) the websocket. Returns true when connected.
        // Safe to call from the reconnect timer thread.
        private bool OpenConnection()
        {
            try
            {
                var ws = new ClientWebSocket();
                var cts = new CancellationTokenSource();

                // Cap the connect attempt, same rationale as TelnetStream: an unreachable host - the
                // controller rebooting, typically - would otherwise sit on the OS connect timeout and
                // stall the reconnect retry loop long enough to look like it had given up.
                if (!ws.ConnectAsync(new Uri(hostUrl), cts.Token).Wait(ConnectTimeout))
                {
                    cts.Cancel();
                    try { ws.Dispose(); } catch { }
                    return false;
                }

                websocket = ws;
                cancel = cts;
                _isOpen = ws.State == WebSocketState.Open;

                // Fire and forget: the loop owns its own lifetime, ends on Close()'s cancellation, and
                // swallows its exceptions, so nothing observes this task.
                Task.Run(() => ReceiveLoop(ws, cts.Token));
            }
            catch
            {
                _isOpen = false;
            }

            return IsOpen;
        }

        private void HandleWriteError(Exception ex)
        {
            // Task.Wait wraps failures in AggregateException - unwrap before testing, or a dropped
            // connection is never recognised here and the reconnector never starts.
            if (ex is AggregateException)
                ex = ex.GetBaseException();

            if (ex is WebSocketException || ex is IOException || ex is ObjectDisposedException ||
                 ex is InvalidOperationException || ex is OperationCanceledException)
            {
                _isOpen = false;
                if (!closing)
                    reconnector?.NotifyLost();
            }
        }

        // The one write path: all of WriteByte/WriteBytes/WriteString funnel through here so the
        // single-send-in-flight rule is enforced in exactly one place.
        private void Send(byte[] bytes, int len)
        {
            var ws = websocket;
            var cts = cancel;

            try
            {
                if (ws != null && IsOpen)
                    // Binary frames: websocket-sharp's Send(byte[]) sent binary, so this keeps the wire
                    // format byte-identical to before the port.
                    lock (sendLock)
                        ws.SendAsync(new ArraySegment<byte>(bytes, 0, len), WebSocketMessageType.Binary,
                                      true, cts?.Token ?? CancellationToken.None).Wait();
            }
            catch (Exception ex)
            {
                HandleWriteError(ex);
            }
        }

        // Replaces websocket-sharp's OnMessage callback. Runs on a pool thread for the life of the
        // connection; ends on cancellation (Close) or on the connection faulting.
        private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[512];

            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.Count > 0)
                        // EndOfMessage is deliberately ignored: unlike websocket-sharp, ClientWebSocket
                        // can hand back a partial message, but no reassembly is needed because the parser
                        // is stream-oriented (append, then split on '\n') exactly like the serial and
                        // telnet readers - a reply split across two frames rejoins in 'input'.
                        ProcessReceived(Encoding.Default.GetString(buffer, 0, result.Count));
                }
            }
            catch
            {
                // Cancellation from Close(), or the connection faulting. Which one it was is decided by
                // 'closing' below, the same test the old OnClose handler made.
            }

            _isOpen = false;

            // Unexpected end -> start the reconnect loop (OpenConnection builds a fresh socket).
            // An intentional Close() sets 'closing' first to suppress this.
            if (!closing)
                reconnector?.NotifyLost();
        }

        ~WebsocketStream()
        {
            Close();
        }

        public Comms.StreamType StreamType { get { return Comms.StreamType.Websocket; } }
        public bool IsOpen { get { return websocket != null && _isOpen; } }
        public int OutCount { get { return 0; } }
        public Comms.State CommandState { get { return state; } set { state = value; } }
        public string Reply { get; private set; }
        public bool EventMode { get; set; } = true;
        public Action<int> ByteReceived { get; set; }

        public bool IsReconnecting { get { return reconnector != null && reconnector.IsReconnecting; } }

        public void NotifyLinkLost() { reconnector?.NotifyLost(); }

        public event System.Action ConnectionLost
        {
            add { reconnector.ConnectionLost += value; }
            remove { reconnector.ConnectionLost -= value; }
        }

        public event System.Action Reconnected
        {
            add { reconnector.Reconnected += value; }
            remove { reconnector.Reconnected -= value; }
        }

        public void PurgeQueue()
        {
            Reply = string.Empty;
            if (!EventMode)
                input.Clear();
        }

        public void Close()
        {
            closing = true;
            reconnector?.Cancel(); // an explicit close must not trigger an auto-reconnect

            var ws = websocket;
            var cts = cancel;

            if (ws != null)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).Wait(300);
                }
                catch { }

                // Cancel AFTER the close handshake attempt (cancelling first would abort it), which is
                // also what unblocks the receive loop's pending ReceiveAsync. The CTS is deliberately
                // NOT disposed: the loop may still be inside that call, and disposing it underneath
                // would race for no benefit - GC handles it.
                try { cts?.Cancel(); } catch { }
                try { ws.Dispose(); } catch { }
            }

            _isOpen = false;
            websocket = null;
            cancel = null;
        }

        public int ReadByte()
        {
            int c = input.Length == 0 ? -1 : input[0];

            if (c != -1)
                input.Remove(0, 1);

            return c;
        }

        protected override void WriteByteRaw(byte data)
        {
            Send(new byte[1] { data }, 1);
        }

        protected override void WriteBytesRaw(byte[] bytes, int len)
        {
            Send(bytes, len);
        }

        public void WriteString(string data)
        {
            byte[] bytes = Encoding.Default.GetBytes(data);
            // Was WriteBytes(bytes, 0): the old body ignored 'len' and always sent the whole array, so 0
            // worked by accident. 'len' is honoured now (as SerialStream and TelnetStream always have),
            // so it has to be right - and YModem, which passes a real length into a larger buffer, stops
            // sending that buffer's trailing garbage over this transport.
            WriteBytes(bytes, bytes.Length);
        }

        public void WriteCommand(string command)
        {
            state = Comms.State.AwaitAck;

            if (command.Length == 1 && command != GrblConstants.CMD_PROGRAM_DEMARCATION)
                WriteByte((byte)command.ToCharArray()[0]);
            else
            {
                command += "\r";
                WriteString(command);
            }
        }

        public void AwaitAck()
        {
            while (Comms.com.CommandState == Comms.State.DataReceived || Comms.com.CommandState == Comms.State.AwaitAck)
                EventUtils.DoEvents();
        }

        public void AwaitAck(string command)
        {
            WriteCommand(command);

            while (Comms.com.CommandState == Comms.State.DataReceived || Comms.com.CommandState == Comms.State.AwaitAck) ;
        }

        public void AwaitResponse()
        {
            while (Comms.com.CommandState == Comms.State.AwaitAck)
                EventUtils.DoEvents();
        }

        public void AwaitResponse(string command)
        {
            WriteCommand(command);

            while (Comms.com.CommandState == Comms.State.AwaitAck) ;
        }

        public string GetReply(string command)
        {
            Reply = string.Empty;
            WriteCommand(command);

            while (state == Comms.State.AwaitAck)
                EventUtils.DoEvents();

            return Reply;
        }

        private int gp()
        {
            int pos = 0; bool found = false;

            while (!found && pos < input.Length)
                found = input[pos++] == '\n';

            return found ? pos - 1 : 0;
        }

        private void ProcessReceived(string data)
        {
            int pos = 0;
            System.Collections.Generic.List<string> replies = null;

            lock (input)
            {
                input.Append(data);

                if (EventMode)
                {
                    // Extract under the lock, dispatch after releasing it - a synchronous Dispatcher.Invoke while
                    // holding lock(input) deadlocks against a UI-thread PurgeQueue / Write (see TelnetStream).
                    while (input.Length > 0 && (pos = gp()) > 0)
                    {
                        (replies ?? (replies = new System.Collections.Generic.List<string>())).Add(input.ToString(0, pos - 1));
                        input.Remove(0, pos + 1);
                    }
                }
                else
                    ByteReceived?.Invoke(ReadByte());
            }

            if (replies != null) foreach (string reply in replies)
            {
                Reply = reply;
                state = reply == "ok" ? Comms.State.ACK : (reply.StartsWith("error") ? Comms.State.NAK : Comms.State.DataReceived);
                // Tap ok/error acks straight to the streamer (when installed), bypassing the UI dispatcher.
                if (AckSink != null && (state == Comms.State.ACK || state == Comms.State.NAK))
                    AckSink(reply);
                // Async marshal (BeginInvoke, not Invoke): a synchronous Invoke blocks this read thread on a
                // busy UI, stalling reads and acks. BeginInvoke keeps reads flowing; the per-call reply value
                // is captured (strings are immutable) so order/content are preserved (see TelnetStream).
                if (reply.Length != 0 && DataReceived != null)
                    Comms.PostTo(SyncContext, DataReceived, reply);
            }
        }
    }
#endif
}
