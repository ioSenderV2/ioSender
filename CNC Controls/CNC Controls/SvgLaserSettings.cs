/*
 * SvgLaserSettings.cs - part of CNC Controls library
 *
 * What the operator chose for an SVG-to-laser conversion. See SvgToLaser for what is done with it.
 *
 * Persisted as its own App.config section, so the numbers you arrived at for a material are still
 * there next time - power and feed are found by burning test strips, and having to re-derive them
 * every session is how a good setting gets lost.
 *
 * It lives HERE, in CNC Controls, rather than next to SvgToLaser in CNC Converters, for one reason:
 * AppConfig registers the config sections and CNC Controls cannot reference CNC Converters (that is
 * the dependency the other way round). Registering late from MainWindow does not work either - a
 * section registered after ConfigStore.ReadDocument has its saved payload stashed in _unknown and
 * never read, so the settings would silently load as defaults forever.
 *
 * The header this file used to carry CLAIMED it was "persisted through the profile store the other
 * converters use". It was not - nothing persisted it, and GCode.LoadViaConverter builds the converter
 * with Activator.CreateInstance on EVERY load, so each import started from the field initializers
 * below. That is now true rather than aspirational, but it is why a comment is not evidence.
 */

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using CNC.Core;

namespace CNC.Controls
{
    public class SvgLaserSettings : INotifyPropertyChanged
    {
        private double _width = 100d, _power = 150d, _feed = 1200d, _travel = 3000d;
        private int _passes = 1;
        private bool _dynamic = true;
        private bool _fill = false, _outlineAfterFill = true;
        private double _interval = 0.1d, _fillPower = 120d, _fillFeed = 3000d;
        private double _originX = 0d, _originY = 0d, _pitchX = 0d, _pitchY = 0d;
        // Power ramp across copies - 0 means every copy burns the same, which is how it behaved
        // before this existed and is what an untouched saved config still deserializes to.
        private double _pitchPower = 0d, _pitchFillPower = 0d;
        private bool _anchorBackLeft = true;
        private bool _beamOn = true;
        private string _filePath = string.Empty;
        private int _copies = 1;

        /// <summary>
        /// The live instance from the config store - the one the dialog edits and AppConfig saves.
        /// Never null: falls back to a private default when the section has not been registered (a
        /// helper tool that never builds AppConfig), so the converter still works, just unremembered.
        /// </summary>
        [XmlIgnore]
        public static SvgLaserSettings Current
        {
            get { return ConfigStore.Get<SvgLaserSettings>() ?? fallback; }
        }

        private static readonly SvgLaserSettings fallback = new SvgLaserSettings();

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Artwork width in mm. Height follows from <see cref="Aspect"/>.</summary>
        public double WidthMm
        {
            get { return _width; }
            set { _width = value; OnPropertyChanged(); OnPropertyChanged("HeightSummary"); OnPropertyChanged("FillSummary"); }
        }

        // The three below are read from the artwork and the controller on every open, never from the
        // saved file. Persisting a $30 or $32 captured against a different machine - or the same machine
        // before a settings change - would scale every power figure in this dialog against a number that
        // is no longer true, and it would look exactly like a correct reading.

        /// <summary>Height divided by width, from SvgOutlines.AspectOf. Set before the dialog opens.</summary>
        [XmlIgnore]
        public double Aspect { get; set; } = 1d;

        /// <summary>Full-power S value, from $30. Shown so the power below has a scale to mean anything against.</summary>
        [XmlIgnore]
        public double MaxPower { get; set; } = 1000d;

        /// <summary>Whether $32 is on. Drives the warning - a rapid travels LIT without it.</summary>
        [XmlIgnore]
        public bool LaserModeOn { get; set; } = true;

        public double Power
        {
            get { return _power; }
            set { _power = value; OnPropertyChanged(); OnPropertyChanged("PowerSummary"); OnPropertyChanged("ExposureSummary"); OnPropertyChanged("FillExposureSummary"); OnPropertyChanged("PowerRampSummary"); }
        }

        public double Feed
        {
            get { return _feed; }
            set { _feed = value; OnPropertyChanged(); OnPropertyChanged("ExposureSummary"); OnPropertyChanged("FillExposureSummary"); }
        }

