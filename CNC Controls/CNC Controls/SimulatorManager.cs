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

        // The grblHAL Web Builder build endpoint. "Generate and download firmware" in the browser is a plain
        // POST of a build-definition JSON to this URL; for the Simulator driver it returns a .zip of the compiled
        // Windows executables (grblHAL_sim.exe + grblHAL_validator.exe). We replicate that request directly.
        public const string WebBuilderUrl = "https://svn.io-engineering.com:8443/builder";

        // Prebuilt, patched (homing-capable) simulator published by CI from the Simulator fork as a stable
        // "sim-latest" release asset. Preferred over the web builder (which compiles a stock upstream sim that
        // cannot home). Change the owner here if the fork lives elsewhere.
        public const string SimulatorReleaseUrl =
            "https://github.com/stevenrwood/Simulator/releases/download/sim-latest/grblHAL_sim.exe";

        // User-editable build definition (a "Save selection" JSON exported from the web builder, Simulator/WIN64).
        // Shipped in the simulator folder so the feature set can be changed without a rebuild; a built-in copy is
        // used if the file is missing. To match a different feature set, re-export it from the web builder.
        public const string BuildTemplateName = "sim-build.json";

        private const string DefaultBuildTemplate =
            "{\"driver\":\"Simulator\",\"URL\":\"https://github.com/grblHAL/Simulator\",\"board\":\"WIN64\"," +
            "\"symbols\":[\"PROBE_ENABLE=1\",\"ACCELERATION_TICKS_PER_SECOND=100\",\"CONTROL_ENABLE=70\"]," +
            "\"docker_instance\":\"\"}";

        // The app folder's "simulator" subfolder - where FindExecutable looks first and where a download installs to.
        public static string SimulatorDir()
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "simulator");
        }

        private static string ReadBuildTemplate()
        {
            try
            {
                string path = System.IO.Path.Combine(SimulatorDir(), BuildTemplateName);
                if (System.IO.File.Exists(path))
                {
                    string s = System.IO.File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
            catch { }
            return DefaultBuildTemplate;
        }

        // Obtain the simulator. Prefer a prebuilt release asset (a patched, homing-capable build published by CI
        // from the Simulator fork); fall back to building a stock one via the grblHAL Web Builder when no release
        // is available. Blocking (a web-builder build can take minutes) - call from a background thread.
        public static bool DownloadSimulator(out string error)
        {
            string relErr;
            if (DownloadFromRelease(out relErr))
            {
                error = null;
                return true;
            }

            string webErr;
            if (DownloadFromWebBuilder(out webErr))
            {
                error = null;
                return true;
            }

            error = "Release download: " + relErr + Environment.NewLine + "Web builder: " + webErr;
            return false;
        }

        // GET the prebuilt grblHAL_sim.exe release asset and install it into the simulator subfolder. Returns
        // false (e.g. "no release found" on a 404) so the caller can fall back to the web builder.
        private static bool DownloadFromRelease(out string error)
        {
            error = null;
            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(SimulatorReleaseUrl);
                req.Method = "GET";
                req.UserAgent = "ioSender";   // GitHub asset downloads expect a User-Agent
                req.Timeout = req.ReadWriteTimeout = 2 * 60 * 1000;

                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        error = "the release server returned " + (int)resp.StatusCode + ".";
                        return false;
                    }
                    byte[] bytes;
                    using (var src = resp.GetResponseStream())
                    using (var ms = new System.IO.MemoryStream())
                    {
                        src.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    // Sanity-check it is a Windows executable ('MZ') before installing it.
                    if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                    {
                        error = "the release asset was not an executable.";
                        return false;
                    }
                    string dir = SimulatorDir();
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, SimulatorExeName), bytes);
                    return true;
                }
            }
            catch (System.Net.WebException wex)
            {
                error = "no release found (" + wex.Message + ").";
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Build the simulator from the grblHAL Web Builder: POST the build definition, receive the .zip of
        // compiled executables, and extract grblHAL_sim.exe into the "simulator" subfolder so FindExecutable then
        // locates it. Blocking (a build can take seconds to minutes). Returns false with a reason on failure
        // (no network, server error, or a build report on a 422).
        private static bool DownloadFromWebBuilder(out string error)
        {
            error = null;
            try
            {
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(ReadBuildTemplate());

                // The builder serves TLS on a non-standard port; ensure TLS 1.2 is permitted on .NET 4.6.2.
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(WebBuilderUrl);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/octet-stream";
                req.Timeout = req.ReadWriteTimeout = 6 * 60 * 1000;   // builds can take minutes on a cold cache
                req.ContentLength = payload.Length;
                using (var rs = req.GetRequestStream())
                    rs.Write(payload, 0, payload.Length);

                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        error = "the build server returned " + (int)resp.StatusCode + ".";
                        return false;
                    }
                    byte[] zip;
                    using (var src = resp.GetResponseStream())
                    using (var ms = new System.IO.MemoryStream())
                    {
                        src.CopyTo(ms);
                        zip = ms.ToArray();
                    }
                    return ExtractSimulator(zip, out error);
                }
            }
            catch (System.Net.WebException wex)
            {
                // A failed build (HTTP 422) returns a plain-text build report in the response body - surface it.
                error = wex.Message;
                try
                {
                    if (wex.Response != null)
                        using (var s = wex.Response.GetResponseStream())
                        using (var r = new System.IO.StreamReader(s))
                        {
                            string report = r.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(report))
                                error = "the build failed:\n\n" + report.Trim();
                        }
                }
                catch { }
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public const string SimulatorExeName = "grblHAL_sim.exe";

        // Install the simulator executable from the downloaded build archive. The web builder bundles the
        // validator (grblHAL_validator.exe) alongside it; we don't use that, so only the simulator is kept.
        private static bool ExtractSimulator(byte[] zipBytes, out string error)
        {
            error = null;
            try
            {
                string dir = SimulatorDir();
                System.IO.Directory.CreateDirectory(dir);

                int extracted = 0;
                using (var ms = new System.IO.MemoryStream(zipBytes))
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (!string.Equals(entry.Name, SimulatorExeName, StringComparison.OrdinalIgnoreCase))
                            continue;   // skip the validator and any other archive members

                        string dest = System.IO.Path.Combine(dir, entry.Name);
                        using (var es = entry.Open())
                        using (var fs = new System.IO.FileStream(dest, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                            es.CopyTo(fs);
                        extracted++;
                    }
                }

                if (extracted == 0)
                {
                    error = "the downloaded archive did not contain " + SimulatorExeName + ".";
                    return false;
                }
                return true;
            }
            catch (System.IO.IOException ioex)
            {
                // Most likely the existing exe is locked by a running simulator - tell the user how to clear it.
                error = "could not write the simulator (is it currently running?):\n" + ioex.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // One-shot request to wipe + reformat the simulator's littlefs filesystem on the next launch (appends
        // the -format option). Set from the connect dialog's "Format FS" checkbox; consumed and cleared by the
        // next StartSimulator that actually launches a process, so it never persists across runs.
        public static bool FormatNextStart { get; set; }

        public static bool StartSimulator(string path, string args, bool autoKill)
        {
            try
            {
                if (started != null && !started.HasExited)
                    return true;

                string name = System.IO.Path.GetFileNameWithoutExtension(path);

                // Honour a one-shot filesystem reset requested from the connect dialog, for this launch only.
                if (FormatNextStart)
                {
                    args = string.IsNullOrWhiteSpace(args) ? "-format" : args.Trim() + " -format";
                    FormatNextStart = false;
                }

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

        // Fixture setup file (-setup): defines the simulated spoilboard/stock/toolsetter/tool-change geometry
        // and drives the controller's G28/G30/G59.3 offsets. Kept next to the simulator exe, like MyMachine.DAT.
        public const string SimSetupName = "sim_setup.cfg";

        public static string SimSetupPath()
        {
            string exe = FindExecutable("grblHAL_sim.exe");
            return exe == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), SimSetupName);
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
