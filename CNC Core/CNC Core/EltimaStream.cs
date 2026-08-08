/*
 * EltimaStream.cs - part of CNC Controls library
 *
 * v0.33 / 2021-05-12 / Io Engineering (Terje Io)
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
using System.Linq;
using System.Text;
using System.IO.Ports;
#if USEELTIMA
using System.Windows.Forms;      // only the Eltima body below needs these; outside the #if they would
using System.Windows.Threading;  // keep CNC.Core tied to WPF for code that is not even compiled
#endif
using System.IO;
using System.Collections.ObjectModel;

namespace CNC.Core
{
#if USEELTIMA
    // NOTE: never compiled in this repo (DefineConstants carries xUSEELTIMA, i.e. disabled), so the
    // StreamCommsBase conversion below is by inspection only, not by build. Its WriteString goes straight
    // to serialPort.WriteStr and so bypasses the traced leaves - unlike the other three transports. Left
    // as-is rather than "fixed" blind: changing an uncompilable write path on a guess is worse than an
    // acknowledged gap.
    public class EltimaStream : StreamCommsBase, StreamComms
    {

        private SPortLib.SPortAx serialPort = null;
        private StringBuilder input = new StringBuilder(400);
        private volatile Comms.State state = Comms.State.ACK;
        private Dispatcher Dispatcher { get; set; }
        public bool EventMode { get; set; } = true;
        public Action<int> ByteReceived { get; set; }

        // Auto-reconnect is not implemented for the (optional, USEELTIMA) Eltima transport;
        // these satisfy the StreamComms contract.
        public bool IsReconnecting { get { return false; } }

        // No auto-reconnect on this transport (see the comment above), so there is nothing to notify.
        public void NotifyLinkLost() { }
        public event System.Action ConnectionLost;
        public event System.Action Reconnected;

        public event DataReceivedHandler DataReceived;

        public event Action<Comms.ReplyClass, string> ReplyClassified;
        public bool BlockingWrites { get; set; }   // Eltima writes are already synchronous; no-op here

#if RESPONSELOG
        StreamWriter log = null;
#endif

        public EltimaStream(string PortParams, int ResetDelay, Dispatcher dispatcher)
        {
            Comms.com = this;
            Dispatcher = dispatcher;
            Reply = string.Empty;

            if (PortParams.IndexOf(":") < 0)
                PortParams += ":115200,N,8,1";

            string[] parameter = PortParams.Substring(PortParams.IndexOf(":") + 1).Split(',');

            if (parameter.Count() < 4)
            {
                UserPrompt.Show("Unable to open serial port: " + PortParams, "ioSender");
                System.Environment.Exit(2);
            }

            try
            {
                this.serialPort = new SPortLib.SPortAx();
            }
            catch
            {
                UserPrompt.Show("Failed to load serial port driver.", "ioSender");
                System.Environment.Exit(1);
            }

            this.serialPort.InitString(PortParams.Substring(PortParams.IndexOf(":") + 1));
            //           this.SerialPort.HandShake = 0x08; // Cannot be used with ESP32
            this.serialPort.FlowReplace = 0x80;
            this.serialPort.CharEvent = 10;
            this.serialPort.InBufferSize = Comms.RXBUFFERSIZE;
            this.serialPort.OutBufferSize = Comms.TXBUFFERSIZE;
            this.serialPort.BlockMode = false;


            try
            {
                serialPort.Open(PortParams.Substring(0, PortParams.IndexOf(":")));
            }
            catch
            {
            }

            if (serialPort.IsOpened)
            {
                serialPort.DTR = true;

                Comms.ResetMode ResetMode = Comms.ResetMode.None;

                PurgeQueue();
                this.serialPort.OnRxFlag += new SPortLib._ISPortAxEvents_OnRxFlagEventHandler(SerialPort_DataReceived);

                if (parameter.Count() > 5)
                    Enum.TryParse(parameter[5], true, out ResetMode);

                switch (ResetMode)
                {
                    case Comms.ResetMode.RTS:
                        /* For resetting ESP32 */
                        serialPort.RTS = true;
                        System.Threading.Thread.Sleep(5);
                        serialPort.RTS = false;
                        if (ResetDelay > 0)
                            System.Threading.Thread.Sleep(ResetDelay);
                        break;

                    case Comms.ResetMode.DTR:
                        /* For resetting Arduino */
                        serialPort.DTR = false;
                        System.Threading.Thread.Sleep(5);
                        serialPort.DTR = true;
                        if (ResetDelay > 0)
                            System.Threading.Thread.Sleep(ResetDelay);
                        break;
                }

#if RESPONSELOG
                if (Resources.DebugFile != string.Empty) try
                {
                    log = new StreamWriter(Resources.DebugFile);
                } catch
                {
                    UserPrompt.Show("Unable to open log file: " + Resources.DebugFile, "ioSender");
                }
