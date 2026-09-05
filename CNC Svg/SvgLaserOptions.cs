/*
 * SvgLaserOptions.cs - part of the CNC.Svg library
 *
 * Everything SvgLaserProgram needs to emit a job, as plain data.
 *
 * ---- Why this is not SvgLaserSettings ----
 *
 * SvgLaserSettings (CNC Controls) is the PERSISTED, dialog-bound object: INotifyPropertyChanged,
 * XML-serialized into App.config, with validation summaries written for an operator to read. None of
 * that belongs in an assembly whose job is to turn artwork into lines of g-code, and an appliance
 * that has no dialog should not have to construct one to get a file out.
 *
 * So ioSender keeps its settings object and copies the values across (SvgLaserSettings.ToOptions);
 * EngravingBox fills this in from whatever it uses. The emitter sees only this.
 *
 * ---- MaxPower is not decoration ----
 *
 * It is the controller's $30. The S word is meaningless without it - S150 is a light mark against a
 * $30 of 1000 and full power against a $30 of 255 - and it is what the power ramp clamps against, so
 * leaving it at 0 disables the clamp rather than defaulting it. Ramped() below says what 0 means.
 */

using System;

namespace CNC.Svg
{
    /// <summary>The operator's choices, as plain data, for SvgLaserProgram.</summary>
    public class SvgLaserOptions
    {
        // ---- exposure ----

        /// <summary>S word for outline moves, before any ramp or beam-disable.</summary>
        public double Power = 200d;

        /// <summary>S word for shading moves.</summary>
        public double FillPower = 200d;

        /// <summary>Feed for outline moves, mm/min.</summary>
        public double Feed = 1200d;

        /// <summary>Feed for shading moves, mm/min.</summary>
        public double FillFeed = 1200d;

        /// <summary>Feed for rapids, mm/min.</summary>
        public double TravelFeed = 3000d;

        /// <summary>The controller's $30. 0 means "unknown", which disables the ramp clamp.</summary>
        public double MaxPower = 1000d;

        // ---- what to burn ----

        /// <summary>Shade the enclosed areas before (or instead of) tracing the outlines.</summary>
        public bool Fill;

        /// <summary>Scan line spacing for shading, mm.</summary>
        public double Interval = 0.2d;

        /// <summary>Trace the outlines after shading. False with Fill on means shading only.</summary>
        public bool OutlineAfterFill = true;

        /// <summary>Times each outline is traced.</summary>
        public int Passes = 1;

        // ---- placement ----

        /// <summary>Where the artwork sits from the parked corner, mm.</summary>
        public double OriginX, OriginY;

        /// <summary>Copies of the artwork, stepped by PitchX/PitchY - the test-strip case.</summary>
        public int Copies = 1;

        /// <summary>Step between copies, mm.</summary>
        public double PitchX, PitchY;

        /// <summary>Power added per copy, for a rising test strip. 0 = no ramp.</summary>
        public double PitchPower, PitchFillPower;

        /// <summary>
        /// True anchors the artwork's TOP-left corner at the origin, so it runs to negative Y. False
        /// anchors the lower-left and it runs to positive Y. SvgOutlines always normalises to the
        /// lower-left with Y up, so this is the one place the two conventions are reconciled.
        /// </summary>
        public bool AnchorBackLeft;

        // ---- machine / safety ----

        /// <summary>M4 dynamic power when true, M3 constant when false.</summary>
        public bool Dynamic = true;

        /// <summary>
        /// False emits the entire job with every S word zeroed and the laser never enabled - a dry run
        /// that moves exactly as the real thing would. Noted in the file, not just in a dialog.
        /// </summary>
        public bool BeamOn = true;

        /// <summary>
        /// Whether $32 laser mode is on. Only used to write a warning into the file: a rapid travels
        /// LIT with $32=0, which is a diagonal scar across the work, so it is said out loud.
        /// </summary>
        public bool LaserModeOn = true;

        /// <summary>
        /// Power for copy <paramref name="copy"/> under a ramp, clamped into [0, MaxPower].
        ///
        /// Identical to SvgLaserSettings.Ramped, which is the dialog's warning path - the two must agree
        /// or the file burns something the operator was not warned about. MaxPower of 0 means the $30 is
        /// unknown, and an unknown ceiling cannot be enforced, so only the floor applies.
        /// </summary>
        public double Ramped(double basePower, double pitch, int copy)
        {
            double v = basePower + pitch * copy;
            return v < 0d ? 0d : (MaxPower > 0d && v > MaxPower ? MaxPower : v);
        }
    }
}
