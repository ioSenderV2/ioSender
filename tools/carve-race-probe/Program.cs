using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CNC.Core;
using CNC.GCode;

namespace CarveRaceProbe
{
    /// <summary>
    /// Issue #24: ioSender died with "Collection was modified; enumeration operation may not execute"
    /// inside GCodeEmulator.run, reached from CarveView.BuildToolpath, after loading a large program
    /// and clicking the 3D view. Reported against a pre-portable-core build, so the first question was
    /// whether it still exists. It does. This reproduces it with no WPF, because the race is not in
    /// the view - every part of it is in CNC Core:
    ///
    ///   GCodeProgram.BackgroundLoad  =>  AddBlock(Action.New) on the UI thread, then ParseFileLines
    ///                                    on a WORKER thread (GCodeJob.cs, and HelperClasses.cs:57)
    ///   GCodeParser.Tokens           =>  a List the worker thread APPENDS to for the whole parse
    ///   GCodeJob.Tokens              =>  { get { return Parser.Tokens; } } - that live list, by reference
    ///   GCodeEmulator.run            =>  foreach (GCodeToken token in Tokens) - a List enumerator
    ///
    /// So a carve-view build on the UI thread, concurrent with a background load, walks a List that a
    /// worker thread is appending to. The enumerator notices and throws. A large program is what makes
    /// it reproducible: it widens the window on both sides.
    ///
    /// THE FIX IS NOT A SNAPSHOT. new List&lt;&gt;(job.Tokens) enumerates the list too, so it carries the
    /// same race in a shorter window - which would turn a reliable crash into a rare one. The guard has
    /// to be "do not build while a load is in flight", which is what GrblViewModel.IsLoading is for and
    /// what GCodeListControl.BuildExplanation already does. CarveView has no reference to IsLoading at
    /// all. Case 3 below is that guard.
    ///
    /// WHAT THIS DOES NOT PROVE: that Phil's session took this exact path. His program would pin that
    /// down. It proves the mechanism is present and reachable, and which guard closes it.
    /// </summary>
    public static class Program
    {
        static int fail;

        static void Check(bool ok, string what, string detail = "")
        {
            Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + (ok || detail.Length == 0 ? "" : "   [" + detail + "]"));
            if (!ok) fail++;
        }

        /// <summary>A program long enough that parsing it is a wide window.</summary>
        static string MakeProgram(int moves)
        {
            var sb = new StringBuilder();
            sb.AppendLine("G21 G90 G17");
            sb.AppendLine("G0 X0 Y0 Z5");
            sb.AppendLine("M3 S12000");
            for (int i = 0; i < moves; i++)
                sb.AppendLine(string.Format("G1 X{0:f3} Y{1:f3} Z{2:f3} F800",
                    (i % 200) * 0.5, (i / 200) * 0.5, -1.0 - (i % 7) * 0.1));
            sb.AppendLine("M5");
            sb.AppendLine("M30");
            return sb.ToString();
        }

        /// <summary>
        /// The load, in the order GCodeProgram.BackgroundLoad does it: the New bookend first (which is
        /// what resets the parser and clears its token list), then the parse on a worker thread.
        /// </summary>
        static Task StartBackgroundLoad(GCodeJob job, string path)
        {
            job.BlockConsumer = b => { };            // what BackgroundLoad sets: blocks go to a sink, not a binding
            job.AddBlock(path, CNC.Core.Action.New);   // CNC.Core.Action, which shadows System.Action here
            return Task.Run(() => { try { job.ParseFileLines(path); } catch { } });
        }

        /// <summary>One carve-view build: walk the program through the emulator, as BuildToolpath does.</summary>
        static int WalkOnce(List<GCodeToken> tokens, out Exception thrown)
        {
            thrown = null;
            int n = 0;
            var emu = new GCodeEmulator(true);
            emu.SetStartPosition(new Point3D(0d, 0d, 0d));
            try
            {
                foreach (var a in emu.Execute(tokens))
                    n++;
            }
            catch (Exception ex) { thrown = ex; }
            return n;
        }

