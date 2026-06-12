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

        // The bundled simulator EEPROM that mirrors the user's real machine ("My Machine" profile), kept next
        // to the simulator exe so the build's *.DAT exclusion leaves it untouched. Null if the sim isn't bundled.
        public const string MyMachineEepromName = "MyMachine.DAT";

        public static string MyMachineEepromPath()
        {
            string exe = FindExecutable("grblHAL_sim.exe");
            return exe == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), MyMachineEepromName);
        }

        // Build MyMachine.DAT by replaying the given "$id=value" settings into a throwaway, headless instance of
        // the bundled simulator: it serializes them into its NVRAM file exactly as real grblHAL would, so the
        // simulator then boots with the same context as the real controller. Settings the sim doesn't support
        // are simply rejected and skipped. Returns false (with a reason) on failure.
        public static bool BuildMyMachineEeprom(System.Collections.Generic.IList<string> settingCommands, out string error)
        {
            error = null;
            string exe = FindExecutable("grblHAL_sim.exe");
            if (exe == null) { error = "the bundled simulator was not found."; return false; }
            if (settingCommands == null || settingCommands.Count == 0) { error = "no settings to copy."; return false; }

            string eeprom = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), MyMachineEepromName);
            Process sim = null;
            try
            {
                try { if (System.IO.File.Exists(eeprom)) System.IO.File.Delete(eeprom); } catch { }   // start from defaults

                int port = FreeTcpPort();
                var psi = new ProcessStartInfo(exe, string.Format("-e \"{0}\" -p {1}", eeprom, port))
                {
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                sim = Process.Start(psi);

                System.Net.Sockets.TcpClient client = null;
                for (int i = 0; i < 50 && client == null; i++)
                {
                    try { client = new System.Net.Sockets.TcpClient("127.0.0.1", port); }
                    catch { System.Threading.Thread.Sleep(100); }   // wait for the sim to bind its listener
                }
                if (client == null) { error = "could not connect to the bundled simulator."; return false; }

                using (client)
                using (var stream = client.GetStream())
                {
                    byte[] buf = new byte[4096];
                    DrainStream(stream, buf, 400);   // consume the startup banner
                    foreach (var cmd in settingCommands)
                    {
                        byte[] b = System.Text.Encoding.ASCII.GetBytes(cmd + "\r\n");
                        stream.Write(b, 0, b.Length);
                        DrainStream(stream, buf, 50);   // wait for ok/error per setting
                    }
                    System.Threading.Thread.Sleep(250);   // let the final NVRAM write flush to disk
                }
                return System.IO.File.Exists(eeprom);
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally
            {
                try { if (sim != null && !sim.HasExited) { sim.Kill(); sim.WaitForExit(2000); } } catch { }
            }
        }

        private static int FreeTcpPort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static void DrainStream(System.Net.Sockets.NetworkStream stream, byte[] buf, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                try
                {
                    if (stream.DataAvailable)
                        stream.Read(buf, 0, buf.Length);
                    else
                        System.Threading.Thread.Sleep(10);
                }
                catch { break; }
            }
        }
    }
}
