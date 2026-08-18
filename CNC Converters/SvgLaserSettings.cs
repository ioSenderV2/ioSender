/*
 * SvgLaserSettings.cs - part of the CNC Converters library
 *
 * What the operator chose for an SVG-to-laser conversion. See SvgToLaser for what is done with it.
 *
 * Persisted through the profile store the other converters use, so the numbers you arrived at for a
 * material are still there next time - power and feed are found by burning test strips, and having to
 * re-derive them every session is how a good setting gets lost.
 */

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CNC.Core;

namespace CNC.Converters
{
    public class SvgLaserSettings : INotifyPropertyChanged
    {
        private double _width = 100d, _power = 150d, _feed = 1200d, _travel = 3000d;
        private int _passes = 1;
        private bool _dynamic = true;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Artwork width in mm. Height follows from <see cref="Aspect"/>.</summary>
        public double WidthMm
        {
            get { return _width; }
            set { _width = value; OnPropertyChanged(); OnPropertyChanged("HeightSummary"); }
        }

        /// <summary>Height divided by width, from SvgOutlines.AspectOf. Set before the dialog opens.</summary>
        public double Aspect { get; set; } = 1d;

        /// <summary>Full-power S value, from $30. Shown so the power below has a scale to mean anything against.</summary>
        public double MaxPower { get; set; } = 1000d;

        /// <summary>Whether $32 is on. Drives the warning - a rapid travels LIT without it.</summary>
        public bool LaserModeOn { get; set; } = true;

        public double Power
        {
            get { return _power; }
            set { _power = value; OnPropertyChanged(); OnPropertyChanged("PowerSummary"); }
        }

        public double Feed
        {
            get { return _feed; }
            set { _feed = value; OnPropertyChanged(); }
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

        public string HeightSummary
        {
            get { return string.Format("{0:0.##} mm tall at this width", _width * Aspect); }
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
