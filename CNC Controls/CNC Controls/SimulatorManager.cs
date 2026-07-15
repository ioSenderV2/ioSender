using System;
using System.Diagnostics;
using System.Globalization;

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
            "https://github.com/ioSenderV2/Simulator/releases/download/sim-latest/grblHAL_sim.exe";

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

        // The manually-built simulator's home (Settings > Simulator, see BuildManualOptionSymbols /
        // EnsureAppDataSimulator below) - independent of the auto-matched one above, which lives in the app
        // folder and is keyed to the connected controller's options rather than a user's explicit picks.
        // %AppData%\Simulator (not under \ioSender\) so it survives an app reinstall/relocate.
        public static string AppDataSimulatorDir()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simulator");
        }

        public static string AppDataSimulatorExePath()
        {
            return System.IO.Path.Combine(AppDataSimulatorDir(), SimulatorExeName);
        }

        // The Connect dialog's gate for the Simulator tab: only offer it once something has actually been built.
        public static bool AppDataSimulatorPresent()
        {
            return System.IO.File.Exists(AppDataSimulatorExePath());
        }

        // True when we're talking to a real grblHAL controller right now (not the bundled simulator itself,
        // and not a plain grbl that lacks the NEWOPT-derived properties BuildOptionSymbols/the manual picker
        // read). The precondition for Settings > Simulator to default its picks to match the connected
        // hardware, and for offering to copy its live NVRAM into the manually-built simulator.
        public static bool IsRealControllerConnected()
        {
            return CNC.Core.Comms.com != null && CNC.Core.Comms.com.IsOpen &&
                   !AppConfig.Settings.Base.StartSimulator && CNC.Core.GrblInfo.IsGrblHAL;
        }

        // True if a process matching the exe's name is running, regardless of who started it (a prior ioSender
        // session, or the user by hand) - unlike IsSimulatorRunning below, which only tracks an instance THIS
        // manager launched. Used to decide whether a fresh launch is needed at all.
        public static bool IsProcessRunningByExe(string exePath)
        {
            try { return Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exePath)).Length > 0; }
            catch { return false; }
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
        // the bundled (app-relative) simulator - see BuildEeprom below for how. Returns false (with a reason)
        // on failure.
        public static bool BuildMyMachineEeprom(System.Collections.Generic.IList<string> settingCommands, out string error)
        {
            error = null;
            string exe = FindExecutable("grblHAL_sim.exe");
            if (exe == null) { error = "the bundled simulator was not found."; return false; }

            string eeprom = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), MyMachineEepromName);
            return BuildEeprom(exe, eeprom, settingCommands, out error);
        }

        // Same idea, for the manually-built %AppData%\Simulator simulator: writes EEPROM.DAT next to it -
        // grblHAL's own default NVRAM filename (the sim falls back to it when launched with no -e and no
        // MyMachine.DAT present, see the Simulator repo's main.c), so nothing else needs to be told about it.
        public const string AppDataEepromName = "EEPROM.DAT";

        public static string AppDataEepromPath()
        {
            return System.IO.Path.Combine(AppDataSimulatorDir(), AppDataEepromName);
        }

        public static bool BuildAppDataEeprom(System.Collections.Generic.IList<string> settingCommands, out string error)
        {
            error = null;
            if (!AppDataSimulatorPresent()) { error = "no simulator has been built yet."; return false; }
            return BuildEeprom(AppDataSimulatorExePath(), AppDataEepromPath(), settingCommands, out error);
        }

        // Shared: replay "$id=value" settings into a throwaway, headless instance of the given simulator exe -
        // it serializes them into destPath's NVRAM exactly as real grblHAL would, so a later normal launch
        // pointed at that folder boots with the same context as the connected controller. Settings the sim
        // doesn't support are simply rejected and skipped.
        private static bool BuildEeprom(string exePath, string destPath, System.Collections.Generic.IList<string> settingCommands, out string error)
        {
            error = null;
            if (settingCommands == null || settingCommands.Count == 0) { error = "no settings to copy."; return false; }

            Process sim = null;
            try
            {
                try { if (System.IO.File.Exists(destPath)) System.IO.File.Delete(destPath); } catch { }   // start from defaults

                int port = FreeTcpPort();
                var psi = new ProcessStartInfo(exePath, string.Format("-e \"{0}\" -p {1}", destPath, port))
                {
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath),
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
                if (client == null) { error = "could not connect to the simulator."; return false; }

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
                return System.IO.File.Exists(destPath);
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

        // ---- Option-matched simulator -------------------------------------------------------------
        //
        // Keep the bundled simulator's build in step with whatever controller ioSender is connected to,
        // so a probe/rotation/multi-axis feature behaves on the sim exactly as on the real firmware.
        //
        // Flow (EnsureMatchedSimulator): read the controller's options from GrblInfo -> a set of compile
        // SYMBOLS -> a short SIGNATURE. If grblHAL_sim.exe already matches that signature, done. Else look
        // for a locally-cached grblHAL_sim-<sig>.exe; else download a sim-<sig> release from the fork (a
        // shared remote cache, public - no token); else dispatch the parameterized build-matched-sim CI
        // workflow (the only step that needs a token) and let the next connect pick up the result.

        // Repo hosting the build-matched-sim workflow and the sim-<sig> release cache (ioSender V2 org).
        public const string SimulatorRepo = "ioSenderV2/Simulator";
        public const string MatchedWorkflowFile = "build-matched-sim.yml";
        // ref the dispatch runs on - integration, NOT the repo's default branch (master). master has never
        // carried littlefs/ATC/YModem/macro-plugin support (a 594-line driver.c gap) - a master-built sim
        // silently fell back to a bare manual-pause M6, never actually exercising macro-driven ATC the way
        // a real tc.macro-based machine does. integration has the full feature set and is what a CI build
        // needs to match a real controller's behavior.
        public const string MatchedWorkflowRef = "integration";
        // Records which option signature the active grblHAL_sim.exe was built for.
        private const string SigMarkerName = "grblHAL_sim.sig";

        public enum MatchResult { AlreadyCurrent, InstalledFromCache, InstalledFromRelease, BuildTriggered, Failed }

        // Signatures already dispatched this session, so a reconnect while a build is still running does not
        // queue a duplicate CI run (the build's sim-<sig> release isn't up yet, so the release probe misses).
        private static readonly System.Collections.Generic.HashSet<string> _dispatched =
            new System.Collections.Generic.HashSet<string>();

        // Map the connected controller's build options (GrblInfo) to the simulator's compile symbols, and
        // derive a stable signature from them. Identical option sets always yield the same symbols and the
        // same signature (hence the same cached exe / sim-<sig> release). The template baked into the fork's
        // CMakeLists already fixes littlefs/expressions/etc; only the behaviour-affecting, controller-reported
        // options are mapped here.
        public static string BuildOptionSymbols(out string signature)
        {
            var symbols = new System.Collections.Generic.List<string>();

            // Axis count is the headline option - a 4-axis controller needs a 4-axis sim.
            symbols.Add("N_AXIS=" + CNC.Core.GrblInfo.NumAxes);

            // A probe input (G38.x) - exercised by the Load Stock / probing flows.
            if (CNC.Core.GrblInfo.HasProbe)
                symbols.Add("PROBE_ENABLE=1");

            // Coordinate-system rotation (WCSROT): match it so the Load Stock skew->WCS rotation can be
            // validated on the sim exactly as on the real controller.
            if (CNC.Core.GrblInfo.RotationSupported)
                symbols.Add("ROTATION_ENABLE=1");

            symbols.Sort(StringComparer.Ordinal);   // canonical order -> stable signature
            string flags = string.Join(" ", symbols);
            signature = ShortHash(flags);
            return flags;
        }

        private static string ShortHash(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty));
                var sb = new System.Text.StringBuilder(12);
                for (int i = 0; i < 6; i++)
                    sb.Append(h[i].ToString("x2"));   // 12 hex chars - short but collision-safe here
                return sb.ToString();
            }
        }

        private static string CachedSimPath(string sig)
        {
            return System.IO.Path.Combine(SimulatorDir(), "grblHAL_sim-" + sig + ".exe");
        }

        // Signature the active grblHAL_sim.exe currently matches (null if unknown/none).
        public static string ActiveSignature()
        {
            try
            {
                string p = System.IO.Path.Combine(SimulatorDir(), SigMarkerName);
                if (System.IO.File.Exists(p))
                    return System.IO.File.ReadAllText(p).Trim();
            }
            catch { }
            return null;
        }

        private static void SetActiveSignature(string sig)
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(SimulatorDir(), SigMarkerName), sig); }
            catch { }
        }

        // Make the cached grblHAL_sim-<sig>.exe the active grblHAL_sim.exe and record its signature.
        private static bool Activate(string cachedPath, string sig, out string error)
        {
            error = null;
            try
            {
                string active = System.IO.Path.Combine(SimulatorDir(), SimulatorExeName);
                if (!string.Equals(cachedPath, active, StringComparison.OrdinalIgnoreCase))
                    System.IO.File.Copy(cachedPath, active, true);
                SetActiveSignature(sig);
                return true;
            }
            catch (System.IO.IOException ioex)
            {
                error = "could not install the matched simulator (is it running?): " + ioex.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // GET the sim-<sig> release asset bytes (public, no token). Null (with a reason) on any failure,
        // including "not built yet" (a 404) - callers use that to fall back to dispatching a build / keep
        // polling. Shared by the auto-matched (app-relative) and manual (AppData) install paths below.
        private static byte[] DownloadReleaseBytes(string sig, out string error)
        {
            error = null;
            string url = "https://github.com/" + SimulatorRepo + "/releases/download/sim-" + sig + "/" + SimulatorExeName;
            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "ioSender";
                req.Timeout = req.ReadWriteTimeout = 2 * 60 * 1000;

                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        error = "the release server returned " + (int)resp.StatusCode + ".";
                        return null;
                    }
                    byte[] bytes;
                    using (var src = resp.GetResponseStream())
                    using (var ms = new System.IO.MemoryStream())
                    {
                        src.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                    {
                        error = "the release asset was not an executable.";
                        return null;
                    }
                    return bytes;
                }
            }
            catch (System.Net.WebException wex)
            {
                error = "no sim-" + sig + " release yet (" + wex.Message + ").";
                return null;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // GET the sim-<sig> release asset into the local cache, then activate it. Returns false (e.g. a 404
        // "not built yet") so the caller can fall back to dispatching a build.
        private static bool DownloadMatchedRelease(string sig, out string error)
        {
            byte[] bytes = DownloadReleaseBytes(sig, out error);
            if (bytes == null)
                return false;
            System.IO.Directory.CreateDirectory(SimulatorDir());
            System.IO.File.WriteAllBytes(CachedSimPath(sig), bytes);
            return Activate(CachedSimPath(sig), sig, out error);
        }

        // Token used ONLY to dispatch the build workflow (downloads are public). Read from the GH_TOKEN /
        // GITHUB_TOKEN environment variable (process first, then the persisted User scope); null if unset.
        public static string GitHubToken()
        {
            foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
                foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User })
                {
                    try
                    {
                        string v = Environment.GetEnvironmentVariable(name, scope);
                        if (!string.IsNullOrWhiteSpace(v))
                            return v.Trim();
                    }
                    catch { }
                }
            return null;
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // POST a workflow_dispatch for build-matched-sim (sig + build_flags). GitHub answers 204 on success.
        // Needs a token with 'workflow' scope; without one, returns a helpful manual gh command as the error.
        private static bool DispatchBuild(string sig, string flags, out string error)
        {
            error = null;
            string token = GitHubToken();
            if (string.IsNullOrEmpty(token))
            {
                error = "no matching simulator (signature " + sig + ") and no GH_TOKEN set to build one.\n" +
                        "Set a GitHub token with 'workflow' scope in GH_TOKEN, or build it manually:\n" +
                        "  gh workflow run " + MatchedWorkflowFile + " -R " + SimulatorRepo +
                        " --ref " + MatchedWorkflowRef + " -f sig=" + sig + " -f build_flags=\"" + flags + "\"";
                return false;
            }
            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                string url = "https://api.github.com/repos/" + SimulatorRepo +
                             "/actions/workflows/" + MatchedWorkflowFile + "/dispatches";
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "POST";
                req.UserAgent = "ioSender";
                req.Accept = "application/vnd.github+json";
                req.Headers.Add("Authorization", "Bearer " + token);
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                req.ContentType = "application/json";
                req.Timeout = req.ReadWriteTimeout = 30 * 1000;

                string body = "{\"ref\":\"" + MatchedWorkflowRef + "\",\"inputs\":{\"sig\":\"" +
                              JsonEscape(sig) + "\",\"build_flags\":\"" + JsonEscape(flags) + "\"}}";
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(body);
                req.ContentLength = payload.Length;
                using (var rs = req.GetRequestStream())
                    rs.Write(payload, 0, payload.Length);

                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode == 204 || resp.StatusCode == System.Net.HttpStatusCode.NoContent)
                        return true;
                    error = "the workflow dispatch returned " + (int)resp.StatusCode + ".";
                    return false;
                }
            }
            catch (System.Net.WebException wex)
            {
                error = "the workflow dispatch failed: " + wex.Message;
                try
                {
                    if (wex.Response != null)
                        using (var s = wex.Response.GetResponseStream())
                        using (var r = new System.IO.StreamReader(s))
                        {
                            string m = r.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(m))
                                error += " - " + m.Trim();
                        }
                }
                catch { }
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Ensure grblHAL_sim.exe matches the connected controller's options. Non-blocking with respect to a
        // CI build: if a matched sim isn't available yet it dispatches the build and returns BuildTriggered,
        // leaving any existing sim in place to use meanwhile (the next connect picks up the finished build).
        // Blocking on network I/O (release probe / dispatch) - call from a background thread.
        public static MatchResult EnsureMatchedSimulator(out string signature, out string detail)
        {
            detail = null;
            string flags = BuildOptionSymbols(out signature);

            // 1. Already the active build?
            if (signature == ActiveSignature() && FindExecutable(SimulatorExeName) != null)
                return MatchResult.AlreadyCurrent;

            // 2. Cached locally from a previous build?
            if (System.IO.File.Exists(CachedSimPath(signature)))
            {
                if (Activate(CachedSimPath(signature), signature, out detail))
                    return MatchResult.InstalledFromCache;
                return MatchResult.Failed;
            }

            // 3. Available as a sim-<sig> release (shared remote cache, public download)?
            string relErr;
            if (DownloadMatchedRelease(signature, out relErr))
                return MatchResult.InstalledFromRelease;

            // 4. Not built yet - dispatch the parameterized CI build (needs a token). Skip a re-dispatch if we
            // already kicked this signature off this session (its release just isn't published yet).
            if (_dispatched.Contains(signature))
            {
                detail = "a matching simulator (signature " + signature + ") is still building.";
                return MatchResult.BuildTriggered;
            }

            string dispErr;
            if (DispatchBuild(signature, flags, out dispErr))
            {
                _dispatched.Add(signature);
                detail = "building a matching simulator (signature " + signature +
                         "); it will be installed on the next connect once ready.";
                return MatchResult.BuildTriggered;
            }

            detail = dispErr;
            return MatchResult.Failed;
        }

        // Poll for a just-dispatched sim-<sig> release to appear and install it when it does. Intended to run
        // on a background thread after EnsureMatchedSimulator returned BuildTriggered, so the matched sim is
        // ready without a reconnect. Gives up after the timeout (a build is a few minutes); harmless if it
        // does - the next connect will find the release. Returns true if it installed the matched sim.
        public static bool PollForMatchedRelease(string sig, int timeoutSeconds = 600)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(20 * 1000);   // builds take minutes; poll gently
                string err;
                if (DownloadMatchedRelease(sig, out err))
                    return true;
            }
            return false;
        }

        // ---- Manual/Settings-tab build -> %AppData%\Simulator -----------------------------------------------
        //
        // Settings > Simulator lets the user pick options directly instead of relying on the auto-matched flow
        // above (which derives them from the connected controller and targets the app-relative simulator\
        // folder). Reuses the same build-matched-sim CI workflow + sim-<sig> release cache (so a signature
        // already built for the auto-matched flow, or by another user, is picked up for free) but always
        // installs as a plain grblHAL_sim.exe (no -<sig> suffix) into AppDataSimulatorDir() - that fixed path
        // is the Connect dialog's Simulator-tab gate (AppDataSimulatorPresent), so it doesn't need to know
        // about signatures at all.

        // The human-readable record of what was last built, written alongside the exe - this IS "the json file
        // used to specify build options" the Settings tab reads back to restore its picks across sessions, and
        // what a user could hand-edit/inspect without decoding a hash. Both files live under
        // AppDataSimulatorDir() (%AppData%\Simulator) - never the app's own install folder, so a build works
        // under an all-users (admin-owned, e.g. Program Files) install with no elevation.
        public const string AppDataOptionsName = "sim-options.json";

        public static string AppDataOptionsPath()
        {
            return System.IO.Path.Combine(AppDataSimulatorDir(), AppDataOptionsName);
        }

        // Every compile-time option Settings > Simulator can toggle. Lathe mode, spindle variability, and
        // tool-change mode itself are runtime $-settings (confirmed against grbl core), NOT compile-time -
        // they don't belong here; only genuine #define-gated capabilities do. Ganged/auto-square target Y
        // specifically (not per-axis X/Y/Z) - the common gantry-router setup this is built for.
        public sealed class ManualSimOptions
        {
            public int Axes;
            public bool Probe;
            public bool Rotation;
            public bool LatheUvw;
            public bool SafetyDoor;
            public bool EStop;
            public bool YGanged;
            public bool YAutoSquare;
            public string Signature;   // only meaningful when read back from sim-options.json
        }

        // The options the currently-installed %AppData%\Simulator\grblHAL_sim.exe was built for (null if
        // unknown/none/unparsable) - lets the tab restore its picks across sessions and report "up to date"
        // without re-downloading when they haven't changed since the last successful build.
        public static ManualSimOptions AppDataActiveOptions()
        {
            try
            {
                string p = AppDataOptionsPath();
                if (!System.IO.File.Exists(p))
                    return null;
                string json = System.IO.File.ReadAllText(p);

                var axesM = System.Text.RegularExpressions.Regex.Match(json, "\"axes\"\\s*:\\s*(\\d+)");
                var sigM = System.Text.RegularExpressions.Regex.Match(json, "\"signature\"\\s*:\\s*\"([^\"]*)\"");
                if (!axesM.Success || !sigM.Success)
                    return null;

                return new ManualSimOptions
                {
                    Axes = int.Parse(axesM.Groups[1].Value, CultureInfo.InvariantCulture),
                    Probe = JsonBool(json, "probe"),
                    Rotation = JsonBool(json, "rotation"),
                    LatheUvw = JsonBool(json, "latheUvw"),
                    SafetyDoor = JsonBool(json, "safetyDoor"),
                    EStop = JsonBool(json, "eStop"),
                    YGanged = JsonBool(json, "yGanged"),
                    YAutoSquare = JsonBool(json, "yAutoSquare"),
                    Signature = sigM.Groups[1].Value
                };
            }
            catch { return null; }
        }

        private static bool JsonBool(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
            return m.Success && m.Groups[1].Value == "true";
        }

        public static string AppDataActiveSignature()
        {
            return AppDataActiveOptions()?.Signature;
        }

        private static void WriteAppDataOptions(ManualSimOptions opts, string signature)
        {
            string json = "{\"axes\":" + opts.Axes.ToString(CultureInfo.InvariantCulture) +
                          ",\"probe\":" + (opts.Probe ? "true" : "false") +
                          ",\"rotation\":" + (opts.Rotation ? "true" : "false") +
                          ",\"latheUvw\":" + (opts.LatheUvw ? "true" : "false") +
                          ",\"safetyDoor\":" + (opts.SafetyDoor ? "true" : "false") +
                          ",\"eStop\":" + (opts.EStop ? "true" : "false") +
                          ",\"yGanged\":" + (opts.YGanged ? "true" : "false") +
                          ",\"yAutoSquare\":" + (opts.YAutoSquare ? "true" : "false") +
                          ",\"signature\":\"" + JsonEscape(signature) + "\"}";
            System.IO.File.WriteAllText(AppDataOptionsPath(), json);
        }

        // Map the user's picked options (Settings > Simulator) to compile symbols + a stable signature, same
        // scheme as BuildOptionSymbols above but from explicit picks instead of GrblInfo. Auto-square implies
        // ganged (driver_opts.h) - checking Auto-square alone still emits Y_GANGED so the combination is valid.
        public static string BuildManualOptionSymbols(ManualSimOptions opts, out string signature)
        {
            var symbols = new System.Collections.Generic.List<string> { "N_AXIS=" + opts.Axes };
            if (opts.Probe) symbols.Add("PROBE_ENABLE=1");
            if (opts.Rotation) symbols.Add("ROTATION_ENABLE=1");
            if (opts.LatheUvw) symbols.Add("LATHE_UVW_OPTION=1");
            if (opts.SafetyDoor) symbols.Add("SAFETY_DOOR_ENABLE=1");
            if (opts.EStop) symbols.Add("ESTOP_ENABLE=1");
            if (opts.YGanged || opts.YAutoSquare) symbols.Add("Y_GANGED=1");
            if (opts.YAutoSquare) symbols.Add("Y_AUTO_SQUARE=1");
            symbols.Sort(StringComparer.Ordinal);
            string flags = string.Join(" ", symbols);
            signature = ShortHash(flags);
            return flags;
        }

        // GET the sim-<sig> release and install it as the plain, unsuffixed %AppData%\Simulator\grblHAL_sim.exe,
        // alongside a sim-options.json recording the picks that produced it.
        private static bool DownloadAppDataRelease(ManualSimOptions opts, string sig, out string error)
        {
            byte[] bytes = DownloadReleaseBytes(sig, out error);
            if (bytes == null)
                return false;
            try
            {
                string dir = AppDataSimulatorDir();
                System.IO.Directory.CreateDirectory(dir);
                string exe = AppDataSimulatorExePath();

                // A prior build's exe (e.g. auto-launched on startup/reconnect, or still running from an
                // earlier Connect) locks the file - overwriting it would otherwise throw every single time,
                // and PollAndInstallAppData would silently retry that same failure for its whole 10-minute
                // window before giving up with a misleading "still building" message. Stop it first so the
                // install can actually succeed; the user just asked to update this exact exe.
                if (IsProcessRunningByExe(exe))
                {
                    try
                    {
                        foreach (var p in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exe)))
                            try { p.Kill(); p.WaitForExit(2000); } catch { }
                    }
                    catch { }
                }

                System.IO.File.WriteAllBytes(exe, bytes);
                WriteAppDataOptions(opts, sig);
                return true;
            }
            catch (System.IO.IOException ioex)
            {
                error = "could not install the simulator (is it running?): " + ioex.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Ensure %AppData%\Simulator\grblHAL_sim.exe matches the given options, same fallback chain as
        // EnsureMatchedSimulator (already current -> shared release cache -> dispatch a CI build). Blocking on
        // network I/O - call from a background thread.
        public static MatchResult EnsureAppDataSimulator(ManualSimOptions opts, out string signature, out string detail)
        {
            detail = null;
            string flags = BuildManualOptionSymbols(opts, out signature);

            if (signature == AppDataActiveSignature() && AppDataSimulatorPresent())
                return MatchResult.AlreadyCurrent;

            string relErr;
            if (DownloadAppDataRelease(opts, signature, out relErr))
                return MatchResult.InstalledFromRelease;

            if (_dispatched.Contains(signature))
            {
                detail = "a matching simulator (signature " + signature + ") is still building.";
                return MatchResult.BuildTriggered;
            }

            string dispErr;
            if (DispatchBuild(signature, flags, out dispErr))
            {
                _dispatched.Add(signature);
                detail = "building a matching simulator (signature " + signature + "); this can take a few minutes.";
                return MatchResult.BuildTriggered;
            }

            detail = dispErr;
            return MatchResult.Failed;
        }

        // Poll for a just-dispatched sim-<sig> release and install it to %AppData%\Simulator once it appears.
        // Intended to run on a background thread after EnsureAppDataSimulator returns BuildTriggered.
        public static bool PollAndInstallAppData(ManualSimOptions opts, string sig, int timeoutSeconds = 600)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(20 * 1000);
                string err;
                if (DownloadAppDataRelease(opts, sig, out err))
                    return true;
            }
            return false;
        }
    }
}
