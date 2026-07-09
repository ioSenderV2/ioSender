/*
 * RelaunchSupervisor.cs - part of Grbl Code Sender
 *
 * Shared protocol for the AppLaunch relaunch supervisor.
 *
 * AppLaunch.exe can wrap ioSender as a tiny, ioSender-agnostic parent process: it starts the
 * child with the env var EXITCODE_TO_RELAUNCH set to a chosen sentinel, waits, and whenever the
 * child exits with exactly that code it starts it again (any other code is propagated and the
 * supervisor quits). The env var IS the whole handshake - its presence tells the child "you are
 * supervised", and its value is the exit code that triggers a relaunch.
 *
 * This lets an in-app "Restart to apply" become a clean Application.Shutdown(RelaunchExitCode)
 * with no in-process Process.Start - so there is no splash-over-teardown race (see #81). When not
 * supervised (headless/CI, debugger, or AppLaunch.exe absent) the caller falls back to relaunching
 * itself in-process.
 */

using System;

namespace CNC.Core
{
    public static class RelaunchSupervisor
    {
        // Sentinel exit code that asks the supervisor to relaunch us. Chosen distinct from a normal
        // quit (0) and from the crash sentinel (App.CrashExitCode = 0xFA11 = 64017) so a crash never
        // triggers a relaunch loop.
        public const int RelaunchExitCode = 240;

        // Env var that carries the protocol: set by AppLaunch on the child, read here.
        public const string EnvVar = "EXITCODE_TO_RELAUNCH";

        // True when this process is running under the AppLaunch supervisor (the env var is present).
        public static bool IsSupervised
        {
            get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)); }
        }
    }
}
