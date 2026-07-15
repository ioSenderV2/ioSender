using System;

namespace CNC.Controls
{
    // Firmware-update checking for the connected grblHAL board against the SRW fork's CI builds
    // (stevenrwood/iMXRT1062 @ srw/local-build-config). Mirrors SimulatorManager's release-download
    // pattern (public GitHub release asset, no token needed for reads) - see the "fw-latest" release
    // published by .github/workflows/firmware.yml's "Publish fw-latest release" step.
    public static class FirmwareUpdateManager
    {
        public const string FirmwareRepo = "stevenrwood/iMXRT1062";
        private const string ReleaseTag = "fw-latest";
        public const string ReleasePageUrl = "https://github.com/" + FirmwareRepo + "/releases/tag/" + ReleaseTag;
        private const string TeensyLoaderExeName = "teensy_loader_cli.exe";
        private const string TeensyMcu = "TEENSY41";   // the only board this fork builds for (BOARD_T41U5XBB)

        public class ReleaseInfo
        {
            // "<branch>@<short-sha>" parsed from the release body, e.g. "srw/local-build-config@abc1234" -
            // same format as GrblInfo.DriverRef, so the two compare directly.
            public string DriverRef;
            public string DriverSha;
            public string HexAssetUrl;
            public string HexAssetName;
        }

        // Query the fw-latest release (public, no token) and parse out its driver ref + hex asset URL.
        // Blocking network I/O - call from a background thread.
        public static ReleaseInfo GetLatestRelease(out string error)
        {
            error = null;
            string url = "https://api.github.com/repos/" + FirmwareRepo + "/releases/tags/" + ReleaseTag;
            try
            {
                // GitHub requires TLS 1.2; .NET Framework 4.6.2 does not enable it by default.
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "ioSender";
                req.Accept = "application/vnd.github+json";
                req.Timeout = req.ReadWriteTimeout = 15 * 1000;

                string json;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new System.IO.StreamReader(s))
                    json = r.ReadToEnd();

                string body = JsonStringField(json, "\"body\"");
                string driverRef = null, driverSha = null;
                if (!string.IsNullOrEmpty(body))
                {
                    foreach (var field in body.Split(' '))
                    {
                        if (!field.StartsWith("drv:"))
                            continue;
                        driverRef = field.Substring(4);
                        int at = driverRef.LastIndexOf('@');
                        driverSha = at >= 0 ? driverRef.Substring(at + 1) : null;
                        break;
                    }
                }

                string assetUrl = FindHexAssetUrl(json);
                if (assetUrl == null)
                {
                    error = "no .hex asset found in the fw-latest release.";
                    return null;
                }

                return new ReleaseInfo
                {
                    DriverRef = driverRef,
                    DriverSha = driverSha,
                    HexAssetUrl = assetUrl,
                    HexAssetName = System.IO.Path.GetFileName(new Uri(assetUrl).LocalPath)
                };
            }
            catch (System.Net.WebException wex)
            {
                var resp = wex.Response as System.Net.HttpWebResponse;
                error = resp != null && resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "no fw-latest release found - the firmware CI may not have run yet."
                    : "could not reach GitHub: " + wex.Message;
                return null;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // GET the hex asset bytes (public, no token). Blocking network I/O - call from a background thread.
        public static byte[] DownloadHex(string url, out string error)
        {
            error = null;
            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "ioSender";
                req.Timeout = req.ReadWriteTimeout = 2 * 60 * 1000;

                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var src = resp.GetResponseStream())
                using (var ms = new System.IO.MemoryStream())
                {
                    src.CopyTo(ms);
                    byte[] bytes = ms.ToArray();
                    if (bytes.Length < 10)
                    {
                        error = "the downloaded file was empty or too small.";
                        return null;
                    }
                    return bytes;
                }
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // Locate the bundled teensy_loader_cli.exe (see tools\teensy_loader_cli\build.ps1 and the
        // CopyFirmwareTools MSBuild target) - same lookup shape as SimulatorManager.FindExecutable.
        public static string FindTeensyLoaderCli()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] {
                System.IO.Path.Combine(baseDir, "firmware-tools", TeensyLoaderExeName),
                System.IO.Path.Combine(baseDir, TeensyLoaderExeName)
            })
            {
                if (System.IO.File.Exists(c))
                    return c;
            }
            return null;
        }

        // Flash a .hex file to the board via teensy_loader_cli. -w waits for the HalfKay bootloader to
        // appear and uploads as soon as it does; -v is verbose (captured into the returned log either
        // way). NOTE: upstream teensy_loader_cli does NOT implement -s (soft/auto reboot) on Windows -
        // confirmed via its own "Soft reboot is not implemented for Win32" message - so on this platform
        // the board must be rebooted into the bootloader by pressing its physical RESET/PROGRAM button;
        // the caller's confirmation prompt must tell the user this. waitSeconds should give them time to
        // walk over and press it. Blocking - call from a background thread.
        public static bool FlashHex(string exePath, string hexPath, int waitSeconds, out string log, out string error)
        {
            log = string.Empty;
            error = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--mcu=" + TeensyMcu + " -w -v \"" + hexPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var p = new System.Diagnostics.Process { StartInfo = psi })
                {
                    var sb = new System.Text.StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(waitSeconds * 1000))
                    {
                        try { p.Kill(); } catch { }
                        log = sb.ToString();
                        error = "teensy_loader_cli did not finish within " + waitSeconds + " seconds - " +
                                "was the board's RESET/PROGRAM button pressed?";
                        return false;
                    }

                    log = sb.ToString();
                    if (p.ExitCode != 0)
                    {
                        error = "teensy_loader_cli exited with code " + p.ExitCode + ".";
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Find the first "browser_download_url" ending in ".hex" in a GitHub release JSON payload.
        private static string FindHexAssetUrl(string json)
        {
            const string key = "\"browser_download_url\"";
            int idx = json.IndexOf(key);
            while (idx >= 0)
            {
                string u = JsonStringValueAt(json, idx + key.Length);
                if (u != null && u.EndsWith(".hex", StringComparison.OrdinalIgnoreCase))
                    return u;
                idx = json.IndexOf(key, idx + key.Length);
            }
            return null;
        }

        // Read a top-level "key": "value" string field from a flat-ish JSON blob (values assumed to contain
        // no unescaped quotes, true for our own release body/asset URLs). Minimal parsing to avoid adding a
        // JSON library dependency - same idiom as MainWindow's ParseGitHubReleaseTag.
        private static string JsonStringField(string json, string key)
        {
            int idx = json.IndexOf(key);
            if (idx < 0)
                return null;
            return JsonStringValueAt(json, idx + key.Length);
        }

        // Given a position just after a JSON key (or at/after a ':'), extract the following quoted string value.
        private static string JsonStringValueAt(string json, int afterKey)
        {
            int colon = json.IndexOf(':', afterKey);
            if (colon < 0)
                return null;
            int start = json.IndexOf('"', colon + 1);
            if (start < 0)
                return null;
            int end = start + 1;
            while (end < json.Length && json[end] != '"')
            {
                if (json[end] == '\\')
                    end++;
                end++;
            }
            if (end >= json.Length)
                return null;
            return json.Substring(start + 1, end - start - 1)
                       .Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
