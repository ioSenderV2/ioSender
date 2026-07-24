/*
 * LimitsBaseControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.01 / 2019-10-21 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for LimitsBaseControl.xaml
    /// </summary>
    public partial class LimitsBaseControl : UserControl
    {
        public LimitsBaseControl()
        {
            InitializeComponent();

            UpdateDisplay();

            // IsImperial is NumericField's inherited attached property (set at the panel's GroupBox by
            // UnitToggleMenu's right-click handler) - there's no NumericField instance left in this control
            // to pick the change up automatically, so listen for it directly the same way any inherited
            // attached DP is observed from outside its own defining type.
            DependencyPropertyDescriptor
                .FromProperty(NumericField.IsImperialProperty, typeof(LimitsBaseControl))
                .AddValueChanged(this, (s, e) => UpdateDisplay());

            //UnitProperty.OverrideMetadata(typeof(string), new PropertyMetadata("mm"));
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(LimitsBaseControl), new PropertyMetadata());
        public string Label
        {
            get { return (string)GetValue(LabelProperty); }
            set { SetValue(LabelProperty, value); }
        }

        public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LimitsBaseControl), new PropertyMetadata("mm"));
        public string Unit
        {
            get { return (string)GetValue(UnitProperty); }
            set { SetValue(UnitProperty, value); }
        }

        public static readonly DependencyProperty MinValueProperty = DependencyProperty.Register(nameof(MinValue), typeof(double), typeof(LimitsBaseControl), new PropertyMetadata(0d, OnRangeChanged));
        public double MinValue
        {
            get { return (double)GetValue(MinValueProperty); }
            set { SetValue(MinValueProperty, value); }
        }

        // Getter used to read MinValueProperty instead of MaxValueProperty - always echoed MinValue back,
        // silently, to any code reading control.MaxValue directly (data binding to the DP itself was
        // unaffected, since WPF's binding engine reads DPs directly rather than through this CLR wrapper).
        public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(LimitsBaseControl), new PropertyMetadata(0d, OnRangeChanged));
        public double MaxValue
        {
            get { return (double)GetValue(MaxValueProperty); }
            set { SetValue(MaxValueProperty, value); }
        }

        private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LimitsBaseControl)d).UpdateDisplay();
        }

        // Builds the whole "[0 .. 860) mm" string by hand - MinValue/MaxValue are always canonical mm
        // (same convention NumericField/NumericProperties use), converted here via
        // NumericProperties.FromCanonicalMm per the inherited IsImperial flag. Trailing zeros dropped via
        // "0.###"/"0.####" (one more digit for inches, matching NumericField's own metric/imperial
        // precision split). Bracket choice: closed "[" / "]" on whichever end sits exactly at 0 (home -
        // always exactly reachable), open "(" / ")" on the far/travel end (a soft-limit boundary rarely
        // reached exactly) - both closed when neither end is 0 (Program limits' bounding box has no "home"
        // concept to hang the convention on).
        private void UpdateDisplay()
        {
            double min = MinValue, max = MaxValue;
            bool isLength = NumericProperties.IsLengthUnit(Unit);
            string unit = isLength ? (NumericField.GetIsImperial(this) ? "in" : "mm") : Unit;
            string format = unit == "in" ? "0.####" : "0.###";

            double dmin = NumericProperties.FromCanonicalMm(min, unit);
            double dmax = NumericProperties.FromCanonicalMm(max, unit);

            string open, close;
            if (min == 0d && max != 0d)
            {
                open = "[";
                close = ")";
            }
            else if (max == 0d && min != 0d)
            {
                open = "(";
                close = "]";
            }
            else
            {
                open = "[";
                close = "]";
            }

            range.Text = string.Format(CultureInfo.InvariantCulture, "{0}{1} .. {2}{3} {4}",
                open, dmin.ToString(format, CultureInfo.InvariantCulture), dmax.ToString(format, CultureInfo.InvariantCulture), close, unit);
        }
    }
}
