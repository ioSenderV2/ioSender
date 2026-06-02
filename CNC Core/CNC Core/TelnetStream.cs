/*
 * TelnetStream.cs - part of CNC Controls library
 *
 * v0.41 / 2022-09-03 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2021, Io Engineering (Terje Io)
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
using System.Net.Sockets;
using System.Windows.Threading;

namespace CNC.Core
{
    public class TelnetStream : StreamComms
    {
        private TcpClient ipserver = null;
        private NetworkStream ipstream = null;
        private byte[] buffer = new byte[512];
        private volatile Comms.State state = Comms.State.ACK;
        private StringBuilder input = new StringBuilder(1024);
        private Dispatcher Dispatcher { get; set; }

        private readonly string hostParams;
        private readonly Reconnector reconnector;
        private volatile bool closing = false;

        public event DataReceivedHandler DataReceived;

        public TelnetStream(string host, Dispatcher dispatcher)
        {
            Comms.com = this;
            Reply = string.Empty;
            Dispatcher = dispatcher;

            if (!host.Contains(":"))
                host += ":23";

            hostParams = host;

            // Auto-reconnect: shares the same Reconnector state machine as the serial transport;
            // only the "reopen" step differs - here it is simply a fresh TCP connect attempt.
            reconnector = new Reconnector(() => OpenConnection());

            OpenConnection();
        }

        // Opens (or re-opens) the connection. Returns true when connected.
        // Safe to call from the reconnect timer thread.
        private bool OpenConnection()
        {
            string[] parameter = hostParams.Split(':');

            if (parameter.Length == 2) try
            {
                ipserver = new TcpClient(parameter[0], int.Parse(parameter[1]));
                ipserver.NoDelay = true;
                ipstream = ipserver.GetStream();
                ipstream.BeginRead(buffer, 0, buffer.Length, ReadComplete, buffer);
            }
            catch
            {
                ipstream = null;
                ipserver = null;
            }

            return IsOpen;
        }

        // Tear down a faulted connection and start the reconnect loop, unless we are
        // closing intentionally.
        private void OnConnectionLost()
        {
            if (closing)
                return;

            try { ipstream?.Close(); ipstream?.Dispose(); } catch { }
            ipstream = null;

            try { ipserver?.Close(); } catch { }
            ipserver = null;

            reconnector?.NotifyLost();
        }

        private void HandleWriteError(Exception ex)
        {
            if (ex is IOException || ex is SocketException ||
                 ex is ObjectDisposedException || ex is InvalidOperationException)
                OnConnectionLost();
        }

        ~TelnetStream()
        {
            Close();
        }

        public Comms.StreamType StreamType { get { return Comms.StreamType.Telnet; } }
        public bool IsOpen { get { return ipserver != null && ipserver.Connected; } }
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
            try
            {
                while (ipstream != null && ipstream.DataAvailable)
                    ipstream.ReadByte();
            }
            catch { }
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
                PurgeQueue();
                ipstream.Close(300);
                ipstream.Dispose();
                ipstream = null;
                ipserver.Close();
                ipserver = null;
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
                if (ipstream != null && IsOpen)
                    ipstream.WriteAsync(new byte[1] { data }, 0, 1);
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
                if (ipstream != null && IsOpen)
                    ipstream.WriteAsync(bytes, 0, len);
            }
            catch (Exception ex)
            {
                HandleWriteError(ex);
            }
        }

        public void WriteString(string data)
        {
            byte[] bytes = Encoding.Default.GetBytes(data);
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

        void ReadComplete(IAsyncResult iar)
        {
            int bytesAvailable = 0;
            bool failed = false;
            byte[] buffer = (byte[])iar.AsyncState;

            try
            {
                bytesAvailable = ipstream.EndRead(iar);
            }
            catch
            {
                failed = true;
            }

            // A read of 0 bytes (or a read error) means the remote end closed the connection -
            // hand off to the reconnect logic rather than silently spinning on dead reads.
            if (failed || bytesAvailable == 0)
            {
                OnConnectionLost();
                return;
            }

            int pos = 0;

            lock (input)
            {
                input.Append(Encoding.ASCII.GetString(buffer, 0, bytesAvailable));

                if (EventMode)
                {
                    while (input.Length > 0 && (pos = gp()) > 0)
                    {
                        Reply = input.ToString(0, pos - 1);
                        input.Remove(0, pos + 1);
                        state = Reply == "ok" ? Comms.State.ACK : (Reply.StartsWith("error") ? Comms.State.NAK : Comms.State.DataReceived);
                        if (Reply.Length != 0 && DataReceived != null)
                            Dispatcher.Invoke(DataReceived, Reply);
                    }
                }
                else
                    ByteReceived?.Invoke(ReadByte());
            }

            try
            {
                if (ipstream != null && ipserver.Connected)
                    ipstream.BeginRead(buffer, 0, buffer.Length, ReadComplete, buffer);
            }
            catch
            {
                OnConnectionLost();
            }
        }
    }
}
