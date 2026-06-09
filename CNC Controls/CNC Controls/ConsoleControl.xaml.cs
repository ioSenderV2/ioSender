/*
 * ConsoleControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.01 / 2020-01-27 / Io Engineering (Terje Io)
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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for ConsoleControl.xaml
    /// </summary>
    public partial class ConsoleControl : UserControl
    {
        // -1 = not navigating history (editing a fresh line); otherwise an index into CommandLog.
        private int historyIndex = -1;

        public ConsoleControl()
        {
            InitializeComponent();
        }

        // Terminal-style prompt: route the typed line through the same MDI command the global
        // MDI strip uses (it echoes the command into ResponseLog), and recall the shared
        // CommandLog history with Up/Down.
        private void txtInput_KeyDown(object sender, KeyEventArgs e)
        {
            var model = DataContext as GrblViewModel;
            var tb = sender as TextBox;
            if (model == null || tb == null)
                return;

            switch (e.Key)
            {
                case Key.Return:
                    {
                        string cmd = tb.Text.Trim();
                        if (!string.IsNullOrEmpty(cmd) && model.MDICommand.CanExecute(null))
                        {
                            if (model.GrblError != 0)
                                model.ExecuteCommand("");   // clear a pending error first, as the MDI strip does
                            model.MDICommand.Execute(cmd);
                            tb.Clear();
                            historyIndex = -1;
                        }
                        e.Handled = true;
                    }
                    break;

                case Key.Up:
                    RecallHistory(tb, -1);
                    e.Handled = true;
                    break;

                case Key.Down:
                    RecallHistory(tb, 1);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    tb.Clear();
                    historyIndex = -1;
                    e.Handled = true;
                    break;
            }
        }

        private void RecallHistory(TextBox tb, int direction)
        {
            var log = (DataContext as GrblViewModel)?.CommandLog;
            if (log == null || log.Count == 0)
                return;

            // CommandLog appends newest last; start "past the end" (empty line) so the first
            // Up recalls the most recent command.
            if (historyIndex == -1)
                historyIndex = log.Count;

            historyIndex = Math.Max(0, Math.Min(historyIndex + direction, log.Count));

            tb.Text = historyIndex == log.Count ? string.Empty : log[historyIndex];
            tb.CaretIndex = tb.Text.Length;
        }

        // Put the cursor in the command prompt as soon as the console becomes visible (tab
        // switch or pop-out window shown) so it can be typed into immediately. Deferred to Input
        // priority so the element is laid out/focusable first.
        private void ConsoleControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(new System.Action(() => txtInput.Focus()), DispatcherPriority.Input);
        }

        private void btn_Clear(object sender, RoutedEventArgs e)
        {
            (DataContext as GrblViewModel).ResponseLog.Clear();
        }

        private void consoleOption_Click(object sender, RoutedEventArgs e)
        {
            // Preserve all three console checkbox states for the next session
            // (the IsChecked bindings have already updated the model)
            if (AppConfig.Settings.Base != null)
            {
                var model = DataContext as GrblViewModel;
                AppConfig.Settings.Base.ConsoleVerbose = model.ResponseLogVerbose;
                AppConfig.Settings.Base.ConsoleFilterRT = model.ResponseLogFilterRT;
                AppConfig.Settings.Base.ConsoleShowRTAll = model.ResponseLogShowRTAll;
                AppConfig.Settings.Save();
            }
        }
    }
}
