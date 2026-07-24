/*
 * GcodeListControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.47 / 2025-12-25 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020-2025, Io Engineering (Terje Io)
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

using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.Win32;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for GCodeListControl.xaml
    /// </summary>
    public partial class GCodeListControl : UserControl
    {
        public ScrollViewer scroll = null;
        private ObservableCollection<GCodeBlock> _program;   // null = show the loaded job (GCode.File)

        // Mint marks the view that is the streamer's configured input source (what Cycle Start will run).
        private static readonly Brush MintBrush = MakeFrozen(Color.FromRgb(0xE4, 0xF6, 0xE6));
        private static Brush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public GCodeListControl()
        {
            InitializeComponent();

            ctxMenu.DataContext = this;
            // Re-mark the source highlight whenever the active program changes (a wizard tab set/cleared, a job
            // loaded), even on views whose own program didn't change (e.g. the loaded-job view).
            MacroProcessor.ActiveProgramChanged += RefreshSourceHighlight;
            Unloaded += (s, e) => MacroProcessor.ActiveProgramChanged -= RefreshSourceHighlight;
        }

        // The program this view renders. A program is just a list of G-code blocks - file, folder or wizard,
        // all the same. Pass null to fall back to the loaded job. Cuts the old hard-wired tie to GCode.File.
        public void SetProgram(ObservableCollection<GCodeBlock> blocks)
        {
            _program = blocks;
            object list = (object)_program ?? GCode.File.Data;
            if (list is System.Collections.IEnumerable en)
                AssignBlockNumbers(en);
            grdGCode.DataContext = list;
            ApplyGrouping(_program == null && ((DataContext as GrblViewModel)?.HasOutline ?? false));
            RefreshSourceHighlight();
        }

        // Block column = a continuous program line-number sequence: jump to an explicit N word when a line carries
        // one, otherwise continue previous + 1 (so O<...> calls, comments and any unnumbered line get a sensible,
        // in-sequence number). Runs on the displayed collection each SetProgram; BlockDisplay only raises a change
        // when it actually differs, so re-binds during a run do not churn the grid.
        private static void AssignBlockNumbers(System.Collections.IEnumerable blocks)
        {
            long n = 0;
            foreach (var o in blocks)
                if (o is GCodeBlock b)
                {
                    n = b.HasExplicitLineNum ? b.ExplicitLineNum : n + 1;
                    b.BlockDisplay = n.ToString();
                }
        }

        // Mint the background only when this view shows the streamer's configured input source: the active
        // wizard program when one is set, otherwise the loaded job. So exactly the program Cycle Start will run
        // is highlighted - a wizard's program view goes mint and the loaded-job view does not, and vice versa.
        private void RefreshSourceHighlight()
        {
            bool isSource = MacroProcessor.ActiveRun != null
                ? _program != null                                // the active wizard program's view
                : _program == null && GCode.File.IsLoaded;        // the loaded-job view

            grdGCode.Background = isSource ? MintBrush : Brushes.Transparent;
            grdGCode.RowBackground = isSource ? MintBrush : Brushes.White;
        }

        #region Dependency properties

        public static readonly DependencyProperty SingleSelectedProperty = DependencyProperty.Register(nameof(SingleSelected), typeof(bool), typeof(GCodeListControl), new PropertyMetadata(false));
        public bool SingleSelected
        {
            get { return (bool)GetValue(SingleSelectedProperty); }
            private set { SetValue(SingleSelectedProperty, value); }
        }

        public static readonly DependencyProperty MultipleSelectedProperty = DependencyProperty.Register(nameof(MultipleSelected), typeof(bool), typeof(GCodeListControl), new PropertyMetadata(false));
        public bool MultipleSelected
        {
            get { return (bool)GetValue(MultipleSelectedProperty); }
            private set { SetValue(MultipleSelectedProperty, value); }
        }
        #endregion

        // Plain-language explanation tooltip. Computed lazily on open for just the hovered row (never
        // precomputed - a program can be 300k+ lines). If the hovered row is part of a multi-row selection,
        // explain every selected line; otherwise explain the one line. Uses the loaded program's parsed
        // tokens (GCode.File.Tokens, keyed to a block by LineNumber); a view without matching tokens (e.g.
        // a wizard's own program) falls back to describing the raw text.
        // --- Hover explanation balloon (interactive Popup - click to copy) ------------------------------
        // A ToolTip can't be moused into and clicked, so the explanation is a StaysOpen Popup driven by two
        // hover timers: show ~450 ms after the pointer settles on a row; close ~250 ms after it leaves the row
        // AND the balloon (the gap lets the pointer travel onto the balloon to click it).
        private System.Windows.Threading.DispatcherTimer _explainShow, _explainClose;
        private DataGridRow _hoverRow;
        // Captured at MouseEnter, not re-read from _hoverRow.Item when the (450ms-delayed) show-timer fires -
        // EnableRowVirtualization means the SAME DataGridRow container can be recycled onto a DIFFERENT
        // GCodeBlock while the timer is pending (a scroll swaps a container's Item without a matching
        // MouseLeave/MouseEnter pair, since the container itself never physically left the pointer), which
        // was showing the explanation for whatever line the row got recycled to - usually adjacent to, not
        // the line the pointer was actually still resting on. Pinning the block at the moment of entry means
        // the explanation always matches what the user pointed at, not whatever the container recycled to.
        private GCodeBlock _hoverBlock;
        private string _explainText = string.Empty;

        private void EnsureExplainTimers()
        {
            if (_explainShow != null)
                return;
            _explainShow = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(450) };
            _explainShow.Tick += (s, e) => { _explainShow.Stop(); ShowExplain(); };
            _explainClose = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(250) };
            _explainClose.Tick += (s, e) => { _explainClose.Stop(); explainPopup.IsOpen = false; };
        }

        private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            EnsureExplainTimers();
            _hoverRow = sender as DataGridRow;
            _hoverBlock = _hoverRow?.Item as GCodeBlock;
            _explainClose.Stop();
            _explainShow.Stop();
            if (_hoverBlock != null)
                _explainShow.Start();
        }

        private void Row_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _explainShow.Stop();
            if (explainPopup.IsOpen)
                _explainClose.Start();   // grace period to let the pointer reach the balloon
        }

        private void ShowExplain()
        {
            var block = _hoverBlock;
            if (block == null || _hoverRow == null || !_hoverRow.IsMouseOver)
                return;
            _explainText = BuildExplanation(block);
            explainText.Text = _explainText;
            explainHint.Text = "click to copy";
            explainPopup.PlacementTarget = _hoverRow;
            explainPopup.IsOpen = false;   // force a reposition when moving between rows
            explainPopup.IsOpen = true;
        }

        private void ExplainPopup_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _explainClose?.Stop();   // pointer is on the balloon - keep it open
        }

        private void ExplainPopup_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _explainClose?.Start();
        }

        private void ExplainPopup_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_explainText ?? string.Empty);
                explainHint.Text = "copied ✓";
            }
            catch { }
            e.Handled = true;
        }

        private string BuildExplanation(GCodeBlock block)
        {
            // A background load is still appending blocks on a worker thread - not wrong to explain a block
            // that's already in the grid (its own Tokens are set at construction, before it's ever added -
            // see GCodeJob.ParseFileLines/AddBlock), just politely say so rather than explain a half-loaded
            // program that's still visibly changing under the pointer.
            if ((DataContext as GrblViewModel)?.IsLoading == true)
                return "Program is still loading…";

            var selected = grdGCode.SelectedItems;

            // Multi-row selection that includes the hovered row -> line-by-line breakdown of the selection.
            if (selected != null && selected.Count > 1 && selected.Contains(block))
            {
                var blocks = selected.OfType<GCodeBlock>().ToList();
                blocks.Sort((a, b) => a.LineNum.CompareTo(b.LineNum));   // program order, not click order
                return GCodeExplainer.ExplainSelection(blocks, blocks.ToDictionary(b => b.LineNum, b => b.Tokens));
            }

            // Each block carries exactly the tokens the parser generated FOR IT (set at construction time -
            // see GCodeBlock.Tokens) - no line-number matching against a global token list needed, and so no
            // risk of a coincidental match against an unrelated line's explicit N-word (see Tokens' comment).
            return GCodeExplainer.ExplainBlock(block, block.Tokens);
        }

        private void grdGCode_Drag(object sender, DragEventArgs e)
        {
            GCode.File.Drag(sender, e);
        }

        private void grdGCode_Drop(object sender, DragEventArgs e)
        {
            GCode.File.Drop(sender, e);
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            scroll = UIUtils.GetScrollViewer(grdGCode);
            grdGCode.DataContext = (object)_program ?? GCode.File.Data;
            if (DataContext is GrblViewModel)
            {
                (DataContext as GrblViewModel).PropertyChanged += GCodeListControl_PropertyChanged;
                ApplyGrouping(_program == null && (DataContext as GrblViewModel).HasOutline);
            }
            RefreshSourceHighlight();
            RefreshDataColumnWidth();
        }

        // Known WPF DataGrid quirk: a Width="*" column (the Data column here) can compute far narrower than
        // its actual share on a layout pass with no/few realized rows - especially combined with row
        // virtualization - and only corrects itself once something forces a star-width recalculation (e.g.
        // the user dragging a column splitter). Toggling the width off Star and back forces that
        // recalculation. Called on control Loaded AND every time a file finishes loading (IsLoading ->
        // false below) - at UserControl_Loaded time the grid is still empty (no file loaded yet), so that
        // alone isn't enough; the recalculation has to run again once real rows exist to measure against.
        private void RefreshDataColumnWidth()
        {
            var dataColumn = grdGCode.Columns[2];
            dataColumn.Width = new DataGridLength(0);
            dataColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }

        private void GCodeListControl_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is GrblViewModel) switch (e.PropertyName)
            {
                case nameof(GrblViewModel.ScrollPosition):
                    // In the compact (3-line) run view, keep the executing line centred instead of the normal
                    // "5 rows from the top" scroll - the executing line drives it (see BlockExecuting below).
                    if (_compactRows > 0)
                    {
                        CenterExecutingLine();
                        break;
                    }
                    // An instance created collapsed (e.g. the program overlay) has no realized ScrollViewer
                    // yet - try to acquire it lazily, and skip until it exists rather than NRE.
                    if (scroll == null)
                        scroll = UIUtils.GetScrollViewer(grdGCode);
                    if (scroll == null)
                        break;
                    int sp = ((GrblViewModel)sender).ScrollPosition;
                    if (sp == 0)
                        scroll.ScrollToTop();
                    else
                        scroll.ScrollToVerticalOffset(sp);
                    break;

                case nameof(GrblViewModel.BlockExecuting):
                    // Drives the compact view: re-centre on the newly executing line (also covers the first few
                    // lines, where ScrollPosition = index-5 stays negative and never fires).
                    if (_compactRows > 0)
                        CenterExecutingLine();
                    break;

                case nameof(GrblViewModel.HasOutline):
                    ApplyGrouping(_program == null && ((GrblViewModel)sender).HasOutline);
                    break;

                case nameof(GrblViewModel.IsLoading):
                    // Scope the "busy" hourglass to the program list only (the rest of the UI stays responsive
                    // while a file/folder loads on the background thread). ForceCursor so it shows over the rows.
                    bool loading = ((GrblViewModel)sender).IsLoading;
                    Cursor = loading ? System.Windows.Input.Cursors.Wait : null;
                    ForceCursor = loading;
                    if (!loading)
                        RefreshDataColumnWidth();   // real rows exist now - see RefreshDataColumnWidth's own comment
                    break;
            }
        }

        // --- Compact (N-line) run view ------------------------------------------------------------------
        // Shrink the list to a few rows kept centred on the executing line (previous / current / next), so a
        // running program takes little space. 0 = normal, unbounded. Driven by ProgramView.Compact.
        private int _compactRows = 0;

        public void SetCompactRows(int rows)
        {
            _compactRows = rows > 0 ? rows : 0;
            if (_compactRows > 0)
            {
                if (!ConstrainHeight())                                   // rows may not be realized yet
                    Dispatcher.BeginInvoke(new System.Action(() => { ConstrainHeight(); CenterExecutingLine(); }),
                                           System.Windows.Threading.DispatcherPriority.Loaded);
                CenterExecutingLine();
            }
            else
                grdGCode.ClearValue(MaxHeightProperty);                   // back to full height
        }

        // Cap the grid at the column header + N data rows. Returns false until a row is measurable.
        private bool ConstrainHeight()
        {
            if (_compactRows <= 0)
                return true;
            double rowH = MeasuredRowHeight(), hdrH = MeasuredHeaderHeight();
            if (rowH <= 0)
                return false;
            grdGCode.MaxHeight = hdrH + rowH * _compactRows + 4;          // +gridlines/border slack
            return true;
        }

        private double MeasuredRowHeight()
        {
            double h = FindVisualChild<DataGridRow>(grdGCode)?.ActualHeight ?? 0;
            return h > 0 ? h : (FontSize > 0 ? FontSize * 1.6 : 20);      // fallback before rows realize
        }

        private double MeasuredHeaderHeight()
        {
            double h = FindVisualChild<System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>(grdGCode)?.ActualHeight ?? 0;
            return h > 0 ? h : 22;
        }

        // Scroll so the executing line sits in the middle of the compact window (previous above, next below).
        private void CenterExecutingLine()
        {
            if (_compactRows <= 0)
                return;
            if (scroll == null)
                scroll = UIUtils.GetScrollViewer(grdGCode);
            if (scroll == null)
                return;
            int exec = (DataContext as GrblViewModel)?.BlockExecuting ?? -1;
            if (exec < 0)
                return;
            int top = exec - _compactRows / 2;
            scroll.ScrollToVerticalOffset(top < 0 ? 0 : top);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t)
                    return t;
                var deeper = FindVisualChild<T>(child);
                if (deeper != null)
                    return deeper;
            }
            return null;
        }

        // Group the gcode list by toolpath section when a folder is loaded (outline view),
        // ungrouped flat list otherwise. The DataGrid uses the default view of GCode.File.Data.
        private void ApplyGrouping(bool grouped)
        {
            var view = CollectionViewSource.GetDefaultView(grdGCode.DataContext);
            if (view == null)
                return;

            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Clear();
                if (grouped)
                    view.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
            }
        }

        private void StartSection_Click(object sender, RoutedEventArgs e)
        {
            StartSection(GetGroup(sender), false);
        }

        private void RunSection_Click(object sender, RoutedEventArgs e)
        {
            StartSection(GetGroup(sender), true);
        }

        private static CollectionViewGroup GetGroup(object sender)
        {
            var mi = sender as MenuItem;
            var group = mi?.DataContext as CollectionViewGroup;
            if (group == null)
            {
                var cm = mi?.Parent as ContextMenu;
                group = (cm?.PlacementTarget as FrameworkElement)?.DataContext as CollectionViewGroup;
            }
            return group;
        }

        // Run a toolpath outline section. runOnlyThisToolpath: stop at the end of this section
        // ("Run just this toolpath"); otherwise run from here to program end ("Start from this toolpath").
        private void StartSection(CollectionViewGroup group, bool runOnlyThisToolpath)
        {
            if (group == null || group.ItemCount == 0)
                return;

            var first = group.Items[0] as GCodeBlock;
            var last = group.Items[group.ItemCount - 1] as GCodeBlock;
            var model = DataContext as GrblViewModel;
            if (first == null || last == null || model == null)
                return;

            int startIndex = GCode.File.Data.IndexOf(first);
            int endIndex = GCode.File.Data.IndexOf(last);
            if (startIndex < 0 || endIndex < 0 || !model.StartFromBlock.CanExecute(startIndex))
                return;

            string prompt = runOnlyThisToolpath
                ? string.Format("Run only toolpath \"{0}\"?\r\rThe program will stop at the end of this toolpath.", group.Name)
                : string.Format("Start the run from toolpath \"{0}\" and continue to the end?", group.Name);

            if (AppDialogs.Show(prompt, "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            // Bound the run to this section only, when requested.
            if (runOnlyThisToolpath)
                model.RunToBlock = endIndex;

            // Re-establish modal state (distance/feed mode, plane, units) for a mid-program start.
            // Queued ahead of the toolpath via the streamed command queue, which is drained first
            // by SendNextLine and survives CycleStart's serial PurgeQueue.
            foreach (var line in GCodeJob.DefaultProlog)
                GCode.File.Commands.Enqueue(line);

            model.StartFromBlock.Execute(startIndex);
        }

        void grdGCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // DataContext can be transiently null/not-yet-a-GrblViewModel here: SetProgram's own DataContext
            // reassignment (switching the active program) fires this SelectionChanged mid-transition.
            var grbl = DataContext as GrblViewModel;
            if (grbl == null)
                return;

            SingleSelected = grdGCode.SelectedItems.Count == 1 && grbl.StartFromBlock.CanExecute(grdGCode.SelectedIndex);
            MultipleSelected = grdGCode.SelectedItems.Count >= 0 && grbl.StartFromBlock.CanExecute(grdGCode.SelectedIndex);
        }

        private void StartHere_Click(object sender, RoutedEventArgs e)
        {
            if (grdGCode.SelectedItems.Count == 1 &&
                 AppDialogs.Show(string.Format(LibStrings.FindResource("VerifyStartFrom"), ((GCodeBlock)(grdGCode.SelectedItems[0])).LineNum),
                                  "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                (DataContext as GrblViewModel).StartFromBlock.Execute(grdGCode.SelectedIndex);
            }
        }

        private void CopyMDI_Click(object sender, RoutedEventArgs e)
        {
            if (grdGCode.SelectedItems.Count == 1)
                (DataContext as GrblViewModel).MDIText = ((GCodeBlock)(grdGCode.SelectedItems[0])).Data;
        }
        private void ToggleBreak_Click(object sender, RoutedEventArgs e)
        {
            if (grdGCode.SelectedItems.Count == 1)
                ((GCodeBlock)(grdGCode.SelectedItems[0])).BreakAt ^= true;
        }

        private void SendController_Click(object sender, RoutedEventArgs e)
        {
            if (grdGCode.SelectedItems.Count >= 1 &&
                 AppDialogs.Show(LibStrings.FindResource("VerifySendController"), "ioSender",
                                  MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                var model = DataContext as GrblViewModel;

                if (model.GrblError != 0)
                    model.ExecuteCommand("");

                List<GCodeBlock> rows = new List<GCodeBlock>();

                for (int i = 0; i < grdGCode.SelectedItems.Count; i++)
                    rows.Add(((GCodeBlock)(grdGCode.SelectedItems[i])));

                rows.Sort(new RowComparer());

                foreach (GCodeBlock row in rows)
                    model.ExecuteCommand(row.Data);

            }
        }

        // Enable Save when either the loaded job (the shared static GCode.File) or this view's OWN program
        // (_program - a wizard's Generate output, e.g. Start Job) has content. Runs each time the menu opens
        // so it tracks load/close without a per-instance binding on the loaded state. Also (re)builds the
        // Transform submenu from the registered transformers - built fresh here rather than sharing the single
        // UIViewModel MenuItem set, which cannot be a child of multiple context menus. Transform only applies
        // to the loaded job - a wizard's generated program has no transformer pipeline.
        private void ctxMenu_Opened(object sender, RoutedEventArgs e)
        {
            mnuSaveProgram.IsEnabled = _program != null ? _program.Count > 0 : GCode.File.IsLoaded;

            mnuTransform.Items.Clear();
            var names = GCode.File.TransformerNames;
            for (int i = 0; i < names.Count; i++)
            {
                var item = new MenuItem { Header = names[i], Tag = i };
                item.Click += Transform_Click;
                mnuTransform.Items.Add(item);
            }
            mnuTransform.IsEnabled = GCode.File.IsLoaded && names.Count > 0;
        }

        private void Transform_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Transform((int)((MenuItem)sender).Tag);
        }

        private void SaveProgram_Click(object sender, RoutedEventArgs e)
        {
            if (_program != null)
                SaveOwnProgram();
            else
                GCode.File.Save();
        }

        // Save this view's OWN program (a wizard's Generate output) to a file the operator picks - mirrors
        // GCode.File.Save() but writes _program instead of the loaded job, since a wizard-generated program
        // (e.g. Start Job) is never itself the loaded job.
        private void SaveOwnProgram()
        {
            var saveDialog = new SaveFileDialog { Filter = "GCode file (*.nc)|*.nc", AddExtension = true, DefaultExt = ".nc" };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    using (var stream = new StreamWriter(saveDialog.FileName))
                    {
                        foreach (var line in _program)
                            stream.WriteLine(line.Data);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }

    internal class RowComparer : IComparer<GCodeBlock>
    {
        public int Compare(GCodeBlock a, GCodeBlock b)
        {
            return (int)a.LineNum - (int)b.LineNum;
        }
    }
}
