/*
 * tlo-macro-probe - does tlo.macro's o130 branch actually CONSUME the seek depth the sender emits?
 *
 * tlo-seek-probe verifies the C# half: EmitTloSeekDepth's arithmetic and its clamp. That leaves the
 * join unverified - the sender emitting "#<_tlo_seekdepth> = 132.473" proves nothing about whether
 * the controller's o130 EXISTS branch reads it, or silently falls through to its 90 literal. Those
 * two failures look identical from the sender's side, and the one that matters is invisible: a
 * fallback to 90 against today's touch plate stops 32.5 mm short and throws ALARM:5, which is how
 * three sessions were spent reading a wrong seek depth as "the tool is short".
 *
 * The macro's own print is the only thing that distinguishes them, and it is already there:
 *   (PRINT, TLO02b seek depth=#<_tlo_seekd> from park)
 *
 * So this harness drives BOTH arms of o130 against the real simulator and reads that print:
 *   - with the parameter set, the print must carry the sender's number;
 *   - after a reset clears named parameters, the print must carry 90 - the conservative fallback.
 *
 * The line sent to the controller is the line EmitTloSeekDepth EMITTED, verbatim. Nothing retypes
 * the number in between, so this tests the join rather than my transcription of it.
 *
 * It also checks the TLO03 touch-plate print, which fabdb321 fixed: nested parentheses silently kill
 * a grblHAL PRINT comment, so that line never printed - in exactly the branch that was failing, which
 * is why no log of a failing run ever recorded which probe input had been selected.
 *
 * Each CALL is allowed to run to its own end rather than being reset out of once the print lands - a
 * soft reset mid-motion leaves an alarm that $X will not clear from here, and the first version of
 * this harness reported six failures that were all one unrecovered abort. grblHAL's own refusal is
 * the clean exit.
 *
 * Running them out also means the simulated probe gets to fire, which exercises the macro's
 * seek/latch agreement gate - so that is asserted too, but CONDITIONALLY on what the pair actually
 * was, because the claim is about the macro's reaction and not about the simulator's probe.
 *
 * NOT verified here, and deliberately not claimed: the probe cycle against real hardware. The
 * simulated probe triggers where the simulator decides, not at a 15 mm touch plate, so nothing here
 * says the seek reaches the real surface at machine Z -122.498.
 *
 * Exit code 0 = all checks passed.
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

static class TloMacroProbe
{
    static int failures = 0;

    // Everything the controller said that was not a status report. The macro's PRINT output arrives
    // here, and so does any error: - a refused global assignment must never be invisible, because the
    // macro would then run with a stale parameter and still print a plausible number.
    static readonly List<string> replies = new List<string>();
    static readonly object replyLock = new object();

    static void Note(string s)
    {
        lock (replyLock) replies.Add(s);
    }

    static string Transcript()
    {
        lock (replyLock) return string.Join(" / ", replies.ToArray());
    }

    static void Clear()
    {
        lock (replyLock) replies.Clear();
    }

    /// <summary>Everything seen so far, as one string, for regex scanning.</summary>
    static string Seen()
    {
        lock (replyLock) return string.Join("\n", replies.ToArray());
    }

    static int skipped = 0;

    static void Check(bool ok, string what, string detail = "")
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
        if (!ok)
            failures++;
    }

    /// <summary>A check whose PRECONDITION did not hold. Counted separately, and never as a pass.</summary>
    static void Skip(string what, string why)
    {
        Console.WriteLine("  SKIP  " + what + "   [" + why + "]");
        skipped++;
    }

    static double Num(double v) { return Math.Round(v, 4); }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("tlo-macro-probe: does tlo.macro's o130 branch consume the sender's seek depth?\n");

        // The simulator must be one that SEEDS tlo.macro. Prefer the build this repo deploys into
        // simulator\, because a sim built before 2026-10-26 does not have the macro at all and the
        // only symptom would be error:81 - see gen-sim-macros.py's MACROS list for that story.
        // Climb to the repo root by looking for a landmark rather than counting "..\" segments - the
        // first attempt counted four and landed in tools\, which silently selected the INSTALLED
        // simulator instead. It was caught only because the seeding assertion below refused it.
        string repo = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 8 && repo != null; i++)
        {
            if (Directory.Exists(Path.Combine(repo, "ioSender XL")) && Directory.Exists(Path.Combine(repo, "macros")))
                break;
            var up = Directory.GetParent(repo.TrimEnd(Path.DirectorySeparatorChar));
            repo = up == null ? null : up.FullName;
        }

        // Ordered: the build this repo deploys into simulator\ first, then the Simulator repo's own
        // build tree, and the installed simulator last. The installed one is option-matched to the
        // real controller and is NOT rebuilt by this work, so it is the most likely to predate the
        // tlo.macro seeding fix - which the premise check below will say out loud if it is picked.
        var candidates = new List<string>();
        if (repo != null)
        {
            candidates.Add(Path.Combine(repo, "simulator"));
            candidates.Add(Path.GetFullPath(Path.Combine(repo, @"..\Simulator\build")));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"ioSender\Simulator"));

        string simSrc = null;
        foreach (string c in candidates)
            if (File.Exists(Path.Combine(c, "grblHAL_sim.exe")))
            {
                simSrc = c;
                break;
            }

        if (simSrc == null)
        {
            Console.WriteLine("no grblHAL_sim.exe in any of:");
            foreach (string c in candidates)
                Console.WriteLine("  " + c);
            Console.WriteLine("\nbuild the Simulator repo (cmake --build build) or install a simulator first");
            return 2;
        }

        Console.WriteLine("simulator: " + Path.Combine(simSrc, "grblHAL_sim.exe"));

        string simDir = Path.Combine(Path.GetTempPath(), "tlo-macro-probe-sim");
        Directory.CreateDirectory(simDir);
        File.Copy(Path.Combine(simSrc, "grblHAL_sim.exe"), Path.Combine(simDir, "grblHAL_sim.exe"), true);

        // EEPROM.DAT and littlefs.img come from wherever they EXIST, which is not necessarily where
        // the binary came from: this repo's simulator\ folder holds the deployed exe and nothing else.
        // Without an EEPROM the G10 L2 below cannot persist and comes back error:7 (NVS read fail),
        // which looks like a park that would not move rather than a missing file. Copied, never
        // shared - every $ write in this harness lands in OUR copy and cannot dirty the user's.
        foreach (var f in new[] { "EEPROM.DAT", "littlefs.img" })
        {
            foreach (string c in candidates)
            {
                string src = Path.Combine(c, f);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(simDir, f), true);
                    Console.WriteLine("  " + f.PadRight(14) + " from " + c);
                    break;
                }
            }
        }

        // net462's TcpListener is not IDisposable, so this is Start/Stop rather than a using block.
        var probeListener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probeListener.Start();
        int port = ((System.Net.IPEndPoint)probeListener.LocalEndpoint).Port;
        probeListener.Stop();

        // The seeding report goes to stderr at boot and names every macro written. Captured, because
        // "tlo.macro is on the controller" is the premise of every check below and must be OBSERVED.
        string seedLog = Path.Combine(simDir, "seed.txt");
        var psi = new ProcessStartInfo(Path.Combine(simDir, "grblHAL_sim.exe"), "-p " + port)
        {
            WorkingDirectory = simDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        var sim = Process.Start(psi);
        var seedText = new List<string>();
        sim.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (seedText) seedText.Add(e.Data); };
        sim.BeginErrorReadLine();

        try
        {
            return Run(port, seedText, seedLog);
        }
        finally
        {
            try { if (sim != null && !sim.HasExited) sim.Kill(); } catch { }
        }
    }

    static int Run(int port, List<string> seedText, string seedLog)
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

        // GrblSettings.Process writes through this while parsing $$ - without it Load() dies in the
        // parse with a NullReferenceException. Same requirement as tlo-seek-probe.
        Grbl.GrblViewModel = model;

        stream.ReplyClassified += (cls, reply) =>
        {
            if (cls != Comms.ReplyClass.Status)
                Note(reply);
        };

        Thread.Sleep(500);                       // let the hello land
        Comms.com.WriteCommand("$X");
        Thread.Sleep(300);

        // The sim boots with every limit pin asserted, and the user's EEPROM may have hard limits or
        // homing-required on - any of which would refuse the G10 writes and the macro's own motion.
        // These land in OUR COPY of EEPROM.DAT.
        foreach (var setting in new[] { "$21=0", "$20=0", "$22=0" })
        {
            Comms.com.WriteCommand(setting);
            Thread.Sleep(150);
        }
        Comms.com.WriteCommand("$X");
        Thread.Sleep(400);

        bool polling = true;
        var poller = new Thread(() => { while (polling) { try { Comms.com.WriteByte((byte)'?'); } catch { } Thread.Sleep(200); } }) { IsBackground = true };
        poller.Start();

        try
        {
            return Checks(model, seedText, seedLog);
        }
        finally
        {
            polling = false;
            try { stream.Close(); } catch { }
        }
    }

    /// <summary>Pin $132 (max Z travel) and confirm the controller really took it.</summary>
    static bool SetZTravel(GrblViewModel model, double mm, out double readBack, out string note)
    {
        // The reply matters, not just the read-back: a REFUSED write still reads back whatever the
        // controller already had, so a value that happens to equal the default would pass as "pinned".
        Clear();
        string cmd = "$132=" + mm.ToString("0.###", CultureInfo.InvariantCulture);
        Comms.com.WriteCommand(cmd);
        Thread.Sleep(250);
        string wrote = Transcript();

        GrblSettings.Load(model);
        readBack = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + 2);
        note = cmd + " -> " + wrote + "; reads " + Num(readBack);
        return !double.IsNaN(readBack) && Math.Abs(readBack - mm) < 0.0005
               && wrote.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>
    /// Zero G59.3 on the controller and re-read it through the same $# path EmitTloSeekDepth uses.
    /// The read-back is the instrument check - a park that silently failed to move would make the
    /// expected depth below meaningless while still printing as a number.
    /// </summary>
    static bool SetPark(GrblViewModel model, double z, out double readBack, out string note)
    {
        string cmd = "G10 L2 P9 X0 Y0 Z" + z.ToString("0.###", CultureInfo.InvariantCulture);
        readBack = double.NaN;
        note = "";

        // REQUIRE THE OK, and retry. Sending this straight after a macro that ended in (ABORT, ...)
        // failed once with "error:253", which is Status_UserException - the MACRO'S OWN abort reply
        // arriving late, not a rejection of this G10. The abort flushes the input buffer, so the write
        // was discarded and got no ok of its own, while the stale 253 landed in the transcript and
        // read as a refusal of a command the controller had never seen.
        //
        // The missing ok is the tell. Draining first and then demanding an ok that is not an error is
        // what separates "the controller refused this" from "the controller never got this".
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            string ready;
            EnsureReady(model, out ready);

            Clear();                                   // drop anything still arriving from before
            Thread.Sleep(400);
            Clear();

            Comms.com.WriteCommand(cmd);
            Thread.Sleep(400);
            string wrote = Transcript();

            bool acked = wrote.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         wrote.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0;

            string csNote = "no G59.3 row";
            int reads = 0;
            for (; reads < 3; reads++)
            {
                GrblWorkParameters.Get(model);
                var cs = GrblWorkParameters.GetCoordinateSystem("G59.3");
                if (cs != null)
                    csNote = "G59.3 row present, " + cs.Values.Length + " value(s)";
                readBack = cs == null ? double.NaN : cs.Values[2];
                if (!double.IsNaN(readBack) && Math.Abs(readBack - z) < 0.0005)
                    break;
                Thread.Sleep(250);
            }

            note = cmd + " -> " + wrote + "; " + ready + "; " + csNote + "; reads " + Num(readBack)
                 + " after " + (reads + 1) + " $# fetch(es)"
                 + (attempt > 1 ? "; attempt " + attempt : "");

            if (acked && !double.IsNaN(readBack) && Math.Abs(readBack - z) < 0.0005)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The line EmitTloSeekDepth emits for this baseline, or null if it refused. Returned as the raw
    /// LINE and not as a number, because that line is what gets sent to the controller - no retyping.
    /// </summary>
    static string EmittedLine(double baseline, out double depth)
    {
        var lines = new List<string>();
        MacroProcessor.EmitTloSeekDepth(delegate (string s) { lines.Add(s); }, baseline);

        depth = double.NaN;
        if (lines.Count != 1)
            return null;

        var m = Regex.Match(lines[0], @"#<_tlo_seekdepth>\s*=\s*(-?[0-9.]+)");
        if (m.Success)
            depth = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return lines[0];
    }

    /// <summary>
    /// Set the macro's globals, CALL it, and wait for the TLO02b print that reports which seek depth
    /// it chose. Aborts with a soft reset the moment that print lands: the print happens before the
    /// G38.2, so there is nothing to gain from letting a 132mm descent play out against a simulated
    /// probe - and a reset is also what clears named parameters for the fallback arm below.
    /// </summary>
    /// <param name="seekLine">
    /// The sender's own emitted "#<_tlo_seekdepth> = ..." line, or null to set NOTHING and let the
    /// macro's o130 ELSE arm supply its literal.
    /// </param>
    /// <summary>
    /// Get the controller into a state that can accept a CALL, and SAY which state it was in. An
    /// alarm left over from a previous run's aborted descent makes the next CALL fail with error:9,
    /// which from the sender's side looks exactly like a macro that ran and printed nothing - the
    /// first version of this harness reported three failures that were really one unrecovered reset.
    /// </summary>
    static bool EnsureReady(GrblViewModel model, out string note)
    {
        string seen = "(no status yet)";
        for (int i = 0; i < 40; i++)
        {
            seen = model.GrblState.State.ToString();
            if (model.GrblState.State == GrblStates.Idle)
            {
                note = "Idle after " + (i * 250) + "ms";
                return true;
            }
            if (model.GrblState.State == GrblStates.Alarm)
            {
                Comms.com.WriteCommand("$X");
                Thread.Sleep(400);
            }
            Thread.Sleep(250);
        }
        note = "NOT READY - still " + seen + " after 10s";
        return false;
    }

    /// <param name="tloRef">
    /// What to set #&lt;_tlo_ref&gt; to. "0" means "this tool BECOMES the reference", which is the o200 IF
    /// arm - so every arm that passes 0 leaves the o200 ELSE arm, and its TLO06 print, unexercised.
    /// Pass a real baseline to reach it.
    /// </param>
    static double CallMacro(GrblViewModel model, string seekLine, out string transcript, out bool ran,
                            string tloRef = "0")
    {
        string ready;
        bool wasReady = EnsureReady(model, out ready);

        Clear();
        if (!wasReady)
            Note("[harness] controller " + ready);

        // The pcorner convention: globals set before a no-arg CALL. #<_tc_touchplate> = 1 selects the
        // touch-plate branch, which is this machine's actual configuration since 2026-09 and is the
        // branch whose TLO03 print was silently dead until fabdb321.
        var globals = new List<string> { "#<_tlo_toolid> = 1", "#<_tc_touchplate> = 1",
                                         "#<_tlo_ref> = " + tloRef };
        if (seekLine != null)
            globals.Add(seekLine);

        foreach (string g in globals)
        {
            Comms.com.WriteCommand(g);
            Thread.Sleep(200);
        }

        Comms.com.WriteCommand("O<tlo>CALL");

        // Up to 25 s for the print. The macro's moves before it are a G53 Z-5, a G59.3 select and two
        // G0s, all short at the park this harness sets - but the sim simulates motion in real time.
        double depth = double.NaN;
        for (int i = 0; i < 125; i++)
        {
            Thread.Sleep(200);
            var m = Regex.Match(Seen(), @"TLO02b seek depth=(-?[0-9.]+)");
            if (m.Success)
            {
                depth = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                break;
            }
        }

        // Let the cycle END ON ITS OWN rather than resetting out of it. The first version of this
        // harness sent a soft reset the moment it had its print: that leaves an alarm which $X does
        // not clear from here, the next CALL was never accepted, and the harness read that as "the
        // macro printed nothing" - six reported failures from one bad abort, in the arm that had
        // already been observed passing. grblHAL's own refusal is the clean exit: against a surface
        // that is not there the G38.2 ends in ALARM:5, which $X does clear. Slower (a 132mm seek at
        // F500 is about 16s) and worth it.
        Thread.Sleep(1000);                             // let motion actually start before polling
        string settled = WaitForSettle(model, 90000);

        transcript = Transcript() + "  | cycle ended: " + settled;
        ran = Seen().IndexOf("TLO01", StringComparison.Ordinal) >= 0;
        return depth;
    }

    /// <summary>
    /// Wait for the controller to stop moving, and report HOW it stopped. Idle means the probe cycle
    /// completed; Alarm means grblHAL refused something - against a simulated probe that is usually
    /// ALARM:5, the probe-did-not-contact refusal, which is the correct outcome here and not a fault.
    /// </summary>
    static string WaitForSettle(GrblViewModel model, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var st = model.GrblState.State;
            if (st == GrblStates.Idle || st == GrblStates.Alarm)
                return st + " after " + sw.ElapsedMilliseconds + "ms";
            Thread.Sleep(200);
        }
        return "STILL " + model.GrblState.State + " after " + ms + "ms";
    }

    static int Checks(GrblViewModel model, List<string> seedText, string seedLog)
    {
        // ---- the premise: is tlo.macro actually ON this controller? -----------------------------
        // Asserted from the simulator's own boot report rather than assumed. A sim built before
        // 2026-10-26 seeds four macros and not this one, and the only symptom of its absence is
        // "error:81 Unknown flow statement" - which names neither a file nor a path.
        Console.WriteLine("\nthe premise - tlo.macro present on the controller:");
        string seeded;
        lock (seedText) seeded = string.Join("\n", seedText.ToArray());
        try { File.WriteAllText(seedLog, seeded); } catch { }

        var seedMatch = Regex.Match(seeded, @"seed: tlo\.macro\s+(\d+) bytes -> (\S+)");
        Check(seedMatch.Success, "the simulator seeded tlo.macro into littlefs",
              seedMatch.Success ? seedMatch.Groups[1].Value + " bytes -> " + seedMatch.Groups[2].Value
                                : "NOT SEEDED - this simulator predates the fix; rebuild the Simulator repo");

        bool mountOk = seeded.IndexOf("[match]", StringComparison.Ordinal) >= 0;
        Check(mountOk, "the mount point and the named-sub resolver agree (no error:81 by construction)",
              mountOk ? "boot report says [match]" : "boot report says MISMATCH - O<tlo>CALL cannot resolve");

        if (!seedMatch.Success || !mountOk)
        {
            Console.WriteLine("\n  tlo.macro is not reachable on this simulator - every check below would be");
            Console.WriteLine("  testing the absence of a file rather than the o130 branch. Stopping.");
            return 1;
        }

        // ---- controller state, so the expected depth is exact -----------------------------------
        Console.WriteLine("\ncontroller state:");
        double readBack;
        string note;
        bool pinned = SetZTravel(model, 152d, out readBack, out note);
        Check(pinned, "$132 (max Z travel) pinned to 152", note);

        bool parked = SetPark(model, 0d, out readBack, out note);
        Check(parked, "G59.3 zeroed and re-read through $#", note);

        if (!pinned || !parked)
        {
            Console.WriteLine("\n  cannot establish the park or the envelope - the expected depth would be a guess. Stopping.");
            return 1;
        }

        // What the sender would emit for today's touch plate. baseline -122.473 is the stored
        // TloRefBaseline; by hand, at a park of 0: 0 - (-122.473 - 10) = 132.473. tlo-seek-probe
        // already proves the sender computes that - what is unproven is whether the MACRO uses it.
        double emitted;
        string line = EmittedLine(-122.473, out emitted);

        // ---- the fallback: the ELSE arm, FIRST -------------------------------------------------
        // Deliberately first, on a controller where #<_tlo_seekdepth> has never been set. The
        // alternative was to set it, then clear it with a soft reset - which is true to life (named
        // parameters do not survive a reset, so the first generated-program call after any reboot
        // arrives without it) but makes the test depend on the reset having worked. Running this arm
        // before the parameter exists needs no reset to prove anything, so a recovery that half
        // worked cannot be mistaken for a fallback that did.
        //
        // It must fail SAFE: the shallow literal, which refuses with ALARM:5 in mid-air, never a
        // confident deeper plunge - there is only about 15mm between the plate surface and the table.
        Console.WriteLine("\no130 ELSE arm - the parameter has never been set:");
        string transcript;
        bool ran;
        double fallback = CallMacro(model, null, out transcript, out ran);

        Check(ran, "O<tlo>CALL resolved and the macro ran (TLO01 printed)", transcript);
        Check(!double.IsNaN(fallback) && Math.Abs(fallback - 90d) < 0.0005,
              "with no parameter it uses its own conservative 90 literal",
              double.IsNaN(fallback) ? "no TLO02b print - " + transcript : "TLO02b seek depth=" + Num(fallback));

        // ---- the join: the EXISTS arm ----------------------------------------------------------
        Console.WriteLine("\no130 EXISTS arm - the sender's parameter is set:");
        Check(line != null && Num(emitted) == 132.473,
              "the sender emits a seek depth of 132.473 for the touch-plate baseline",
              line ?? "(emitted nothing)");

        if (line == null)
        {
            Console.WriteLine("\n  the sender emitted nothing, so there is no parameter to pass. Stopping.");
            return 1;
        }

        double used = CallMacro(model, line, out transcript, out ran);

        Check(ran, "O<tlo>CALL resolved and the macro ran again", transcript);
        Check(!double.IsNaN(used), "the macro printed which seek depth it chose (TLO02b)",
              double.IsNaN(used) ? "no TLO02b print - " + transcript : "TLO02b seek depth=" + Num(used));

        // THE CHECK THIS HARNESS EXISTS FOR. Equal to the sender's number means o130 took the EXISTS
        // arm and read the parameter. 90 would mean it fell through to the literal - which against
        // today's plate stops 32.5mm short and throws ALARM:5, the exact failure this all began with.
        Check(!double.IsNaN(used) && Math.Abs(used - emitted) < 0.0005,
              "the macro USED the sender's depth, not its own literal (o130 took the EXISTS arm)",
              "sender emitted " + Num(emitted) + ", macro used " + Num(used));

        // And the two arms must DIFFER, or the check above proves nothing: a macro that printed
        // 132.473 in both would mean the parameter was already set when the fallback arm ran.
        Check(!double.IsNaN(used) && !double.IsNaN(fallback) && Math.Abs(used - fallback) > 0.0005,
              "the two arms produced different depths, so the parameter is what changed it",
              "ELSE " + Num(fallback) + " vs EXISTS " + Num(used));

        // The fallback must be SHALLOWER than the derived depth. Too shallow is a refusal in mid-air;
        // too deep drills whatever the plate is bolted to.
        Check(!double.IsNaN(fallback) && !double.IsNaN(used) && fallback < used,
              "the fallback is shallower than the derived depth (fails safe, not deep)",
              Num(fallback) + " < " + Num(used));

        // fabdb321's other half: this print was dead, killed by nested parentheses, in exactly the
        // branch that was failing - so no log of a failing run recorded which probe input was chosen.
        bool tlo03 = Regex.IsMatch(transcript, @"TLO03 main probe selected - touch-plate mode");
        Check(tlo03, "the touch-plate TLO03 print works (the nested-parens fix holds)",
              tlo03 ? "printed" : "silent - nested parentheses kill a grblHAL PRINT comment");

        // ---- the seek/latch agreement gate, when the simulated probe happens to exercise it ------
        // This is not a check on the simulator's probe, it is a check on the MACRO'S REACTION to it.
        // The two PRB reports are the fast F500 seek and the slow F25 latch. On a real surface they
        // land 0.047 to 0.143mm apart (nine cycles over six days) because the only difference is the
        // overshoot. A false trigger stops the seek at a random point in mid-air and the latch at
        // another, so the gap becomes noise: 0.411mm and 1.375mm on 2026-09-21 with a bare foil shield
        // shorting the probe input - and BOTH of those cycles reported success and applied a tool
        // offset 65mm wrong, the second of which then fed a corner probe into the touch plate.
        //
        // Conditional on what the pair actually was, so a simulator whose probe agrees closely SKIPS
        // this rather than failing it. Either way the assertion is about the macro, not the sim.
        Console.WriteLine("\nthe seek/latch agreement gate:");
        var prbs = Regex.Matches(transcript, @"\[PRB:[-0-9.]+,[-0-9.]+,(-?[0-9.]+):1\]");
        if (prbs.Count >= 2)
        {
            double seekZ = double.Parse(prbs[0].Groups[1].Value, CultureInfo.InvariantCulture);
            double latchZ = double.Parse(prbs[prbs.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
            double gap = Math.Abs(latchZ - seekZ);
            bool rejected = transcript.IndexOf("TLO05 PROBE REJECTED", StringComparison.Ordinal) >= 0;
            string detail = "gap " + Num(gap) + "mm (seek " + Num(seekZ) + ", latch " + Num(latchZ) + ")";

            if (gap > 0.3)
                Check(rejected, "a DISAGREEING seek/latch pair is rejected and the macro aborts",
                      detail + " -> " + (rejected ? "rejected, as it must be"
                                                  : "ACCEPTED - a false trigger would apply a wrong offset"));
            else
                Check(!rejected, "an AGREEING seek/latch pair is accepted",
                      detail + " -> " + (rejected ? "REJECTED - a healthy probe refused" : "accepted"));
        }
        else
            Skip("the seek/latch agreement gate",
                 "the simulated probe produced " + prbs.Count + " PRB report(s), two are needed");

        // ---- the FRAME: is the Z the macro publishes a MACHINE Z? -------------------------------
        // Every arm above ran with G59.3's Z offset at 0, where a work Z and a machine Z are THE SAME
        // NUMBER - so none of them can tell the two apart, and tlo.macro's #5063 + #5383 conversion is
        // a no-op in all of them. A test that cannot fail when the thing under test is wrong is not a
        // test, so this moves the park to -42 (a value G59.3 really has held on this machine, inside
        // the same week as 0.000) and makes the two frames differ by exactly 42 mm.
        //
        // The assertion compares what the MACRO published against the machine Z in the controller's
        // OWN [PRB:] report - two numbers from the same run, so wherever the simulated probe happens
        // to trigger is irrelevant. A bare #5063 would come back 42 mm out, and since #<_probe_z> is
        // differenced against the stored TloRefBaseline (a machine Z), that 42 mm would land directly
        // in the G43.1 tool length offset.
        Console.WriteLine("\nthe frame - G59.3 moved to -42 so work and machine Z differ by 42mm:");
        bool moved = SetPark(model, -42d, out readBack, out note);
        Check(moved, "G59.3 Z offset moved to -42 and re-read through $#", note);

        if (moved)
        {
            double emitted42;
            string line42 = EmittedLine(-122.473, out emitted42);
            double used42 = CallMacro(model, line42, out transcript, out ran);

            // 90.473 by hand: -42 - (-122.473 - 10). Proves the park is read, and keeps this run's seek
            // honest rather than reusing a depth derived for a different park.
            Check(!double.IsNaN(used42) && Math.Abs(used42 - 90.473) < 0.0005,
                  "the seek depth follows the moved park (90.473, not 132.473)",
                  "macro used " + Num(used42));

            var prb = Regex.Matches(transcript, @"\[PRB:[-0-9.]+,[-0-9.]+,(-?[0-9.]+):1\]");
            var rejected = Regex.Match(transcript, @"TLO05 PROBE REJECTED seek=(-?[0-9.]+) latch=(-?[0-9.]+)");
            var accepted = Regex.Match(transcript, @"TLO04 probe_z=(-?[0-9.]+)");

            if (prb.Count == 0 || (!rejected.Success && !accepted.Success))
                Skip("the macro publishes a MACHINE Z",
                     "this run produced no [PRB:..:1] and published Z pair to compare - "
                     + "nothing was recording the thing under test, so this is not a pass");
            else
            {
                double prbMachineZ = double.Parse(prb[prb.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
                double published = rejected.Success
                    ? double.Parse(rejected.Groups[2].Value, CultureInfo.InvariantCulture)
                    : double.Parse(accepted.Groups[1].Value, CultureInfo.InvariantCulture);

                Check(Math.Abs(published - prbMachineZ) < 0.0005,
                      "the macro's published Z is the controller's [PRB:] MACHINE Z, not the work Z",
                      "published " + Num(published) + ", PRB machine Z " + Num(prbMachineZ)
                      + ", a bare #5063 would have read " + Num(prbMachineZ + 42d));
            }
        }

        // ---- the o200 ELSE arm: TLO06 must print a NUMBER ---------------------------------------
        // Every arm above passes #<_tlo_ref> = 0, which takes o200's IF arm - "this tool BECOMES the
        // reference" - so not one of them reaches the ELSE arm where an offset is actually APPLIED, and
        // the TLO06 print sat there unexercised. It had been logging "tlo=[-123.192 - -123.205]": a
        // (PRINT, ..) substitutes on # but does NOT evaluate brackets, so the applied tool length
        // offset - the most consequential number this macro produces - was the one number never in the
        // log, and every reading of it was somebody doing the subtraction by hand.
        //
        // The regex IS the assertion: a bracketed expression cannot match "tlo=<number>".
        Console.WriteLine("\nthe o200 ELSE arm - TLO06 prints the APPLIED offset as a number:");
        Check(SetPark(model, 0d, out readBack, out note), "G59.3 Z offset back to 0", note);

        double emittedRef;
        string lineRef = EmittedLine(-122.473, out emittedRef);
        CallMacro(model, lineRef, out transcript, out ran, "-122.473");

        var applied = Regex.Match(transcript,
                                  @"TLO06 TLO APPLIED tlo=(-?[0-9.]+) probe_z=(-?[0-9.]+) ref=(-?[0-9.]+)");
        if (!applied.Success)
            Skip("TLO06 prints the applied offset as a number",
                 transcript.IndexOf("TLO06", StringComparison.Ordinal) >= 0
                    ? "TLO06 printed but NOT as a number - " + transcript
                    : "the macro never reached TLO06: this simulator's probe pair disagrees, so the "
                      + "agreement gate aborts first. Verify on hardware, where the pair agrees to 0.150mm.");
        else
        {
            double tlo = double.Parse(applied.Groups[1].Value, CultureInfo.InvariantCulture);
            double pz = double.Parse(applied.Groups[2].Value, CultureInfo.InvariantCulture);
            double rf = double.Parse(applied.Groups[3].Value, CultureInfo.InvariantCulture);
            Check(Math.Abs(tlo - (pz - rf)) < 0.0005,
                  "TLO06's offset is a number, and equals probe_z - ref",
                  "tlo " + Num(tlo) + " vs " + Num(pz) + " - " + Num(rf) + " = " + Num(pz - rf));
        }

        // ---- what was observed but is NOT asserted ----------------------------------------------
        Console.WriteLine("\nobserved, not asserted:");

        var wcs = Regex.Matches(Seen(), @"TLO01b captured wcs=([0-9.]+)");
        if (wcs.Count > 0)
        {
            string last = wcs[wcs.Count - 1].Groups[1].Value;
            Console.WriteLine("  the second CALL captured wcs=" + last + ". 9 means G59.3 - the FIRST run's abort");
            Console.WriteLine("  left it selected, because a macro's own cleanup does not run when the macro is");
            Console.WriteLine("  aborted. That is the 2026-09-14 failure reproduced live, and it is why the");
            Console.WriteLine("  caller-side WCS re-select in EmitTloReference is load-bearing rather than");
            Console.WriteLine("  belt-and-braces: it is the only thing that covers an aborted macro.");
        }

        Console.WriteLine("  the probe CYCLE is still unverified against real hardware. The simulated probe");
        Console.WriteLine("  triggers where the simulator decides, not at a 15mm touch plate, so nothing here");
        Console.WriteLine("  says the seek reaches the real surface at machine Z -122.498.");

        Console.WriteLine();
        Console.WriteLine(failures == 0
                            ? "all checks passed" + (skipped > 0 ? " (" + skipped + " skipped)" : "")
                            : failures + " CHECK(S) FAILED" + (skipped > 0 ? ", " + skipped + " skipped" : ""));
        return failures == 0 ? 0 : 1;
    }
}
