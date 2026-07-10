/*
 * NumericField.xaml.cs - part of CNC Controls library
 *
 * v0.43 / 2023-06-28 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2023, Io Engineering (Terje Io)
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
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for NumericField.xaml
    /// </summary>
    public partial class NumericField : UserControl
    {
        protected string format;
        protected bool metric = true, allow_dp = true, allow_sign = false;
        protected int precision = 3;

        public NumericField()
        {
            InitializeComponent();

            data.DataContext = this;
        }

        // Without a custom peer, UI Automation (and so the WPF test server) sees only the base
        // UserControl - no Value pattern - even though Value is a perfectly normal DependencyProperty.
        // This is what makes every Settings NumericField scriptable via /set/{uid}?value=.
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new NumericFieldAutomationPeer(this);
        }

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumericField), new PropertyMetadata(double.NaN, new PropertyChangedCallback(OnValueChanged)), new ValidateValueCallback(IsValidReading));
        public double Value
        {
            get { double v = (double)GetValue(ValueProperty); return double.IsNaN(v) ? 0d : v; }
            set { SetValue(ValueProperty, value); }
        }
        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (double.IsNaN((double)e.NewValue))
                ((NumericField)d).data.Clear();
        }

        public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(string), typeof(NumericField), new PropertyMetadata(NumericProperties.MetricFormat));
        public string Format
        {
            get { return (string)GetValue(FormatProperty); }
            set { SetValue(FormatProperty, value); }
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(NumericField), new PropertyMetadata("Label:"));
        public string Label
        {
            get { return (string)GetValue(LabelProperty); }
            set { SetValue(LabelProperty, value); }
        }

        public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumericField), new PropertyMetadata("mm"));
        public string Unit
        {
            get { return (string)GetValue(UnitProperty); }
            set { SetValue(UnitProperty, value); }
        }

        public static readonly DependencyProperty Tooltip2Property = DependencyProperty.Register(nameof(Tooltip2), typeof(string), typeof(NumericField), new PropertyMetadata(string.Empty));
        public string Tooltip2
        {
            get { return (string)GetValue(Tooltip2Property); }
            set { SetValue(Tooltip2Property, value); }
        }

        public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(NumericField), new PropertyMetadata());
        public bool IsReadOnly
        {
            get { return (bool)GetValue(IsReadOnlyProperty); }
            set { SetValue(IsReadOnlyProperty, value); }
        }

        public static readonly DependencyProperty ColonAtProperty = DependencyProperty.Register(nameof(ColonAt), typeof(double), typeof(NumericField), new PropertyMetadata(70.0d, new PropertyChangedCallback(OnColonAtChanged)));
        public double ColonAt
        {
            get { return (double)GetValue(ColonAtProperty); }
            set { SetValue(ColonAtProperty, value); }
        }
        private static void OnColonAtChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((NumericField)d).OnColonAtChanged();
        }
        private void OnColonAtChanged()
        {
            // ColonAt is the minimum label-column width; the column itself is Auto so the label can
            // grow with the font instead of being truncated at high DPI / large text sizes.
            grid.ColumnDefinitions[0].MinWidth = ColonAt;
        }

        public string Text
        {
            get { return Value.ToInvariantString(data.DisplayFormat); }
        }

        public static bool IsValidReading(object value)
        {
            double v = (double)value;
            return (!v.Equals(double.PositiveInfinity));
        }
                   
        public Control Field { get { return data; } }

        public void Clear()
        {
            data.Clear();
        }

        // Factory-default Config instance; the field's Value binding path is resolved against it
        // to find a settings field's default value. Non-settings fields don't resolve -> no reset.
        private static readonly Config defaultConfig = new Config();

        private object GetDefaultValue()
        {
            var be = GetBindingExpression(ValueProperty);
            string path = be?.ParentBinding?.Path?.Path;
            if (string.IsNullOrEmpty(path))
                return null;

            object cur = defaultConfig;
            foreach (var part in path.Split('.'))
            {
                if (cur == null)
                    return null;
                var pi = cur.GetType().GetProperty(part);
                if (pi == null || pi.GetIndexParameters().Length > 0)
                    return null;
                cur = pi.GetValue(cur);
            }
            return cur is double || cur is int || cur is float || cur is decimal ? cur : null;
        }

        private void Data_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // Only offer "Reset to default" for fields backed by a Config setting.
            miReset.IsEnabled = !IsReadOnly && GetDefaultValue() != null;
        }

        private void ResetToDefault_Click(object sender, RoutedEventArgs e)
        {
            var def = GetDefaultValue();
            if (def != null)
                Value = Convert.ToDouble(def);
        }
    }

    // Exposes NumericField.Value to UI Automation as a standard Value pattern, so it can be read/set
    // like any built-in editable control - by a screen reader, or by the WPF test server's /set/{uid}.
    public class NumericFieldAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
    {
        public NumericFieldAutomationPeer(NumericField owner) : base(owner) { }

        private NumericField Field { get { return (NumericField)Owner; } }

        protected override string GetClassNameCore() { return "NumericField"; }
        protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Edit; }

        // Implementing IValueProvider on the class is not enough - GetPattern is WPF's separate dispatch
        // for "which interfaces do you actually support"; without this override it's never consulted and
        // callers (screen readers, the test server) see a peer with no patterns at all.
        public override object GetPattern(PatternInterface patternInterface)
        {
            if (patternInterface == PatternInterface.Value)
                return this;
            return base.GetPattern(patternInterface);
        }

        public bool IsReadOnly { get { return Field.IsReadOnly; } }

        public string Value { get { return Field.Value.ToString(CultureInfo.InvariantCulture); } }

        public void SetValue(string value)
        {
            double d;
            if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                return;
            Field.Value = d;

            // Most of these fields bind Value with UpdateSourceTrigger=LostFocus (commit-on-tab-away, not
            // every keystroke) - correct for real typing, but automation never causes a real focus/blur
            // cycle, so the change would otherwise sit on the DependencyProperty without ever reaching the
            // bound Config property. Push it through explicitly so a scripted SetValue behaves like a real
            // edit-then-tab-away, regardless of the binding's trigger mode.
            BindingExpression be = Field.GetBindingExpression(NumericField.ValueProperty);
            be?.UpdateSource();
        }
    }
}

