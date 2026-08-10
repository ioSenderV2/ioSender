/*
 * ConsoleWindow.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.27 / 2020-09-19 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020, Io Engineering (Terje Io)
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
using System.Windows;

namespace CNC.Controls
{
    public partial class ConsoleWindow : Window
    {
        private bool userClosing = true;

        public ConsoleWindow()
        {
            InitializeComponent();
            RestorePlacement();
        }

        public new void Close()
        {
            userClosing = false;
            base.Close();
        }

        /// <summary>
        /// Esc hides the console, the same way every other floating window here closes on Esc.
        ///
        /// It used to happen for free, because Esc WAS the global console toggle - so when that moved to F12
        /// (2026-08-03, Esc being swallowed app-wide was costing it its real job) this window lost its close
        /// key and needed its own handler.
        ///
        /// Deliberately the BUBBLING KeyDown, not PreviewKeyDown: preview tunnels from the root down, so it
        /// would beat the command box's own Esc-clears-the-line binding. Bubbling means the input gets first
        /// refusal and this only runs when nothing else wanted the key.
        ///
        /// Hide(), not Close(): matches the F12 toggle and the window's X (see Window_Closing), both of which
        /// keep the instance and its scrollback alive.
        /// </summary>
        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && !e.Handled)
            {
                Hide();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Preserve size and position for the next session (runs both on user hide and on app shutdown close)
            SavePlacement();

            if (userClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void RestorePlacement()
        {
            var cfg = AppConfig.Settings.Base;

            if (cfg == null)
                return;

            if (!double.IsNaN(cfg.ConsoleWindowWidth))
                Width = Math.Max(Math.Min(cfg.ConsoleWindowWidth, SystemParameters.VirtualScreenWidth), MinWidth);
            if (!double.IsNaN(cfg.ConsoleWindowHeight))
                Height = Math.Max(Math.Min(cfg.ConsoleWindowHeight, SystemParameters.VirtualScreenHeight), MinHeight);

            if (!double.IsNaN(cfg.ConsoleWindowLeft) && !double.IsNaN(cfg.ConsoleWindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                // Clamp so the window stays on the visible virtual desktop
                Left = Math.Max(Math.Min(cfg.ConsoleWindowLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width), SystemParameters.VirtualScreenLeft);
                Top = Math.Max(Math.Min(cfg.ConsoleWindowTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height), SystemParameters.VirtualScreenTop);
            }
        }

        private void SavePlacement()
        {
            var cfg = AppConfig.Settings.Base;

            if (cfg == null)
                return;

            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            if (cfg.ConsoleWindowLeft == bounds.Left && cfg.ConsoleWindowTop == bounds.Top &&
                 cfg.ConsoleWindowWidth == bounds.Width && cfg.ConsoleWindowHeight == bounds.Height)
                return;

            cfg.ConsoleWindowLeft = bounds.Left;
            cfg.ConsoleWindowTop = bounds.Top;
            cfg.ConsoleWindowWidth = bounds.Width;
            cfg.ConsoleWindowHeight = bounds.Height;

            AppConfig.Settings.Save();
        }

        /// <summary>Show (if hidden) and put the caret in the MDI input.</summary>
        public void FocusInput()
        {
            if (!IsVisible)
                Show();
            Activate();
            console.FocusInput();
        }
    }
}
