/*
 * stuck-probe-retract - exercises MacroProcessor.ClearStuckProbe against the real grblHAL
 * simulator. See the csproj header. Exit code 0 = all checks passed.
 *
 * The probe's asserted state is injected as a STATUS REPORT through the model's own parser
 * (model.DataReceived, the same entry point the live stream feeds), not by poking Signals - the
 * thing under test is what ClearStuckProbe reads out of the model, so the model has to arrive at
 * that state the way it really does. The status poller is stopped first, because a real report
 * with no |Pn: field clears Signals and would undo the injection mid-test. Every injection is
 * read back before it is used: if Signals does not actually carry Probe, the case that follows
 * proves nothing and must not print as a pass.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CNC.Core;
using CNC.Controls;

static class StuckProbeRetract
{
    static int failures = 0;

    // Anything that tried to open a message box. ClearStuckProbe's refusal path goes through
    // MacroProcessor.Run, which reports an unmet PREREQ to the operator - in a harness that must
    // be recorded and auto-answered, never shown, or the run blocks on a modal nobody can click.
    static readonly List<string> dialogs = new List<string>();

    static void Check(bool ok, string what, string detail = "")
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
        if (!ok)
            failures++;
    }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("stuck-probe-retract: MacroProcessor.ClearStuckProbe vs the real simulator\n");

        AppDialogs.CustomMessageBox = (owner, message, caption, buttons, icon, def, yes, no) =>
        {
            lock (dialogs) dialogs.Add(caption + ": " + message);
            return def == MessageBoxResult.None ? MessageBoxResult.OK : def;
        };

        string simSrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"ioSender\Simulator");
        if (!File.Exists(Path.Combine(simSrc, "grblHAL_sim.exe")))
        {
            Console.WriteLine("no simulator at " + simSrc + " - build one in Settings > Simulator first");
            return 2;
        }

        string simDir = Path.Combine(Path.GetTempPath(), "stuck-probe-retract-sim");
        Directory.CreateDirectory(simDir);
        foreach (var f in new[] { "grblHAL_sim.exe", "EEPROM.DAT", "littlefs.img" })
            if (File.Exists(Path.Combine(simSrc, f)))
                File.Copy(Path.Combine(simSrc, f), Path.Combine(simDir, f), true);

        int port;
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

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
        var stream = new TelnetStream("127.0.0.1:" + port, null);
        for (int i = 0; i < 50 && !stream.IsOpen; i++)
            Thread.Sleep(100);

        Check(stream.IsOpen, "telnet connected to the simulator", "port " + port);
        if (!stream.IsOpen)
            return 1;

        var model = new GrblViewModel();
        stream.DataReceived += model.DataReceived;
        Grbl.GrblViewModel = model;      // GrblSettings.Process and friends write through this

        Thread.Sleep(600);
        Comms.com.WriteCommand("$X");
        Thread.Sleep(300);

        // Deliberately NOT homed: the retract's own (PREREQ, connected, homed, noalarm) must then
        // refuse, which is the fail-closed path this harness can take all the way to the operator
        // message. Poll briefly so the model has real state before anything is injected.
        bool polling = true;
        var poller = new Thread(() => { while (polling) { try { Comms.com.WriteByte((byte)'?'); } catch { } Thread.Sleep(200); } }) { IsBackground = true };
        poller.Start();
        Thread.Sleep(600);

        Check(model.GrblState.State != GrblStates.Unknown, "the model has live controller state",
              "state=" + model.GrblState.State + " homed=" + model.HomedState);

        polling = false;                 // from here the probe state is ours to set
        Thread.Sleep(400);

        try
        {
            return Checks(model);
        }
        finally
        {
            try { stream.Close(); } catch { }
        }
    }

    /// <summary>
    /// Push a status report through the model's own parser and report whether Probe actually took.
    /// </summary>
    static bool SetProbe(GrblViewModel model, bool asserted)
    {
        model.DataReceived(asserted
            ? "<Idle|MPos:119.338,-626.579,-105.603|FS:0,0|Pn:P>"
            : "<Idle|MPos:119.338,-626.579,-105.603|FS:0,0>");
        Thread.Sleep(150);
        return model.Signals.Value.HasFlag(Signals.Probe) == asserted;
    }

    /// <summary>Pump this thread's dispatcher for a while, so DispatcherTimer callbacks can run.</summary>
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        stop.Tick += (s, e) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    const string Sentinel = "(nothing happened)";

    static int Checks(GrblViewModel model)
    {
        // ---- it must not fire when it should not ------------------------------------------------
        // These run FIRST and in this order on purpose: once it does arm, an internal latch keeps it
        // armed until the retract completes or is refused, so a non-firing case asserted afterwards
        // would pass for the wrong reason.
        Console.WriteLine("\nit must NOT fire:");

        Check(SetProbe(model, true), "probe injected as asserted and read back from the model",
              "Signals=" + model.Signals.Value);

        model.Message = Sentinel;
        MacroProcessor.ClearStuckProbe(model, "a run that finished cleanly", false);
        Pump(300);
        Check(model.Message == Sentinel,
              "probe asserted but the run did NOT fail -> no retract (a clean run must never move Z)",
              model.Message);

        Check(SetProbe(model, false), "probe injected as released and read back from the model",
              "Signals=" + model.Signals.Value);

        model.Message = Sentinel;
        MacroProcessor.ClearStuckProbe(model, "a failed run, probe clear", true);
        Pump(300);
        Check(model.Message == Sentinel,
              "run failed but the probe reads RELEASED -> no retract (it is a live reading, not a latch)",
              model.Message);

        model.Message = Sentinel;
        MacroProcessor.ClearStuckProbe(null, "a failed run, no model", true);
        Pump(100);
        Check(model.Message == Sentinel, "no model -> no retract, no throw", model.Message);

        // ---- it must fire when it should, and say what it is doing ------------------------------
        Console.WriteLine("\nit must fire, once:");

        Check(SetProbe(model, true), "probe injected as asserted again", "Signals=" + model.Signals.Value);

        model.Message = Sentinel;
        MacroProcessor.ClearStuckProbe(model, "the X face seek", true);
        bool armed = model.Message != Sentinel && model.Message.IndexOf("retracting", StringComparison.OrdinalIgnoreCase) >= 0;
        Check(armed, "failed run + probe asserted -> arms and tells the operator a retract is coming",
              model.Message);

        // The storm this fixes: the guard tests the probe's INITIAL state, so every retry re-tripped
        // instantly. A second call while one is already in flight must do nothing at all.
        string armedMessage = model.Message;
        MacroProcessor.ClearStuckProbe(model, "the X face seek, retried", true);
        Check(model.Message == armedMessage,
              "a second call while the first is in flight does NOT arm a second retract",
              model.Message);

        // ---- and when it cannot confirm the state, it must refuse OUT LOUD ----------------------
        // The retract is (PREREQ, connected, homed, noalarm) and this simulator was never homed, so
        // the PREREQ is genuinely unmet: nothing may be sent, and the operator must be told to jog
        // clear by hand rather than left watching nothing happen.
        Console.WriteLine("\nand it must fail closed:");
        Pump(3000);                      // the 400ms arming timer, the Run attempt, and its callbacks

        bool refused = model.Message.IndexOf("jog", StringComparison.OrdinalIgnoreCase) >= 0;
        Check(refused,
              "unhomed: the retract is refused and the operator is told to jog Z clear by hand",
              model.Message);

        string shown;
        lock (dialogs) shown = dialogs.Count == 0 ? "(no dialog)" : string.Join(" / ", dialogs.ToArray());
        Check(model.GrblState.State != GrblStates.Run && model.GrblState.State != GrblStates.Jog,
              "no motion was started by the refused retract", "state=" + model.GrblState.State);
        Console.WriteLine("  note  dialogs raised: " + shown);

        // The latch must have been released, or the NEXT genuine stuck probe is silently ignored -
        // which would be the original bug back again, one run later.
        Console.WriteLine("\nthe latch must not stick:");
        Check(SetProbe(model, true), "probe asserted once more", "Signals=" + model.Signals.Value);
        model.Message = Sentinel;
        MacroProcessor.ClearStuckProbe(model, "the next failed run", true);
        Check(model.Message != Sentinel,
              "after a refusal it can arm AGAIN on the next failed run (the latch released)",
              model.Message);
        Pump(3000);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "all checks passed" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
