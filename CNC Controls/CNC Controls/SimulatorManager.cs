using System;
using System.Diagnostics;

namespace CNC.Controls
{
    public static class SimulatorManager
    {
        private static Process started = null;

        // Locate a bundled executable (simulator or validator): looks in the app folder's "simulator"
        // subfolder first, then the app folder itself. Returns the full path, or null if not found.
        public static string FindExecutable(string exeName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] {
                System.IO.Path.Combine(baseDir, "simulator", exeName),
                System.IO.Path.Combine(baseDir, exeName)
            })
            {
                if (System.IO.File.Exists(c))
                    return c;
            }
            return null;
        }

        public static bool StartSimulator(string path, string args, bool autoKill)
        {
            try
            {
                if (started != null && !started.HasExited)
                    return true;

                string name = System.IO.Path.GetFileNameWithoutExtension(path);

                // Reap stale instances of the managed simulator before launching. Our normal cleanup
                // (KillStarted via Application.Exit / ProcessExit) does NOT run when the VS debugger is
                // stopped, so a previous debug session leaves the simulator alive - and it still holds
                // the listen port. A fresh launch then cannot bind, and the connect lands on that zombie,
                // which has already served its single client and never responds, hanging startup. Killing
                // leftover copies of our own bundled exe first guarantees a clean single instance.
                if (autoKill)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                        try { p.Kill(); p.WaitForExit(2000); } catch { }
                }

                // Snapshot existing instances so we can identify the one we launch below.
                var existing = new System.Collections.Generic.HashSet<int>();
                foreach (var p in Process.GetProcessesByName(name))
                    existing.Add(p.Id);

                // Launch via cmd's "start" so the console window gets a useful title (the file name,
                // shown on the taskbar) and starts minimized. "start" detaches the child, so we then
                // locate it for Stop / auto-kill. Format: start "<title>" /min "<exe>" <args>
                string startArgs = string.Format("/c start \"{0}\" /min \"{1}\" {2}",
                    name, path, args);   // window/taskbar title = file name without extension
                var psi = new ProcessStartInfo("cmd.exe", startArgs)
                {
                    WorkingDirectory = System.IO.Path.GetDirectoryName(path),
                    UseShellExecute = false,
                    CreateNoWindow = true   // hide the transient cmd window
                };
                Process.Start(psi); // cmd runs hidden and exits once "start" has launched the simulator

                // Find the simulator process just spawned (usually within a few hundred ms).
                started = null;
                for (int i = 0; i < 40 && started == null; i++)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                        if (!existing.Contains(p.Id)) { started = p; break; }
                    if (started == null)
                        System.Threading.Thread.Sleep(50);
                }

                if (started == null)
                    return false;

                if (autoKill)
                {
                    // Attach once - remove first so repeated start/stop cycles don't stack duplicate handlers.
                    AppDomain.CurrentDomain.ProcessExit -= CurrentDomain_ProcessExit;
                    AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
                    try
                    {
                        System.Windows.Application.Current.Exit -= Current_Exit;
                        System.Windows.Application.Current.Exit += Current_Exit;
                    }
                    catch { }
                }

                return true;
            }
            catch { return false; }
        }

        private static void Current_Exit(object sender, System.Windows.ExitEventArgs e)
        {
            KillStarted();
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            KillStarted();
        }

        public static bool StopSimulator()
        {
            try
            {
                if (started != null && !started.HasExited)
                {
                    started.Kill();
                    started = null;
                    return true;
                }
            }
            catch { }

            started = null;
            return false;
        }

        public static bool IsSimulatorRunning
        {
            get { return started != null && !started.HasExited; }
        }

        private static void KillStarted()
        {
            try
            {
                if (started != null && !started.HasExited)
                {
                    try { started.Kill(); } catch { }
                    started = null;
                }
            }
            catch { }
        }
    }
}
