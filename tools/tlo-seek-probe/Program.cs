/*
 * tlo-seek-probe - exercises MacroProcessor.EmitTloSeekDepth against the real grblHAL simulator.
 * See the csproj header for why this is a separate net462 harness. Exit code 0 = all checks passed.
 *
 * The expected depths below are HAND-COMPUTED from the controller's own numbers, deliberately not
 * re-derived with the formula under test - a test that recomputes the implementation proves only
 * that it equals itself. $132 is pinned to a known value first so those constants are exact, and
 * then changed, because "the clamp tracks the controller's real envelope" is a separate claim from
 * "the clamp caps the depth".
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using CNC.Core;
using CNC.Controls;

static class TloSeekProbe
{
    static int failures = 0;
    static int skipped = 0;

    // Everything the controller said that was not a status report. An error: reply to a G10 write
    // must never be invisible here - a park that silently did not move would make every depth
    // below meaningless while still looking like a pass.
    static readonly List<string> otherReplies = new List<string>();
    static readonly object replyLock = new object();

    static string DrainReplies()
    {
        lock (replyLock)
        {
            string s = otherReplies.Count == 0 ? "(no replies)" : string.Join(" / ", otherReplies.ToArray());
            otherReplies.Clear();
            return s;
        }
    }

    static void Check(bool ok, string what, string detail = "")
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
        if (!ok)
            failures++;
    }

    static void Skip(string what, string why)
    {
        Console.WriteLine("  SKIP  " + what + "   [" + why + "]");
        skipped++;
    }

    /// <summary>
    /// Call the real EmitTloSeekDepth and report how many lines it emitted, plus the depth it
    /// carried. Zero lines is the refusal the macro's own conservative literal backs up.
    /// </summary>
    static int EmitCount(double baseline, out double depth, out string raw)
    {
        var lines = new List<string>();
        MacroProcessor.EmitTloSeekDepth(delegate (string s) { lines.Add(s); }, baseline);

        depth = double.NaN;
        raw = lines.Count == 0 ? "(emitted nothing)" : string.Join(" | ", lines.ToArray());

        if (lines.Count == 1)
        {
            var m = Regex.Match(lines[0], @"#<_tlo_seekdepth>\s*=\s*(-?[0-9.]+)");
            if (m.Success)
                depth = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return lines.Count;
    }

    static double Num(double v) { return Math.Round(v, 4); }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("tlo-seek-probe: MacroProcessor.EmitTloSeekDepth vs the real simulator\n");

        // ---- the "no fresh $#" refusal, while that state is still GENUINE -----------------------
        // GrblWorkParameters holds no coordinate systems until something reads $#, so
        // GetCoordinateSystem("G59.3") really is null here. After the connect below it never can be
        // again, which is why this one is asserted before the simulator is even started.
        Console.WriteLine("refusal paths, before any controller state exists:");
        double d; string raw;
        Check(EmitCount(-122.473, out d, out raw) == 0,
              "emits NOTHING when no $# has been read (the park's machine Z is unknown)", raw);

        string simSrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"ioSender\Simulator");
        if (!File.Exists(Path.Combine(simSrc, "grblHAL_sim.exe")))
        {
            Console.WriteLine("\nno simulator at " + simSrc + " - build one in Settings > Simulator first");
            return 2;
        }

        string simDir = Path.Combine(Path.GetTempPath(), "tlo-seek-probe-sim");
        Directory.CreateDirectory(simDir);
        foreach (var f in new[] { "grblHAL_sim.exe", "EEPROM.DAT", "littlefs.img" })
            if (File.Exists(Path.Combine(simSrc, f)))
                File.Copy(Path.Combine(simSrc, f), Path.Combine(simDir, f), true);

        // net462's TcpListener is not IDisposable, so this is Start/Stop rather than a using block.
        int port;
        var probeListener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probeListener.Start();
        port = ((System.Net.IPEndPoint)probeListener.LocalEndpoint).Port;
        probeListener.Stop();

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
        var stream = new TelnetStream("127.0.0.1:" + port, null);   // null context = headless, inline handlers
        for (int i = 0; i < 50 && !stream.IsOpen; i++)
            Thread.Sleep(100);

        Console.WriteLine();
        Check(stream.IsOpen, "telnet connected to the simulator", "port " + port);
        if (!stream.IsOpen)
            return 1;

        var model = new GrblViewModel();
        stream.DataReceived += model.DataReceived;

        // The app's MainWindow publishes its view model here, and GrblSettings.Process writes
        // through it while parsing $$ (IsParserStateLive, off the status report mask). Without it
        // Load() dies with a NullReferenceException inside the parse - found the hard way.
        Grbl.GrblViewModel = model;

        stream.ReplyClassified += (cls, reply) =>
        {
            if (cls != Comms.ReplyClass.Status)
                lock (replyLock) otherReplies.Add(reply);
        };

        Thread.Sleep(500);                       // let the hello land
        Comms.com.WriteCommand("$X");
        Thread.Sleep(300);

        // The sim boots with every limit pin asserted and the user's EEPROM may have hard limits on,
        // which would refuse the G10 writes below. These land in OUR COPY of EEPROM.DAT.
        foreach (var setting in new[] { "$21=0", "$20=0", "$22=0" })
        {
            Comms.com.WriteCommand(setting);
            Thread.Sleep(150);
        }
        Comms.com.WriteCommand("$X");
        Thread.Sleep(300);

        bool polling = true;
        var poller = new Thread(() => { while (polling) { try { Comms.com.WriteByte((byte)'?'); } catch { } Thread.Sleep(200); } }) { IsBackground = true };
        poller.Start();

        try
        {
            return Checks(model);
        }
        finally
        {
            polling = false;
            try { stream.Close(); } catch { }
        }
    }

    /// <summary>Pin $132 (max Z travel) and confirm the controller really took it.</summary>
    static bool SetZTravel(GrblViewModel model, double mm, out double readBack)
    {
        Comms.com.WriteCommand("$132=" + mm.ToString("0.###", CultureInfo.InvariantCulture));
        Thread.Sleep(250);
        GrblSettings.Load(model);
        readBack = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + 2);
        return !double.IsNaN(readBack) && Math.Abs(readBack - mm) < 0.0005;
    }

    /// <summary>
    /// Set G59.3's Z offset on the controller and re-read it through the same $# path the method
    /// uses. The read-back is the instrument check: if this is stale, every depth below is noise.
    /// </summary>
    static bool SetParkZ(GrblViewModel model, double z, out double readBack)
    {
        string note;
        return SetParkZ(model, z, out readBack, out note);
    }

    /// <summary>
    /// As above, but also reports what the controller SAID and how many $# reads it took. The first
    /// read after connect came back with no G59.3 at all, and a park that silently failed to move
    /// would make every depth derived from it meaningless while still printing as a number.
    /// </summary>
    static bool SetParkZ(GrblViewModel model, double z, out double readBack, out string note)
    {
        DrainReplies();
        string cmd = "G10 L2 P9 Z" + z.ToString("0.###", CultureInfo.InvariantCulture);
        Comms.com.WriteCommand(cmd);
        Thread.Sleep(300);
        string wrote = DrainReplies();

        readBack = double.NaN;
        int reads = 0;
        for (; reads < 3; reads++)
        {
            GrblWorkParameters.Get(model);
            var cs = GrblWorkParameters.GetCoordinateSystem("G59.3");
            readBack = cs == null ? double.NaN : cs.Values[2];
            if (!double.IsNaN(readBack) && Math.Abs(readBack - z) < 0.0005)
                break;
            Thread.Sleep(250);
        }

        note = cmd + " -> " + wrote + "; reads " + Num(readBack) + " after " + (reads + 1) + " $# fetch(es)";
        return !double.IsNaN(readBack) && Math.Abs(readBack - z) < 0.0005;
    }

    static int Checks(GrblViewModel model)
    {
        double d, raw2, readBack;
        string raw;
        int n;

        // ---- pin the envelope so the expected depths below are exact ----------------------------
        Console.WriteLine("\ncontroller state:");
        bool pinned = SetZTravel(model, 152d, out raw2);
        Check(pinned, "$132 (max Z travel) pinned to 152 and read back from the controller",
              "reads " + Num(raw2));
        if (!pinned)
        {
            Console.WriteLine("\n  cannot pin the envelope - every expected depth below would be a guess. Stopping.");
            return 1;
        }

        string note;
        bool park0 = SetParkZ(model, 0d, out readBack, out note);
        Check(park0, "G59.3 Z offset set to 0 and re-read through $#", note);

        // ---- the nominal depth, at the park the machine actually had on 2026-10-09 --------------
        // baseline -122.473 (the stored TloRefBaseline for the 15mm touch plate), park Z 0.
        // By hand: 0 - (-122.473 - 10) = 132.473. That is the number the commit claims.
        Console.WriteLine("\nderived depth:");
        n = EmitCount(-122.473, out d, out raw);
        Check(n == 1 && Num(d) == 132.473,
              "park 0, baseline -122.473 -> seek depth 132.473 (reaches the plate, 10mm to spare)", raw);

        // ---- the same baseline at the OTHER park this machine has had inside one week ----------
        // By hand: -42 - (-122.473 - 10) = 90.473. If the method assumed a park of machine zero it
        // would emit 132.473 here and drive 42mm past the plate.
        bool park42 = SetParkZ(model, -42d, out readBack, out note);
        Check(park42, "G59.3 Z offset moved to -42 and re-read through $#", note);
        n = EmitCount(-122.473, out d, out raw);
        Check(n == 1 && Num(d) == 90.473,
              "park -42, same baseline -> 90.473, not 132.473 (the live park is READ, not assumed)", raw);

        // ---- the clamp, which is the safety property -------------------------------------------
        // Back to park 0 and ask for a baseline far below the table: unclamped this is
        // 0 - (-200 - 10) = 210mm of descent. The cap is the envelope less 5mm: 152 - 5 = 147.
        Console.WriteLine("\nthe clamp:");
        Check(SetParkZ(model, 0d, out readBack, out note), "G59.3 Z offset back to 0", note);
        n = EmitCount(-200d, out d, out raw);
        Check(n == 1 && Num(d) == 147d,
              "baseline -200 would ask for 210mm -> capped at 147 ($132 less the 5mm margin)", raw);

        // The cap must follow the controller's OWN envelope, not a constant that happens to be 147.
        bool shrunk = SetZTravel(model, 100d, out raw2);
        Check(shrunk, "$132 shrunk to 100 and read back", "reads " + Num(raw2));
        if (shrunk)
        {
            n = EmitCount(-200d, out d, out raw);
            Check(n == 1 && Num(d) == 95d,
                  "same baseline on a 100mm envelope -> 95, so the cap tracks $132 and is not a constant", raw);
        }

        // ---- every input unknown or nonsense must emit NOTHING ---------------------------------
        // The macro then uses its own literal. An unknown must not become a confident deep plunge.
        Console.WriteLine("\nrefusals:");
        Check(SetZTravel(model, 152d, out raw2), "$132 restored to 152", "reads " + Num(raw2));

        n = EmitCount(0d, out d, out raw);
        Check(n == 0, "baseline 0 (never referenced) emits NOTHING", raw);

        n = EmitCount(10d, out d, out raw);
        Check(n == 0, "baseline exactly one allowance above the park (depth 0) emits NOTHING", raw);

        n = EmitCount(20d, out d, out raw);
        Check(n == 0, "baseline above the park (negative depth) emits NOTHING", raw);

        // "No configured envelope" - only assertable if the controller will accept $132=0 at all.
        double zeroed;
        if (SetZTravel(model, 0d, out zeroed))
        {
            n = EmitCount(-122.473, out d, out raw);
            Check(n == 0, "no Z envelope ($132=0) emits NOTHING rather than an unclamped depth", raw);
        }
        else
            Skip("no Z envelope ($132=0) emits NOTHING",
                 "the controller refused $132=0, reads " + Num(zeroed) + " - path not exercised");

        // ---- the invariant, stated independently of the formula --------------------------------
        // Whatever the inputs, an emitted depth must stay inside the envelope and be a real descent.
        Console.WriteLine("\nthe invariant, swept:");
        Check(SetZTravel(model, 152d, out raw2), "$132 restored to 152 for the sweep", "reads " + Num(raw2));
        bool invariantHeld = true;
        string firstBreach = "";
        foreach (double parkZ in new[] { 0d, -42d, -10d })
        {
            double gotPark;
            if (!SetParkZ(model, parkZ, out gotPark))
            {
                invariantHeld = false;
                firstBreach = "could not set park " + parkZ;
                break;
            }
            double floor = gotPark - 152d;                      // machine Z at the bottom of travel
            for (double baseline = -5d; baseline >= -300d; baseline -= 7.3d)
            {
                n = EmitCount(baseline, out d, out raw);
                if (n == 0)
                    continue;
                double reaches = gotPark - d;                   // machine Z the seek would bottom at
                if (d <= 0d || reaches < floor + 4.999d)
                {
                    invariantHeld = false;
                    firstBreach = "park " + Num(gotPark) + " baseline " + Num(baseline) + " -> depth " + Num(d)
                                + " bottoms at machine Z " + Num(reaches) + ", floor " + Num(floor);
                    break;
                }
            }
            if (!invariantHeld)
                break;
        }
        Check(invariantHeld,
              "across 3 parks x 41 baselines, every emitted depth is a real descent that stops at least 5mm above the Z floor",
              firstBreach);

        Console.WriteLine();
        if (skipped > 0)
            Console.WriteLine(skipped + " check(s) SKIPPED - not exercised, do not read as passing.");
        Console.WriteLine(failures == 0 ? "all checks passed" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
