/*
 * SignalsControl.xaml.cs - part of CNC Controls library
 *
 * v0.14 / 2020-03-16 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2020, Io Engineering (Terje Io)
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CNC.Controls
{
    public partial class SignalControl : UserControl
    {
        static readonly Brush LabelSet = Brushes.Black;
        Brush LabelOff, ShapeOff;

        // Most signals are alarm/limit-style (red = "triggered", a thing to notice/worry about) - the
        // default. A handful (e.g. TLO ref'd) are the OPPOSITE: set = good/ready, so red would read
        // backwards. Per-instance override, defaulting to the historical red for every existing caller.
        public static readonly DependencyProperty SetColorProperty = DependencyProperty.Register(nameof(SetColor), typeof(Brush), typeof(SignalControl), new PropertyMetadata(Brushes.Red));
        public Brush SetColor
        {
            get { return (Brush)GetValue(SetColorProperty); }
            set { SetValue(SetColorProperty, value); }
        }

        public SignalControl()
        {
            InitializeComponent();

            btnLED.Visibility = Visibility.Collapsed;   // replaced by the outlined box around the letter
            LabelOff = txtSignal.Foreground;
            ShapeOff = shpSignal.Fill;               // Transparent when clear
        }

        // The lit fill: the set colour at low alpha, so the box reads as lit without the letter having to
        // sit on a solid field. Derived from SetColor rather than being a second constant, so a per-instance
        // colour (green for the TLO reference lamp) tints to match instead of reverting to the default.
        private Brush TintOf(Brush b)
        {
            var solid = b as SolidColorBrush;
            if (solid == null)
                return Brushes.Transparent;

            var c = solid.Color;
            var tint = new SolidColorBrush(Color.FromArgb(48, c.R, c.G, c.B));
            tint.Freeze();
            return tint;
        }

        public static readonly DependencyProperty IsSetProperty = DependencyProperty.Register(nameof(IsSet), typeof(bool), typeof(SignalControl), new PropertyMetadata(false, new PropertyChangedCallback(OnIsSetChanged)));
        public bool IsSet
        {
            get { return (bool)GetValue(IsSetProperty); }
            set { SetValue(IsSetProperty, value); }
        }
        private static void OnIsSetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = d as SignalControl;
            bool set = (bool)e.NewValue;
            c.shpSignal.Stroke = set ? c.SetColor : Brushes.Transparent;   // coloured box around a set signal
            c.shpSignal.Fill = set ? c.TintOf(c.SetColor) : c.ShapeOff;    // ...with a light tint inside it
            c.txtSignal.Foreground = set ? LabelSet : c.LabelOff;    // bold black letter on top when set
            c.txtSignal.FontWeight = set ? FontWeights.Bold : FontWeights.Normal;
        }

        // Width of the lamp box. 19 (a square) for a single letter; wider for a label that spells
        // something - the TLO reference lamp. The Viewbox shrinks the text to whatever this allows.
        public static readonly DependencyProperty BoxWidthProperty = DependencyProperty.Register(
            nameof(BoxWidth), typeof(double), typeof(SignalControl), new PropertyMetadata(19d, OnBoxWidthChanged));

        public double BoxWidth
        {
            get { return (double)GetValue(BoxWidthProperty); }
            set { SetValue(BoxWidthProperty, value); }
        }

        private static void OnBoxWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (SignalControl)d;
            double w = (double)e.NewValue;
            c.shpSignal.Width = w;
            c.boxSignal.Width = w + 1;   // the grid carried 1px more than the shape before BoxWidth existed
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SignalControl), new PropertyMetadata());
        public string Label
        {
            get { return (string)GetValue(LabelProperty); }
            set { SetValue(LabelProperty, value); }
        }
    }
}

