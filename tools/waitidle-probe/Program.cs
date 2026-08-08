/*
 * waitidle-probe - exercises StreamPump's (WAITIDLE) dispatch barrier against the real grblHAL
 * simulator over the real TelnetStream. See the csproj header. Exit code 0 = all checks passed.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using CNC.Core;

static class Probe
{
    static int failures = 0;

    static void Check(bool ok, string what, string detail = "")
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
        if (!ok) failures++;
    }

    // (timestamp, grbl state name) timeline harvested from this probe's OWN ReplyClassified
    // subscription - independent of the pump's, which is the multicast event doing its job.
    static readonly List<(DateTime t, string state)> statusTimeline = new();
    static readonly object timelineLock = new();

    static int Main()
    {
        Console.WriteLine("waitidle-probe: StreamPump (WAITIDLE) barrier vs the real simulator\n");

        // ---- stand up an isolated simulator ------------------------------------------------------
        string simSrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"ioSender\Simulator");
        if (!File.Exists(Path.Combine(simSrc, "grblHAL_sim.exe")))
        {
            Console.WriteLine("no simulator at " + simSrc + " - build one in Settings > Simulator first");
            return 2;
        }
        string simDir = Path.Combine(Path.GetTempPath(), "waitidle-probe-sim");
        Directory.CreateDirectory(simDir);
        foreach (var f in new[] { "grblHAL_sim.exe", "EEPROM.DAT", "littlefs.img" })
            if (File.Exists(Path.Combine(simSrc, f)))
                File.Copy(Path.Combine(simSrc, f), Path.Combine(simDir, f), true);

        int port;
        using (var l = new TcpListener(System.Net.IPAddress.Loopback, 0))
        { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }

        var sim = Process.Start(new ProcessStartInfo(Path.Combine(simDir, "grblHAL_sim.exe"), "-p " + port)
        { WorkingDirectory = simDir, UseShellExecute = false, CreateNoWindow = true });

        try
        {
            return Run(port);
        }
        finally
        {
            try { if (sim != null && !sim.HasExited) sim.Kill(); } catch { }
        }
    }

    static int Run(int port)
    {
        // ---- connect the real transport ----------------------------------------------------------
        var stream = new TelnetStream("127.0.0.1:" + port, null);   // null context = headless, inline handlers
        for (int i = 0; i < 50 && !stream.IsOpen; i++) Thread.Sleep(100);
        Check(stream.IsOpen, "telnet connected to the simulator", "port " + port);
        if (!stream.IsOpen) return 1;

        var otherReplies = new List<string>();   // alarms/messages - an ALARM must never hide again
        stream.ReplyClassified += (cls, reply) =>
        {
            if (cls == Comms.ReplyClass.Status)
            {
                int bar = reply.IndexOf('|');
                string state = bar > 1 ? reply.Substring(1, bar - 1) : "?";
                lock (timelineLock) statusTimeline.Add((DateTime.Now, state));
            }
            else if (cls == Comms.ReplyClass.Other)
                lock (timelineLock) otherReplies.Add(reply);
        };

        Thread.Sleep(500);                       // let the hello land
        Comms.com.WriteCommand("$X");            // clear a homing-required alarm if armed
        Thread.Sleep(300);

        // The sim boots with ALL limit pins asserted (Pn:XYZ in its first report), and the user's
        // copied EEPROM has hard limits enabled - so the first real motion instantly trips ALARM:1.
        // Found the hard way: motion "completed" in ~0.4s and the first probe version read the
        // post-alarm silence as success. These writes land in OUR COPY of EEPROM.DAT, never the
        // user's; realtime pacing itself works fine (Run at FS:60 observed on the wire).
        foreach (var setting in new[] { "$21=0", "$20=0", "$22=0" })
        {
            Comms.com.WriteCommand(setting);
            Thread.Sleep(150);
        }
        Comms.com.WriteCommand("$X");            // unlock again in case the writes re-latched anything
        Thread.Sleep(300);

        // ---- status poll (the app's PollGrbl stands in for nothing here - we poll ourselves) ------
        bool polling = true;
        var poller = new Thread(() => { while (polling) { try { Comms.com.WriteByte((byte)'?'); } catch { } Thread.Sleep(200); } }) { IsBackground = true };
        poller.Start();

        // ---- build the program through the REAL load-time path (GCodeJob.AddBlock sets Directive) --
        var model = new GrblViewModel();
        stream.DataReceived += model.DataReceived;   // headless model wiring (net8-smoke's pattern) -
                                                     // feeds GrblState/homed state for the PREREQ checks below
        var prog = new GCodeProgram(model);      // transient ctor - never touches shared state
        prog.AddBlock("waitidle-probe", CNC.Core.Action.New);   // New's arg is a NAME, not a block
        prog.AddBlock("(header comment that mentions WAITIDLE mid-prose - must NOT arm)");
        prog.AddBlock("G21 G91");
        prog.AddBlock("G1 Z-5 F60");             // 5s of genuine Run state
        prog.AddBlock("G1 Z5 F60");              // 5s back
        prog.AddBlock("(WAITIDLE)");
        prog.AddBlock("G4 P0.5");                // the tail the barrier must hold back
        prog.AddBlock("M2");
        prog.AddBlock("", CNC.Core.Action.End);

        int waitIdleRow = -1, tailRow = -1;
        for (int i = 0; i < prog.Data.Count; i++)
        {
            if (prog.Data[i].Directive == "WAITIDLE") waitIdleRow = i;
            if (prog.Data[i].Data.StartsWith("G4")) tailRow = i;
        }
        Check(prog.Data[0].Directive == null, "prose comment NOT recognized as a directive", prog.Data[0].Data);
        Check(waitIdleRow >= 0, "(WAITIDLE) row flagged at load time", "row " + waitIdleRow);
        Check(tailRow > waitIdleRow, "tail row sits after the barrier row", "row " + tailRow);

        // ---- stream it through the real pump ------------------------------------------------------
        PumpLog.Enabled = true;
        PumpLog.Clear();

        var finished = new ManualResetEventSlim();
        string error = null;
        var pump = new StreamPump(model, null, null);   // null marshals = inline on pump thread (headless)
        var t0 = DateTime.Now;
        pump.Start(prog, 0, prog.Blocks - 1, 512, true, true, false,
                   () => finished.Set(), e => { error = e; finished.Set(); });

        bool done = finished.Wait(TimeSpan.FromSeconds(45));
        polling = false;
        Check(done, "job reached a terminal state within 45s");
        Check(error == null, "no streaming error", error ?? "");
        if (!done) return 1;

        // ---- assertions from the two independent timelines ---------------------------------------
        // PumpLog: SEND/armed/clear with HH:mm:ss.fff stamps; statusTimeline: our own Run/Idle log.
        var log = File.ReadAllLines(PumpLog.FilePath);
        DateTime? armed = null, cleared = null, tailSent = null;
        int clearedLine = -1, tailLine = -1;   // log ORDER, not stamps: clear->tail can land in the same ms
        var sendsWhileArmed = new List<string>();
        for (int li = 0; li < log.Length; li++)
        {
            string line = log[li];
            DateTime ts = ParseStamp(line, t0);
            if (line.Contains("WAITIDLE armed")) armed = ts;
            else if (line.Contains("WAITIDLE clear")) { cleared = ts; clearedLine = li; }
            else if (line.Contains("SEND idx=" + tailRow + " ")) { tailSent = ts; tailLine = li; }
            else if (armed != null && cleared == null && line.Contains("SEND idx=")) sendsWhileArmed.Add(line);
        }

        Check(armed != null, "barrier armed");
        Check(cleared != null, "barrier cleared");
        Check(sendsWhileArmed.Count == 0, "no line sent while the barrier held", string.Join(" | ", sendsWhileArmed));
        Check(tailSent != null, "tail line was sent after release");

        (DateTime t, string state)[] timeline;
        lock (timelineLock) timeline = statusTimeline.ToArray();
        var byState = timeline.GroupBy(s => s.state).Select(g => g.Key + "x" + g.Count());
        Console.WriteLine("  states seen: " + string.Join(", ", byState) +
                          (armed != null && cleared != null ? string.Format("   barrier held {0:0}ms", (cleared.Value - armed.Value).TotalMilliseconds) : ""));
        string[] others;
        lock (timelineLock) others = otherReplies.Where(r => r.StartsWith("ALARM") || r.StartsWith("[MSG")).Distinct().ToArray();
        if (others.Length > 0)
            Console.WriteLine("  non-status replies: " + string.Join(" | ", others));
        Check(!others.Any(r => r.StartsWith("ALARM")), "no ALARM during the run");
        bool sawRun = timeline.Any(s => s.state == "Run");
        Check(sawRun, "simulator actually reported Run during the moves (real motion happened)");

        if (armed != null && cleared != null && tailSent != null && sawRun)
        {
            var lastRun = timeline.Where(s => s.state == "Run").Max(s => s.t);
            Check(cleared > armed, "clear follows arm", string.Format("held {0:0}ms", (cleared.Value - armed.Value).TotalMilliseconds));
            Check(cleared > lastRun, "barrier released only AFTER the last Run report",
                  string.Format("lastRun={0:HH:mm:ss.fff} clear={1:HH:mm:ss.fff}", lastRun, cleared));
            Check(tailLine > clearedLine, "tail hit the wire only after the release",
                  string.Format("clear@line{0} tail@line{1}", clearedLine, tailLine));
            // Two consecutive idle reports at a ~200ms poll = at least ~200ms between the last Run
            // report and release; a release inside that window would mean the streak logic is broken.
            Check((cleared.Value - lastRun).TotalMilliseconds >= 150, "release waited out the two-report idle streak",
                  string.Format("{0:0}ms after last Run", (cleared.Value - lastRun).TotalMilliseconds));
        }

        // ---- PREREQ evaluator against the live sim (unified streaming Step 5's shared evaluator) ----
        // The sim is unhomed (homing disabled in our EEPROM copy), so 'homed' must fail; the link is
        // up and the controller idle, so 'connected' must pass - and a combined line must report only
        // the one genuinely unmet condition.
        Console.WriteLine();
        var unmetHomed = MacroRunner.EvaluatePrereqLines(model, new[] { "(PREREQ homed)" });
        Check(unmetHomed.Count == 1 && unmetHomed[0].Contains("not homed"), "PREREQ homed correctly unmet on the unhomed sim",
              string.Join("; ", unmetHomed));
        var unmetConn = MacroRunner.EvaluatePrereqLines(model, new[] { "(PREREQ connected)" });
        Check(unmetConn.Count == 0, "PREREQ connected passes on a live link", string.Join("; ", unmetConn));
        var unmetBoth = MacroRunner.EvaluatePrereqLines(model, new[] { "(PREREQ homed, connected)" });
        Check(unmetBoth.Count == 1, "combined PREREQ reports only the genuinely unmet condition", string.Join("; ", unmetBoth));

        // ---- (MBOX) barrier, OK path (unified streaming Step 4a) ----------------------------------
        Console.WriteLine();
        string promptMsg = null;
        DateTime promptAt = DateTime.MinValue;
        MacroRunner.HoldPrompt = (t, m, cancellable, yesNo) =>
        {
            promptMsg = m; promptAt = DateTime.Now;
            Thread.Sleep(700);           // operator "reading" - dispatch must stay held this whole time
            return true;
        };

        var prog2 = new GCodeProgram(model);
        prog2.AddBlock("mbox-ok", CNC.Core.Action.New);
        prog2.AddBlock("G21 G91");
        prog2.AddBlock("G1 Z-1 F120");
        prog2.AddBlock("(MBOX, OKCANCEL, install the probe now)");
        prog2.AddBlock("G4 P0.5");
        prog2.AddBlock("M2");
        prog2.AddBlock("", CNC.Core.Action.End);
        int mboxTail = -1;
        for (int i = 0; i < prog2.Data.Count; i++) if (prog2.Data[i].Data.StartsWith("G4")) mboxTail = i;

        PumpLog.Clear();
        var fin2 = new ManualResetEventSlim();
        string err2 = null;
        var pump2 = new StreamPump(model, null, null);
        pump2.Start(prog2, 0, prog2.Blocks - 1, 512, true, true, false,
                    () => fin2.Set(), e => { err2 = e; fin2.Set(); });
        bool done2 = fin2.Wait(TimeSpan.FromSeconds(20));
        Check(done2 && err2 == null, "MBOX-OK job finished cleanly", err2 ?? "");
        Check(promptMsg == "install the probe now", "prompt shown with the parsed message", promptMsg ?? "(never shown)");

        var log2 = File.ReadAllLines(PumpLog.FilePath);
        DateTime? mArmed = null, mOk = null, mTail = null;
        foreach (var line in log2)
        {
            DateTime ts = ParseStamp(line, t0);
            if (line.Contains("MBOX armed")) mArmed = ts;
            else if (line.Contains("MBOX OK")) mOk = ts;
            else if (line.Contains("SEND idx=" + mboxTail + " ")) mTail = ts;
        }
        Check(mArmed != null && mOk != null && mTail != null, "armed/answered/tail all logged",
              string.Format("armed={0} ok={1} tail={2}", mArmed != null, mOk != null, mTail != null));
        if (mOk != null && mTail != null && promptAt != DateTime.MinValue)
        {
            Check((mOk.Value - promptAt).TotalMilliseconds >= 600, "dispatch held while the operator was answering",
                  string.Format("{0:0}ms prompt-to-answer", (mOk.Value - promptAt).TotalMilliseconds));
            Check(mTail >= mOk, "tail only after the answer",
                  string.Format("ok={0:HH:mm:ss.fff} tail={1:HH:mm:ss.fff}", mOk, mTail));
        }

        // ---- (MBOX) barrier, Cancel path: host Stop callback, tail never sent ----------------------
        MacroRunner.HoldPrompt = (t, m, cancellable, yesNo) => false;   // instant Cancel
        var cancelFired = new ManualResetEventSlim();

        var prog3 = new GCodeProgram(model);
        prog3.AddBlock("mbox-cancel", CNC.Core.Action.New);
        prog3.AddBlock("G21 G91");
        prog3.AddBlock("(MBOX, OKCANCEL, about to cut - continue?)");
        prog3.AddBlock("G1 Z-1 F120");
        prog3.AddBlock("M2");
        prog3.AddBlock("", CNC.Core.Action.End);
        int cancelTail = -1;
        for (int i = 0; i < prog3.Data.Count; i++) if (prog3.Data[i].Data.StartsWith("G1")) cancelTail = i;

        PumpLog.Clear();
        var fin3 = new ManualResetEventSlim();
        var pump3 = new StreamPump(model, null, null);
        pump3.Start(prog3, 0, prog3.Blocks - 1, 512, true, true, false,
                    () => fin3.Set(), e => fin3.Set(),
                    onOperatorCancel: () => cancelFired.Set());
        bool cancelled = cancelFired.Wait(TimeSpan.FromSeconds(10));
        Check(cancelled, "Cancel routed to the host's Stop callback");
        Thread.Sleep(500);              // anything wrongly queued would go out in this window
        pump3.Abort();                  // play JobRunner.Stop's AbortPump role for the headless harness
        bool finishedAnyway = fin3.Wait(TimeSpan.FromMilliseconds(300));
        Check(!finishedAnyway, "cancelled job did NOT report finished");
        var log3 = File.ReadAllLines(PumpLog.FilePath);
        Check(!log3.Any(l => l.Contains("SEND idx=" + cancelTail + " ")), "post-MBOX line never hit the wire on Cancel");

        // ---- (PROMPT) slice (Step 4b) --------------------------------------------------------------
        // Field substitution needs the #<...> passthrough load path, which is gated on the controller
        // reporting EXPR - ask via $I (the model is wired, so NEWOPT parsing is the real one).
        Console.WriteLine();
        GrblInfo.Get(model);   // the REAL $I handshake - populates GrblInfo (incl. ExpressionsSupported) from NEWOPT
        Check(GrblInfo.ExpressionsSupported, "sim reports EXPR (needed for #<...> passthrough load)");

        if (GrblInfo.ExpressionsSupported)
        {
            var prog4 = new GCodeProgram(model);
            prog4.AddBlock("prompt-fields", CNC.Core.Action.New);
            prog4.AddBlock("(PROMPT depth, 3, Cut depth)");
            prog4.AddBlock("G21 G91");
            prog4.AddBlock("G1 Z-#<_depth> F120");
            prog4.AddBlock("G1 Z#<_depth> F120");
            prog4.AddBlock("M2");
            prog4.AddBlock("", CNC.Core.Action.End);

            // JobRunner's half (collect + dialog) played by the harness: collect via the REAL collector,
            // then stand in for the operator changing the default from 3 to 2.
            var fields = MacroRunner.CollectPromptFields(
                prog4.Data.Where(b => b.Directive == "PROMPT").Select(b => (string)b.Data).ToList());
            Check(fields.Count == 1 && fields[0].Inner == "_depth" && fields[0].Value == "3",
                  "field collected with declared default", fields.Count > 0 ? fields[0].Inner + "=" + fields[0].Value : "(none)");
            if (fields.Count == 1) fields[0].Value = "2";

            PumpLog.Clear();
            var fin4 = new ManualResetEventSlim();
            string err4 = null;
            var pump4 = new StreamPump(model, null, null);
            pump4.Start(prog4, 0, prog4.Blocks - 1, 512, true, true, false,
                        () => fin4.Set(), e => { err4 = e; fin4.Set(); }, promptFields: fields);
            bool done4 = fin4.Wait(TimeSpan.FromSeconds(20));
            Check(done4 && err4 == null, "prompt-fields job finished cleanly", err4 ?? "");

            var log4 = File.ReadAllLines(PumpLog.FilePath);
            var sent4 = log4.Where(l => l.Contains("SEND idx=")).ToArray();
            Check(sent4.Any(l => l.Contains("Z-2")) && !sent4.Any(l => l.Contains("#<")),
                  "wire got the substituted value, never the raw reference",
                  string.Join(" | ", sent4.Where(l => l.Contains("Z"))));
            Check(!sent4.Any(l => l.Contains("PROMPT")), "field row consumed sender-side, never streamed");
        }

        // Bare (PROMPT) = mid-stream checkpoint through the MBOX machinery.
        string checkpointMsg = null;
        MacroRunner.HoldPrompt = (t, m, c, y) => { checkpointMsg = m; return true; };
        var prog5 = new GCodeProgram(model);
        prog5.AddBlock("prompt-bare", CNC.Core.Action.New);
        prog5.AddBlock("G21 G91");
        prog5.AddBlock("(PROMPT)");
        prog5.AddBlock("G4 P0.2");
        prog5.AddBlock("M2");
        prog5.AddBlock("", CNC.Core.Action.End);

        PumpLog.Clear();
        var fin5 = new ManualResetEventSlim();
        string err5 = null;
        var pump5 = new StreamPump(model, null, null);
        pump5.Start(prog5, 0, prog5.Blocks - 1, 512, true, true, false,
                    () => fin5.Set(), e => { err5 = e; fin5.Set(); });
        bool done5 = fin5.Wait(TimeSpan.FromSeconds(15));
        Check(done5 && err5 == null, "bare-PROMPT job finished cleanly", err5 ?? "");
        Check(checkpointMsg == "Ready to continue?", "checkpoint prompted with the canned confirmation", checkpointMsg ?? "(never shown)");
        Check(File.ReadAllLines(PumpLog.FilePath).Any(l => l.Contains("PROMPT checkpoint armed")), "checkpoint went through the barrier");

        // ---- Load File path with #-expressions (the 2026-08-08 crash) ------------------------------
        // ParseFileLines never had AddBlock's passthrough, so a FILE with a #<...> line threw out of
        // the parser and the per-line error dialog then blew up cross-thread. Exercise the REAL file
        // loader on a temp file shaped exactly like the failing prompt-test.nc.
        if (GrblInfo.ExpressionsSupported)
        {
            Console.WriteLine();
            string ncPath = Path.Combine(Path.GetTempPath(), "waitidle-probe-load.nc");
            File.WriteAllText(ncPath, "(load test)\n(PROMPT depth, 2, Cut depth)\nG21 G91\nG1 Z-#<_depth> F120\nM2\n");
            var loadJob = new GCodeJob();
            var loaded = new List<GCodeBlock>();
            loadJob.BlockConsumer = b => loaded.Add(b);
            bool loadOk = false;
            string loadErr = null;
            try { loadOk = loadJob.ParseFileLines(ncPath); }
            catch (Exception ex) { loadErr = ex.GetType().Name + ": " + ex.Message; }
            loadJob.BlockConsumer = null;
            Check(loadOk && loadErr == null, "file with #-expression loads without throwing", loadErr ?? "");
            Check(loaded.Any(b => b.Data.Contains("#<_depth>")), "#-line kept verbatim (passthrough)",
                  string.Join(" | ", loaded.Select(b => b.Data)));
            Check(loaded.Any(b => b.Directive == "PROMPT"), "PROMPT row flagged through the file loader");
            try { File.Delete(ncPath); } catch { }
        }

        Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : string.Format("\n{0} CHECK(S) FAILED", failures));
        return failures == 0 ? 0 : 1;
    }

    // PumpLog stamps are HH:mm:ss.fff on today's date; anchor to the run's own day (a run never
    // spans midnight - it lasts seconds).
    static DateTime ParseStamp(string line, DateTime anchor)
    {
        if (line.Length < 12 || !TimeSpan.TryParse(line.Substring(0, 12), out var tod))
            return DateTime.MinValue;
        return anchor.Date + tod;
    }
}
