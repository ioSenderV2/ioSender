/*
 * AtcMacros.cs - part of CNC Controls library
 *
 * Provisions the ATC support macros (tc/pcorner) onto the controller's filesystem when it reports them
 * missing - $I [NEWOPT:...] "ATC=0" means an ATC is configured but tc.macro is not present.
 * The canonical copies are embedded from the repo's macros/ folder; missing ones are written to the root
 * volume, where grblHAL looks for them (/tc.macro replaces the built-in M6 flow once the SD/LittleFS plugin
 * attaches at boot/$FM, after which the firmware reports ATC=1 and provisioning stops).
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using CNC.Core;

namespace CNC.Controls
{
    public static class AtcMacros
    {
        // Embedded with LogicalName == file name (see CNC Controls.csproj), so they round-trip by bare name.
        // start_job.macro is intentionally NOT here: it runs ioSender-side through MacroProcessor
        // (seeded via SeedStartJobMacro), not on the controller. tc/pcorner stay on littlefs
        // because grblHAL resolves their O<...> CALL / M6 references from its own filesystem.
        static readonly string[] Required = { "tc.macro", "pcorner.macro" };

        // Re-entrancy guard. EnsureProvisioned pumps the WPF dispatcher (controller file reads via DoEvents, the
        // YModem upload), so a queued UI event can re-enter it before it returns - mutually recursing with the SD
        // Card tab's on-show check (or the macro-run dependency check) and overflowing the stack. Bail on re-entry.
        static bool _provisioning;

        // Sidecar file storing the checksum of the macro set last written by ioSender, so a later run can tell
        // whether the controller's copies are out of date with this install's embedded macros. The ".sum"
        // extension is not in grblHAL's file-type filter, so it never shows in the SD Card listing or run lists.
        const string ChecksumFile = "atc.sum";

        public enum ProvisionResult
        {
            Skipped,     // nothing to do (gate failed, user declined, or already handled this run)
            UpToDate,    // macros present and matching this install's embedded set - nothing to do
            Uploaded,    // macros were written or updated
            Failed       // a write was attempted but did not complete
        }

        public enum UpdateReason
        {
            Missing,     // one or more required macros are absent
            Outdated     // macros are present but their checksum no longer matches the embedded set
        }

        public enum MacroState { Installed, Outdated, Missing }

        public class MacroStatusRow
        {
            public string Name { get; set; }
            public string Size { get; set; }
            public string FS { get; set; }
            public MacroState State { get; set; }
        }

        // Per-macro status for the Machine Setup "Controller macros" step. Lists the controller filesystem and
        // marks each required macro Installed (present + the set checksum matches the embedded copies), Outdated
        // (present but the set checksum differs) or Missing (absent). Size/FS come from the filesystem listing.
        // Serialises GetStatus: two overlapping callers (the setup gate + the tab refresh) otherwise reload
        // GrblSDCard's table under each other, detaching cached rows mid-read (RowNotInTableException).
        private static readonly object _statusLock = new object();

        public static List<MacroStatusRow> GetStatus(GrblViewModel model)
        {
            var rows = new List<MacroStatusRow>();

            if (model == null || !GrblInfo.HasFS || Comms.com == null || !Comms.com.IsOpen)
            {
                foreach (string name in Required)
                    rows.Add(new MacroStatusRow { Name = name, State = MacroState.Missing });
                return rows;
            }

            lock (_statusLock)
            {
                try
                {
                    GrblSDCard.Load(model, false);

                    // Snapshot the values NOW into plain rows - never hold DataRow references past here. The
                    // table can be rebuilt by a poll / another load, which detaches cached rows and throws on
                    // access. ToList() materialises a snapshot so iteration can't fault on a live edit either.
                    var present = new Dictionary<string, MacroStatusRow>(StringComparer.OrdinalIgnoreCase);
                    foreach (DataRowView rv in GrblSDCard.Files.Cast<DataRowView>().ToList())
                    {
                        var r = rv.Row;
                        if (r.RowState == DataRowState.Detached || r.RowState == DataRowState.Deleted)
                            continue;
                        if ((string)r["Dir"] == GrblSDCard.EmptyMountMarker)
                            continue;
                        string nm = Path.GetFileName((string)r["Name"]);
                        present[nm] = new MacroStatusRow
                        {
                            Name = nm,
                            Size = r.Table.Columns.Contains("Size") ? r["Size"]?.ToString() : string.Empty,
                            FS = r.Table.Columns.Contains("Location") ? r["Location"]?.ToString() : string.Empty
                        };
                    }

                    FsMount target = GrblSDCard.Mounts.FirstOrDefault(m =>
                                         m.Name.IndexOf("littlefs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         m.Path.IndexOf("littlefs", StringComparison.OrdinalIgnoreCase) >= 0)
                                     ?? GrblSDCard.Mounts.FirstOrDefault(m => m.Path == "/")
                                     ?? GrblSDCard.Mounts.FirstOrDefault();
                    string destPath = target != null ? target.Path : "/littlefs";

                    // Only flag Outdated when we actually read a DIFFERENT checksum. An empty/failed read (a
                    // raced FS query right after connect) must NOT flip every present macro to Outdated.
                    string onFs = ReadControllerFile(model, JoinPath(destPath, ChecksumFile)).Trim();
                    bool stale = onFs.Length > 0 && onFs != EmbeddedChecksum();

                    foreach (string name in Required)
                    {
                        MacroStatusRow found;
                        if (present.TryGetValue(name, out found))
                            rows.Add(new MacroStatusRow { Name = name, Size = found.Size, FS = found.FS, State = stale ? MacroState.Outdated : MacroState.Installed });
                        else
                            rows.Add(new MacroStatusRow { Name = name, State = MacroState.Missing });
                    }
                }
                catch
                {
                    // Couldn't read the filesystem reliably (transient during connect/activate). Report nothing
                    // rather than crash or guess - callers treat an empty list as "unknown" (no gate, no false status).
                    rows.Clear();
                }
            }

            return rows;
        }

        // Reconcile the controller's ATC macros with this install's embedded copies. Lists the filesystem and
        // compares an embedded checksum against the sidecar written on the previous upload; if a macro is
        // missing or the checksum differs, confirmUpload(reason) is asked and - if approved - all macros plus a
        // refreshed checksum are (re)written to littlefs. Returns what happened.
        //
        // Writes go over YModem when advertised (it streams to $CWD and can create files on littlefs, which the
        // FTP server here cannot); otherwise the supplied upload delegate (the SD Card tab's FTP path) is used.
        public static ProvisionResult EnsureProvisioned(GrblViewModel model, Func<string, string, bool> upload, Func<UpdateReason, bool> confirmUpload)
        {
            if (_provisioning)
                return ProvisionResult.Skipped;   // re-entrancy guard - see _provisioning
            _provisioning = true;
            try
            {
                // Runs whenever an ATC is configured: "ATC=0" (tc.macro missing) or "ATC=1" (present, but its
                // content may be out of date with a newer ioSender).
                if (model == null || upload == null || !GrblInfo.HasFS || !(GrblInfo.AtcMacrosRequired || GrblInfo.HasATC)
                     || Comms.com == null || !Comms.com.IsOpen)
                    return ProvisionResult.Skipped;

                // Enumerate current files across every mount (also refreshes the SD Card tab's cache).
                GrblSDCard.Load(model, false);

                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DataRowView rv in GrblSDCard.Files)
                {
                    if ((string)rv.Row["Dir"] == GrblSDCard.EmptyMountMarker)
                        continue;                                   // skip empty-filesystem placeholder rows
                    present.Add(Path.GetFileName((string)rv.Row["Name"]));
                }

                // Target littlefs specifically: it persists with no SD card and (unlike the SD root) is writable
                // over YModem on this firmware. Fall back to the root / first mount if no littlefs is reported.
                FsMount target = GrblSDCard.Mounts.FirstOrDefault(m =>
                                     m.Name.IndexOf("littlefs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     m.Path.IndexOf("littlefs", StringComparison.OrdinalIgnoreCase) >= 0)
                                 ?? GrblSDCard.Mounts.FirstOrDefault(m => m.Path == "/")
                                 ?? GrblSDCard.Mounts.FirstOrDefault();
                string destPath = target != null ? target.Path : "/littlefs";

                bool missing = !Required.All(present.Contains);
                string embeddedSum = EmbeddedChecksum();
                bool stale = ReadControllerFile(model, JoinPath(destPath, ChecksumFile)).Trim() != embeddedSum;

                if (!missing && !stale)
                    return ProvisionResult.UpToDate;   // present and current - nothing to do

                bool accepted = confirmUpload == null || confirmUpload(missing ? UpdateReason.Missing : UpdateReason.Outdated);
                if (!accepted)
                    return ProvisionResult.Skipped;   // user declined

                model.Message = missing ? "Installing ATC macros..." : "Updating ATC macros...";

                // File create/delete requires the controller to be idle; in a homing-required (or other) alarm
                // it returns error:9 "not allowed until homed". Unlock so the macros can be written without first
                // homing - the upload moves nothing, and the user homes when ready afterwards.
                if (model.GrblState.State == GrblStates.Alarm)
                    Grbl.WaitForResponse(GrblConstants.CMD_UNLOCK);

                // Remove any stray copies of these files that live OUTSIDE the littlefs target - e.g. on an SD
                // card mounted at "/" from an earlier mis-targeted upload. They are ours by name, and a stray
                // "/name.macro" on the SD root can shadow the intended "/littlefs/name.macro" (the firmware's
                // named-sub resolution falls back to "/<name>.macro"). Unlink raw ($FD=) to bypass the parser's
                // underscore-filename mangling.
                string targetPrefix = destPath.TrimEnd('/') + "/";
                foreach (DataRowView rv in GrblSDCard.Files)
                {
                    if ((string)rv.Row["Dir"] == GrblSDCard.EmptyMountMarker)
                        continue;
                    string full = (string)rv.Row["Name"];
                    string bn = Path.GetFileName(full);
                    bool ours = ChecksumFile.Equals(bn, StringComparison.OrdinalIgnoreCase)
                                || Required.Any(r => r.Equals(bn, StringComparison.OrdinalIgnoreCase));
                    if (ours && !full.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_UNLINK + full);
                        Comms.com.AwaitAck();
                    }
                }

                // (Re)write the full set - macros are small and this is rare - then refresh the checksum sidecar.
                bool wrote = false, allWritten = true;
                foreach (string name in Required)
                {
                    string content = ReadEmbedded(name);
                    if (content == null)
                    {
                        allWritten = false;
                        continue;
                    }

                    bool ok = GrblInfo.HasYModem
                                ? YModemWrite(model, name, content, destPath, present.Contains(name))
                                : WriteFile(name, content, destPath, upload);
                    if (ok)
                        wrote = true;
                    else
                        allWritten = false;                         // a write failed - reported as Failed so the user can retry
                }

                if (wrote && allWritten)
                {
                    // Trailing newline is required: when the controller dumps a file with no final newline via
                    // $F<=, the content runs straight into the "ok" with no line break, so the read-back would
                    // return "<hash>ok" and never match - making it re-prompt on every connect.
                    string sumContent = embeddedSum + "\n";
                    bool sumOk = GrblInfo.HasYModem
                                    ? YModemWrite(model, ChecksumFile, sumContent, destPath, present.Contains(ChecksumFile))
                                    : WriteFile(ChecksumFile, sumContent, destPath, upload);
                    if (!sumOk)
                        allWritten = false;
                }

                model.Message = allWritten ? "ATC macros up to date." : "ATC macro update incomplete - will retry.";

                if (wrote)
                    GrblSDCard.Load(model, false);   // refresh the listing (also restores $CWD to a valid mount)

                return wrote ? ProvisionResult.Uploaded : ProvisionResult.Failed;
            }
            catch
            {
                return ProvisionResult.Failed;   // best-effort; never break the connect flow
            }
            finally
            {
                _provisioning = false;
            }
        }

        // SHA-256 over the embedded macro set (name + content, in fixed Required order), as lower-case hex.
        // Changes whenever any shipped macro changes, so it doubles as the "version" of this install's macros.
        static string EmbeddedChecksum()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var ms = new MemoryStream())
            {
                foreach (string name in Required)
                {
                    byte[] header = System.Text.Encoding.UTF8.GetBytes(name + "\n");
                    byte[] body = System.Text.Encoding.UTF8.GetBytes(ReadEmbedded(name) ?? string.Empty);
                    ms.Write(header, 0, header.Length);
                    ms.Write(body, 0, body.Length);
                    ms.WriteByte(0);
                }
                return BitConverter.ToString(sha.ComputeHash(ms.ToArray())).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        // Read a controller file's text via $F<= (dump). Returns "" if it does not exist / cannot be read.
        static string ReadControllerFile(GrblViewModel model, string path)
        {
            var sb = new System.Text.StringBuilder();
            bool? res = null;
            var ct = new System.Threading.CancellationToken();

            Comms.com.PurgeQueue();
            model.SuspendProcessing = true;

            // IsBackground so an unresponsive controller (WaitFor never returns) can't keep the process alive and
            // hang ioSender on close - a foreground worker here is exactly what wedged shutdown before.
            new System.Threading.Thread(() =>
            {
                try { res = WaitFor.AckResponse<string>(
                    ct,
                    response => { if (response != "ok" && !response.StartsWith("error") && !response.StartsWith("[")) sb.AppendLine(response); },
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    400, () => Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_DUMP + path)); }
                catch { res = false; }
            }) { IsBackground = true }.Start();

            // Hard wall-clock cap so the UI thread can't spin forever if the controller never answers; on timeout
            // we return what little was read (empty), which reads as "missing/stale" and just re-prompts an upload.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (res == null && sw.ElapsedMilliseconds < 3000)
                EventUtils.DoEvents();

            model.SuspendProcessing = false;
            return sb.ToString();
        }

        static string JoinPath(string dir, string name)
        {
            return (string.IsNullOrEmpty(dir) ? string.Empty : dir.TrimEnd('/')) + "/" + name;
        }

        static string ReadEmbedded(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null)
                    return null;
                using (var r = new StreamReader(s))
                    return r.ReadToEnd();
            }
        }

        // Seed the ioSender-side "Start Job" macro once. Unlike the littlefs ATC macros, start_job.macro
        // runs through MacroProcessor (line-by-line via MDI), so it is materialised into the config folder
        // and referenced by an "@start_job.macro" macro entry. Idempotent and cheap, so safe to call on
        // every connect: the file is (re)written only when missing/changed (self-healing without clobbering
        // a live edit) and the macro entry is added only if no "Start Job" macro already exists.
        public static void SeedStartJobMacro()
        {
            try
            {
                var macros = AppConfig.Settings?.Macros;
                if (macros == null)
                    return;

                string body = ReadEmbedded("start_job.macro");
                if (body == null)
                    return;

                // Seed the embedded body only when the file is ABSENT. The Start Job tab regenerates this file
                // (its pcorner-based program) via WriteStartJobMacro, so do NOT self-heal to the embedded copy on
                // change - that would clobber the operator's generated Start Job on the next connect.
                string path = Path.Combine(CNC.Core.Resources.ConfigPath ?? "./", "start_job.macro");
                try
                {
                    if (!File.Exists(path))
                        File.WriteAllText(path, body);
                }
                catch { /* best effort - the macro still works once the file exists / is created via the tab */ }

                if (macros.Any(m => string.Equals(m.Name, "Start Job", StringComparison.OrdinalIgnoreCase)))
                    return;   // already seeded or user-created - leave the entry (and its F-key) alone

                int id = 0;
                foreach (var m in macros)
                    id = Math.Max(id, m.Id);

                macros.Add(new CNC.GCode.Macro {
                    Id = id + 1,
                    Name = "Start Job",
                    Code = "@start_job.macro",
                    ConfirmOnExecute = true,
                    FKey = 0                 // no default F-key - Start Job is driven from its tab, not a hotkey
                });

                AppConfig.Settings.Save();
            }
            catch { /* never break the connect flow */ }
        }

        // Stream <content> to the controller as <name> on the target (littlefs) filesystem via YModem. First
        // tries the absolute path (<destPath>/<name>) so the firmware's vfs_open lands it on littlefs directly;
        // if that is refused, fall back to $CWD=<destPath> + a bare name and stay relative for the rest. YModem
        // writes wherever $CWD points and bypasses the FTP server entirely.
        static bool YModemWrite(GrblViewModel model, string name, string content, string destPath, bool fileExists)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ioSenderMacros");
            string temp;
            try
            {
                Directory.CreateDirectory(dir);
                temp = Path.Combine(dir, name);
                File.WriteAllText(temp, content);
            }
            catch { return false; }

            // Always write to the ABSOLUTE target path so the firmware's vfs_open routes the file to the
            // littlefs mount regardless of $CWD. A bare (relative) name resolves against $CWD instead, and
            // when an SD card is mounted at "/" that is the SD root - so a relative write leaks the macro onto
            // the card (seen as 0-byte /tc.macro etc. duplicated off /littlefs). Absolute-only removes that
            // ambiguity entirely; the firmware we target (and the simulator) both honour the path. No bare-name
            // $CWD fallback - if a controller ever refused absolute vfs paths the correct fix would be to set
            // AND verify $CWD first, never an unguarded relative write.
            string baseDir = string.IsNullOrEmpty(destPath) ? string.Empty : destPath.TrimEnd('/');
            string target = baseDir + "/" + name;   // e.g. /littlefs/tc.macro

            try
            {
                model.Message = "Installing " + name + "...";

                // Retry: a YModem write over an existing file truncates-in-place and can stall the stream, and a
                // dropped ACK leaves a 0-byte file - so unlink any existing/partial copy first and re-upload a
                // fresh file. Unlink raw (WriteCommand+AwaitAck), NOT via the MDI path: that runs the line through
                // the g-code parser, which - with NGC expressions enabled - mangles an underscore filename
                // into "error:71 - Unknown operation found in expression". $FD= bypasses it.
                // Skip the unlink only on the first attempt to a known-absent file (avoids a cosmetic error:61).
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (fileExists || attempt > 0)
                    {
                        Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_UNLINK + target);
                        Comms.com.AwaitAck();
                    }

                    if (new YModem().Upload(temp, target))
                        return true;
                }

                return false;
            }
            catch { return false; }
            finally { try { File.Delete(temp); } catch { } }
        }

        // Materialise the embedded macro to a temp file (named so the controller receives <name>), then hand it
        // to the supplied uploader, which targets destPath's filesystem and picks the transport. The base name
        // of the temp file is what lands on the controller, so it must equal <name>.
        static bool WriteFile(string name, string content, string destPath, Func<string, string, bool> upload)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ioSenderMacros");
            string temp;
            try
            {
                Directory.CreateDirectory(dir);
                temp = Path.Combine(dir, name);
                File.WriteAllText(temp, content);
            }
            catch { return false; }

            try { return upload(temp, destPath); }
            catch { return false; }
            finally { try { File.Delete(temp); } catch { } }
        }
    }
}
