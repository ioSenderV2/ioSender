/*
 * Comms.cs - part of CNC Controls library
 *
 * v0.31 / 2021-04-23 / Io Engineering (Terje Io)
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

namespace CNC.Core
{
    public delegate void DataReceivedHandler(string data);

    public class Comms
    {
        /// <summary>
        /// Marshal a received reply onto the host's context, asynchronously.
        /// Replaces Dispatcher.BeginInvoke(DataReceived, reply) in the stream classes - see their call
        /// sites for why this must stay async: a synchronous marshal blocks the read thread on a busy
        /// host, stalling reads and therefore the stream acks. SynchronizationContext.Post is the
        /// portable equivalent of BeginInvoke (on WPF the context IS the dispatcher's), and the reply
        /// value is captured per call, so order and content are preserved exactly as before.
        ///
        /// A null context - a headless server with no marshalling requirement - invokes inline on the
        /// read thread, which is correct there and keeps CNC.Core free of any UI-thread assumption.
        /// </summary>
        public static void PostTo(System.Threading.SynchronizationContext context, DataReceivedHandler handler, string reply)
        {
            if (handler == null)
                return;

            // Every reply crosses here, on the read thread, before any marshalling - so this is where the
            // wire's own timing is visible, and where an unfiltered trace has to be taken. Upstream of
            // SuspendProcessing, Silent and the console's own filter, so a reply the app then ignores is
            // still recorded. See PollDiag's and WireLog's headers.
            if (PollDiag.Enabled)
                PollDiag.RxArrived();

            WireLog.Rx(reply);

            if (context == null)
                handler(reply);
            else if (PollDiag.Enabled)
            {
                // Stamped here on the READ thread and read again inside the callback, so what is measured is
                // exactly how long this reply waited for the target thread to get to it - the one number that
                // separates "the poller is late" from "the UI thread is saturated".
                double stamp = PollDiag.MarshalStamp();
                context.Post(state => { PollDiag.MarshalArrived(stamp); handler((string)state); }, reply);
            }
            else
                context.Post(state => handler((string)state), reply);
        }

        public enum State
        {
            AwaitAck,
            DataReceived,
            ACK,
            NAK
        }

        public enum ResetMode
        {
            None,
            DTR,
            RTS
        }

        public enum StreamType
        {
            Serial,
            Telnet,
            Websocket
        }

        public const int TXBUFFERSIZE = 4096, RXBUFFERSIZE = 1024;

        public static StreamComms com = null;
    }

    public interface StreamComms
    {
        bool IsOpen { get; }
        int OutCount { get; }
        string Reply { get; }
        Comms.StreamType StreamType { get; }
        Comms.State CommandState { get; set; }
        bool EventMode { get; set; }
        Action<int> ByteReceived { get; set; }

        // Optional tap for ok/error acks, invoked ON THE READ THREAD the instant a reply is assembled -
        // before (and in addition to) the DataReceived marshal to the UI thread. The streamer thread
        // installs this so job flow control never waits on a busy UI dispatcher. Null (the default) =
        // exactly today's behaviour. Implementations must call it only for "ok"/"error" replies and must
        // not block (the handler does a non-blocking enqueue).
        Action<string> AckSink { get; set; }

        // When true, multi-byte writes (WriteBytes/WriteString) are SYNCHRONOUS so back-to-back job lines
        // from the streamer thread can't overlap (a fire-and-forget async write would throw "a write is
        // already in progress"). The streamer sets this true for the duration of a job. Default false =
        // the proven non-blocking path. Single-byte real-time writes (WriteByte) are unaffected and stay
        // non-blocking, so Reset/Feed-Hold/overrides are never delayed by a streamer write.
        bool BlockingWrites { get; set; }

        bool IsReconnecting { get; }

        void Close();
        int ReadByte();
        void WriteByte(byte data);
        void WriteBytes(byte[] bytes, int len);
        void WriteString(string data);
        void WriteCommand(string command);
        string GetReply(string command);
        void AwaitAck();
        void AwaitAck(string command);
        void AwaitResponse(string command);
        void AwaitResponse();
        void PurgeQueue();

        event DataReceivedHandler DataReceived;

        // Raised (on a background timer thread) when the link to the controller is lost
        // and again when it has been re-established. Handlers must marshal to the UI thread.
        event System.Action ConnectionLost;
        event System.Action Reconnected;
    }

    /// <summary>
    /// Transport-agnostic auto-reconnect state machine shared by all <see cref="StreamComms"/>
    /// implementations. A stream calls <see cref="NotifyLost"/> when a read/write detects the
    /// link is gone; the supplied <c>tryReopen</c> delegate is then invoked periodically (on a
    /// background timer thread) until it succeeds, at which point <see cref="Reconnected"/> fires.
    /// Only the loss detection and the reopen step are transport specific - the retry loop and
    /// notifications are identical for serial, network and websocket connections.
    /// </summary>
    public class Reconnector
    {
        private readonly System.Timers.Timer timer;
        private readonly Func<bool> tryReopen;
        private volatile bool reconnecting = false;

        public event System.Action ConnectionLost;
        public event System.Action Reconnected;

        public Reconnector(Func<bool> tryReopen, double retryIntervalMs = 1000d)
        {
            this.tryReopen = tryReopen;
            timer = new System.Timers.Timer(retryIntervalMs) { AutoReset = false };
            timer.Elapsed += (s, e) => Tick();
        }

        public bool IsReconnecting { get { return reconnecting; } }

        /// <summary>Called by the transport when a write/read fails because the link is gone.</summary>
        public void NotifyLost()
        {
            if (reconnecting)
                return;

            reconnecting = true;
            ConnectionLost?.Invoke();
            timer.Start();
        }

        private void Tick()
        {
            bool ok;

            try
            {
                ok = tryReopen();
            }
            catch
            {
                ok = false;
            }

            if (ok)
            {
                reconnecting = false;
                Reconnected?.Invoke();
            }
            else
                timer.Start(); // AutoReset is false, so re-arm for the next attempt
        }

        /// <summary>Abandon any in-progress reconnect attempts (e.g. on an explicit Close).</summary>
        public void Cancel()
        {
            timer.Stop();
            reconnecting = false;
        }
    }

    /// <summary>
    /// Nested message pump, used by the blocking controller handshakes (connect, settings read, macro
    /// execution) to keep the host responsive while they wait for replies. ~70 call sites.
    ///
    /// The WPF implementation (DispatcherFrame + Dispatcher.PushFrame) cannot live in CNC.Core, so the
    /// host installs it - see CNC.Controls.UiPump.Register, called from the same startup line as
    /// UiContext.Register so the two cannot get out of step. A host with no message loop (a headless
    /// server) leaves it null, where doing nothing is correct: there is no UI to keep alive.
    /// </summary>
    public static class EventUtils
    {
        public static System.Action Pump;

        public static void DoEvents()
        {
            var pump = Pump;
            if (pump != null)
                pump();
        }
    }
}
