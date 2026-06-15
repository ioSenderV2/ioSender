/*
 * AtcMacros.cs - part of CNC Controls library
 *
 * Provisions the ATC support macros (cal/probe_tfl/tc/start_job.macro) onto the controller's filesystem when
 * it reports them missing - $I [NEWOPT:...] "ATC=0" means an ATC is configured but tc.macro is not present.
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
        // (seeded via SeedStartJobMacro), not on the controller. cal/probe_tfl/tc stay on littlefs
        // because grblHAL resolves their O<...> CALL / M6 references from its own filesystem.
        static readonly string[] Required = { "cal.macro", "probe_tfl.macro", "tc.macro" };

        // The controller (by reported version) we have already verified this app run - so InitSystem's repeated
        // calls (reconnect, soft reset) don't re-list the filesystem every time. A different controller re-checks.
        static string _checkedFor;

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

        // Reconcile the controller's ATC macros with this install's embedded copies. Lists the filesystem and
        // compares an embedded checksum against the sidecar written on the previous upload; if a macro is
        // missing or the checksum differs, confirmUpload(reason) is asked and - if approved - all macros plus a
        // refreshed checksum are (re)written to littlefs. Returns what happened.
        //
        // Writes go over YModem when advertised (it streams to $CWD and can create files on littlefs, which the
        // FTP server here cannot); otherwise the supplied upload delegate (the SD Card tab's FTP path) is used.
        public static ProvisionResult EnsureProvisioned(GrblViewModel model, Func<string, string, bool> upload, Func<UpdateReason, bool> confirmUpload)
        {
            try
            {
                // Runs whenever an ATC is configured: "ATC=0" (tc.macro missing) or "ATC=1" (present, but its
                // content may be out of date with a newer ioSender).
                if (model == null || upload == null || !GrblInfo.HasFS || !(GrblInfo.AtcMacrosRequired || GrblInfo.HasATC)
                     || Comms.com == null || !Comms.com.IsOpen)
                    return ProvisionResult.Skipped;

                if (_checkedFor == GrblInfo.Version)   // already handled for this controller this run
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
                {
                    _checkedFor = GrblInfo.Version;   // present and current - nothing to do
                    return ProvisionResult.UpToDate;
                }

                bool accepted = confirmUpload == null || confirmUpload(missing ? UpdateReason.Missing : UpdateReason.Outdated);
                if (!accepted)
                    return ProvisionResult.Skipped;   // user declined

                model.Message = missing ? "Installing ATC macros..." : "Updating ATC macros...";

                // File create/delete requires the controller to be idle; in a homing-required (or other) alarm
                // it returns error:9 "not allowed until homed". Unlock so the macros can be written without first
                // homing - the upload moves nothing, and the user homes when ready afterwards.
                if (model.GrblState.State == GrblStates.Alarm)
                    Grbl.WaitForResponse(GrblConstants.CMD_UNLOCK);

                // (Re)write the full set - macros are small and this is rare - then refresh the checksum sidecar.
                bool wrote = false, allWritten = true, useAbsolute = true, cwdSet = false;
                foreach (string name in Required)
                {
                    string content = ReadEmbedded(name);
                    if (content == null)
                    {
                        allWritten = false;
                        continue;
                    }

                    bool ok = GrblInfo.HasYModem
                                ? YModemWrite(model, name, content, destPath, present.Contains(name), ref useAbsolute, ref cwdSet)
                                : WriteFile(name, content, destPath, upload);
                    if (ok)
                        wrote = true;
                    else
                        allWritten = false;                         // leave _checkedFor unset so we retry later
                }

                if (wrote && allWritten)
                {
                    // Trailing newline is required: when the controller dumps a file with no final newline via
                    // $F<=, the content runs straight into the "ok" with no line break, so the read-back would
                    // return "<hash>ok" and never match - making it re-prompt on every connect.
                    string sumContent = embeddedSum + "\n";
                    bool sumOk = GrblInfo.HasYModem
                                    ? YModemWrite(model, ChecksumFile, sumContent, destPath, present.Contains(ChecksumFile), ref useAbsolute, ref cwdSet)
                                    : WriteFile(ChecksumFile, sumContent, destPath, upload);
                    if (!sumOk)
                        allWritten = false;
                }

                model.Message = allWritten ? "ATC macros up to date." : "ATC macro update incomplete - will retry.";

                if (wrote)
                    GrblSDCard.Load(model, false);   // refresh the listing (also restores $CWD to a valid mount)

                if (allWritten)
                    _checkedFor = GrblInfo.Version;   // everything in place and recorded; stop checking this run

                return wrote ? ProvisionResult.Uploaded : ProvisionResult.Failed;
            }
            catch
            {
                return ProvisionResult.Failed;   // best-effort; never break the connect flow
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

            new System.Threading.Thread(() =>
            {
                try { res = WaitFor.AckResponse<string>(
                    ct,
                    response => { if (response != "ok" && !response.StartsWith("error") && !response.StartsWith("[")) sb.AppendLine(response); },
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    400, () => Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_DUMP + path)); }
                catch { res = false; }
            }).Start();

            while (res == null)
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

                string path = Path.Combine(CNC.Core.Resources.ConfigPath ?? "./", "start_job.macro");
                try
                {
                    if (!File.Exists(path) || File.ReadAllText(path) != body)
                        File.WriteAllText(path, body);
                }
                catch { /* best effort - the macro still works once the file exists / is created via View */ }

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
                    FKey = FirstFreeFKey(macros)
                });

                AppConfig.Settings.Save();
            }
            catch { /* never break the connect flow */ }
        }

        // First function key (1-12) not already bound to a macro; 0 if all are taken.
        // Mirrors MacroManagerDialog.FirstFreeFKey.
        static int FirstFreeFKey(System.Collections.Generic.IEnumerable<CNC.GCode.Macro> macros)
        {
            var used = new HashSet<int>();
            foreach (var m in macros)
                if (m.FKey >= 1 && m.FKey <= 12)
                    used.Add(m.FKey);

            for (int i = 1; i <= 12; i++)
                if (!used.Contains(i))
                    return i;

            return 0;
        }

        // Stream <content> to the controller as <name> on the target (littlefs) filesystem via YModem. First
        // tries the absolute path (<destPath>/<name>) so the firmware's vfs_open lands it on littlefs directly;
        // if that is refused, fall back to $CWD=<destPath> + a bare name and stay relative for the rest. YModem
        // writes wherever $CWD points and bypasses the FTP server entirely.
        static bool YModemWrite(GrblViewModel model, string name, string content, string destPath, bool fileExists, ref bool useAbsolute, ref bool cwdSet)
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

            string baseDir = string.IsNullOrEmpty(destPath) ? string.Empty : destPath.TrimEnd('/');

            try
            {
                model.Message = "Installing " + name + "...";

                // Delete any existing copy first: a YModem write over an already-present littlefs file stalls the
                // stream (vfs_open "w" truncate path), so we create a fresh file instead. Only when it actually
                // exists - deleting an absent file returns "error:61 File delete failed", which would otherwise
                // surface in the status line (and set GrblError) on a first-time upload to an empty filesystem.
                // Send it raw (WriteCommand+AwaitAck), NOT via the MDI path: that runs the line through the
                // g-code parser, which - with NGC expressions enabled - mangles a filename containing an
                // underscore (e.g. probe_tfl.macro) into an invalid expression and the controller rejects it
                // with "error:71 - Unknown operation found in expression". A $FD= system command must bypass it.
                if (fileExists)
                {
                    Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_UNLINK + baseDir + "/" + name);
                    Comms.com.AwaitAck();
                }

                if (useAbsolute)
                {
                    bool absOk = new YModem().Upload(temp, baseDir + "/" + name);   // e.g. /littlefs/tc.macro
                    if (absOk)
                        return true;
                    useAbsolute = false;   // absolute path refused once - switch to $CWD + relative for the rest
                }

                if (!cwdSet)
                {
                    Grbl.WaitForResponse("$CWD=" + (baseDir == string.Empty ? "/" : baseDir));
                    cwdSet = true;
                }

                bool relOk = new YModem().Upload(temp, name);   // relative to $CWD
                return relOk;
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