#endif
            }
        }

        ~EltimaStream()
        {
#if RESPONSELOG
            if (log != null) try
                {
                    log.Close();
                    log = null;
                }
                catch { }
#endif
            if (!IsClosing && IsOpen)
                Close();
        }

        public Comms.StreamType StreamType { get { return Comms.StreamType.Serial; } }
        public Comms.State CommandState { get { return state; } set { state = value; } }
        public string Reply { get; private set; }
        public bool IsOpen { get { return serialPort != null && serialPort.IsOpened; } }
        public bool IsClosing { get; private set; }
        public int OutCount { get { return serialPort.OutCount; } }

        public void PurgeQueue()
        {
            serialPort.PurgeQueue();
            Reply = string.Empty;
        }

        private Parity ParseParity(string parity)
        {
            Parity res = Parity.None;

            switch (parity)
            {
                case "E":
                    res = Parity.Even;
                    break;

                case "O":
                    res = Parity.Odd;
                    break;

                case "M":
                    res = Parity.Mark;
                    break;

                case "S":
                    res = Parity.Space;
                    break;
            }

            return res;
        }

        public void Close()
        {
            if (!IsClosing && IsOpen)
            {
                IsClosing = true;
                try
                {
                    //serialPort.DataReceived -= SerialPort_DataReceived;
                    //serialPort.DtrEnable = false;
                    //serialPort.RtsEnable = false;
                    //serialPort.DiscardInBuffer();
                    //serialPort.DiscardOutBuffer();
                    System.Threading.Thread.Sleep(100);
                    serialPort.Close();
                    serialPort = null;
                }
                catch { }
                IsClosing = false;
            }
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
            serialPort.Write(ref data, 1);
        }

        protected override void WriteBytesRaw(byte[] bytes, int len)
        {
            serialPort.Write(ref bytes[0], len);
        }

        public void WriteString(string data)
        {
            serialPort.WriteStr(data);
#if RESPONSELOG
            log.WriteLine(data);
            log.Flush();
#endif
        }

        public void WriteCommand(string command)
        {
            state = Comms.State.AwaitAck;

            if (command.Length == 1 && command != GrblConstants.CMD_PROGRAM_DEMARCATION)
                WriteByte((byte)command.ToCharArray()[0]);
            else
            {
                command += "\r";
                serialPort.WriteStr(command);
            }
        }

        public void AwaitAck()
        {
            while (Comms.com.CommandState == Comms.State.DataReceived || Comms.com.CommandState == Comms.State.AwaitAck)
                EventUtils.DoEvents();
        }

        public void AwaitAck(string command)
        {
            PurgeQueue();
            Reply = string.Empty;
            WriteCommand(command);

            while (Comms.com.CommandState == Comms.State.DataReceived || Comms.com.CommandState == Comms.State.AwaitAck)
                EventUtils.DoEvents();
        }

        public void AwaitResponse()
        {
            while (Comms.com.CommandState == Comms.State.AwaitAck)
                EventUtils.DoEvents();
        }

        public void AwaitResponse(string command)
        {
            PurgeQueue();
            Reply = string.Empty;
            WriteCommand(command);

            while (Comms.com.CommandState == Comms.State.AwaitAck)
                System.Threading.Thread.Sleep(15);
        }

        public string GetReply(string command)
        {
            Reply = string.Empty;
            WriteCommand(command);

            AwaitResponse();

            return Reply;
        }

        private void SerialPort_DataReceived()
        {
            int pos = 0;

            lock (input)
            {
                input.Append(serialPort.ReadStr());

                if (EventMode)
                {
                    while (input.Length > 0 && (pos = gp()) > 0)
                    {
                        Reply = pos == 0 ? string.Empty : input.ToString(0, pos - 1);
                        input.Remove(0, pos + 1);
#if RESPONSELOG
                        if (log != null)
                        {
                            log.WriteLine(Reply);
                            log.Flush();
                        }
#endif
                        if (Reply.Length != 0 && DataReceived != null)
                            Dispatcher.BeginInvoke(DataReceived, Reply);

                        state = Reply == "ok" ? Comms.State.ACK : (Reply.StartsWith("error") ? Comms.State.NAK : Comms.State.DataReceived);

                        // Classified-reply tap straight to any subscriber, bypassing the UI dispatcher.
                        // Raised for every reply, not just ack/nak - see the interface doc-comment.
                        ReplyClassified?.Invoke(
                            state == Comms.State.ACK ? Comms.ReplyClass.Ack :
                            state == Comms.State.NAK ? Comms.ReplyClass.Nak :
                            Reply.Length > 0 && Reply[0] == '<' ? Comms.ReplyClass.Status :
                            Comms.ReplyClass.Other,
                            Reply);
                    }
                }
                else
                    ByteReceived?.Invoke(ReadByte());
            }
        }
    }
#endif
}
