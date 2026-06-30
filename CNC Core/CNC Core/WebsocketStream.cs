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
using System.Windows.Threading;
using WebSocketSharp;

namespace CNC.Core
{
#if USEWEBSOCKET
    public class WebsocketStream : StreamComms
    {
        private WebSocket websocket = null;
        private volatile bool _isOpen = false;
        private volatile Comms.State state = Comms.State.ACK;
        private StringBuilder input = new StringBuilder(1024);
        private Dispatcher Dispatcher { get; set; }

        private readonly string hostUrl;
        private readonly Reconnector reconnector;
        private volatile bool closing = false;

        // Serialises all writes: the UI thread (real-time bytes, MDI), the streamer thread (job lines)
        // and the poll timer all write concurrently. Keeps each line/command atomic on the socket.
        private readonly object writeLock = new object();

        public event DataReceivedHandler DataReceived;

        public Action<string> AckSink { get; set; }

        public WebsocketStream(string host, Dispatcher dispatcher)
        {
            Comms.com = this;
            Reply = string.Empty;
            Dispatcher = dispatcher;

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
                websocket = new WebSocketSharp.WebSocket(hostUrl);
                websocket.OnMessage += OnMessage;
                websocket.OnOpen += OnOpen;
                websocket.OnClose += OnClose;
                websocket.Connect();
            }
            catch
            {
            }

            return IsOpen;
        }

        private void HandleWriteError(Exception ex)
        {
            if (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                _isOpen = false;
                if (!closing)
                    reconnector?.NotifyLost();
            }
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

            if (IsOpen)
            {
                websocket.OnMessage -= OnMessage;
                websocket.OnOpen -= OnOpen;
                websocket.Close();
            }
        }

        public int ReadByte()
        {
            int c = input.Length == 0 ? -1 : input[0];

            if (c != -1)
                input.Remove(0, 1);

            return c;
        }

        public void WriteByte(byte data)
        {
            try
            {
                lock (writeLock)
                {
                    if (websocket != null && IsOpen)
                        websocket.Send(new byte[1] { data });
                }
            }
            catch (Exception ex)
            {
                HandleWriteError(ex);
            }
        }

        public void WriteBytes(byte[] bytes, int len)
        {
            try
            {
                lock (writeLock)
                {
                    if (websocket != null && IsOpen)
                        websocket.Send(bytes);
                }
            }
            catch (Exception ex)
            {
                HandleWriteError(ex);
            }
        }

        public void WriteString(string data)
        {
            WriteBytes(Encoding.Default.GetBytes(data), 0);
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

        private void OnOpen(object sender, EventArgs e)
        {
            _isOpen = true;
        }

        private void OnClose(object sender, CloseEventArgs e)
        {
            _isOpen = false;

            var ws = sender as WebSocket;
            if (ws != null)
            {
                ws.OnMessage -= OnMessage;
                ws.OnOpen -= OnOpen;
                ws.OnClose -= OnClose;
            }

            // Unexpected close -> start the reconnect loop (OpenConnection will create a fresh
            // socket). An intentional Close() sets 'closing' first to suppress this.
            if (!closing)
                reconnector?.NotifyLost();
        }

        private int gp()
        {
            int pos = 0; bool found = false;

            while (!found && pos < input.Length)
                found = input[pos++] == '\n';

            return found ? pos - 1 : 0;
        }

        private void OnMessage(object sender, MessageEventArgs e)
        {
            int pos = 0;
            System.Collections.Generic.List<string> replies = null;

            lock (input)
            {
                if (e.IsText)
                    input.Append(e.Data);
                else
                    input.Append(Encoding.Default.GetString(e.RawData, 0, e.RawData.Length));

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
                    Dispatcher.BeginInvoke(DataReceived, reply);
            }
        }
    }
#endif
}
