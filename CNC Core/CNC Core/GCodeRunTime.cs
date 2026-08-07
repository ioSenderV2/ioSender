/*
 * GCodeRunTime.cs - part of CNC Core library
 *
 * Estimate how long a program will take to run: walk it through GCodeEmulator, sum distance over
 * rate for every motion, add dwells. Rapids use the controller's own $110-$112 max rates when a
 * controller has been seen (falling back to a sane default offline); feed moves use the programmed
 * feed rate current at that token.
 *
 * This is a NAIVE kinematic estimate - no acceleration model, no junction speed, no G93 inverse
 * time - so it reads optimistic, and most so on programs made of many short segments (a V-carve of
 * script lettering being the canonical case) where the machine spends much of its life
 * accelerating. It is presented with a "~" for exactly that reason. What it is for is telling a
 * 4-minute engraving from a 40-minute one before pressing Start, not timing the job.
 */

using System;
using System.Collections.Generic;
using CNC.GCode;

namespace CNC.Core
{
    public static class GCodeRunTime
    {
        // Rapid rate assumed per axis when no controller settings are cached (never connected).
        private const double DefaultRapidMmMin = 5000d;

        /// <summary>Estimate the run time of raw NGC text. Zero on empty or unparseable input.</summary>
        public static TimeSpan EstimateText(string ngc)
        {
            if (string.IsNullOrEmpty(ngc))
                return TimeSpan.Zero;

            var parser = new GCodeParser();
            foreach (var raw in ngc.Replace("\r", string.Empty).Split('\n'))
            {
                string line = raw;
                try
                {
                    // quiet:false, because quiet mode skips parsing entirely (it exists for
                    // pre-scan/validation passes) - no tokens would ever be produced.
                    parser.ParseBlock(ref line, false);
                }
                catch
                {
                    // A line the parser refuses costs the estimate that line, not the whole answer.
                }
            }
            return Estimate(parser.Tokens);
        }

        /// <summary>Estimate the run time of an already-tokenized program.</summary>
        public static TimeSpan Estimate(List<GCodeToken> tokens)
        {
            if (tokens == null || tokens.Count == 0)
                return TimeSpan.Zero;

            double minutes = 0d;
            var emu = new GCodeEmulator();

            // The emulator assumes a controller has been seen (it reads the active work coordinate
            // system, null before the first connect). An estimate must never take Generate down with
            // it - whatever was summed before a refusal is still the best answer available.
            try
            {
                minutes = Walk(emu, tokens, RapidRates(), false);
            }
            catch
            {
            }

            return TimeSpan.FromMinutes(minutes);
        }

        /// <summary>
        /// Per-section estimates for a program split into named sections (the loaded outline). Sections are
        /// walked in order through ONE emulator that is never reset between them, so modal state - feed
        /// rate, position, plane - carries across a boundary as it does on the machine. Estimating each
        /// section standalone would restore a default feed and origin the program never re-states, making
        /// every section after the first wrong.
        /// </summary>
        /// <returns>One TimeSpan per entry of <paramref name="sections"/>, in the same order.</returns>
        public static List<TimeSpan> EstimateSections(List<List<GCodeToken>> sections)
        {
            var result = new List<TimeSpan>();
            if (sections == null)
                return result;

            var rapid = RapidRates();
            var emu = new GCodeEmulator();

            // Reset ONCE, up front. Execute() does this per call, which is exactly what must not happen
            // between sections - but a never-reset emulator has no coordinate system at all and throws on
            // its first token, silently zeroing every section.
            emu.Reset();

            foreach (var section in sections)
            {
                double minutes = 0d;
                try
                {
                    minutes = Walk(emu, section, rapid, true);
                }
                catch
                {
                    // This section's estimate is lost; the emulator's state may now be mid-program, so the
                    // sections after it are suspect too - but a wrong-ish estimate beats none, and the
                    // whole-program figure shown beside the file name is computed independently.
                }
                result.Add(TimeSpan.FromMinutes(minutes));
            }

            return result;
        }

        private static double[] RapidRates()
        {
            var rapid = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double r = 0d;
                try { r = GrblSettings.GetDouble(GrblSetting.MaxFeedRateBase + i); } catch { }
                rapid[i] = r > 0d ? r : DefaultRapidMmMin;
            }
            return rapid;
        }