        public static int Main()
        {
            Console.WriteLine();
            Console.WriteLine("carve-race-probe - issue #24");
            Console.WriteLine();

            string path = Path.Combine(Path.GetTempPath(), "carve-race.nc");
            File.WriteAllText(path, MakeProgram(40000));
            Console.WriteLine("  program: {0:n0} bytes", new FileInfo(path).Length);
            Console.WriteLine();

            // --- 1. the bug: build on this thread while the loader parses on another ----------------
            Console.WriteLine("  --- 1. building while a background load runs (the bug) ---");
            var job = new GCodeJob();
            var load = StartBackgroundLoad(job, path);

            Exception caught = null;
            int attempts = 0, walked = 0;
            while (!load.IsCompleted && caught == null && attempts < 2000)
            {
                attempts++;
                Exception ex;
                walked = WalkOnce(job.Tokens, out ex);
                if (ex != null) caught = ex;
            }
            load.Wait();

            Console.WriteLine("       builds attempted while loading : {0}", attempts);
            Console.WriteLine("       threw                          : {0}",
                caught == null ? "(nothing)" : caught.GetType().Name + ": " + caught.Message);
            Check(caught is InvalidOperationException && caught.Message.Contains("Collection was modified"),
                  "reproduces issue #24's exception",
                  caught == null ? "no throw in " + attempts + " builds" : caught.Message);

            // --- 2. the same list, once the load has finished, is fine ------------------------------
            Console.WriteLine();
            Console.WriteLine("  --- 2. the same build after the load completes (not a bad list) ---");
            Exception after;
            int walkedAfter = WalkOnce(job.Tokens, out after);
            Console.WriteLine("       walked {0:n0} actions, threw: {1}", walkedAfter,
                after == null ? "(nothing)" : after.GetType().Name);
            Check(after == null, "a completed program walks clean", after?.Message ?? "");
            Check(walkedAfter > 1000, "and it is a real program", walkedAfter.ToString());

            // --- 3. the guard: do not build while loading -------------------------------------------
            // Exactly what CarveView lacks. Modelled on GrblViewModel.IsLoading, which is what
            // GCodeListControl already keys off, and which the real fix should set and clear.
            Console.WriteLine();
            Console.WriteLine("  --- 3. with an IsLoading guard (the fix) ---");
            var job2 = new GCodeJob();
            var load2 = StartBackgroundLoad(job2, path);
            bool isLoading() => !load2.IsCompleted;

            Exception guarded = null;
            int skipped = 0, built = 0;
            for (int i = 0; i < 2000 && guarded == null; i++)
            {
                if (isLoading()) { skipped++; continue; }      // the guard: reschedule, do not build
                built++;
                Exception ex;
                WalkOnce(job2.Tokens, out ex);
                if (ex != null) guarded = ex;
                if (built >= 3) break;
            }
            load2.Wait();
            Exception finalEx;
            int finalWalk = WalkOnce(job2.Tokens, out finalEx);   // the rebuild once loading ends
            if (finalEx != null) guarded = finalEx;

            Console.WriteLine("       builds skipped while loading   : {0}", skipped);
            Console.WriteLine("       builds run                     : {0}", built + 1);
            Console.WriteLine("       final walk                     : {0:n0} actions", finalWalk);
            Console.WriteLine("       threw                          : {0}",
                guarded == null ? "(nothing)" : guarded.GetType().Name + ": " + guarded.Message);
            Check(guarded == null, "the guard closes it", guarded?.Message ?? "");
            Check(skipped > 0, "and the guard actually engaged (a load really was in flight)", skipped.ToString());

            Console.WriteLine();
            if (fail == 0)
            {
                Console.WriteLine("OK  the race reproduces, and an IsLoading guard closes it");
                return 0;
            }
            Console.WriteLine("FAILED  {0} check(s)", fail);
            return 1;
        }
    }
}
