/*
 * AtcMacros.cs - part of CNC Controls library
 *
 * Provisions the ATC support macros (tc/pcorner/pvisecorner) onto the controller's filesystem when it
 * reports them missing - $I [NEWOPT:...] "ATC=0" means an ATC is configured but tc.macro is not present.
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
        // tc/pcorner/pvisecorner stay on littlefs because grblHAL resolves their O<...> CALL / M6 references
        // from its own filesystem. Start Job itself runs ioSender-side through MacroProcessor's in-memory
        // ActiveRun, not from a controller-side or disk-based macro file. pvisecorner.macro is only ever
        // CALLed from FixtureEditDialog's vise Set position, not from a Start Job program, but it needs the
        // same on-controller presence as pcorner.macro for that O-word CALL to resolve.
        static readonly string[] Required = { "tc.macro", "pcorner.macro", "pvisecorner.macro" };

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

            ConsoleLog.Write(string.Format("[AtcMacros] GetStatus: enter, model={0}, HasFS={1}, comOpen={2}, GrblState={3}/{4}",
                model != null, GrblInfo.HasFS, Comms.com != null && Comms.com.IsOpen,
                model?.GrblState.State, model?.GrblState.Substate));

            if (model == null || !GrblInfo.HasFS || Comms.com == null || !Comms.com.IsOpen)
            {
                ConsoleLog.Write("[AtcMacros] GetStatus: short-circuit - reporting all Missing (no model/FS/comms)");
                foreach (string name in Required)
                    rows.Add(new MacroStatusRow { Name = name, State = MacroState.Missing });
                return rows;
            }

            lock (_statusLock)
            {
                try
                {
                    // If the listing was skipped (a concurrent Load held the re-entrancy guard) or the link
                    // dropped mid-call, the shared file table is NOT a trustworthy snapshot - it may be empty
                    // or half-cleared. Return UNKNOWN (empty list) rather than reading it: fabricating Missing
                    // rows here makes the Machine Setup gate mistake a raced read for "macros not installed" and
                    // jump to step 6 even though they are present (and turn green a moment later). The caller
                    // treats an empty list as "unknown" - no gate, no false status; the SD/setup tab re-queries
                    // on show, when no concurrent load is running, and gets the real state.
                    bool loaded = GrblSDCard.Load(model, false);
                    ConsoleLog.Write(string.Format("[AtcMacros] GetStatus: GrblSDCard.Load returned {0}, GrblState now {1}/{2}",
                        loaded, model.GrblState.State, model.GrblState.Substate));
                    if (!loaded)
                    {
                        ConsoleLog.Write("[AtcMacros] GetStatus: Load skipped (re-entrancy guard or link down) - returning UNKNOWN (empty)");
                        return new List<MacroStatusRow>();
                    }

                    // Snapshot the values NOW into plain rows - never hold DataRow references past here. The
                    // table can be rebuilt by a poll / another load, which detaches cached rows and throws on
                    // access. ToList() materialises a snapshot so iteration can't fault on a live edit either.
                    var present = new Dictionary<string, MacroStatusRow>(StringComparer.OrdinalIgnoreCase);
                    int totalRows = 0, detachedSkipped = 0, emptyMountSkipped = 0;
                    foreach (DataRowView rv in GrblSDCard.Files.Cast<DataRowView>().ToList())
                    {
                        totalRows++;
                        var r = rv.Row;
                        if (r.RowState == DataRowState.Detached || r.RowState == DataRowState.Deleted)
                        {
                            detachedSkipped++;
                            continue;
                        }
                        if ((string)r["Dir"] == GrblSDCard.EmptyMountMarker)
                        {
                            emptyMountSkipped++;
                            continue;
                        }
                        string nm = Path.GetFileName((string)r["Name"]);
                        present[nm] = new MacroStatusRow
                        {
                            Name = nm,
                            Size = r.Table.Columns.Contains("Size") ? r["Size"]?.ToString() : string.Empty,
                            FS = r.Table.Columns.Contains("Location") ? r["Location"]?.ToString() : string.Empty
                        };
                    }
                    ConsoleLog.Write(string.Format("[AtcMacros] GetStatus: table snapshot - totalRows={0}, detachedSkipped={1}, emptyMountSkipped={2}, present={3}",
                        totalRows, detachedSkipped, emptyMountSkipped, string.Join(",", present.Keys)));

                    FsMount target = GrblSDCard.Mounts.FirstOrDefault(m =>
                                         m.Name.IndexOf("littlefs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         m.Path.IndexOf("littlefs", StringComparison.OrdinalIgnoreCase) >= 0)
                                     ?? GrblSDCard.Mounts.FirstOrDefault(m => m.Path == "/")
                                     ?? GrblSDCard.Mounts.FirstOrDefault();
                    string destPath = target != null ? target.Path : "/littlefs";
                    ConsoleLog.Write(string.Format("[AtcMacros] GetStatus: mounts=[{0}], target={1}, destPath={2}",
                        string.Join(",", GrblSDCard.Mounts.Select(m => m.Name + "@" + m.Path)), target?.Name, destPath));

                    // Only flag Outdated when we actually read a DIFFERENT checksum. An empty/failed read (a
                    // raced FS query right after connect) must NOT flip every present macro to Outdated.
                    string embeddedSumNow = EmbeddedChecksum();
                    string onFs = ReadControllerFile(model, JoinPath(destPath, ChecksumFile)).Trim();
                    bool stale = onFs.Length > 0 && onFs != embeddedSumNow;
                    ConsoleLog.Write(string.Format("[AtcMacros] GetStatus: checksum sidecar '{0}' -> onFs='{1}' (len={2}), embedded='{3}', stale={4}, GrblState={5}/{6}",
                        JoinPath(destPath, ChecksumFile), onFs, onFs.Length, embeddedSumNow, stale, model.GrblState.State, model.GrblState.Substate));

                    foreach (string name in Required)
                    {
                        MacroStatusRow found;
                        bool present1 = present.TryGetValue(name, out found);
                        int sz = 0;
                        bool sizeKnown = present1 && int.TryParse(found.Size, out sz);
                        int expected = ExpectedSize(name);

                        // A zero-length file is present-by-name but not actually installed - confirmed on real
                        // hardware: an interrupted/truncated write left tc.macro at 0 bytes, which still matched
                        // here (name present, sidecar checksum untouched since IT wasn't rewritten) and reported
                        // Installed, so M6 T8 silently ran the FIRMWARE'S DEFAULT tool change instead of the
                        // macro - no error, no gate, just a TLO reference that quietly never got set. Treated as
                        // Missing (not just Outdated) so the Machine Setup gate still fires on it.
                        bool empty = sizeKnown && sz <= 0;

                        // Beyond zero: any size that doesn't match the embedded copy's own byte count is a
                        // cheap, per-file signal of PARTIAL truncation/corruption too - no controller round-trip
                        // needed (Size is already in the listing), and unlike the atc.sum sidecar it can't go
                        // stale independently (ExpectedSize is derived fresh from this install's embedded macro
                        // every call). Flagged Outdated rather than Missing - the file IS there, just wrong.
                        bool sizeMismatch = sizeKnown && sz > 0 && sz != expected;

                        ConsoleLog.Write(string.Format(
                            "[AtcMacros] GetStatus: {0} - present={1}, rawSize='{2}', sizeKnown={3}, sz={4}, expected={5}, empty={6}, sizeMismatch={7}, checksumStale={8}, FS={9}",
                            name, present1, found?.Size, sizeKnown, sz, expected, empty, sizeMismatch, stale, found?.FS));

                        if (present1 && !empty)
                            rows.Add(new MacroStatusRow { Name = name, Size = found.Size, FS = found.FS, State = (stale || sizeMismatch) ? MacroState.Outdated : MacroState.Installed });
                        else
                            rows.Add(new MacroStatusRow { Name = name, State = MacroState.Missing });

                        ConsoleLog.Write(string.Format("[AtcMacros] GetStatus: {0} -> {1}", name, rows[rows.Count - 1].State));
                    }
                }
                catch (Exception ex)
                {
                    // Couldn't read the filesystem reliably (transient during connect/activate). Report nothing
                    // rather than crash or guess - callers treat an empty list as "unknown" (no gate, no false status).
                    ConsoleLog.Write("[AtcMacros] GetStatus: EXCEPTION, clearing rows (reporting UNKNOWN) - " + ex);
                    rows.Clear();
                }
            }

            ConsoleLog.Write(string.Format("[AtcMacros] GetStatus: exit, returning {0} row(s)", rows.Count));
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
            ConsoleLog.Write(string.Format("[AtcMacros] EnsureProvisioned: enter, _provisioning={0}", _provisioning));
            if (_provisioning)
            {
                ConsoleLog.Write("[AtcMacros] EnsureProvisioned: re-entrancy guard tripped - Skipped");
                return ProvisionResult.Skipped;   // re-entrancy guard - see _provisioning
            }
            _provisioning = true;
            try
            {
                // Runs whenever an ATC is configured: "ATC=0" (tc.macro missing) or "ATC=1" (present, but its
                // content may be out of date with a newer ioSender).
                ConsoleLog.Write(string.Format(
                    "[AtcMacros] EnsureProvisioned: model={0}, upload={1}, HasFS={2}, AtcMacrosRequired={3}, HasATC={4}, comOpen={5}, GrblState={6}/{7}",
                    model != null, upload != null, GrblInfo.HasFS, GrblInfo.AtcMacrosRequired, GrblInfo.HasATC,
                    Comms.com != null && Comms.com.IsOpen, model?.GrblState.State, model?.GrblState.Substate));
                if (model == null || upload == null || !GrblInfo.HasFS || !(GrblInfo.AtcMacrosRequired || GrblInfo.HasATC)
                     || Comms.com == null || !Comms.com.IsOpen)
                {
                    ConsoleLog.Write("[AtcMacros] EnsureProvisioned: precondition failed - Skipped");
                    return ProvisionResult.Skipped;
                }

                // Enumerate current files across every mount (also refreshes the SD Card tab's cache).
                bool loaded = GrblSDCard.Load(model, false);
                ConsoleLog.Write(string.Format("[AtcMacros] EnsureProvisioned: GrblSDCard.Load returned {0}", loaded));

                // A zero-length file counts as absent here too (same reasoning as GetStatus above) - otherwise
                // EnsureProvisioned's whole point (self-healing a broken install) can't fire for exactly the
                // failure it exists to catch: a truncated/interrupted write leaves the name present at 0 bytes,
                // which - without this check - satisfies Required.All(present.Contains) and never gets re-sent.
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sizeByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (DataRowView rv in GrblSDCard.Files)
                {
                    if ((string)rv.Row["Dir"] == GrblSDCard.EmptyMountMarker)
                        continue;                                   // skip empty-filesystem placeholder rows
                    if (rv.Row.Table.Columns.Contains("Size") && rv.Row["Size"] is int sz && sz <= 0)
                        continue;                                   // zero-length - treat as not present
                    string nm = Path.GetFileName((string)rv.Row["Name"]);
                    present.Add(nm);
                    if (rv.Row.Table.Columns.Contains("Size") && rv.Row["Size"] is int s)
                        sizeByName[nm] = s;
                }
                ConsoleLog.Write(string.Format("[AtcMacros] EnsureProvisioned: present=[{0}], sizeByName=[{1}]",
                    string.Join(",", present), string.Join(",", sizeByName.Select(kv => kv.Key + "=" + kv.Value))));
                // A required macro present at the wrong (nonzero) byte count is PARTIAL truncation/corruption -
                // same cheap per-file discriminator as GetStatus, folded into `stale` below so a re-upload still
                // fires even if the aggregate atc.sum sidecar didn't happen to catch it.
                bool anySizeMismatch = Required.Any(n => sizeByName.TryGetValue(n, out int s) && s != ExpectedSize(n));
                foreach (string n in Required)
                    ConsoleLog.Write(string.Format("[AtcMacros] EnsureProvisioned: {0} - sizeOnFs={1}, expected={2}, mismatch={3}",
                        n, sizeByName.TryGetValue(n, out int sv) ? sv.ToString() : "(absent)", ExpectedSize(n),
                        sizeByName.TryGetValue(n, out int sv2) && sv2 != ExpectedSize(n)));

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
                string sidecarRead = ReadControllerFile(model, JoinPath(destPath, ChecksumFile)).Trim();
                bool checksumStale = sidecarRead != embeddedSum;
                bool stale = anySizeMismatch || checksumStale;
                ConsoleLog.Write(string.Format(
                    "[AtcMacros] EnsureProvisioned: missing={0}, anySizeMismatch={1}, sidecarRead='{2}', embeddedSum='{3}', checksumStale={4}, stale={5}, GrblState={6}/{7}",
                    missing, anySizeMismatch, sidecarRead, embeddedSum, checksumStale, stale, model.GrblState.State, model.GrblState.Substate));

                if (!missing && !stale)
                {
                    ConsoleLog.Write("[AtcMacros] EnsureProvisioned: UpToDate - nothing to do");
                    return ProvisionResult.UpToDate;   // present and current - nothing to do
                }

                bool accepted = confirmUpload == null || confirmUpload(missing ? UpdateReason.Missing : UpdateReason.Outdated);
                ConsoleLog.Write(string.Format("[AtcMacros] EnsureProvisioned: confirmUpload -> accepted={0} (reason={1})",
                    accepted, missing ? UpdateReason.Missing : UpdateReason.Outdated));
                if (!accepted)
                    return ProvisionResult.Skipped;   // user declined

                model.Message = missing ? "Installing ATC macros..." : "Updating ATC macros...";

                // File create/delete requires the controller to be idle; in a homing-required (or other) alarm
                // it returns error:9 "not allowed until homed". Unlock so the macros can be written without first
                // homing - the upload moves nothing, and the user homes when ready afterwards.
                if (model.GrblState.State == GrblStates.Alarm)
                {
                    ConsoleLog.Write(string.Format("[AtcMacros] EnsureProvisioned: controller in Alarm (substate={0}) - sending $X to unlock",
                        model.GrblState.Substate));
                    Grbl.WaitForResponse(GrblConstants.CMD_UNLOCK);
                    ConsoleLog.Write(string.Format("[AtcMacros] EnsureProvisioned: post-unlock GrblState={0}/{1}",
                        model.GrblState.State, model.GrblState.Substate));
                    // $X can fail to clear some alarms (e.g. certain homing-required configs still refuse file
                    // ops after unlock). Bail out here rather than fall through to the raw unlink/YModem calls
                    // below: those use Comms.AwaitAck(), which has NO timeout anywhere in this codebase - a
                    // rejected ("error:9") response to an unlink issued while still alarmed hangs the UI thread
                    // forever instead of failing. Surfacing a clear message beats a silent freeze.
                    if (model.GrblState.State == GrblStates.Alarm)
                    {
                        ConsoleLog.Write("[AtcMacros] EnsureProvisioned: still in Alarm after $X - bailing out, Failed");
                        model.Message = "ATC macro update skipped: controller is still in alarm - clear it (e.g. Home) and try again.";
                        return ProvisionResult.Failed;
                    }
                }

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
            var rawResponses = new List<string>();
            bool? res = null;
            var ct = new System.Threading.CancellationToken();
            string cmd = GrblConstants.CMD_SDCARD_DUMP + path;

            ConsoleLog.Write(string.Format("[AtcMacros] ReadControllerFile: sending '{0}', GrblState={1}/{2}",
                cmd, model.GrblState.State, model.GrblState.Substate));

            Comms.com.PurgeQueue();
            model.SuspendProcessing = true;

            // IsBackground so an unresponsive controller (WaitFor never returns) can't keep the process alive and
            // hang ioSender on close - a foreground worker here is exactly what wedged shutdown before.
            new System.Threading.Thread(() =>
            {
                try { res = WaitFor.AckResponse<string>(
                    ct,
                    response => {
                        rawResponses.Add(response);
                        // Realtime status reports ("<Idle|...>") keep arriving during this read - the poll
                        // thread isn't paused by SuspendProcessing, only rerouted here - and must be filtered
                        // out same as "ok"/error/"[...", or a poll landing mid-dump corrupts the checksum text
                        // and false-trips the Outdated gate (confirmed root cause of the Alarm-state repro).
                        if (response != "ok" && !response.StartsWith("error") && !response.StartsWith("[") && !response.StartsWith("<"))
                            sb.AppendLine(response);
                    },
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    400, () => Comms.com.WriteCommand(cmd)); }
                catch (Exception ex) { ConsoleLog.Write("[AtcMacros] ReadControllerFile: worker EXCEPTION - " + ex); res = false; }
            }) { IsBackground = true }.Start();

            // Hard wall-clock cap so the UI thread can't spin forever if the controller never answers; on timeout
            // we return what little was read (empty), which reads as "missing/stale" and just re-prompts an upload.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (res == null && sw.ElapsedMilliseconds < 3000)
                EventUtils.DoEvents();

            ConsoleLog.Write(string.Format(
                "[AtcMacros] ReadControllerFile: '{0}' -> res={1} after {2}ms, rawResponses=[{3}], result='{4}' (len={5}), GrblState now={6}/{7}",
                cmd, res, sw.ElapsedMilliseconds, string.Join(" | ", rawResponses), sb.ToString().Trim(), sb.Length,
                model.GrblState.State, model.GrblState.Substate));

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

        // Byte length of the embedded copy, as written to the controller (UTF8, matching YModemWrite's own
        // encoding) - a cheap first-order discriminator alongside the aggregate atc.sum checksum. Unlike that
        // sidecar, this needs no extra controller round-trip (Size is already in the listing GetStatus/
        // EnsureProvisioned fetch anyway) and catches PARTIAL truncation too, not just a 0-byte file - and it
        // can't go stale independently the way the sidecar could (it's derived fresh from THIS install's
        // embedded macro every call, never written to the controller or cached).
        static readonly Dictionary<string, int> _expectedSizeCache = new Dictionary<string, int>();
        static int ExpectedSize(string name)
        {
            int size;
            if (_expectedSizeCache.TryGetValue(name, out size))
                return size;
            size = System.Text.Encoding.UTF8.GetByteCount(ReadEmbedded(name) ?? string.Empty);
            _expectedSizeCache[name] = size;
            return size;
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
