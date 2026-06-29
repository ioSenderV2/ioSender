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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CNC.Core;

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
            grdGCode.DataContext = (object)_program ?? GCode.File.Data;
            ApplyGrouping(_program == null && ((DataContext as GrblViewModel)?.IsFolderView ?? false));
            RefreshSourceHighlight();
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
                ApplyGrouping(_program == null && (DataContext as GrblViewModel).IsFolderView);
            }
            RefreshSourceHighlight();
        }

        private void GCodeListControl_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is GrblViewModel) switch (e.PropertyName)
            {
                case nameof(GrblViewModel.ScrollPosition):
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

                case nameof(GrblViewModel.IsFolderView):
                    ApplyGrouping(_program == null && ((GrblViewModel)sender).IsFolderView);
                    break;
            }
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

            if (MessageBox.Show(prompt, "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            // Bound the run to this section only, when requested.
            if (runOnlyThisToolpath)
                model.RunToBlock = endIndex;

            // Re-establish modal state (distance/feed mode, plane, units) for a mid-program start.
            // Queued ahead of the toolpath via the streamed command queue, which is drained first
            // by SendNextLine and survives CycleStart's serial PurgeQueue.
            foreach (var line in FusionFolderLoader.Prolog)
                GCode.File.Commands.Enqueue(line);

            model.StartFromBlock.Execute(startIndex);
        }

        void grdGCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SingleSelected = grdGCode.SelectedItems.Count == 1 && (DataContext as GrblViewModel).StartFromBlock.CanExecute(grdGCode.SelectedIndex);
            MultipleSelected = grdGCode.SelectedItems.Count >= 0 && (DataContext as GrblViewModel).StartFromBlock.CanExecute(grdGCode.SelectedIndex);
        }

        private void StartHere_Click(object sender, RoutedEventArgs e)
        {
            if (grdGCode.SelectedItems.Count == 1 &&
                 MessageBox.Show(string.Format(LibStrings.FindResource("VerifyStartFrom"), ((GCodeBlock)(grdGCode.SelectedItems[0])).LineNum),
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
                 MessageBox.Show(LibStrings.FindResource("VerifySendController"), "ioSender",
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
    }

    internal class RowComparer : IComparer<GCodeBlock>
    {
        public int Compare(GCodeBlock a, GCodeBlock b)
        {
            return (int)a.LineNum - (int)b.LineNum;
        }
    }
}