        public double TravelFeed
        {
            get { return _travel; }
            set { _travel = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// How many times each outline is traced. Several light passes generally beat one hot one -
        /// less charring, less chance of the beam wandering out of focus as the material chars.
        /// </summary>
        public int Passes
        {
            get { return _passes; }
            set { _passes = Math.Max(1, value); OnPropertyChanged(); }
        }

        /// <summary>
        /// M4 (power scales with speed) rather than M3 (constant). Dynamic is what stops corners
        /// burning dark as the machine decelerates into them, and needs $32=1 to do anything.
        /// </summary>
        public bool Dynamic
        {
            get { return _dynamic; }
            set { _dynamic = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Shade enclosed areas by scanning back and forth across them, rather than only tracing their
        /// boundary. Off leaves the emitted program exactly as it was before shading existed.
        /// </summary>
        public bool Fill
        {
            get { return _fill; }
            set { _fill = value; OnPropertyChanged(); OnPropertyChanged("PowerRampSummary"); }
        }

        /// <summary>Trace the boundary after shading, so the edge is crisp over the fill.</summary>
        public bool OutlineAfterFill
        {
            get { return _outlineAfterFill; }
            set { _outlineAfterFill = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Distance between scan lines, mm. Around the beam's spot size: tighter overlaps and darkens,
        /// wider leaves visible banding. 0.1 suits a typical diode.
        /// </summary>
        public double Interval
        {
            get { return _interval; }
            set { _interval = value; OnPropertyChanged(); OnPropertyChanged("FillSummary"); OnPropertyChanged("FillExposureSummary"); }
        }

        /// <summary>Shading usually wants less power and more speed than an outline - it covers area, not edges.</summary>
        public double FillPower
        {
            get { return _fillPower; }
            set { _fillPower = value; OnPropertyChanged(); OnPropertyChanged("FillExposureSummary"); OnPropertyChanged("PowerRampSummary"); }
        }

        public double FillFeed
        {
            get { return _fillFeed; }
            set { _fillFeed = value; OnPropertyChanged(); OnPropertyChanged("FillSummary"); OnPropertyChanged("FillExposureSummary"); }
        }

        /// <summary>
        /// Roughly how long the shading will take, so a 0.05 mm interval on a big piece of artwork is
        /// questioned before it is started rather than forty minutes in. Deliberately crude: it assumes
        /// every scan line crosses the full width, which is the worst case, and ignores acceleration.
        /// </summary>
        public string FillSummary
        {
            get
            {
                if (_interval <= 0d || _fillFeed <= 0d)
                    return string.Empty;

                double lines = _width * Aspect / _interval;
                double minutes = lines * _width / _fillFeed;
                return string.Format("~{0:0} scan lines, {1:0} min or less", lines, minutes);
            }
        }

        // ---- placement: where the artwork goes, and how many of it ----
        //
        // SvgOutlines normalises artwork to its own BOUNDING BOX - the lower-left of the drawn geometry
        // becomes 0,0 and empty canvas around it is discarded. So without these the logo can only ever
        // start exactly at the origin, and no amount of editing the SVG will move it: adding whitespace
        // changes nothing because nothing was drawn in it.
        //
        // That is fine for a bench job where you jog to the corner first, and useless for a fixture, where
        // the whole point is that the work sits in a known place and the file has to reach it.

        /// <summary>
        /// Where the artwork's anchor corner sits relative to the origin, mm. WHICH corner that is comes
        /// from AnchorBackLeft.
        /// </summary>
        public double OriginX
        {
            get { return _originX; }
            set { _originX = value; OnPropertyChanged(); OnPropertyChanged("PlacementSummary"); }
        }

        public double OriginY
        {
            get { return _originY; }
            set { _originY = value; OnPropertyChanged(); OnPropertyChanged("PlacementSummary"); }
        }

        /// <summary>
        /// Which corner of the artwork lands on the origin - and so which way the job runs from there.
        ///
        /// SvgOutlines hands over artwork normalised to its LOWER-left with Y growing upward, which suits a
        /// machine whose work origin is the front-left of the stock: the job then runs away from the
        /// operator, into positive Y. That is the ordinary CNC arrangement and it is what false means.
        ///
        /// A diode laser homed to its back-left corner is the mirror of that. Its whole table lies at
        /// NEGATIVE Y, so artwork placed the ordinary way runs off the back edge into the stop - which is
        /// not a wrong-looking job, it is a stalled axis. True anchors the artwork's TOP-left corner
        /// instead, so it occupies Y 0 down to -height and the work is in front of the origin.
        ///
        /// Defaults to true because that is the machine this was built for. It is a setting rather than a
        /// constant because the two conventions are equally real and the same dialog serves both; hard
        /// coding either one silently misplaces every job on the other kind of machine.
        /// </summary>
        public bool AnchorBackLeft
        {
            get { return _anchorBackLeft; }
            set
            {
                _anchorBackLeft = value;
                OnPropertyChanged();
                OnPropertyChanged("PlacementSummary");
                OnPropertyChanged("AnchorSummary");
            }
        }

        /// <summary>
        /// How many times to repeat the artwork, each offset by the pitch from the one before.
        ///
        /// For a fixture holding several identical parts this is the whole difference between one file and
        /// four: the same SVG, engraved once or three times at the pocket spacing. Doing the repetition here
        /// rather than by duplicating the art in the SVG keeps one drawing as the single source of the shape,
        /// so a change to the logo does not have to be made three times and cannot be made inconsistently.
        /// </summary>
        public int Copies
        {
            get { return _copies; }
            set { _copies = Math.Max(1, value); OnPropertyChanged(); OnPropertyChanged("PlacementSummary"); OnPropertyChanged("PowerRampSummary"); }
        }

        /// <summary>Spacing between copies. Both axes, because a row of parts may run either way.</summary>
        public double PitchX
        {
            get { return _pitchX; }
            set { _pitchX = value; OnPropertyChanged(); OnPropertyChanged("PlacementSummary"); }
        }

        public double PitchY
        {
            get { return _pitchY; }
            set { _pitchY = value; OnPropertyChanged(); OnPropertyChanged("PlacementSummary"); }
        }

        /// <summary>
        /// How much the OUTLINE power changes per copy, as an S value. Copy n burns at Power + n * this.
        ///
        /// The point is a test strip: lay five copies of the same artwork down at rising power and pick
        /// the one that looks right, instead of running five separate jobs and trying to remember which
        /// was which. Zero (the default) leaves every copy at the same power, exactly as before.
        ///
        /// Signed, so a negative value ramps down.
        /// </summary>
        public double PitchPower
        {
            get { return _pitchPower; }
            set { _pitchPower = value; OnPropertyChanged(); OnPropertyChanged("PowerRampSummary"); }
        }

        /// <summary>Same, for the shading power. Independent of <see cref="PitchPower"/>.</summary>
        public double PitchFillPower
        {
            get { return _pitchFillPower; }
            set { _pitchFillPower = value; OnPropertyChanged(); OnPropertyChanged("PowerRampSummary"); }
        }

        /// <summary>
        /// What the ramp actually produces, first copy to last, and whether it runs out of headroom.
        ///
        /// The clamp is the part worth showing. Power cannot exceed $30, so a ramp that overshoots
        /// gives two or more copies burned at the SAME power while the file claims they differ - and a
        /// test strip that silently compares a value against itself is worse than no test strip.
        /// </summary>
        [XmlIgnore]
        public string PowerRampSummary
        {
            get
            {
                if (_pitchPower == 0d && _pitchFillPower == 0d)
                    return "Every copy burns at the same power.";

                var parts = new System.Collections.Generic.List<string>();
                if (_pitchPower != 0d)
                    parts.Add(string.Format("outline {0:0} to {1:0}", Ramped(_power, _pitchPower, 0), Ramped(_power, _pitchPower, _copies - 1)));
                if (_fill && _pitchFillPower != 0d)
                    parts.Add(string.Format("shading {0:0} to {1:0}", Ramped(_fillPower, _pitchFillPower, 0), Ramped(_fillPower, _pitchFillPower, _copies - 1)));

                string s = "Across " + _copies + (_copies == 1 ? " copy: " : " copies: ") + string.Join(", ", parts.ToArray()) + ".";

                if (Clamps(_power, _pitchPower) || (_fill && Clamps(_fillPower, _pitchFillPower)))
                    s += string.Format("  CLAMPED at {0:0} - the last copies repeat the same power.", MaxPower);

                return s;
            }
        }

        /// <summary>Power for copy n, held inside 0..$30. The emitter uses the same rule.</summary>
        public double Ramped(double basePower, double pitch, int copy)
        {
            double v = basePower + pitch * copy;
            return v < 0d ? 0d : (MaxPower > 0d && v > MaxPower ? MaxPower : v);
        }

        private bool Clamps(double basePower, double pitch)
        {
            if (pitch == 0d || _copies < 2)
                return false;

            double last = basePower + pitch * (_copies - 1);
            return last < 0d || (MaxPower > 0d && last > MaxPower);
        }

        /// <summary>
        /// What the placement actually amounts to on the machine: where the far corner of the last copy
        /// lands. That is the number worth checking against the travel, and it is not obvious from four
        /// separate boxes - a pitch that looks modest becomes a reach that does not fit.
        /// </summary>
        public string PlacementSummary
        {
            get
            {
                double spanX = _originX + _width + (_copies - 1) * _pitchX;

                // Which way the artwork extends is the anchor's doing, and this summary is read to check
                // the job against travel - reporting +height on a machine that runs to -Y would name a
                // corner on the wrong side of the origin.
                double artH = _width * Aspect;
                double spanY = _originY + (_anchorBackLeft ? -artH : artH) + (_copies - 1) * _pitchY;

                if (_copies <= 1)
                    return string.Format("one copy, reaching X{0:0.#} Y{1:0.#}", spanX, spanY);

                return string.Format("{0} copies, reaching X{1:0.#} Y{2:0.#} - check that is within travel",
                                     _copies, spanX, spanY);
            }
        }

        /// <summary>
        /// Which corner to jog to before starting, spelled out. The dialog cannot show both conventions at
        /// once and this is the one instruction that ruins the material if it is followed wrongly, so it is
        /// derived from the setting rather than written as fixed text that is right half the time.
        /// </summary>
        public string AnchorSummary
        {
            get
            {
                return _anchorBackLeft
                    ? "Jog to the artwork's TOP-left corner: from there the job runs toward the front (-Y), which is where the table is on a back-left origin."
                    : "Jog to the artwork's LOWER-left corner: from there the job runs toward the back (+Y).";
            }
        }

        /// <summary>
        /// The SVG being imported. Transient: it names ONE import, where the rest of this section is a
        /// material recipe meant to outlive it, so persisting it would be remembering the wrong thing.
        /// </summary>
        [XmlIgnore]
        public string FilePath
        {
            get { return _filePath; }
            set
            {
                _filePath = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged("FileName");
            }
        }

        /// <summary>Just the file name - the dialog has no room for a path, and the path is the tooltip.</summary>
        [XmlIgnore]
        public string FileName
        {
            get
            {
                if (string.IsNullOrEmpty(_filePath))
                    return "(no file)";
                try { return Path.GetFileName(_filePath); }
                catch { return _filePath; }
            }
        }

        /// <summary>
        /// Whether the laser actually fires. Unticked is a dry run: identical motion at identical feeds,
        /// every S word zero and the laser never enabled, so the path can be watched against the work
        /// before any material is spent.
        ///
        /// NOT persisted, and it comes back ticked on every import. This is an intent about one job, not a
        /// setting about a material - and a "no burn" that quietly survived into a later session would show
        /// up as a job that ran perfectly and marked nothing, which is a confusing way to lose an hour.
        /// The state is stated in the pinned note and written into the .nc header so it is never a mystery
        /// WHILE it applies.
        /// </summary>
        [XmlIgnore]
        public bool BeamOn
        {
            get { return _beamOn; }
            set { _beamOn = value; OnPropertyChanged(); OnPropertyChanged("BeamSummary"); }
        }

        /// <summary>
        /// Said in both directions rather than only when disabled. A note that appears only in the unusual
        /// case is a note nobody has learned to look for; one that is always there is read.
        /// </summary>
        public string BeamSummary
        {
            get
            {
                return _beamOn
                    ? "Beam enabled - this job will burn."
                    : "BEAM DISABLED - the head follows the whole path but the laser never fires.";
            }
        }

        public string HeightSummary
        {
            get { return string.Format("{0:0.##} mm tall at this width", _width * Aspect); }
        }

        /// <summary>
        /// Power divided by feed - how much beam energy lands per mm travelled. This, not the S value on
        /// its own, is what decides whether wood browns or chars, and it is the number the two S fields
        /// in this dialog conspire to hide: an outline at S150/F1200 and a fill at S400/F3000 look like
        /// a large power increase and are in fact the same burn (0.125 vs 0.133). Raising fill power
        /// while the fill feed runs 2.5x faster buys nothing, which is not visible from the S values.
        ///
        /// The unit (S per mm/min) is arbitrary and only meaningful against itself - hence the ratio.
        /// </summary>
        [XmlIgnore]
        public double Exposure
        {
            get { return _feed > 0d ? _power / _feed : 0d; }
        }

        /// <summary>Same for the shading pass. Compare with <see cref="Exposure"/>, never in isolation.</summary>
        [XmlIgnore]
        public double FillExposure
        {
            get { return _fillFeed > 0d ? _fillPower / _fillFeed : 0d; }
        }

        /// <summary>
        /// Energy per unit AREA for the shading pass: power / (feed x interval).
        ///
        /// This, not FillExposure, is what sets how deep a fill cuts. A fill is not a line - it is a
        /// raster of lines a fixed distance apart, so halving the interval puts twice the energy into
        /// the same square millimetre while every number on the Burn tab stays where it was.
        ///
        /// Added after a fill at S600/F800 with a 0.1 mm interval removed nearly 7 mm of cedar. The line
        /// exposure said "8x the outline", which sounded like a strong engrave rather than a cut, and it
        /// was not wrong about lines - it simply did not have the interval in it at all. Below roughly a
        /// beam width the lines also overlap and reheat wood the previous pass already dried, so the real
        /// curve is steeper than this ratio suggests. Treat it as a relative figure, never a prediction.
        /// </summary>
        [XmlIgnore]
        public double FillArealExposure
        {
            get { return _fillFeed > 0d && _interval > 0d ? _fillPower / (_fillFeed * _interval) : 0d; }
        }

        public string ExposureSummary
        {
            get
            {
                return Exposure <= 0d
                    ? string.Empty
                    : string.Format("exposure {0:0.###} (S per mm/min) - the reference for shading", Exposure);
            }
        }

        /// <summary>
        /// The shading's burn stated against the outline's, because that is the comparison the operator
        /// is actually making and the one the raw numbers obscure. Deliberately says nothing about what
        /// ratio is "right" - that depends on material, wattage and focus, and is found with a test strip.
        /// </summary>
        public string FillExposureSummary
        {
            get
            {
                if (FillExposure <= 0d)
                    return string.Empty;

                if (Exposure <= 0d)
                    return string.Format("exposure {0:0.###} (S per mm/min)", FillExposure);

                double ratio = FillExposure / Exposure;

                // Areal first, because it is the one that decides depth, and with the interval named in
                // the same breath so it is obvious which field moves it.
                return string.Format("areal {0:0.##} per mm2 at {1:0.###} mm interval - line exposure {2:0.###}, {3:0.##}x the outline{4}",
                                     FillArealExposure, _interval, FillExposure, ratio,
                                     ratio <= 1d ? " (lighter than the edge it fills)" : string.Empty);
            }
        }

        public string PowerSummary
        {
            get { return string.Format("{0:0}% of full power (S{1:0} max)", MaxPower > 0d ? _power / MaxPower * 100d : 0d, MaxPower); }
        }

        public string LaserModeSummary
        {
            get
            {
                return LaserModeOn
                    ? "$32 laser mode is on - rapids travel dark and power scales with speed."
                    : "$32 laser mode is OFF. Rapids may travel LIT and corners will burn dark. Set $32=1 first.";
            }
        }
    }
}
