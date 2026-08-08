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
        var sendsWhileArmed = new List<string>();
        foreach (var line in log)
        {
            DateTime ts = ParseStamp(line, t0);
            if (line.Contains("WAITIDLE armed")) armed = ts;
            else if (line.Contains("WAITIDLE clear")) cleared = ts;
            else if (line.Contains("SEND idx=" + tailRow + " ")) tailSent = ts;
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
            Check(tailSent > cleared, "tail hit the wire only after the release",
                  string.Format("clear={0:HH:mm:ss.fff} tail={1:HH:mm:ss.fff}", cleared, tailSent));
            // Two consecutive idle reports at a ~200ms poll = at least ~200ms between the last Run
            // report and release; a release inside that window would mean the streak logic is broken.
            Check((cleared.Value - lastRun).TotalMilliseconds >= 150, "release waited out the two-report idle streak",
                  string.Format("{0:0}ms after last Run", (cleared.Value - lastRun).TotalMilliseconds));
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
