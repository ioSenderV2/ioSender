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
 *   G21 G90 G17
 *   G92 X0 Y0          the head's CURRENT position becomes the artwork's origin
 *   M4 S0              dynamic power (or M3 constant), beam off
 *   G0 X.. Y.. S0 F<travel>
 *   G1 X.. Y.. S<power> F<feed>
 *   ...
 *   M5
 *   G0 X0 Y0
 *   G92.1              clear the temporary origin
 *   M30
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
        private static string N(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }

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
            settings.LaserModeOn = IsLaserMode();
            settings.MaxPower = MaxPower();

            if (new SvgLaserDialog(settings) { Owner = Application.Current.MainWindow }.ShowDialog() != true)
                return false;

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

                job.AddBlock("(Origin: the head's position when this starts becomes 0,0.)");
                job.AddBlock("(Position it at the artwork's LOWER-LEFT corner before running.)");
                job.AddBlock("(If this job is ABORTED the G92 offset stays set - clear it with G92.1.)");

                job.AddBlock("G21 G90 G17");
                job.AddBlock("G92 X0 Y0");
                job.AddBlock((settings.Dynamic ? "M4" : "M3") + " S0");

                if (settings.Fill)
                    EmitFill(job, art);

                if (settings.Fill && !settings.OutlineAfterFill)
                {
                    job.AddBlock("M5");
                    job.AddBlock(string.Format(CultureInfo.InvariantCulture, "G0 X0 Y0 F{0}", N(settings.TravelFeed)));
                    job.AddBlock("G92.1");
                    job.AddBlock("M30", CNC.Core.Action.End);
                    return;
                }

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
                            "G0 X{0} Y{1} S0 F{2}", F(p0.X), F(p0.Y), N(settings.TravelFeed)));

                        for (int i = 1; i < contour.Points.Count; i++)
                            job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                                "G1 X{0} Y{1} S{2} F{3}",
                                F(contour.Points[i].X), F(contour.Points[i].Y), N(settings.Power), N(settings.Feed)));

                        // SvgOutlines returns CLOSED contours, but whether the closing point is repeated in
                        // Points is the loader's business, not this emitter's - so close explicitly when the
                        // last point is not already the first. A gap in a logo outline is obvious on the
                        // material and invisible in the file.
                        var pn = contour.Points[contour.Points.Count - 1];
                        if (Math.Abs(pn.X - p0.X) > 1e-6 || Math.Abs(pn.Y - p0.Y) > 1e-6)
                            job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                                "G1 X{0} Y{1} S{2} F{3}", F(p0.X), F(p0.Y), N(settings.Power), N(settings.Feed)));
                    }
                }

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
                "(shading: {0} spans at {1} mm interval, S{2} F{3})",
                spans.Count, F(settings.Interval), N(settings.FillPower), N(settings.FillFeed)));

            foreach (var s in spans)
            {
                job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                    "G0 X{0} Y{1} S0 F{2}", F(s.X0), F(s.Y), N(settings.TravelFeed)));
                job.AddBlock(string.Format(CultureInfo.InvariantCulture,
                    "G1 X{0} Y{1} S{2} F{3}", F(s.X1), F(s.Y), N(settings.FillPower), N(settings.FillFeed)));
            }

            if (settings.OutlineAfterFill)
                job.AddBlock("(outline follows the shading, so the edge lands crisp over it)");
        }
    }
}
