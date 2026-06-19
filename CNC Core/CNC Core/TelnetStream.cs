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
                // Connect with a short timeout. The synchronous TcpClient(host, port) constructor blocks on the
                // OS connect timeout (~21s) when the host is unreachable - e.g. while the controller is rebooting -
                // which stalls the reconnect retry loop so long it looks like it gave up. Cap it so each retry is
                // quick and we reconnect within ~a second of the controller coming back.
                var client = new TcpClient();
                var ar = client.BeginConnect(parameter[0], int.Parse(parameter[1]), null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(3000)) || !client.Connected)
                {
                    try { client.Close(); } catch { }
                    ipserver = null;
                    ipstream = null;
                    return false;
                }
                client.EndConnect(ar);
                ipserver = client;
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
            // Do NOT read ipstream directly here. A continuous async BeginRead (ReadComplete) is always in
            // flight, and a second, synchronous read on the same NetworkStream is illegal - the two race and
            // drop bytes. During a YModem transfer (PurgeQueue runs before every block) this lost the per-block
            // ACKs, so blocks timed out and retried to CAN: 0-byte files and multi-minute uploads. The async
            // reader already drains the socket into 'input'; clearing that buffer is the correct, race-free purge.
            Reply = string.Empty;
            lock (input)
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

        // Write synchronously, NOT WriteAsync. YModem sends three back-to-back WriteBytes per block
        // (header, payload, CRC); un-awaited WriteAsync calls overlap, and a second write while the first
        // is still pending throws "a write is already in progress" - swallowed by the catch, silently
        // dropping the payload/CRC. The controller then never sees a complete block, never ACKs, and the
        // transfer times out and CANs (0-byte files). Synchronous Write serialises the bytes correctly;
        // on a local/normal connection it returns as soon as the OS buffers them.
        public void WriteByte(byte data)
        {
            try
            {
                if (ipstream != null && IsOpen)
                    ipstream.Write(new byte[1] { data }, 0, 1);
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
                    ipstream.Write(bytes, 0, len);
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

            // Close() disposes and nulls ipstream while this read may still be in flight. Capture the
            // field once into a local so an intentional close races to a clean stop instead of throwing
            // a NullReferenceException when EndRead dereferences a field that just became null.
            NetworkStream stream = ipstream;
            if (closing || stream == null)
                return;

            try
            {
                bytesAvailable = stream.EndRead(iar);
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
            System.Collections.Generic.List<string> replies = null;

            lock (input)
            {
                input.Append(Encoding.ASCII.GetString(buffer, 0, bytesAvailable));

                if (EventMode)
                {
                    // Extract complete replies under the lock, but DISPATCH them after releasing it (below).
                    // DataReceived is marshalled to the UI thread with a synchronous Dispatcher.Invoke, and a
                    // UI-thread PurgeQueue / Write also takes lock(input). Invoking while holding the lock
                    // deadlocks: the UI thread blocks on the lock while this thread blocks on the UI thread
                    // (seen as a frozen UI with a modal macro dialog up while polling continues).
                    while (input.Length > 0 && (pos = gp()) > 0)
                    {
                        (replies ?? (replies = new System.Collections.Generic.List<string>())).Add(input.ToString(0, pos - 1));
                        input.Remove(0, pos + 1);
                    }
                }
                else
                {
                    // Hand over every buffered byte, not just one. During a YModem transfer EventMode is off
                    // and the only consumer is a one-shot ByteReceived waiter (WaitFor) that takes the first
                    // byte and ignores the rest. Draining a single byte per socket callback while appending
                    // the whole burst let `input` grow without bound if the controller ever flooded bytes (a
                    // stalled/!confused transfer) - eventually an OutOfMemoryException. Draining fully here
                    // bounds memory and cannot starve the waiter.
                    int b;
                    while ((b = ReadByte()) != -1)
                        ByteReceived?.Invoke(b);
                }
            }

            if (replies != null) foreach (string reply in replies)
            {
                Reply = reply;
                state = Reply == "ok" ? Comms.State.ACK : (Reply.StartsWith("error") ? Comms.State.NAK : Comms.State.DataReceived);
                if (Reply.Length != 0 && DataReceived != null)
                    Dispatcher.Invoke(DataReceived, Reply);
            }

            try
            {
                // Same close race as above: re-read both fields into locals and bail if a close slipped
                // in, so we never dereference a nulled ipserver while re-arming the read.
                NetworkStream s = ipstream;
                TcpClient server = ipserver;
                if (!closing && s != null && server != null && server.Connected)
                    s.BeginRead(buffer, 0, buffer.Length, ReadComplete, buffer);
            }
            catch
            {
                OnConnectionLost();
            }
        }
    }
}
