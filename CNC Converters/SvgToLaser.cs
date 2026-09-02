/*
 * SvgToLaser.cs - part of the CNC Converters library
 *
 * Turns an SVG into plain-GRBL laser g-code and hands it to the Job tab, the same way the HPGL and
 * Excellon converters do (IGCodeConverter, registered in MainWindow).
 *
 * ---- Why this is not a Work Order operation ----
 *
 * The Work Order compiler emits grblHAL: bracket expressions, #<named> parameters, M6 tool changes,
 * G43 tool length offsets, Z depth per pass. A diode laser controller is typically plain Grbl - the
 * machine this was written against reports "Grbl 1.1h" on an MKS DLC32 - and rejects every one of
 * those. So this is a separate emitter that shares only the GEOMETRY: SvgOutlines.Load, which already
 * produces closed contours in mm and is the same code that carves artwork on the mill.
 *
 * ---- What it emits ----
 *
 * Outlines - each contour traced once per pass - and optionally SHADING first, which scans back and
 * forth across the enclosed areas (see SvgLaserFill). Shading off leaves the program byte-for-byte
 * what it was before shading existed.
 *
 * No overscan. It exists to stop a constant-power beam scorching the ends of each scan line while the
 * head decelerates - and M4 dynamic power already solves that by scaling power with speed, which is
 * what laser mode is for. It would be worth adding if the M3 constant-power path is ever used for
 * shading; it is not needed for the M4 one.
 *
 *   #<s_line> = 200    exposure as four named constants, so the job can be retuned by
 *   #<f_line> = 1200   editing four numbers instead of hundreds of cut moves
 *   G21 G90 G17
 *   G92 X0 Y0          the head's CURRENT position becomes the artwork's origin
 *   M4 S0              dynamic power (or M3 constant), beam off
 *   G0 X.. Y.. S0 F<travel>
 *   G1 X.. Y.. S#<s_line> F#<f_line>
 *   ...
 *   M5
 *   G0 X0 Y0
 *   G92.1              clear the temporary origin
 *   M30
 *
 * The parameters are an NGC feature this controller class does NOT have - see the note above about
 * plain Grbl rejecting #<named> parameters. That is deliberate and not a contradiction: the FILE is
 * canonical and each READER resolves it. ioSender substitutes on load when the controller does not
 * report EXPR (CNC.Core.NgcConstants), and the EngravingBox appliance does the same when it runs the
 * file off its SD card. What reaches the wire is always literal S and F words.
 *
 * A constant can also be RE-declared part way through, and that is what the per-copy power ramp does:
 * copy 2 opens with "#<s_line> = 250" and every reference below it takes the new value. Five copies of
 * one logo at rising power, burned in a single job, is the cheapest way to find the right exposure -
 * same material, same focus, same session.
 *
 * G92 rather than absolute work coordinates because that is how a diode laser is actually set up:
 * jog the head to the corner of the material and burn from there. There is no fixture and often no
 * homing - the machine this targets reports MPos from wherever it happened to power up, so an
 * absolute coordinate means nothing.
 *
 * The cost of G92, stated plainly: an ABORTED job leaves the offset applied, because G92.1 is on the
 * last line. Same shape as any trailing cleanup. It is emitted as a comment in the file too, so the
 * operator has been told rather than discovering it on the next job.
 *
 * ---- Laser mode ----
 *
 * Assumes $32=1. With laser mode on, Grbl blanks the beam during G0 by itself and scales power with
 * acceleration, which is what stops corners burning dark. With $32=0 a rapid travels LIT. Checked
 * before emitting rather than trusted, and S0 goes on the rapids anyway - one extra word, and it
 * makes the same file safe on a controller where laser mode is off.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using CNC.Core;
using CNC.Controls;

namespace CNC.Converters
{
    public class SvgToLaser : IGCodeConverter
    {
        public string FileType { get { return "SVG files (laser)"; } }
        public string FileExtensions { get { return "svg"; } }

        // Everything the operator chose, so the emitter reads as one thing rather than a parameter list.
        // The instance is the PERSISTED one (App.config, "SvgLaser" section), not a fresh default: this
        // converter is rebuilt by Activator.CreateInstance on every load, so a per-instance settings
        // object meant the operator retyped power and feed for each import. See SvgLaserSettings.
        private SvgLaserSettings settings = SvgLaserSettings.Current;

        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        // Where the copy currently being emitted sits. Applied at the point every coordinate is formatted,
        // so there is exactly one place a copy's offset can be forgotten, rather than one per emit site.
        private double offX, offY;

        // Shift that puts the artwork on the correct side of the origin. SvgOutlines normalises to the
        // artwork's LOWER-left with Y upward; anchoring to the back-left corner means subtracting the
        // artwork's height so it occupies Y 0 down to -height instead. See SvgLaserSettings.AnchorBackLeft.
        //
        // Held here rather than folded into offY because offY is rebuilt per copy by SetCopyOffset, and a
        // value that has to be re-added every time something else is recalculated is a value that will
        // eventually be forgotten once.
        private double anchorY;

        // What each exposure constant currently holds in the emitted program, so a re-declaration that
        // would change nothing is not written at all. Seeded from the declarations at the top of the file:
        // without that seed the first copy of a ramp restates the value the header just set, which reads
        // like the ramp starts one step late. Keyed by constant name (NgcConstants.SvgLaser.*).
        private readonly Dictionary<string, double> declared = new Dictionary<string, double>();

        private string FX(double v) { return F(v + offX); }
        private string FY(double v) { return F(v + offY + anchorY); }

        /// <summary>Place copy n: the artwork's own origin plus n pitches.</summary>
        private void SetCopyOffset(int n)
        {
            offX = settings.OriginX + n * settings.PitchX;
            offY = settings.OriginY + n * settings.PitchY;
        }

        /// <summary>
        /// Back to no offset before the closing moves.
        ///
        /// The program ends with "G0 X0 Y0" to return to where it started, and that has to mean the ORIGIN -
        /// not the origin plus wherever the last copy happened to be. Leaving the offset applied would send
        /// the head to the last copy's corner instead, which on a fixture that expects the machine parked at
        /// its start position is a wrong answer that looks like a right one.
        /// </summary>
        private void ClearCopyOffset()
        {
            offX = offY = 0d;
        }
        private static string N(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }

        // The S and F words the cut moves carry: references to the four constants declared at the top of
        // the program, not literals. Spelled through NgcConstants.SvgLaser so the emitter and the resolver
        // cannot drift apart on a name - a typo here would produce a file that resolves to nothing useful
        // and still looks like valid g-code.
        private static readonly string SLine = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.LinePower);
        private static readonly string SFill = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.FillPower);
        private static readonly string FLine = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.LineFeed);
        private static readonly string FFill = NgcConstants.SvgLaser.Ref(NgcConstants.SvgLaser.FillFeed);

        /// <summary>
        /// The S word actually emitted, which is 0 for the whole job when the beam is disabled.
        ///
        /// Zeroed here rather than by altering the motion, so a dry run rehearses the real thing: same
        /// path, same feeds, same time on the clock. A rehearsal that moves differently from the job it
        /// stands in for answers a question nobody asked.
        /// </summary>
        private double BeamPower(double configured)
        {
            return settings.BeamOn ? configured : 0d;
        }

        /// <summary>
        /// Re-declare one exposure constant for this copy, when a power ramp is in effect.
        ///
        /// This is what makes a test strip possible: five copies of the same artwork at rising power,
        /// burned in one job, so the comparison is against the same material, same focus, same session.
        /// It works because the constants are DECLARED rather than inlined - a redeclaration governs
        /// every reference below it, which is how a controller with EXPR reads the same file and what
        /// NgcConstants does when it does not.
        ///
        /// Emits nothing when the pitch is zero: the declaration at the top of the program already says
        /// it, and repeating it per copy would just be noise in the listing.
        ///
        /// The CLAMP is announced, never silent. Power cannot exceed $30, so a ramp that overshoots
        /// burns the last copies at identical power - and a test strip whose last two squares are the
        /// same while the file says they differ is worse than no test strip, because it reads as a
        /// result. Same rule as SvgLaserSettings.PowerRampSummary, which warns before the job is built.
        /// </summary>
        private void DeclareCopyPower(CNC.Controls.GCode job, string name, double basePower, double pitch, int copy)
        {
            if (pitch == 0d)
                return;

            double wanted = basePower + pitch * copy;
            double used = settings.Ramped(basePower, pitch, copy);
            double value = BeamPower(used);

            // Only when it actually moves. Copy 1 always lands on the value the header declared, and a
            // ramp that has hit the clamp repeats the same number for every copy after it - writing those
            // assignments out says "this changed here" about a line that changed nothing.
            double current;
            if (!declared.TryGetValue(name, out current) || Math.Abs(current - value) > 1e-9)
            {
                job.AddBlock(string.Format(CultureInfo.InvariantCulture, "#<{0}> = {1}",
                                           name, N(value)));
                declared[name] = value;
            }

            if (Math.Abs(wanted - used) > 1e-9)
                job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                    "(power ramp CLAMPED: copy {0} asked for {1} and can only have {2})",
                    copy + 1, N(wanted), N(used)));
        }

        public bool LoadFile(CNC.Controls.GCode job, string filename)
        {
            // Aspect first, so the dialog can show the height a chosen width implies without loading and
            // flattening the whole artwork on every keystroke. AspectOf is height/width, and memoized.
            double aspect = SvgOutlines.AspectOf(filename);
            if (aspect <= 0d)
            {
                AppDialogs.Show("That SVG could not be measured - no drawable outlines were found in it.",
                                "SVG to laser", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            settings.Aspect = aspect;
            settings.FilePath = filename;
            settings.LaserModeOn = IsLaserMode();
            settings.MaxPower = MaxPower();

            // Loop, because a rejected placement should put the operator back in the dialog with their
            // numbers intact rather than dumping them out of the import entirely.
            while (true)
            {
                if (new SvgLaserDialog(settings) { Owner = Application.Current.MainWindow }.ShowDialog() != true)
                    return false;

                string problem;
                double fixedPitchY;
                if (settings.Validate(out problem, out fixedPitchY))
                    break;

                // A wrong-signed Pitch Y has one obvious correction, so OFFER it. Not applied silently:
                // flipping a sign the operator typed is inventing intent, and if they really did mean
                // +Y the job would run and quietly not be the one they pictured. Explicit consent gets
                // the convenience without the guessing.
                if (fixedPitchY != 0d)
                {
                    var answer = AppDialogs.Show(
                        problem + "\n\nUse " + fixedPitchY.ToString("0.###", CultureInfo.InvariantCulture)
                        + " mm instead?\n\nYes corrects it and carries on. No takes you back to the dialog.",
                        "SVG to laser - placement", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                    if (answer == MessageBoxResult.Cancel)
                        return false;
                    if (answer == MessageBoxResult.Yes)
                    {
                        settings.PitchY = fixedPitchY;
                        // Re-validate rather than assume: the sign was only the FIRST fault found, and a
                        // corrected pitch can still reach past the travel.
                        if (settings.Validate(out problem, out fixedPitchY))
                            break;
                    }
                    else
                        continue;
                }

                if (problem != null && AppDialogs.Show(
                        problem + "\n\nGo back and change it?\n\nNo abandons this import.",
                        "SVG to laser - placement", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;
            }

            SvgImportResult art;
            using (new UIUtils.WaitCursor())
                art = SvgOutlines.Load(filename, settings.WidthMm);

            if (art.Error != null)
            {
                AppDialogs.Show("That SVG could not be read:\n\n" + art.Error,
                                "SVG to laser", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // An incomplete import is surfaced rather than quietly burned. SvgOutlines reports exactly
            // what it could not handle, and a partial logo looks like a successful job right up until
            // you notice a piece missing - by which point the material is spent.
            if (!art.IsComplete &&
                AppDialogs.Show("This SVG contains elements the importer cannot handle:\n\n    " + art.Describe() +
                                "\n\nWhat it CAN read will burn; the rest will simply be absent. Continue anyway?",
                                "SVG to laser", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                                MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;

            if (art.Contours.Count == 0)
            {
                AppDialogs.Show("Nothing to burn - that SVG produced no closed outlines.",
                                "SVG to laser", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            Emit(job, filename, art);

            return true;
        }

        // Whether the controller has laser mode switched on ($32). Not a hard stop - the operator may be
        // about to set it, or may genuinely want constant-power M3 on a machine without it - but a rapid
        // travels LIT with $32=0, which is the difference between a clean job and a diagonal scar across
        // the work, so it is said out loud rather than assumed.
        private static bool IsLaserMode()
        {
            return GrblSettings.HasSetting(GrblSetting.Mode)
                && GrblSettings.GetInteger(GrblSetting.Mode) == (int)GrblMode.Laser;
        }

        // Full power, from $30. The S word is meaningless without it - S150 is a light mark against a
        // $30 of 1000 and full power against a $30 of 255 - so the dialog shows what it is scaling to
        // rather than leaving the operator to remember.
        private static double MaxPower()
        {
            double max = GrblSettings.HasSetting(GrblSetting.RpmMax) ? GrblSettings.GetDouble(GrblSetting.RpmMax) : 0d;
            return max > 0d ? max : 1000d;
        }

        private void Emit(CNC.Controls.GCode job, string filename, SvgImportResult art)
        {
            using (new UIUtils.WaitCursor())
            {
                job.AddBlock(filename, CNC.Core.Action.New);

                // Set before ANY coordinate is formatted - every X and Y below goes through FX/FY.
                anchorY = settings.AnchorBackLeft ? -art.HeightMm : 0d;

                job.AddBlock(settings.Fill ? "(SVG to laser - shading then outline)" : "(SVG to laser - outlines only)");
                job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                    "(artwork {0} x {1} mm, {2} outline{3}, {4} pass{5})",
                    F(art.WidthMm), F(art.HeightMm), art.Contours.Count, art.Contours.Count == 1 ? "" : "s",
                    settings.Passes, settings.Passes == 1 ? "" : "es"));
                job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                    "(power S{0} of {1}, feed {2} mm/min, travel {3} mm/min, {4})",
                    N(settings.Power), N(settings.MaxPower), N(settings.Feed), N(settings.TravelFeed),
                    settings.Dynamic ? "M4 dynamic" : "M3 constant"));

                if (!settings.LaserModeOn)
                    job.AddBlock("(WARNING: $32 laser mode is NOT enabled on this controller)");

                // Recorded in the file, not just shown in the dialog. A dry run that gets saved and opened
                // next week looks exactly like a real job apart from this line.
                if (!settings.BeamOn)
                    job.AddBlock("(BEAM DISABLED - dry run: every S word is 0 and the laser is never enabled.)");

                if (settings.Copies > 1 || settings.OriginX != 0d || settings.OriginY != 0d)
                    job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                        "(placement: origin X{0} Y{1}, {2} cop{3} at pitch X{4} Y{5})",
                        F(settings.OriginX), F(settings.OriginY), settings.Copies,
                        settings.Copies == 1 ? "y" : "ies", F(settings.PitchX), F(settings.PitchY)));

                // Named in the file as well as the dialog. The .nc outlives the dialog - it gets loaded on
                // another day, or streamed by something that never showed one - and "which corner" is not
                // recoverable by looking at the coordinates unless it is written down.
                job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                    "(anchor: {0}-left corner at origin, artwork runs to Y{1})",
                    settings.AnchorBackLeft ? "TOP" : "LOWER",
                    F(settings.AnchorBackLeft ? -art.HeightMm : art.HeightMm)));

                job.AddBlock("(Origin: the head's position when this starts becomes 0,0.)");
                job.AddBlock(string.Format("(Position it at the artwork's {0}-LEFT corner before running.)",
                                           settings.AnchorBackLeft ? "TOP" : "LOWER"));
                job.AddBlock("(If this job is ABORTED the G92 offset stays set - clear it with G92.1.)");

                // Exposure as four named constants at the top, rather than repeated on every one of the
                // hundreds of cut moves below. Retuning the job is then editing four numbers in the .nc,
                // which is the difference between a file you can adjust on the machine and one you have to
                // regenerate. A controller reporting EXPR evaluates them; ioSender substitutes them on load
                // when it does not (CNC.Core.NgcConstants), and the EngravingBox appliance does the same.
                foreach (var d in CNC.Core.NgcConstants.SvgLaser.Declarations(
                             BeamPower(settings.Power), BeamPower(settings.FillPower),
                             settings.Feed, settings.FillFeed, settings.Fill))
                    job.AddBlock(d);

                // Record what those blocks just set, so DeclareCopyPower can tell a real change from a
                // restatement. Same expressions as the call above - if either moves, both move.
                //
                // Cleared first: this is per-PROGRAM state on an object that outlives one emit (the
                // dialog loops on a rejected placement), and a value carried over from a previous run
                // would suppress a declaration the new program needs - silently, and only for the copy
                // whose power happened to match.
                declared.Clear();
                declared[NgcConstants.SvgLaser.LinePower] = BeamPower(settings.Power);
                if (settings.Fill)
                    declared[NgcConstants.SvgLaser.FillPower] = BeamPower(settings.FillPower);

                job.AddBlock("G21 G90 G17");
                job.AddBlock("G92 X0 Y0");
                // With the beam disabled the laser is never ENABLED either - belt and braces with the zeroed
                // S words. Either alone would do; both together means hand-editing one line cannot quietly
                // turn a rehearsal into a burn.
                job.AddBlock(settings.BeamOn ? (settings.Dynamic ? "M4" : "M3") + " S0" : "M5");

                if (settings.Fill)
                    EmitFill(job, art);

                if (settings.Fill && !settings.OutlineAfterFill)
                {
                    ClearCopyOffset();
                    job.AddBlock("M5");
                    job.AddBlock(string.Format(CultureInfo.InvariantCulture, "G0 X0 Y0 F{0}", N(settings.TravelFeed)));
                    job.AddBlock("G92.1");
                    job.AddBlock("M30", CNC.Core.Action.End);
                    return;
                }

                for (int copy = 0; copy < settings.Copies; copy++)
                {
                  SetCopyOffset(copy);
                  if (settings.Copies > 1)
                      job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                          "(copy {0} of {1} at X{2} Y{3})", copy + 1, settings.Copies, F(offX), F(offY)));

                  DeclareCopyPower(job, NgcConstants.SvgLaser.LinePower, settings.Power, settings.PitchPower, copy);

                  for (int pass = 0; pass < settings.Passes; pass++)
                  {
                    if (settings.Passes > 1)
                        job.AddBlock(string.Format("(pass {0} of {1})", pass + 1, settings.Passes));

                    foreach (var contour in art.Contours)
                    {
                        if (contour.Points.Count < 2)
                            continue;

                        var p0 = contour.Points[0];

                        // Rapid to the start with the beam commanded off. Laser mode already blanks a G0;
                        // the S0 costs one word and makes the same file safe where it is switched off.
                        job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                            "G0 X{0} Y{1} S0 F{2}", FX(p0.X), FY(p0.Y), N(settings.TravelFeed)));

                        for (int i = 1; i < contour.Points.Count; i++)
                            job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                                "G1 X{0} Y{1} S{2} F{3}",
                                FX(contour.Points[i].X), FY(contour.Points[i].Y), SLine, FLine));

                        // SvgOutlines returns CLOSED contours, but whether the closing point is repeated in
                        // Points is the loader's business, not this emitter's - so close explicitly when the
                        // last point is not already the first. A gap in a logo outline is obvious on the
                        // material and invisible in the file.
                        var pn = contour.Points[contour.Points.Count - 1];
                        if (Math.Abs(pn.X - p0.X) > 1e-6 || Math.Abs(pn.Y - p0.Y) > 1e-6)
                            job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                                "G1 X{0} Y{1} S{2} F{3}", FX(p0.X), FY(p0.Y), SLine, FLine));
                    }
                  }
                }

                ClearCopyOffset();
                job.AddBlock("M5");
                job.AddBlock(string.Format(CultureInfo.InvariantCulture, "G0 X0 Y0 F{0}", N(settings.TravelFeed)));
                job.AddBlock("G92.1");
                job.AddBlock("M30", CNC.Core.Action.End);
            }
        }

        // The shading pass: horizontal spans across the interior, burned one row at a time.
        //
        // Rows alternate direction (SvgLaserFill serpentines them), so consecutive spans are usually
        // adjacent and the G0 between them is short. The beam is commanded off on every one of those
        // rapids for the same reason as elsewhere - laser mode already blanks a G0, but a file that
        // does not rely on it is a file that cannot scar the work if $32 is ever cleared.
        private void EmitFill(CNC.Controls.GCode job, SvgImportResult art)
        {
            var spans = SvgLaserFill.Build(art.Contours, settings.Interval);

            if (spans.Count == 0)
            {
                job.AddBlock("(shading: nothing enclosed to fill at this interval)");
                return;
            }

            job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                "(shading: {0} spans at {1} mm interval, exposure from {2} / {3})",
                spans.Count, F(settings.Interval), SFill, FFill));

            for (int copy = 0; copy < settings.Copies; copy++)
            {
                SetCopyOffset(copy);

                if (settings.Copies > 1)
                    job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                        "(shading copy {0} of {1})", copy + 1, settings.Copies));

                DeclareCopyPower(job, NgcConstants.SvgLaser.FillPower, settings.FillPower, settings.PitchFillPower, copy);

                foreach (var s in spans)
                {
                    job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                        "G0 X{0} Y{1} S0 F{2}", FX(s.X0), FY(s.Y), N(settings.TravelFeed)));
                    job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                        "G1 X{0} Y{1} S{2} F{3}", FX(s.X1), FY(s.Y), SFill, FFill));
                }
            }
            ClearCopyOffset();

            if (settings.OutlineAfterFill)
                job.AddBlock("(outline follows the shading, so the edge lands crisp over it)");
        }
    }
}
