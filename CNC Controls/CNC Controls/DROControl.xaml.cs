/*
 * DROControl.xaml.cs - part of CNC Controls library
 *
 * v0.47 / 2026-01-16 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2026, Io Engineering (Terje Io)
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
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public partial class DROControl : UserControl
    {
        private double orgpos;
        private bool hasFocus = false;
        private Brush background = null;
        private static bool keyboardMappingsOk = false;

        public string DisplayFormat { get; private set; }

        public delegate void DROEnabledChangedHandler(bool enabled);
        public event DROEnabledChangedHandler DROEnabledChanged;

        public DROControl()
        {
            InitializeComponent();

            foreach (DROBaseControl axis in UIUtils.FindLogicalChildren<DROBaseControl>(this))
            {
                axis.txtReadout.GotFocus += txtReadout_GotFocus;
                axis.txtReadout.LostFocus += txtReadout_LostFocus;
                axis.txtReadout.PreviewKeyDown += txtReadout_PreviewKeyDown;
                axis.txtReadout.PreviewKeyUp += txtReadout_PreviewKeyUp;
                axis.btnZero.Click += btnZero_Click;
            }
        }

        /// <summary>
        /// Build the work-offset menu as it opens, so it shows what the controller reports NOW and which
        /// offset is active NOW - both change under this control while it is on screen.
        ///
        /// Lives on DROControl itself, which is why it works in both places the DRO appears: the Job tab's
        /// panel and the run strip's scaled copy are two instances of this one control, not two controls.
        /// </summary>
        private void DroWcs_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            BuildWcsMenu((sender as FrameworkElement)?.ContextMenu);
        }

        /// <summary>
        /// Left-click opens the same list. The title reads "DRO (G54)" with a chevron after it, so it
        /// looks like what it is - a dropdown naming the current work offset - rather than relying on
        /// someone guessing that a right-click does something. Right-click still works; it costs nothing
        /// to leave it.
        /// </summary>
        private void DroWcs_Click(object sender, MouseButtonEventArgs e)
        {
            var panel = sender as FrameworkElement;
            var menu = panel?.ContextMenu;
            if (menu == null)
                return;

            BuildWcsMenu(menu);
            menu.PlacementTarget = panel;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void BuildWcsMenu(ContextMenu menu)
        {
            var model = DataContext as GrblViewModel;
            if (menu == null)
                return;

            menu.Items.Clear();

            if (model == null || model.CoordinateSystems == null || model.CoordinateSystems.Count == 0)
            {
                // An empty menu appears as a stray grey sliver with no explanation. Say why instead.
                menu.Items.Add(new MenuItem { Header = "No work offsets reported yet", IsEnabled = false });
                return;
            }

            string active = model.WorkCoordinateSystem;

            // CoordinateSystem.IsSelectableWcs, not a list kept here: the same question is asked by the
            // Work Parameters offset picker, and two copies of "which of these is selectable" would be two
            // chances to let G28 into a list of offsets - where choosing it moves the machine.
            foreach (var cs in model.CoordinateSystems.Where(c => c.IsSelectableWcs))
            {
                var item = new MenuItem
                {
                    Header = cs.Code,
                    IsCheckable = true,
                    IsChecked = cs.Code == active,
                    // What is IN that offset, so the choice can be made without first selecting it and
                    // looking at the DRO - which would mean changing the machine's state to read a number.
                    ToolTip = DescribeOffset(cs, model)
                };
                item.Click += WcsMenuItem_Click;
                menu.Items.Add(item);
            }
        }

        /// <summary>The offset's own values, as the operator would need to see them to choose between them.</summary>
        private static string DescribeOffset(CoordinateSystem cs, GrblViewModel model)
        {
            string fmt = model.Format;
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < GrblInfo.NumAxes && i < cs.Values.Length; i++)
            {
                if (sb.Length > 0)
                    sb.Append("   ");
                sb.Append(GrblInfo.AxisLetters.Substring(i, 1)).Append(' ')
                  .Append(double.IsNaN(cs.Values[i]) ? "?" : cs.Values[i].ToString(fmt));
            }

            // Rotation, from the coordinate system's OWN Rotation - not from any R word elsewhere in the
            // parameter report, where R is a tool radius. Shown always rather than only when non-zero: on a
            // rotated WCS it is the one number that explains why a move went somewhere unexpected, and its
            // absence would read as "no rotation" rather than "not shown".
            sb.Append("\r\nR ").Append(cs.Rotation.ToString("0.###"));

            return sb.ToString();
        }

        private void WcsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var code = (sender as MenuItem)?.Header as string;
            var model = DataContext as GrblViewModel;
            if (model != null && !string.IsNullOrEmpty(code))
                model.ExecuteCommand(code);
        }

        public new bool IsFocused { get { return hasFocus; } }
        public bool IsFocusable { get; set; }

        public void EnableFocus()
        {
            IsFocusable = true;
        }

        private void DRO_Loaded(object sender, RoutedEventArgs e)
        {
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
                return;

            if (!keyboardMappingsOk && (DataContext as GrblViewModel)?.Keyboard is KeypressHandler keyboard)
            {
                keyboardMappingsOk = true;

                keyboard.AddHandler(Key.X, ModifierKeys.Control | ModifierKeys.Shift, ZeroX);
                keyboard.AddHandler(Key.Y, ModifierKeys.Control | ModifierKeys.Shift, ZeroY);
                keyboard.AddHandler(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, ZeroZ);
                if (GrblInfo.AxisFlags.HasFlag(AxisFlags.A))
                    keyboard.AddHandler(Key.A, ModifierKeys.Control | ModifierKeys.Shift, ZeroA);
                if (GrblInfo.AxisFlags.HasFlag(AxisFlags.B))
                    keyboard.AddHandler(Key.B, ModifierKeys.Control | ModifierKeys.Shift, ZeroB);
                if (GrblInfo.AxisFlags.HasFlag(AxisFlags.C))
                    keyboard.AddHandler(Key.C, ModifierKeys.Control | ModifierKeys.Shift, ZeroC);
                if (GrblInfo.AxisFlags.HasFlag(AxisFlags.U))
                    keyboard.AddFunction(ZeroU, null);
                if (GrblInfo.AxisFlags.HasFlag(AxisFlags.V))
                    keyboard.AddFunction(ZeroV, null);
                if (GrblInfo.AxisFlags.HasFlag(AxisFlags.W))
                    keyboard.AddFunction(ZeroW, null);
                keyboard.AddHandler(Key.D0, ModifierKeys.Control | ModifierKeys.Shift, ZeroAxes);
            }

            foreach (DROBaseControl axis in UIUtils.FindLogicalChildren<DROBaseControl>(this))
                axis.Tag = GrblInfo.AxisLetterToIndex(axis.Label);
        }

        private void txtReadout_GotFocus(object sender, RoutedEventArgs e)
        {
            if (IsFocusable && !(DataContext as GrblViewModel).IsJobRunning)
            {
                (DataContext as GrblViewModel).SuspendPositionNotifications = true;

                orgpos = (DataContext as GrblViewModel).Position.Values[(int)((NumericTextBox)(sender)).Tag];

                background = (sender as NumericTextBox).Background;
                (sender as NumericTextBox).IsReadOnly = false;
                (sender as NumericTextBox).Background = Brushes.White;

                hasFocus = true;

                DROEnabledChanged?.Invoke(true);
            }
        }

        void txtReadout_LostFocus(object sender, EventArgs e)
        {
            ((NumericTextBox)(sender)).IsReadOnly = true;

            (DataContext as GrblViewModel).SuspendPositionNotifications = false;

            if (hasFocus)
            {
                (sender as NumericTextBox).Background = background;
                (DataContext as GrblViewModel).Position.Values[(int)(sender as NumericTextBox).Tag] = orgpos;
            }

            hasFocus = false;

            DROEnabledChanged?.Invoke(false);
        }

        private void txtReadout_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!hasFocus)
                e.Handled = true;
        }

        private void txtReadout_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                NumericTextBox axis = (NumericTextBox)sender;

                if (axis.Value != orgpos)
                    AxisPositionChanged(GrblInfo.AxisIndexToLetter((int)axis.Tag), axis.Value);

                axis.IsReadOnly = true;

                DROEnabledChanged?.Invoke(false);
            }
        }

        void btnZero_Click(object sender, EventArgs e)
        {
            AxisPositionChanged(GrblInfo.AxisIndexToLetter((int)(sender as Button).Tag), 0.0d);
        }

        void btnZeroAll_Click(object sender, EventArgs e)
        {
            AxisPositionChanged("ALL", 0.0d);
        }

        private bool ZeroAxes(Key key)
        {
            AxisPositionChanged("ALL", 0d);

            return true;
        }

        private bool ZeroX(Key key)
        {
            AxisPositionChanged("X", 0d);

            return true;
        }

        private bool ZeroY(Key key)
        {
            AxisPositionChanged("Y", 0d);

            return true;
        }
        private bool ZeroZ(Key key)
        {
            AxisPositionChanged("Z", 0d);

            return true;
        }
        private bool ZeroA(Key key)
        {
            AxisPositionChanged(GrblInfo.AxisIndexToLetter(3), 0d);

            return true;
        }
        private bool ZeroB(Key key)
        {
            AxisPositionChanged(GrblInfo.AxisIndexToLetter(4), 0d);

            return true;
        }
        private bool ZeroC(Key key)
        {
            AxisPositionChanged(GrblInfo.AxisIndexToLetter(5), 0d);

            return true;
        }
        private bool ZeroU(Key key)
        {
            AxisPositionChanged(GrblInfo.AxisIndexToLetter(6), 0d);

            return true;
        }
        private bool ZeroV(Key key)
        {
            AxisPositionChanged(GrblInfo.AxisIndexToLetter(7), 0d);

            return true;
        }
        private bool ZeroW(Key key)
        {
            AxisPositionChanged(GrblInfo.AxisIndexToLetter(8), 0d);

            return true;
        }

        void AxisPositionChanged(string axis, double position)
        {
            if (GrblParserState.IsMetric != (DataContext as GrblViewModel).IsMetric)
            {
                if(GrblParserState.IsMetric)
                    position *= MeasureViewModel.MM_PER_INCH;
                else
                    position /= MeasureViewModel.MM_PER_INCH;
            }

            if (axis == "ALL")
            {
                string s = "G90G10L20P0";
                foreach (int i in GrblInfo.AxisFlags.ToIndices())
                    s += GrblInfo.AxisIndexToLetter(i) + "{0}";
                (DataContext as GrblViewModel).ExecuteCommand(string.Format(s, position.ToInvariantString(GrblParserState.IsMetric ? "F3" : "F4")));
            }
            else
                (DataContext as GrblViewModel).ExecuteCommand(string.Format("G10L20P0{0}{1}", axis, position.ToInvariantString(GrblParserState.IsMetric ? "F3" : "F4")));
        }
    }
}
