/*
 * SidebarItem.cs - part of CNC Controls library for Grbl
 *
 * v0.36 / 2021-12-27 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020-2021, Io Engineering (Terje Io)
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
    public class SidebarItem : Button
    {
        private static double top = 0;
        private UserControl view { get; }
        private static UserControl last = null;

        public new Visibility Visibility { get { return view.Visibility; } set { view.Visibility = value; } }
        public new bool IsEnabled { get { return base.IsEnabled; } set { base.IsEnabled = value; } }

        public SidebarItem(ISidebarControl view) : base()
        {
            if (view.MenuLabel.Contains("_"))
                Content = new AccessText()
                {
                    Text = view.MenuLabel
                };
            else
                Content = view.MenuLabel;

            this.view = view as UserControl;

            // Size the tab to its label (text length + pad) so labels aren't clipped, with a gap between
            // tabs, and floor the flyout's open height to the tab length so each flyout matches its tab;
            // the flyout content then distributes within that height. The tab height is also floored to
            // minTab so a short-label flyout (e.g. an offset "G28") is still tall enough to hold its
            // content plus the inside padding - otherwise it would render past its slot and the next
            // flyout, drawn on top, would cover its bottom padding.
            const double pad = 14, gap = 0, minTab = 46;
            Width = System.Math.Max(MeasureLabel(view.MenuLabel) + pad, minTab);
            Height = 25;
            Focusable = false;
            Margin = new Thickness(0, gap / 2, 0, gap / 2);
            if (this.view != null)
                this.view.MinHeight = Width;

            Canvas.SetTop(this.view, top + gap / 2);
            top += Width + gap;

            try
            {
                Style = Application.Current.FindResource("btnSidebar") as Style;
            }
            catch { }

            LayoutTransform = new RotateTransform(90d);

            Click += button_Click;
        }

        public void PerformClick()
        {
            button_Click(this, null);
        }

        // Rendered length of the (un-rotated) label text - becomes the tab's on-screen height once rotated.
        private static double MeasureLabel(string text)
        {
            text = (text ?? string.Empty).Replace("_", string.Empty);
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14.0, Brushes.Black, 1.0);
            return ft.Width;
        }

        private void button_Click(object sender, RoutedEventArgs e)
        {
            if (last != null && last != view && last.IsVisible && !(last is IPinnableFlyout pinned && pinned.Pinned))
                last.Visibility = Visibility.Hidden;

            view.Visibility = view.IsVisible ? Visibility.Hidden : Visibility.Visible;
            last = view;
        }
    }
}
