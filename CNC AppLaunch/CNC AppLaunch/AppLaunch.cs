/*
 * AppLaunch.cs - part of CNC Library
 *
 * v2 / 2026-07-09 - repurposed as a generic, application-agnostic relaunch supervisor.
 *
 * Usage:  AppLaunch.exe -ExitCodeToRelaunch <code> -- <exe> [args...]
 *
 * Starts <exe> with the env var EXITCODE_TO_RELAUNCH=<code> set, waits for it, and whenever the
 * child exits with exactly <code> starts it again with the same command line. Any other exit code
 * is propagated (this process exits with it) and the loop ends. The env var is the whole protocol -
 * the child reads it to know it is supervised and which code triggers a relaunch. AppLaunch has no
 * knowledge of ioSender; it is a plain parent process.
 *
 * Legacy fallback: if invoked the old way (a single existing file path, from a lingering file
 * association) it cold-starts ioSender.exe with that file - ioSender itself now owns single-instance
 * file forwarding.
 *
 * v0.33 / 2021-05-17 / Io Engineering (Terje Io) - original single-instance file forwarder.
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

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CNC.AppLaunch
{
    class AppLaunch
    {
        const string RelaunchEnvVar = "EXITCODE_TO_RELAUNCH";

        static int Main(string[] args)
        {
            // Relauncher mode:  -ExitCodeToRelaunch <code> -- <exe> [args...]
            int sep = Array.IndexOf(args, "--");
            int relaunchCode;
            if (args.Length >= 4 && args[0] == "-ExitCodeToRelaunch" &&
                 int.TryParse(args[1], out relaunchCode) && sep >= 2 && sep + 1 < args.Length)
            {
                string exe = args[sep + 1];

                // Re-quote the child args (they arrived already parsed by the OS) into a single
                // command-line string; a matching quoter on the child side round-trips them exactly.
                var sb = new StringBuilder();
                for (int i = sep + 2; i < args.Length; i++)
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(QuoteArg(args[i]));
                }
                string childArgs = sb.ToString();

                for (;;)
                {
                    int code;
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = childArgs,
                            UseShellExecute = false
                        };
                        psi.EnvironmentVariables[RelaunchEnvVar] = relaunchCode.ToString();

                        using (var child = Process.Start(psi))
                        {
                            child.WaitForExit();
                            code = child.ExitCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Rare (bad path / missing exe). Leave a breadcrumb in %TEMP% - the app folder may
                        // be read-only (Program Files) and AppLaunch has no window to show an error in.
                        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "applaunch.err.txt"), ex.ToString()); } catch { }
                        return 3; // couldn't start the child - give up
                    }

                    if (code != relaunchCode)
                        return code; // normal quit / crash / anything else: propagate and stop
                    // else: relaunch the same command line
                }
            }

            // Legacy fallback: a lingering file association may still hand us a single file path.
            // ioSender now owns single-instance file forwarding, so just cold-start it with the file.
            if (args.Length == 1 && File.Exists(args[0]))
            {
                string cmd = AppDomain.CurrentDomain.BaseDirectory + "ioSender.exe";
                if (File.Exists(cmd))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = cmd, Arguments = QuoteArg(args[0]) });
                    }
                    catch { return 2; }
                }
                return 0;
            }

            return 1;
        }

        // Quote a single argument per the CommandLineToArgvW rules the child uses to re-parse it,
        // so a value survives the extra round-trip through this process unchanged.
        static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; ; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { i++; backslashes++; }

                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                else if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
