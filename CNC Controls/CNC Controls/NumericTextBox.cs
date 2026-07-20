/*
 * NumericTextBox.cs - part of CNC Controls library
 *
 * v0.45 / 2024-01-09 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2024, Io Engineering (Terje Io)
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

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CNC.Controls
{
    public class NumericTextBox : TextBox
    {
        private NumericProperties np = new NumericProperties();

        private bool updateText = true;

        public NumericTextBox()
        {
            MinHeight = 24;   // MinHeight, not Height, so the box grows with the font at high text scaling instead of clipping
            HorizontalContentAlignment = HorizontalAlignment.Right;
            VerticalContentAlignment = VerticalAlignment.Bottom;
            TextWrapping = TextWrapping.NoWrap;
            if (Format == NumericProperties.MetricFormat)
                NumericProperties.OnFormatChanged(this, np, Format);
        }

        // Select the whole value the first time the field gains focus (tab or click) so the user can
        // just type the replacement instead of clearing the old value first. We must NOT swallow the
        // focusing click - that would break native caret placement on later clicks. Instead:
        //  - keyboard/tab focus: SelectAll right away in OnGotKeyboardFocus;
        //  - click focus: let WPF place the caret natively, then SelectAll on the mouse-up (only for a
        //    plain click, so a drag-select still keeps the user's partial selection).
        // Once the box already has focus, every click falls through untouched -> caret lands where the
        // user clicks, so single-digit edits work as expected.
        private bool selectAllOnMouseUp;

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            if (Mouse.LeftButton != MouseButtonState.Pressed)
                SelectAll();
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (!IsKeyboardFocusWithin)
                selectAllOnMouseUp = true;
            base.OnPreviewMouseLeftButtonDown(e);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            if (selectAllOnMouseUp)
            {
                selectAllOnMouseUp = false;
                if (SelectionLength == 0)   // plain click (not a drag-select) -> select the whole value
                    SelectAll();
            }
        }

        public new string Text { get { return base.Text; } set { base.Text = value; } }
        public NumberStyles Styles { get { return np.Styles; } }
        public string DisplayFormat { get { return np.DisplayFormat; } }

        // Canonical mm always (see NumericProperties.ToCanonicalMm/FromCanonicalMm) when Unit is a length
        // unit ("mm"/"in") - the displayed Text is a UNIT CONVERSION of Value, never Value itself, so an
        // ancestor's IsImperial toggle (NumericField.EffectiveUnit -> this Unit) can reformat the display
        // without any external code touching Value. Non-length units (the vast majority of existing
        // fields) are a no-op conversion, so this is behaviorally identical to before for them.
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumericTextBox), new PropertyMetadata(double.NaN, new PropertyChangedCallback(OnValueChanged)));
        public double Value
        {
            get { double v = (double)GetValue(ValueProperty); return double.IsNaN(v) ? 0d : v; }
            set { SetValue(ValueProperty, value); }
        }
        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ntb = (NumericTextBox)d;
            if (ntb.updateText)
                ntb.Text = double.IsNaN((double)e.NewValue) || double.IsNegativeInfinity((double)e.NewValue) ? string.Empty : FormatValue((double)e.NewValue, ntb.Unit, ntb.np);
        }

        // Display/parse unit ("mm", "in", or any pass-through non-length unit - see IsLengthUnit). Bound
        // from NumericField.EffectiveUnit; defaults to "mm" so a NumericTextBox used standalone (outside
        // NumericField) behaves exactly as before.
        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumericTextBox), new PropertyMetadata("mm", OnUnitChanged));
        public string Unit
        {
            get { return (string)GetValue(UnitProperty); }
            set { SetValue(UnitProperty, value); }
        }
        private static void OnUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // Reformat the DISPLAY only - Value (canonical mm) is untouched. This is the whole mechanism
            // that lets a parent's mm/in toggle flip every field's shown number with zero external code.
            var ntb = (NumericTextBox)d;
            ntb.Text = FormatValue(ntb.Value, (string)e.NewValue, ntb.np);
        }

        private static string FormatValue(double mm, string unit, NumericProperties np)
        {
            return Math.Round(NumericProperties.FromCanonicalMm(mm, unit), np.Precision).ToString(np.DisplayFormat, CultureInfo.InvariantCulture);
        }
        //        public static bool CoerceValueChanged(DependencyObject d, object value)
        //        {
        //            double v = (double)value;
        //            NumericTextBox ntb = (NumericTextBox)d;
        //            return get { return (double.IsNaN(ValueMin) || Value >= ValueMin) && (double.IsNaN(ValueMax) || Value <= ValueMax); }
        //;
        //        }

        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register(nameof(Format), typeof(string), typeof(NumericTextBox), new PropertyMetadata(NumericProperties.MetricFormat, new PropertyChangedCallback(OnFormatChanged)));
        public string Format
        {
            get { return (string)GetValue(FormatProperty); }
            set { SetValue(FormatProperty, value); }
        }
        private static void OnFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            NumericProperties.OnFormatChanged(d, ((NumericTextBox)d).np, (string)e.NewValue);
        }

        public new void Clear()
        {
            updateText = false;
            Value = double.NaN;
            updateText = true;
            base.Text = string.Empty;
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);

            // Length units (mm/in) commit on blur/Enter instead (CommitLengthText) - see OnPreviewTextInput's
            // comment for why live per-keystroke parsing doesn't work once unit-suffix letters are allowed.
            if (NumericProperties.IsLengthUnit(Unit))
                return;

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                string text = SelectionLength > 0 ? Text.Remove(SelectionStart, SelectionLength) : Text;

                updateText = false;
                Value = double.Parse(text == string.Empty || text == "." ? "0" : (text == "-" || text == "-." ? "-0" : text), np.Styles, CultureInfo.InvariantCulture);
                updateText = true;
            }
        }

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            if (NumericProperties.IsLengthUnit(Unit))
            {
                // Free typing: digits/'.'/'-'/'"'/letters all accepted live (so "12.5mm" can be typed at
                // all - rejecting "m" the instant it's typed the way the strict path below does would make
                // that impossible) - only clearly-invalid characters are blocked. The real parse, including
                // interpreting a trailing unit suffix, happens on commit (CommitLengthText - OnLostFocus/
                // Enter), not per keystroke.
                char c = e.Text.Length > 0 ? e.Text[0] : '\0';
                e.Handled = !(char.IsDigit(c) || c == '.' || c == '-' || c == '"' || char.IsLetter(c));
                base.OnPreviewTextInput(e);
                return;
            }

            TextBox textBox = (TextBox)e.OriginalSource;
            string text = textBox.SelectionLength > 0 ? textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength) : textBox.Text;
            text = text.Insert(textBox.CaretIndex, e.Text);
            if (!(e.Handled = !NumericProperties.IsStringNumeric(text, np)))
            {
                updateText = false;
                Value = double.Parse(text == "" || text == "." ? "0" : (text == "-" || text == "-." ? "-0" : text), np.Styles, CultureInfo.InvariantCulture);
                updateText = true;
            }

            base.OnPreviewTextInput(e);
        }

        protected override void OnTextChanged(TextChangedEventArgs e)
        {
            if (NumericProperties.IsLengthUnit(Unit))
            {
                base.OnTextChanged(e);   // no live parse/sync - CommitLengthText (OnLostFocus/Enter) does it
                return;
            }

            double val = 0d;
            if (double.TryParse(Text == string.Empty ? "NaN" : Text, np.Styles, CultureInfo.InvariantCulture, out val))
            {
                if (!IsReadOnly && IsEnabled)
                {
                    updateText = false;
                    Value = val;
                    updateText = true;
                }

                base.OnTextChanged(e);
            }
            else if(Text == string.Empty || Text == ".")
            {
                updateText = false;
                Value = 0d;
                updateText = true;
            }
            else if (Text == "-" || Text == "-.")
            {
                updateText = false;
                Value = -0d;
                updateText = true;
            }
            else
                Text = Math.Round(Value, (np.Precision)).ToString(np.DisplayFormat, CultureInfo.InvariantCulture);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter && NumericProperties.IsLengthUnit(Unit))
                CommitLengthText();
            base.OnPreviewKeyDown(e);
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            if (NumericProperties.IsLengthUnit(Unit))
                CommitLengthText();
            base.OnLostFocus(e);
        }

        // Parses the currently-typed text (a number, optionally with a trailing mm/in/" unit suffix) into
        // canonical mm and commits it to Value, then reformats Text cleanly in the field's OWN unit -
        // dropping whatever suffix was typed (a field always displays in its own unit; typing "1in" into a
        // mm-displaying field stores/shows 25.4, it does not switch the field to inches). An unparseable
        // entry is discarded - Text reverts to the last good Value, the same fallback the old per-keystroke
        // path used (OnTextChanged's final `else` branch).
        private void CommitLengthText()
        {
            if (NumericProperties.TryParseLength(Text, Unit, out double mm))
            {
                updateText = false;
                Value = mm;
                updateText = true;
            }
            Text = FormatValue(Value, Unit, np);
        }
    }
}
