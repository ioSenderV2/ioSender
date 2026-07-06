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
using System.Collections.Generic;
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
        // -1 = not navigating history (editing a fresh line); otherwise an index into the shared
        // GrblViewModel.MDIHistory (newest first), which both this prompt and the MDI strip feed.
        private int historyIndex = -1;

        public ConsoleControl()
        {
            InitializeComponent();

            // Handle keys in the tunnelling PreviewKeyDown (with handledEventsToo) so Up/Down
            // reach us before the TextBox/keyboard-navigation consumes them - a plain bubbling
            // KeyDown never sees the arrows on a focused single-line TextBox.
            txtInput.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(txtInput_KeyDown), true);
        }

        // LogOnly: hide the inline input prompt and show just the scrollback - used by the run-control
        // bar's console overlay, where the bar's command box is the input. Default false = full console
        // (input + log), as on the Console tab.
        public static readonly DependencyProperty LogOnlyProperty = DependencyProperty.Register(
            nameof(LogOnly), typeof(bool), typeof(ConsoleControl), new PropertyMetadata(false, OnLogOnlyChanged));

        public bool LogOnly
        {
            get { return (bool)GetValue(LogOnlyProperty); }
            set { SetValue(LogOnlyProperty, value); }
        }

        private static void OnLogOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (ConsoleControl)d;
            c.inputRow.Visibility = (bool)e.NewValue ? Visibility.Collapsed : Visibility.Visible;
        }

        // Terminal-style prompt: route the typed line through the same MDI command the global
        // MDI strip uses (it echoes the command into ResponseLog), recording it in the shared
        // MDIHistory; Up/Down recall it.
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
                        // Send each line: a single typed line is the common case, but a MULTI-LINE PASTE sends
                        // every non-empty line in order (one MDI command each) - so you can paste a block of
                        // commands and fire them all with one Enter.
                        var lines = tb.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                        bool sent = false;
                        if (model.MDICommand.CanExecute(null))
                        {
                            if (model.GrblError != 0)
                                model.ExecuteCommand("");   // clear a pending error first, as the MDI strip does
                            foreach (var raw in lines)
                            {
                                string cmd = raw.Trim();
                                if (cmd.Length == 0)
                                    continue;
                                model.MDICommand.Execute(cmd);
                                model.AddMDIHistory(cmd);
                                sent = true;
                            }
                        }
                        if (sent)
                        {
                            tb.Clear();
                            historyIndex = -1;
                        }
                        e.Handled = true;
                    }
                    break;

                case Key.Up:
                    RecallHistory(tb, true);
                    e.Handled = true;
                    break;

                case Key.Down:
                    RecallHistory(tb, false);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    tb.Clear();
                    historyIndex = -1;
                    e.Handled = true;
                    break;
            }
        }

        private void RecallHistory(TextBox tb, bool older)
        {
            var hist = (DataContext as GrblViewModel)?.MDIHistory;
            if (hist == null || hist.Count == 0)
                return;

            // hist[0] is the most recent. Up steps to older entries; Down steps back toward the
            // (empty) edit line. historyIndex == -1 is the fresh line.
            historyIndex = older ? Math.Min(historyIndex + 1, hist.Count - 1)
                                 : Math.Max(historyIndex - 1, -1);

            tb.Text = historyIndex < 0 ? string.Empty : hist[historyIndex];
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

        // Output-window right-click menu. Capture which line was clicked so "Clear up to here" knows where.
        private int _rightClickLine = -1;

        private void Output_RightDown(object sender, MouseButtonEventArgs e)
        {
            var tb = sender as TextBox;
            int ci = tb.GetCharacterIndexFromPoint(e.GetPosition(tb), true);
            _rightClickLine = ci >= 0 ? tb.GetLineIndexFromCharacterIndex(ci) : -1;
        }

        private void OutClearAll_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as GrblViewModel)?.ResponseLog.Clear();
        }

        private void OutClearToHere_Click(object sender, RoutedEventArgs e)
        {
            var log = (DataContext as GrblViewModel)?.ResponseLog;
            if (log == null || _rightClickLine < 0)
                return;
            int n = Math.Min(_rightClickLine + 1, log.Count);   // remove the clicked line and everything above it
            for (int i = 0; i < n; i++)
                log.RemoveAt(0);
        }

        private void OutSave_Click(object sender, RoutedEventArgs e)
        {
            var log = (DataContext as GrblViewModel)?.ResponseLog;
            if (log == null)
                return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", FileName = "console.txt" };
            if (dlg.ShowDialog() == true)
                System.IO.File.WriteAllLines(dlg.FileName, log);
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

        // ---- Console text size (persisted in Config.ConsoleFontSize; the scrollback binds to it) ----

        private void FontSmaller_Click(object sender, RoutedEventArgs e) { AdjustFont(-1d); }
        private void FontLarger_Click(object sender, RoutedEventArgs e) { AdjustFont(1d); }

        private void AdjustFont(double delta)
        {
            var b = AppConfig.Settings.Base;
            if (b == null)
                return;
            b.ConsoleFontSize = b.ConsoleFontSize + delta;   // clamped 6-32 in the setter
            AppConfig.Settings.Save();
        }

        // ---- Find in log: search the console scrollback, select/scroll each match, n-of-m + up/down / F3-F4 ----

        private readonly List<int> _matches = new List<int>();   // character offsets of each match in txtOutput
        private int _matchIndex = -1;
        private string _matchQuery = string.Empty;

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RunSearch(searchBox.Text);
        }

        private void searchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) { NavMatch(1); e.Handled = true; }
            else if (e.Key == Key.F3) { NavMatch(1); e.Handled = true; }
            else if (e.Key == Key.F4) { NavMatch(-1); e.Handled = true; }
        }

        private void Console_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F3) { NavMatch(1); e.Handled = true; }
            else if (e.Key == Key.F4) { NavMatch(-1); e.Handled = true; }
        }

        private void btnMatchNext_Click(object sender, RoutedEventArgs e) { NavMatch(1); }
        private void btnMatchPrev_Click(object sender, RoutedEventArgs e) { NavMatch(-1); }

        // (Re)compute all match offsets for the current query against the live scrollback text.
        private void RunSearch(string query)
        {
            _matches.Clear();
            _matchIndex = -1;
            _matchQuery = query ?? string.Empty;

            string text = txtOutput.Text ?? string.Empty;
            if (_matchQuery.Length > 0)
            {
                int i = 0;
                while ((i = text.IndexOf(_matchQuery, i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    _matches.Add(i);
                    i += _matchQuery.Length;
                }
            }

            if (_matches.Count > 0)
            {
                _matchIndex = 0;
                SelectMatch();
            }
            else
                txtOutput.Select(txtOutput.SelectionStart, 0);

            UpdateMatchUi();
        }

        // Step to the next (+1) / previous (-1) match, wrapping. Re-runs the search first so matches stay in
        // sync with a scrollback that may have grown since the last keystroke.
        private void NavMatch(int dir)
        {
            if (_matchQuery.Length == 0)
                return;

            // Recompute against current text; keep the caret near where we were.
            int prev = _matchIndex >= 0 && _matchIndex < _matches.Count ? _matches[_matchIndex] : -1;
            RecomputeMatches();
            if (_matches.Count == 0)
            {
                UpdateMatchUi();
                return;
            }

            if (prev >= 0)
                _matchIndex = _matches.IndexOf(prev);
            if (_matchIndex < 0)
                _matchIndex = 0;

            _matchIndex = ((_matchIndex + dir) % _matches.Count + _matches.Count) % _matches.Count;
            SelectMatch();
            UpdateMatchUi();
        }

        private void RecomputeMatches()
        {
            _matches.Clear();
            string text = txtOutput.Text ?? string.Empty;
            if (_matchQuery.Length == 0)
                return;
            int i = 0;
            while ((i = text.IndexOf(_matchQuery, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                _matches.Add(i);
                i += _matchQuery.Length;
            }
        }

        private void SelectMatch()
        {
            if (_matchIndex < 0 || _matchIndex >= _matches.Count)
                return;
            int start = _matches[_matchIndex];
            txtOutput.Select(start, _matchQuery.Length);
            int line = txtOutput.GetLineIndexFromCharacterIndex(start);
            if (line >= 0)
                txtOutput.ScrollToLine(line);
        }

        private void UpdateMatchUi()
        {
            btnMatchPrev.IsEnabled = btnMatchNext.IsEnabled = _matches.Count > 0;
            txtMatchCount.Text = _matches.Count > 0
                ? string.Format("{0} of {1}", _matchIndex + 1, _matches.Count)
                : (_matchQuery.Length > 0 ? "no matches" : string.Empty);
        }
    }
}
