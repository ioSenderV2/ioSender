/*
 * SvgLaserProgram.cs - part of the CNC.Svg library
 *
 * Turns imported artwork into plain-GRBL laser g-code, as a list of lines.
 *
 * ---- Lines, not a GCode document ----
 *
 * This was SvgToLaser.Emit, which wrote straight into CNC.Controls.GCode via AddBlock. That type is
 * the sender's in-memory program model and lives in the WPF assembly, so the emitter could only run
 * inside ioSender. Here it returns strings: ioSender feeds them to AddBlock exactly as before, and
 * the EngravingBox appliance writes them to a file. Neither cares what the other does with them.
 *
 * The FILE is the canonical artefact either way - see the header of SvgToLaser for why the exposure
 * and placement are named constants rather than literals, and why the placement is applied by a rapid
 * rather than by a G92.
 *
 * ---- What it emits ----
 *
 *   #<s_line> = 200    exposure as four named constants, so the job can be retuned by
 *   #<f_line> = 1200   editing four numbers instead of hundreds of cut moves
 *   #<x_org> = 16.5    and the placement as two more
 *   #<y_org> = -9.525
 *   G21 G90 G17
 *   G92 X0 Y0          the PARKED corner becomes 0,0
 *   M4 S0              dynamic power (or M3 constant), beam off
 *   G0 X#<x_org> Y#<y_org> S0 F<travel>   move in by the placement
 *   G92 X0 Y0          and THAT is the artwork's origin
 *   ... artwork geometry from 0,0 ...
 *   M5
 *   G0 X0 Y0                              back to the artwork origin
 *   G92 X#<x_org> Y#<y_org>               re-label it: we are in the parked frame again
 *   G0 X0 Y0                              back to the parked corner
 *   G92.1              clear the temporary origin
 *   M30
 *
 * No overscan. It exists to stop a constant-power beam scorching the ends of each scan line while the
 * head decelerates - and M4 dynamic power already solves that by scaling power with speed. It would
 * be worth adding if the M3 constant-power path is ever used for shading; it is not needed for M4.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using CNC.Core;

namespace CNC.Svg
{
    /// <summary>Artwork plus options to laser g-code.</summary>
    public class SvgLaserProgram
    {
        private readonly SvgLaserOptions o;
        private readonly List<string> lines = new List<string>();

        // Where the copy currently being emitted sits. Applied at the point every coordinate is
        // formatted, so there is exactly one place a copy's offset can be forgotten rather than one per
        // emit site.
        private double offX, offY;

        // Shift that puts the artwork on the correct side of the origin. SvgOutlines normalises to the
        // artwork's LOWER-left with Y upward; anchoring to the back-left corner means subtracting the
        // artwork's height so it occupies Y 0 down to -height instead.
        //
        // Held separately rather than folded into offY because offY is rebuilt per copy by SetCopyOffset,
        // and a value that has to be re-added every time something else is recalculated is a value that
        // will eventually be forgotten once.
        private double anchorY;

        // What each exposure constant currently holds in the emitted program, so a re-declaration that
        // would change nothing is not written at all. Seeded from the declarations at the top of the
        // file: without that seed the first copy of a ramp restates the value the header just set, which
        // reads like the ramp starts one step late.
        private readonly Dictionary<string, double> declared = new Dictionary<string, double>();

        private SvgLaserProgram(SvgLaserOptions options)
        {
            o = options;
        }

        /// <summary>
        /// The complete program for <paramref name="art"/>. <paramref name="art"/> must have loaded
        /// (Error null) and contain at least one contour - callers report an empty or failed import
        /// rather than emitting a program that burns nothing.
        /// </summary>
        public static List<string> Build(SvgImportResult art, SvgLaserOptions options)
        {
            if (art == null) throw new ArgumentNullException("art");
            if (options == null) throw new ArgumentNullException("options");

            return new SvgLaserProgram(options).Emit(art);
        }

        private List<string> Emit(SvgImportResult art)
        {
            // Set before ANY coordinate is formatted - every X and Y below goes through FX/FY.
            anchorY = o.AnchorBackLeft ? -art.HeightMm : 0d;

            Add(o.Fill ? "(SVG to laser - shading then outline)" : "(SVG to laser - outlines only)");
            Add(string.Format(CultureInfo.InvariantCulture,
                "(artwork {0} x {1} mm, {2} outline{3}, {4} pass{5})",
                F(art.WidthMm), F(art.HeightMm), art.Contours.Count, art.Contours.Count == 1 ? "" : "s",
                o.Passes, o.Passes == 1 ? "" : "es"));
            Add(string.Format(CultureInfo.InvariantCulture,
                "(power S{0} of {1}, feed {2} mm/min, travel {3} mm/min, {4})",
                N(o.Power), N(o.MaxPower), N(o.Feed), N(o.TravelFeed),
                o.Dynamic ? "M4 dynamic" : "M3 constant"));

            if (!o.LaserModeOn)
                Add("(WARNING: $32 laser mode is NOT enabled on this controller)");

            // Recorded in the file, not just shown in a dialog. A dry run that gets saved and opened next
            // week looks exactly like a real job apart from this line.
            if (!o.BeamOn)
                Add("(BEAM DISABLED - dry run: every S word is 0 and the laser is never enabled.)");

            if (o.Copies > 1 || o.OriginX != 0d || o.OriginY != 0d)
                Add(string.Format(CultureInfo.InvariantCulture,
                    "(placement: origin X{0} Y{1}, {2} cop{3} at pitch X{4} Y{5})",
                    F(o.OriginX), F(o.OriginY), o.Copies, o.Copies == 1 ? "y" : "ies",
                    F(o.PitchX), F(o.PitchY)));

            // Named in the file as well as the dialog. The .nc outlives the dialog - it gets loaded on
            // another day, or streamed by something that never showed one - and "which corner" is not
            // recoverable by looking at the coordinates unless it is written down.
            Add(string.Format(CultureInfo.InvariantCulture,
                "(anchor: {0}-left corner at origin, artwork runs to Y{1})",
                o.AnchorBackLeft ? "TOP" : "LOWER",
                F(o.AnchorBackLeft ? -art.HeightMm : art.HeightMm)));

            Add("(Origin: the head's position when this starts is the PARKED corner.)");
            Add(string.Format("(Park it at the stock's {0}-LEFT corner; the job moves in by the placement.)",
                              o.AnchorBackLeft ? "TOP" : "LOWER"));
            Add("(If this job is ABORTED the G92 offset stays set - clear it with G92.1.)");

            // Exposure as four named constants at the top rather than repeated on every one of the
            // hundreds of cut moves below. Retuning the job is then editing four numbers in the .nc.
            foreach (var d in NgcConstants.SvgLaser.Declarations(
                         BeamPower(o.Power), BeamPower(o.FillPower), o.Feed, o.FillFeed, o.Fill))
                Add(d);

            // Record what those blocks just set, so DeclareCopyPower can tell a real change from a
            // restatement. Same expressions as the call above - if either moves, both move.
            declared.Clear();
            declared[NgcConstants.SvgLaser.LinePower] = BeamPower(o.Power);
            if (o.Fill)
                declared[NgcConstants.SvgLaser.FillPower] = BeamPower(o.FillPower);

            // The placement, as two constants holding exactly what the operator chose. Everything below
            // is then the artwork's own geometry from 0,0 - the offsets are applied once, here, rather
            // than added to every one of the thousands of coordinates in the file.
            foreach (var d in NgcConstants.SvgLaser.PlacementDeclarations(o.OriginX, o.OriginY))
                Add(d);

            Add("G21 G90 G17");
            Add("G92 X0 Y0");
            // With the beam disabled the laser is never ENABLED either - belt and braces with the zeroed
            // S words. Either alone would do; both together means hand-editing one line cannot quietly
            // turn a rehearsal into a burn.
            Add(o.BeamOn ? (o.Dynamic ? "M4" : "M3") + " S0" : "M5");

            // Park -> placement -> zero. Two zeroing G92s rather than one G92 naming the offsets, because
            // "G92 X16.5" would declare the PARKED point to be 16.5 and send the artwork the other way.
            Add(string.Format(CultureInfo.InvariantCulture, "G0 X{0} Y{1} S0 F{2}",
                NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.OriginX),
                NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.OriginY),
                N(o.TravelFeed)));
            Add("G92 X0 Y0");

            if (o.Fill)
                EmitFill(art);

            if (o.Fill && !o.OutlineAfterFill)
            {
                EmitClose();
                return lines;
            }

            for (int copy = 0; copy < o.Copies; copy++)
            {
                SetCopyOffset(copy);
                if (o.Copies > 1)
                    Add(string.Format(CultureInfo.InvariantCulture,
                        "(copy {0} of {1} at X{2} Y{3})", copy + 1, o.Copies, F(offX), F(offY)));

                DeclareCopyPower(NgcConstants.SvgLaser.LinePower, o.Power, o.PitchPower, copy);

                for (int pass = 0; pass < o.Passes; pass++)
                {
                    if (o.Passes > 1)
                        Add(string.Format("(pass {0} of {1})", pass + 1, o.Passes));

                    foreach (var contour in art.Contours)
                    {
                        if (contour.Points.Count < 2)
                            continue;

                        var p0 = contour.Points[0];

                        // Rapid to the start with the beam commanded off. Laser mode already blanks a G0;
                        // the S0 costs one word and makes the same file safe where it is switched off.
                        Add(string.Format(CultureInfo.InvariantCulture,
                            "G0 X{0} Y{1} S0 F{2}", FX(p0.X), FY(p0.Y), N(o.TravelFeed)));

                        for (int i = 1; i < contour.Points.Count; i++)
                            Add(string.Format(CultureInfo.InvariantCulture,
                                "G1 X{0} Y{1} S{2} F{3}",
                                FX(contour.Points[i].X), FY(contour.Points[i].Y), SLine, FLine));

                        // SvgOutlines returns CLOSED contours, but whether the closing point is repeated
                        // in Points is the loader's business, not this emitter's - so close explicitly
                        // when the last point is not already the first. A gap in a logo outline is
                        // obvious on the material and invisible in the file.
                        var pn = contour.Points[contour.Points.Count - 1];
                        if (Math.Abs(pn.X - p0.X) > 1e-6 || Math.Abs(pn.Y - p0.Y) > 1e-6)
                            Add(string.Format(CultureInfo.InvariantCulture,
                                "G1 X{0} Y{1} S{2} F{3}", FX(p0.X), FY(p0.Y), SLine, FLine));
                    }
                }
            }

            EmitClose();
            return lines;
        }

        /// <summary>
        /// The closing moves: back to the artwork origin, back to the parked corner, offsets cleared.
        ///
        /// Both exits emit this - the shading-only one and the full one - and they used to carry their
        /// own copy of it, which is one edit away from two programs that end differently.
        ///
        /// The return to park is why the placement constants appear a second time. After the artwork the
        /// head is in the ARTWORK's frame, in which the parked corner is at minus the placement; naming
        /// that as a literal would put a number in the file that no longer tracks x_org when someone
        /// edits it. Re-declaring the current point AS the placement restores the frame the job started
        /// in - the one where park is 0,0 - and the return is then a plain G0 X0 Y0.
        ///
        /// Order matters and is not interchangeable: return first, THEN G92.1. Clearing the offset while
        /// still at the artwork sends the following move to wherever the machine's own frame has 0,0,
        /// which on a machine with no homing and no limits is not somewhere to guess at.
        /// </summary>
        private void EmitClose()
        {
            ClearCopyOffset();
            Add("M5");
            Add(string.Format(CultureInfo.InvariantCulture, "G0 X0 Y0 F{0}", N(o.TravelFeed)));
            Add(string.Format(CultureInfo.InvariantCulture, "G92 X{0} Y{1}",
                NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.OriginX),
                NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.OriginY)));
            Add(string.Format(CultureInfo.InvariantCulture, "G0 X0 Y0 F{0}", N(o.TravelFeed)));
            Add("G92.1");
            Add("M30");
        }

        // The shading pass: horizontal spans across the interior, burned one row at a time.
        //
        // Rows alternate direction (SvgLaserFill serpentines them), so consecutive spans are usually
        // adjacent and the G0 between them is short. The beam is commanded off on every one of those
        // rapids for the same reason as elsewhere - laser mode already blanks a G0, but a file that does
        // not rely on it is a file that cannot scar the work if $32 is ever cleared.
        private void EmitFill(SvgImportResult art)
        {
            var spans = SvgLaserFill.Build(art.Contours, o.Interval);

            if (spans.Count == 0)
            {
                Add("(shading: nothing enclosed to fill at this interval)");
                return;
            }

            Add(string.Format(CultureInfo.InvariantCulture,
                "(shading: {0} spans at {1} mm interval, exposure from {2} / {3})",
                spans.Count, F(o.Interval), SFill, FFill));

            for (int copy = 0; copy < o.Copies; copy++)
            {
                SetCopyOffset(copy);

                if (o.Copies > 1)
                    Add(string.Format(CultureInfo.InvariantCulture,
                        "(shading copy {0} of {1})", copy + 1, o.Copies));

                DeclareCopyPower(NgcConstants.SvgLaser.FillPower, o.FillPower, o.PitchFillPower, copy);

                foreach (var s in spans)
                {
                    Add(string.Format(CultureInfo.InvariantCulture,
                        "G0 X{0} Y{1} S0 F{2}", FX(s.X0), FY(s.Y), N(o.TravelFeed)));
                    Add(string.Format(CultureInfo.InvariantCulture,
                        "G1 X{0} Y{1} S{2} F{3}", FX(s.X1), FY(s.Y), SFill, FFill));
                }
            }
            ClearCopyOffset();

            if (o.OutlineAfterFill)
                Add("(outline follows the shading, so the edge lands crisp over it)");
        }

        /// <summary>
        /// Re-declare one exposure constant for this copy, when a power ramp is in effect.
        ///
        /// This is what makes a test strip possible: five copies of the same artwork at rising power,
        /// burned in one job, so the comparison is against the same material, same focus, same session.
        /// It works because the constants are DECLARED rather than inlined - a redeclaration governs
        /// every reference below it.
        ///
        /// The CLAMP is announced, never silent. Power cannot exceed $30, so a ramp that overshoots
        /// burns the last copies at identical power - and a test strip whose last two squares are the
        /// same while the file says they differ is worse than no test strip, because it reads as a
        /// result.
        /// </summary>
        private void DeclareCopyPower(string name, double basePower, double pitch, int copy)
        {
            if (pitch == 0d)
                return;

            double wanted = basePower + pitch * copy;
            double used = o.Ramped(basePower, pitch, copy);
            double value = BeamPower(used);

            // Only when it actually moves. Copy 1 always lands on the value the header declared, and a
            // ramp that has hit the clamp repeats the same number for every copy after it - writing those
            // assignments out says "this changed here" about a line that changed nothing.
            double current;
            if (!declared.TryGetValue(name, out current) || Math.Abs(current - value) > 1e-9)
            {
                Add(string.Format(CultureInfo.InvariantCulture, "#<{0}> = {1}", name, N(value)));
                declared[name] = value;
            }

            if (Math.Abs(wanted - used) > 1e-9)
                Add(string.Format(CultureInfo.InvariantCulture,
                    "(power ramp CLAMPED: copy {0} asked for {1} and can only have {2})",
                    copy + 1, N(wanted), N(used)));
        }

        // ------------------------------------------------------------------ placement and formatting

        /// <summary>
        /// Place copy n: n pitches from the artwork origin.
        ///
        /// The PLACEMENT is not in here. It is applied once, by the rapid the program opens with, so what
        /// is written below the header is the artwork's own geometry. The pitch stays, because that is
        /// not placement: it is where copy n sits relative to copy 1, and it belongs to the geometry.
        /// </summary>
        private void SetCopyOffset(int n)
        {
            offX = n * o.PitchX;
            offY = n * o.PitchY;
        }

        /// <summary>
        /// Back to no offset before the closing moves.
        ///
        /// The program ends with "G0 X0 Y0" to return to where it started, and that has to mean the
        /// ORIGIN - not the origin plus wherever the last copy happened to be. Leaving the offset applied
        /// would send the head to the last copy's corner instead, which on a fixture that expects the
        /// machine parked at its start position is a wrong answer that looks like a right one.
        /// </summary>
        private void ClearCopyOffset()
        {
            offX = offY = 0d;
        }

        /// <summary>
        /// The S word actually emitted, which is 0 for the whole job when the beam is disabled.
        ///
        /// Zeroed here rather than by altering the motion, so a dry run rehearses the real thing: same
        /// path, same feeds, same time on the clock. A rehearsal that moves differently from the job it
        /// stands in for answers a question nobody asked.
        /// </summary>
        private double BeamPower(double configured)
        {
            return o.BeamOn ? configured : 0d;
        }

        private void Add(string line)
        {
            lines.Add(line);
        }

        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string N(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }

        private string FX(double v) { return F(v + offX); }
        private string FY(double v) { return F(v + offY + anchorY); }

        // The S and F words the cut moves carry: references to the four constants declared at the top of
        // the program, not literals. Spelled through NgcConstants.SvgLaser so the emitter and the
        // resolver cannot drift apart on a name - a typo here would produce a file that resolves to
        // nothing useful and still looks like valid g-code.
        private static readonly string SLine = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.LinePower);
        private static readonly string SFill = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.FillPower);
        private static readonly string FLine = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.LineFeed);
        private static readonly string FFill = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.FillFeed);
    }
}
