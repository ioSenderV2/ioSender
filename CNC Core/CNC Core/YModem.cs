/*
 * YModem.cs - part of CNC Controls library
 *
 * v0.47 / 2025-11-11 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2021-2025, Io Engineering (Terje Io)
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

using System.IO;
using System.Threading;

namespace CNC.Core
{
    public class YModem
    {
        public delegate void DataTransferredHandler(long size, long transferred);

        private const byte SOH = 0x01, STX = 0x02, EOT = 0x04, ACK = 0x06, NAK = 0x15, CAN = 0x18, C = (byte)'C';

        private int packetNum, bytes;
        private byte[] hdr = new byte[3], payload = new byte[1024], crc = new byte[2];
        private int response;

        public event DataTransferredHandler DataTransferred;

        private enum TransferState {
            ACK,
            NAK,
            CAN
        };

        public bool Upload (string path)
        {
            return Upload(path, null);
        }

        // Upload local file <path>, telling the controller to store it as <remoteName> (sent verbatim in the
        // YModem block 0 name field, which the firmware passes to vfs_open). remoteName may be an absolute path
        // (e.g. "/littlefs/tc.macro") to target a specific filesystem, or a bare name to write to the current
        // working directory. Null => use the local file's base name (legacy behaviour).
        public bool Upload (string path, string remoteName)
        {
            TransferState state = TransferState.NAK;
            FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            long bytesRemaining = fileStream.Length;

            // Stop the status poller for the whole transfer. YModem is a framed byte protocol on a link the
            // poller otherwise writes a '?' into every 200ms, and that byte lands INSIDE a packet - there is
            // no layer here to keep them apart.
            //
            // Traced on the wire 2026-08-06, and the shape of it is unmistakable: packets 0, 1 and 2 were
            // acked 23-25ms apart, a poll went out at 58.291 between packet 2's CRC and packet 3's header,
            // packet 2->3 then took 244ms (ten times longer), and packet 3 was never acked again - ten
            // retransmissions at 5s apart, CAN, and the outer retry in AtcMacros.YModemWrite started over.
            // The controller was left mid-protocol and mute, which is what "the tc.macro install hangs"
            // actually was. It is not a race that a longer timeout can win: while a transfer is in flight
            // this link has exactly one conversation on it.
            //
            // Suspend() also purges the queue, and Resume() restarts LinkMonitor's clock - which matters
            // because LinkMonitor.Rx() only stamps in Comms.PostTo, so it sees zero RX for the whole
            // transfer BY CONSTRUCTION and would otherwise report the link lost the moment polling resumed.
            PollGrbl.Suspend();

            Comms.com.EventMode = false;
            Comms.com.PurgeQueue();

            try
            {
                ClearPayload();

                if (TransferInitalPacket(remoteName ?? Path.GetFileName(path), fileStream) == TransferState.ACK)
                {
                // Always send 128-byte (SOH) blocks, never 1024-byte (STX). grblHAL's YModem receiver buffers
                // into a ring of RX_BUFFER_SIZE (1024) whose usable capacity is 1023 bytes - so a full 1024-byte
                // STX packet (hdr 3 + payload 1024 + crc 2 = 1029) cannot fit. Over a slow serial link the
                // firmware drains byte-by-byte and never fills, but over telnet the whole block arrives in a TCP
                // burst faster than the foreground loop drains it -> buffer overflow -> corrupt packet -> the
                // transfer stalls/retries. 128-byte blocks (133 bytes on the wire) fit with margin and are
                // reliable on any transport. (More blocks = more round-trips, but these files are small.)
                    do
                    {
                        packetNum++;
                        if (bytesRemaining < 128)
                            ClearPayload();
                        bytes = fileStream.Read(payload, 0, 128);
                        bytesRemaining -= bytes;
                        DataTransferred?.Invoke(fileStream.Length, fileStream.Length - bytesRemaining);
                        state = TransferPacket(128);
                    } while (bytesRemaining > 0 && state == TransferState.ACK);

                    if (state == TransferState.ACK)
                    {
                        hdr[0] = EOT;
                        Comms.com.WriteBytes(hdr, 1);
                    }
                }

                Thread.Sleep(100);
            }
            finally
            {
                // Restore in a finally, and in this order. Before, a throw anywhere above left EventMode
                // false for the rest of the session - the app keeps polling but every reply is routed to
                // ByteReceived instead of Comms.PostTo, so the UI silently stops updating on a link that is
                // working. The file stream was leaked on that path too.
                try { fileStream.Dispose(); } catch { }
                try { Comms.com.PurgeQueue(); } catch { }
                Comms.com.EventMode = true;
                PollGrbl.Resume();
            }

            return state == TransferState.ACK;
        }

        private TransferState TransferInitalPacket (string remoteName, FileStream fileStream)
        {
            int i, j = 0;
            char[] fileName = remoteName.ToCharArray(), fileSize = fileStream.Length.ToString().ToCharArray();

            for (i = 0; i < fileName.Length; i++)
                payload[j++] = (byte)fileName[i];

            j++;

            for (i = 0; i < fileSize.Length; i++)
                payload[j++] = (byte)fileSize[i];

            packetNum = 0;

            return TransferPacket(128);
        }

        private TransferState TransferPacket(int length)
        {
            TransferState state;
            uint errors = 0, crc16 = CRC16.Calculate(payload, length);

            hdr[0] = length == 128 ? SOH : STX;
            hdr[1] = (byte)(packetNum & 0xFF);
            hdr[2] = (byte)(hdr[1] ^ 0xFF);
            crc[0] = (byte)((crc16 >> 8) & 0xFF);
            crc[1] = (byte)(crc16 & 0xFF);

            do
            {
                state = Send(length);
                if (state == TransferState.NAK)
                    errors++;
            } while (state == TransferState.NAK && errors < 10);

            return errors < 10 ? state : TransferState.CAN;
        }

        private void GetByte (int c)
        {
            response = c;
        }

        private TransferState Send(int length)
        {
            // NAK, not ACK. The switch below has no default, so any byte that is not ACK/NAK/CAN used to
            // leave this at its initializer and report a packet the receiver never acknowledged as
            // delivered - the transfer would advance over a block that never landed and the file would be
            // silently corrupt. Anything unexpected is now a failed packet, which TransferPacket retries.
            TransferState state = TransferState.NAK;
            bool? wait = null;
            CancellationToken cancellationToken = new CancellationToken();

            Comms.com.PurgeQueue();
            Comms.com.WriteBytes(hdr, 3);
            Comms.com.WriteBytes(payload, length);

            response = NAK;

            // Generous per-packet ACK timeouts: writing each block to a used/fragmented flash filesystem
            // (littlefs garbage collection) can take several seconds, and too short a wait makes the transfer
            // retry/abort mid-file - leaving a truncated, hard-to-delete file. 10s to open, 5s per data block.
            int ackTimeout = packetNum == 0 ? 10000 : 5000;

            // IsBackground so a worker still parked in SingleEvent can't keep the process alive on close - a
            // foreground worker here is exactly what wedged shutdown before (see AtcMacros.ReadControllerFile,
            // which this now mirrors). The try/catch matters just as much: without it, an exception inside
            // SingleEvent left 'wait' null forever and the DoEvents loop below span the UI thread with no way
            // out - an unrecoverable hang needing exit/restart, reported against a tc.macro upload.
            new Thread(() =>
            {
                try
                {
                    wait = WaitFor.SingleEvent<int>(
                        cancellationToken,
                        s => GetByte(s),
                        a => Comms.com.ByteReceived += a,
                        a => Comms.com.ByteReceived -= a,
                        ackTimeout, () => Comms.com.WriteBytes(crc, 2));
                }
                catch { wait = false; }
            }) { IsBackground = true }.Start();

            // Hard wall-clock cap on top of the worker's own timeout, so even a worker that never returns at
            // all cannot hang the UI. Falling out with 'response' still NAK reads as a failed packet, which
            // Upload's retry/abort logic already handles - the transfer fails cleanly instead of wedging.
            // Sleep between pumps. DoEvents is not free: each call allocates a DispatcherFrame, a delegate
            // and a DispatcherOperation (see UiPump), and this loop used to spin as fast as the CPU allows.
            // A packet that never gets acked waits the full 7s here, ten times over, three times over
            // again from AtcMacros' outer retry - tens of millions of frames in a 32-BIT address space.
            // That is not a slow leak, it is an eviction notice: ioSender died of OutOfMemoryException
            // twice on 2026-08-06 (14:17 and 15:07), both times parked in this loop, and the second time
            // it went from 142MB to exhausted in eight minutes. The stack blamed a timer callback both
            // times, because the timer was merely the allocation unlucky enough to ask last.
            // 1ms still pumps ~1000 times a second, far more than a responsive UI needs.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (wait == null && sw.ElapsedMilliseconds < ackTimeout + 2000)
            {
                EventUtils.DoEvents();
                Thread.Sleep(1);
            }

            switch (response)
            {
                case ACK:
                    state = TransferState.ACK;
                    break;

                case NAK:
                    state = TransferState.NAK;
                    break;

                case CAN:
                    state = TransferState.CAN;
                    break;
            }

            if(packetNum == 0) // Read 'C' from input
                Comms.com.ReadByte();

            return state;
        }

        private void ClearPayload ()
        {
            int i = payload.Length;
            do
            {
                payload[--i] = 0;
            } while (i > 0);
        }
    }

    class CRC16
    {
        public static uint Calculate(byte[] buf, int len)
        {
            uint x, i = 0, crc = 0;

            do
            {
                x = (crc >> 8) ^ buf[i++];
                x ^= x >> 4;
                crc = ((crc << 8) ^ (x << 12) ^ (x << 5) ^ x) & 0xFFFF;
                len--;
            } while (len > 0);

            return crc;
        }
    }
}
