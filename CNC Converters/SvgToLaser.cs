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
 *   #<x_org> = 16.5    and the placement as two more, exactly as the dialog showed them
 *   #<y_org> = -9.525
 *   G21 G90 G17
 *   G92 X0 Y0          the PARKED corner becomes 0,0
 *   M4 S0              dynamic power (or M3 constant), beam off
 *   G0 X#<x_org> Y#<y_org> S0 F<travel>   move in by the placement
 *   G92 X0 Y0          and THAT is the artwork's origin
 *   G0 X.. Y.. S0 F<travel>               everything below is artwork geometry from 0,0
 *   G1 X.. Y.. S#<s_line> F#<f_line>
 *   ...
 *   M5
 *   G0 X0 Y0                              back to the artwork origin
 *   G92 X#<x_org> Y#<y_org>               re-label it: we are in the parked frame again
 *   G0 X0 Y0                              back to the parked corner
 *   G92.1              clear the temporary origin
 *   M30
 *
 * The placement is applied ONCE, by that rapid, rather than added to every coordinate in the file.
 * Two constants at the top then say where the job sits, and the geometry below is the artwork's own -
 * which is what makes the same .nc re-placeable by editing two lines.
 *
 * It is a rapid and not "G92 X#<x_org> Y#<y_org>" at the start, which is the shape it first looks like
 * it should be: G92 LABELS the point the head is standing on, so naming the placement there would call
 * the parked corner 16.5 and put the artwork the same distance the other way. A single G92 could carry
 * these only NEGATED, and a file whose constants are the negatives of the numbers the operator typed is
 * a file that gets hand-edited in the wrong direction exactly once.
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
using CNC.Svg;

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

        /// <summary>
        /// Hand the artwork to CNC.Svg and pour the resulting lines into the job.
        ///
        /// The whole emitter used to live in this file, writing straight into GCode via AddBlock. It
        /// moved to CNC.Svg.SvgLaserProgram on 2026-09-05 so the EngravingBox appliance can build the
        /// same file without WPF and without the sender - see that class and CNC.Svg.csproj. What is
        /// left here is what genuinely belongs to the Windows app: the wait cursor, the settings object,
        /// and turning a list of strings into a loaded program.
        ///
        /// The LAST line is M30 and is added with Action.End, which is what closes the program model.
        /// Asserted rather than assumed - if SvgLaserProgram ever stops ending that way, this must be
        /// the thing that notices, not a program that loads and will not run.
        /// </summary>
        private void Emit(CNC.Controls.GCode job, string filename, SvgImportResult art)
        {
            using (new UIUtils.WaitCursor())
            {
                var program = SvgLaserProgram.Build(art, settings.ToOptions());

                if (program.Count == 0 || program[program.Count - 1] != "M30")
                    throw new InvalidOperationException(
                        "SvgLaserProgram did not end with M30 - the program model needs that block flagged Action.End.");

                job.AddBlock(filename, CNC.Core.Action.New);

                for (int i = 0; i < program.Count - 1; i++)
                    job.AddBlock(program[i]);

                job.AddBlock(program[program.Count - 1], CNC.Core.Action.End);
            }
        }
    }
}