        private static double Walk(GCodeEmulator emu, List<GCodeToken> tokens, double[] rapid, bool continueState)
        {
            double minutes = 0d;
            if (tokens == null || tokens.Count == 0)
                return minutes;

            foreach (var a in continueState ? emu.ExecuteContinue(tokens) : emu.Execute(tokens))
            {
                switch (a.Token.Command)
                {
                    case Commands.G0:
                        minutes += RapidMinutes(a, rapid);
                        break;

                    case Commands.G1:
                        if (emu.Feedrate > 0d)
                            minutes += Distance(a) / emu.Feedrate;
                        break;

                    case Commands.G2:
                    case Commands.G3:
                        if (emu.Feedrate > 0d)
                            minutes += ArcLength(a, emu) / emu.Feedrate;
                        break;

                    case Commands.G4:
                        minutes += ((GCDwell)a.Token).Delay / 60d;
                        break;

                    default:
                        // Canned drill cycles expand into several actions per hole, each carrying the
                        // drill token itself: treat descending motion as the feed plunge, the rest
                        // (retracts, repositioning) as rapids.
                        if (a.Token is GCCannedDrill)
                        {
                            if (a.End.Z < a.Start.Z - 1e-9)
                            {
                                if (emu.Feedrate > 0d)
                                    minutes += Distance(a) / emu.Feedrate;
                            }
                            else
                                minutes += RapidMinutes(a, rapid);
                        }
                        break;
                }
            }

            return minutes;
        }

        /// <summary>"~45 s", "~14 min", "~1 h 20 min" - deliberately coarse; see the header on why "~".</summary>
        public static string Format(TimeSpan t)
        {
            if (t.TotalSeconds < 1d)
                return string.Empty;
            if (t.TotalSeconds < 90d)
                return string.Format("~{0:0} s", t.TotalSeconds);
            if (t.TotalHours < 1d)
                return string.Format("~{0:0} min", t.TotalMinutes);
            return string.Format("~{0:0} h {1:0} min", Math.Floor(t.TotalHours), t.Minutes);
        }

        private static double Distance(RunAction a)
        {
            double dx = a.End.X - a.Start.X, dy = a.End.Y - a.Start.Y, dz = a.End.Z - a.Start.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Axes rapid simultaneously, so the move takes as long as its slowest axis.
        private static double RapidMinutes(RunAction a, double[] rapid)
        {
            double t = Math.Abs(a.End.X - a.Start.X) / rapid[0];
            t = Math.Max(t, Math.Abs(a.End.Y - a.Start.Y) / rapid[1]);
            t = Math.Max(t, Math.Abs(a.End.Z - a.Start.Z) / rapid[2]);
            return t;
        }

        // r * sweep in the arc plane, helical travel folded in. The sweep logic mirrors
        // GCArc.GeneratePoints - including "equal angles means a full circle", which is why a
        // chord-length shortcut would be exactly wrong for a helical bore (its chord is zero).
        private static double ArcLength(RunAction a, GCodeEmulator emu)
        {
            var tok = (GCArc)a.Token;
            var plane = emu.Plane;
            bool rel = emu.DistanceMode == DistanceMode.Incremental;
            double[] start = { a.Start.X, a.Start.Y, a.Start.Z };

            double sa = tok.GetStartAngle(plane, start, rel);
            double ea = tok.GetEndAngle(plane, start, rel);
            var center = tok.GetCenter(plane, start, rel);
            double dx = start[plane.Axis0] - center[0], dy = start[plane.Axis1] - center[1];
            double r = Math.Sqrt(dx * dx + dy * dy);

            double sweep;
            if (sa == ea)
                sweep = Math.PI * 2d;
            else
            {
                if (ea == 0d)
                    ea = Math.PI * 2d;
                if (!tok.IsClocwise && ea < sa)
                    sweep = (Math.PI * 2d - sa) + ea;
                else if (tok.IsClocwise && ea > sa)
                    sweep = (Math.PI * 2d - ea) + sa;
                else
                    sweep = Math.Abs(ea - sa);
            }
            if (tok.P > 1)
                sweep += (tok.P - 1) * Math.PI * 2d;

            double[] s = { a.Start.X, a.Start.Y, a.Start.Z };
            double[] e = { a.End.X, a.End.Y, a.End.Z };
            double linear = e[plane.AxisLinear] - s[plane.AxisLinear];

            return Math.Sqrt(r * sweep * r * sweep + linear * linear);
        }
    }
}
