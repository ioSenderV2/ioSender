/*
 * PipeServer.cs - part of Grbl Code Sender
 *
 * v0.33 / 2021-05-17 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020-2021, Io Engineering (Terje Io)
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

// https://www.c-sharpcorner.com/article/aborting-thread-vs-cancelling-task/

using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Tasks;

namespace CNC.Controls
{
    public class PipeServer
    {
        // Well-known name of the single-instance pipe. The running instance listens on it (server);
        // a newly launched instance probes it (client) to detect that an instance already exists.
        public const string PipeName = "ioSender";

        public delegate void FileTransferHandler(string filename);
        public static event FileTransferHandler FileTransfer;

        // Raised (on the UI thread) each time another launch connects, so the running instance can
        // bring itself to the foreground - a second launch should surface the existing window.
        public static event Action ActivateRequested;

        public PipeServer(System.Windows.Threading.Dispatcher dispatcher)
        {
            Task.Factory.StartNew(() => RunServer(dispatcher));
        }

        // Client-side single-instance probe: if an instance is already listening, hand it the file to
        // open (if any) and return true (the caller should exit). Returns false on connect timeout -
        // i.e. no instance is running, so the caller is the first instance.
        public static bool TryForwardToRunningInstance(string filename)
        {
            try
            {
                using (var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation))
                {
                    pipeClient.Connect(250);
                    if (!string.IsNullOrEmpty(filename) && File.Exists(filename))
                    {
                        using (var pipe = new StreamWriter(pipeClient))
                            pipe.WriteLine(filename);
                    }
                    return true;
                }
            }
            catch
            {
                return false; // timeout / no server: we are the first instance
            }
        }

        private static void RunServer(System.Windows.Threading.Dispatcher dispatcher)
        {
            string filename; int c;

            try {

                using (var pipeServer = new NamedPipeServerStream(PipeName, PipeDirection.InOut))
                {
                    using (var reader = new StreamReader(pipeServer))
                    {
                        while (true)
                        {
                            filename = string.Empty;

                            pipeServer.WaitForConnection();

                            // Any connection means another launch happened - surface the running window.
                            dispatcher.Invoke(() => ActivateRequested?.Invoke());

                            while (pipeServer.IsConnected)
                            {
                                if ((c = reader.Read()) == -1)
                                    break; // client closed (EOF): no more data on this connection
                                if (c >= ' ')
                                    filename += (char)c;
                                else if (c == 10 && FileTransfer != null && File.Exists(filename))
                                    dispatcher.Invoke(FileTransfer, filename);
                            }
                            pipeServer.Disconnect();
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
