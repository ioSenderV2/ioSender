/*
 * GrblSDCard.cs - part of CNC Core library
 *
 * The live client for the controller's filesystems: $FI mount discovery, $F/$F+ directory
 * listings, and the parsed file table they produce.
 *
 * Lives in CNC.Core, and used to live in SDCardView.xaml.cs. It talks to the machine - Comms.com,
 * grblHAL commands, reply parsing - and has no WPF in it at all; only the browser that DISPLAYS
 * the listing is client functionality. Being parked in a view's code-behind meant AtcMacros, which
 * needs the listing to decide whether the controller-side macros are installed, had to reach into
 * the WPF assembly for it.
 *
 * v0.01 / 2026-08-04 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2026, Io Engineering (Terje Io)
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
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

namespace CNC.Core
{
    public static class GrblSDCard
    {
        private static DataTable data;
        private static int id = 0;
        private static GrblViewModel grbl;
        private static string curLocation = string.Empty, curPath = string.Empty;

        // One-line free-space banner across the mounted filesystems (set on each Load).
        public static string FreeSpace { get; private set; }

        // Dir-column marker for an empty-filesystem placeholder row (a mount with no files), so the browser
        // can show it and use it as an upload target while run/delete skip it.
        public const string EmptyMountMarker = "fs";

        // Filesystems reported by the last $FI (for the Copy To / Move To submenus). Empty on legacy single-FS.
        public static List<FsMount> Mounts { get; private set; } = new List<FsMount>();

        static GrblSDCard()
        {
            data = new DataTable("Filelist");

            data.Columns.Add("Id", typeof(int));
            data.Columns.Add("Dir", typeof(string));
            data.Columns.Add("Name", typeof(string));
            data.Columns.Add("Size", typeof(int));
            data.Columns.Add("Invalid", typeof(bool));
            data.Columns.Add("Location", typeof(string));  // which filesystem the file lives on (SD / littlefs)
            data.Columns.Add("Path", typeof(string));       // that filesystem's mount path, for $F=/$FD=/$F<=
            data.PrimaryKey = new DataColumn[] { data.Columns["Id"] };

            FreeSpace = string.Empty;
        }

        public static DataView Files { get { return data.DefaultView; } }
        public static bool Loaded { get { return data.Rows.Count > 0; } }

        public static void Clear()
        {
            data.Clear();
            FreeSpace = string.Empty;
        }

        private static bool _loading;

        // Returns true if a filesystem listing was actually performed (the table is a fresh snapshot), false
        // if the call was skipped by the re-entrancy guard or the link was down - in which case the table's
        // contents are NOT a trustworthy result of this call. Callers that judge presence/absence (e.g.
        // AtcMacros.GetStatus feeding the Machine Setup gate) must treat false as "unknown", not "empty".
        // True when the SENDER has a program in flight, whatever the controller happens to be reporting.
        // It must be the sender's view, NOT GrblState: a macro's own (WAITIDLE) deliberately parks the
        // controller at Idle mid-program, and a status report reading "Idle" there is exactly how the
        // collision below got through.
        private static bool ProgramInFlight(GrblViewModel model)
        {
            if (model == null)
                return false;

            if (model.IsJobRunning)
                return true;

            switch (model.StreamingState)
            {
                case StreamingState.NoFile:
                case StreamingState.Idle:
                case StreamingState.Stop:
                case StreamingState.JobFinished:
                    return false;
                default:
                    return true;
            }
        }

        public static bool Load(GrblViewModel model, bool ViewAll)
        {
            // Most callers pass "DataContext as GrblViewModel", which is null whenever the view has not been
            // realized - and every path below dereferences it, starting with ProgramInFlight. Answering
            // "unknown" is both truthful and already handled by every caller (see the contract note above),
            // which beats an NRE surfacing from whatever unrelated operation happened to trigger the refresh.
            if (model == null)
                return false;

            // NEVER enumerate the controller filesystem while a program is streaming. $F / $CWD / $F<= are
            // ordinary commands on the same wire as the g-code, and the controller rejects a g-code line that
            // lands while it is busy servicing one. Confirmed on real hardware 2026-08-04: a Start Job tab
            // activation ran the readiness check during a Setup macro's (WAITIDLE) pause, this listing went
            // out mid-stream, and N600 came back "error:9 - G-code commands are locked out". The run stopped
            // there, so N630 - the G10 L2 P1 that writes the probed corner into G54 - never executed and the
            // operator was left with a completed-looking Setup and a zero work origin. The listing corrupted
            // its own answer too: atc.sum read back empty, so every macro reported Missing until the next poll.
            // False = "unknown", which every caller already handles (see the contract note above).
            if (ProgramInFlight(model))
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("fs", "Load: REFUSED - a program is in flight");
                return false;
            }

            // Load pumps the dispatcher (EventUtils.DoEvents) while waiting on the controller, so a refresh
            // queued meanwhile - tab activation, ATC provisioning - can re-enter and clear/rebuild the shared
            // DataTable mid-listing. That corrupted the parse state and cascaded into unhandled exceptions
            // (and a stack overflow via the modal error handler). Ignore re-entrant calls.
            if (_loading)
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("fs", "Load: REFUSED - re-entrant (a listing is already running)");
                return false;
            }
            _loading = true;

            try
            {
                grbl = model;

                // No point talking to the controller if the link is down or reconnecting - this
                // also avoids serial I/O exceptions when the SD tab is opened after a disconnect.
                // The user-facing message is set by the caller (SDCardView.Activate).
                if (Comms.com == null || !Comms.com.IsOpen)
                    return false;

                // Prefer $FI: it enumerates every mounted filesystem (SD and/or LittleFS) so they can
                // be shown together with a Location column and per-FS free space. Builds that do not
                // implement $FI (or that report nothing mounted) return no [FS:...] lines; in that case
                // fall back to the original single-filesystem listing so behaviour is unchanged.
                bool answered;
                var mounts = GetMounts(model, out answered);

                // A query the controller never answered says NOTHING about what is mounted, and this
                // method's whole contract is that false means "unknown" (see the header). Rendering a
                // timeout as an empty filesystem is what made the ATC macros look uninstalled every time
                // a listing landed during a homing cycle - grblHAL is silent for its entire duration.
                // Bail BEFORE clearing, so whatever was last read stays on screen instead of being
                // replaced by a confident, wrong "no files".
                if (!answered)
                {
                    if (DebugLog.Enabled)
                        DebugLog.Write("fs", "Load: UNKNOWN - $FI was not answered (controller busy?); keeping the previous listing");
                    model.Message = "Filesystem listing skipped - the controller did not answer (busy?). Reopen to retry.";
                    return false;
                }

                // The entry check above is not enough on its own. Every wait in here pumps the dispatcher
                // (EventUtils.DoEvents), which deliberately keeps the UI alive - so the operator can press
                // Run WHILE this listing is in flight, and the guard that legitimately passed on entry is
                // by then stale. Observed 2026-08-11 on the simulator: the listing began at 19:16:39 with
                // nothing streaming, the job started at 19:16:45.779, and this method's next $F went out at
                // 19:16:46.096 into the live stream - tearing the $# reply mid-line and consuming seven of
                // the job's "ok" acks. The stream then waited forever at Ln:40 for acks that no longer
                // existed, on a link that stayed perfectly healthy. So re-check before every further
                // command, not just once. Bail BEFORE clearing, so the previous listing stays on screen.
                if (ProgramInFlight(model))
                {
                    if (DebugLog.Enabled)
                        DebugLog.Write("fs", "Load: ABANDONED - a program started while $FI was in flight; keeping the previous listing");
                    return false;
                }

                data.Clear();
                FreeSpace = string.Empty;
                id = 0;
                Mounts = mounts;

                if (DebugLog.Enabled)
                    DebugLog.Write("fs", string.Format("Load: HasFS={0} HasSDCard={1} mountStatus={2} -> {3} mount(s) [{4}]",
                        GrblInfo.HasFS, GrblInfo.HasSDCard, model.SDCardMountStatus, mounts.Count,
                        string.Join(" | ", mounts.ConvertAll(m => m.Name + "@" + m.Path))));

                if (mounts.Count > 0)
                {
                    FreeSpace = GrblFilesystems.FreeSpaceSummary(mounts);

                    foreach (var mount in mounts)
                    {
                        // Re-checked per mount: ListMount pumps while awaiting each answer, so a job can
                        // start between one mount and the next. This is the exact edge that fired - the
                        // collision was the SECOND mount's $CWD/$F going out after Run was pressed.
                        if (ProgramInFlight(model))
                        {
                            if (DebugLog.Enabled)
                                DebugLog.Write("fs", "Load: ABANDONED mid-listing - a program started; the table is incomplete, so this returns UNKNOWN");
                            return false;
                        }

                        int before = data.Rows.Count;
                        bool listed = ListMount(model, mount.Name, mount.Path, ViewAll);
                        if (DebugLog.Enabled)
                            DebugLog.Write("fs", string.Format("Load: mount {0}@{1} contributed {2} row(s), answered={3}",
                                mount.Name, mount.Path, data.Rows.Count - before, listed));
                        // Only claim a filesystem is empty when the controller actually said so. An
                        // unanswered $F leaves the mount out entirely rather than labelling it "(no files)".
                        if (data.Rows.Count == before && listed)
                            // Empty filesystem: add a non-file placeholder so the mount stays visible and can be
                            // selected as an upload target. Marked via the (hidden, otherwise unused) Dir column so
                            // run/delete skip it (see SDCardView), and Upload targets its mount path.
                            data.Rows.Add(new object[] { id++, EmptyMountMarker, "(no files)", 0, false, mount.Name, mount.Path });
                    }

                    // Leave the working directory on a valid mount. Restoring to "/" errors (error:63 - Directory
                    // not found) when the root filesystem isn't mounted - e.g. SD enabled but no card inserted, with
                    // littlefs at /littlefs - so only use "/" when a mount actually lives there.
                    string cwd = mounts.Exists(m => m.Path == "/") ? "/" : mounts[0].Path;
                    // Tidy-up, but still a command on the shared wire - and the last mount's listing pumped
                    // on the way here. Skipping it only leaves the working directory where the listing left
                    // it, which is harmless; issuing it mid-stream is not.
                    if (!ProgramInFlight(model))
                    {
                        model.Silent = true;
                        Grbl.WaitForResponse("$CWD=" + (cwd.Length > 1 ? cwd.TrimEnd('/') : cwd));
                        model.Silent = false;
                    }
                    else if (DebugLog.Enabled)
                        DebugLog.Write("fs", "Load: skipped the $CWD restore - a program started while listing");
                }
                else
                    LegacyLoad(model, ViewAll);

                data.AcceptChanges();
                return true;
            }
            finally
            {
                _loading = false;
            }
        }

        // Enumerate mounted filesystems via $FI. Empty list => $FI unsupported or nothing mounted
        // (error:65), which steers Load() to the legacy single-filesystem path.
        // answered: the controller REPLIED to $FI (with or without any [FS:...] lines). False means the
        // query timed out, which is NOT the same fact as "this controller has no filesystems" - and the
        // difference matters enormously, because the caller renders one of them as "no files". grblHAL
        // goes completely silent for the duration of a homing cycle, so this times out routinely on a
        // connect that homes; observed 2026-08-11, and it is why the ATC macros kept "disappearing".
        private static List<FsMount> GetMounts(GrblViewModel model, out bool answered)
        {
            var mounts = new List<FsMount>();
            bool? res = null;
            var ct = new CancellationToken();

            Comms.com.PurgeQueue();
            model.Silent = true;

            new Thread(() =>
            {
                // A worker exception must still set res, or the res==null pump below spins forever.
                try
                {
                    res = WaitFor.AckOrErrorResponse<string>(
                        ct,
                        response => {
                            var fs = GrblFilesystems.ParseMountLine(response);
                            if (DebugLog.Enabled)
                                DebugLog.Write("fs", string.Format("$FI saw \"{0}\" -> {1}", response,
                                    fs == null ? "(not a mount line)" : fs.Name + "@" + fs.Path));
                            if (fs != null) mounts.Add(fs);
                        },
                        a => model.OnResponseReceived += a,
                        a => model.OnResponseReceived -= a,
                        1500, () => Comms.com.WriteCommand("$FI")) == WaitFor.AckOutcome.Ok;
                }
                catch (Exception ex) { res = false; if (DebugLog.Enabled) DebugLog.Write("fs", "$FI threw: " + ex.Message); }
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            model.Silent = false;

            // One mount, listed once. A controller that reports the same filesystem twice used to get it
            // enumerated twice - double the $CWD/$F traffic on the wire the g-code shares, and a phantom
            // second mount whose listing could collide with a running job. grblHAL's own simulator did
            // exactly this (mounted littlefs twice, fixed simulator-side), but the cost of believing a
            // duplicate is high enough that it is not worth trusting the controller to never repeat one.
            int seen = mounts.Count;
            mounts = mounts
                .GroupBy(m => (m.Name ?? string.Empty) + "@" + (m.Path ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (DebugLog.Enabled && mounts.Count != seen)
                DebugLog.Write("fs", string.Format("$FI reported {0} mount(s), {1} distinct - ignoring the duplicate(s)", seen, mounts.Count));

            answered = res == true;
            return mounts;
        }

        // List one filesystem by changing to its mount path ($CWD) and issuing $F / $F+. Each file
        // is tagged with its Location/Path so run/delete/download can target the right filesystem.
        // Returns whether the controller ANSWERED - a timeout here means "no files" is unknown for this
        // mount, not established (same distinction as GetMounts; see its comment).
        private static bool ListMount(GrblViewModel model, string location, string path, bool ViewAll)
        {
            model.Silent = true;
            Grbl.WaitForResponse("$CWD=" + (path.Length > 1 ? path.TrimEnd('/') : path));

            bool? res = null;
            var outcome = WaitFor.AckOutcome.Timeout;
            var ct = new CancellationToken();

            Comms.com.PurgeQueue();
            curLocation = location;
            curPath = path;

            new Thread(() =>
            {
                // A worker exception must still set res, or the res==null pump below spins forever.
                // An "error:N" answer ends the wait immediately (see AckOrErrorResponse) and counts as
                // NOT answered - the listing may be partial, and a partial listing reported as complete
                // is how present macros come to be called missing.
                try
                {
                    outcome = WaitFor.AckOrErrorResponse<string>(
                        ct,
                        response => Process(response),
                        a => model.OnResponseReceived += a,
                        a => model.OnResponseReceived -= a,
                        2000, () => Comms.com.WriteCommand(ViewAll ? GrblConstants.CMD_SDCARD_DIR_ALL : GrblConstants.CMD_SDCARD_DIR));
                    res = outcome == WaitFor.AckOutcome.Ok;
                }
                catch (Exception ex) { res = false; if (DebugLog.Enabled) DebugLog.Write("fs", "$F threw: " + ex.Message); }
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            if (DebugLog.Enabled)
                DebugLog.Write("fs", string.Format("ListMount {0}@{1}: $F completed res={2} ({3})", location, path, res, outcome));

            model.Silent = false;
            return res == true;
        }

        // Original single-filesystem listing: mount the SD card if needed, then $F the current FS.
        private static void LegacyLoad(GrblViewModel model, bool ViewAll)
        {
            bool? res = null;
            CancellationToken cancellationToken = new CancellationToken();

            curLocation = GrblInfo.HasSDCard ? "SD" : string.Empty;
            curPath = string.Empty;

            if (GrblInfo.HasSDCard && grbl.SDCardMountStatus == SDState.Unmounted)
            {
                Comms.com.PurgeQueue();

                new Thread(() =>
                {
                    res = WaitFor.AckResponse<string>(
                        cancellationToken,
                        response => CardCheck(response),
                        a => model.OnResponseReceived += a,
                        a => model.OnResponseReceived -= a,
                        1500, () => Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_MOUNT));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();
            }

            if (!GrblInfo.HasSDCard || grbl.SDCardMountStatus == SDState.Mounted || grbl.SDCardMountStatus == SDState.Detected)
            {
                Comms.com.PurgeQueue();

                res = null;
                model.Silent = true;

                new Thread(() =>
                {
                res = WaitFor.AckResponse<string>(
                    cancellationToken,
                    response => Process(response),
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    2000, () => Comms.com.WriteCommand(ViewAll ? GrblConstants.CMD_SDCARD_DIR_ALL : GrblConstants.CMD_SDCARD_DIR));
                }).Start();

                while (res == null)
                    EventUtils.DoEvents();

                model.Silent = false;
            }
        }

        private static void CardCheck(string data)
        {
            if(data == "ok")
                grbl.SDCardMountStatus = SDState.Mounted;
        }

        private static void Process(string data)
        {
            string filename = "";
            int filesize = 0;
            bool invalid = false;

            if (data.StartsWith("[FILE:"))
            {
                string[] parameters = data.TrimEnd(']').Split('|');
                foreach (string parameter in parameters)
                {
                    string[] valuepair = parameter.Split(':');
                    switch (valuepair[0])
                    {
                        case "[FILE":
                            filename = valuepair[1];
                            break;

                        case "SIZE":
                            filesize = int.Parse(valuepair[1]);
                            break;

                        case "INVALID":
                            invalid = true;
                            break;
                    }
                }

                // $F at a root filesystem recurses into nested mounts, so a file can be reported while
                // listing an ancestor - e.g. on a board with the SD card at "/" and LittleFS at
                // "/littlefs", the SD listing returns the /littlefs/* files too. Attribute each file to
                // its deepest containing mount; skip it here otherwise, so it isn't duplicated under (and
                // mis-pathed on) the parent filesystem - the SD card should show "/..." paths, not
                // "/littlefs/...". Mounts is empty on the legacy single-FS path, so that is unaffected.
                string owner = curPath;
                foreach (var m in GrblSDCard.Mounts)
                {
                    if (m.Path.Length > owner.Length &&
                         filename.StartsWith(m.Path.TrimEnd('/') + "/", System.StringComparison.OrdinalIgnoreCase))
                        owner = m.Path;
                }
                if (owner != curPath)
                {
                    if (DebugLog.Enabled)
                        DebugLog.Write("fs", string.Format("Process: SKIPPED \"{0}\" - owned by {1}, listing {2}", filename, owner, curPath));
                    return;
                }

                if (DebugLog.Enabled)
                    DebugLog.Write("fs", string.Format("Process: ROW \"{0}\" size={1} loc={2} path={3}", filename, filesize, curLocation, curPath));
                GrblSDCard.data.Rows.Add(new object[] { id++, "", filename, filesize, invalid, curLocation, curPath });
            }
            else if (data == "error:62" || data == "error:64")
            {
                if (DebugLog.Enabled)
                    DebugLog.Write("fs", "Process: " + data + " -> SDCardMountStatus = Unmounted");
                grbl.SDCardMountStatus = SDState.Unmounted;
            }
            else if (DebugLog.Enabled && data != "ok")
                DebugLog.Write("fs", "Process: ignored \"" + data + "\"");
        }
    }
}
