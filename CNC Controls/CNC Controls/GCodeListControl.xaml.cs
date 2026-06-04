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
using System.Collections.Generic;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for GCodeListControl.xaml
    /// </summary>
    public partial class GCodeListControl : UserControl
    {
        public ScrollViewer scroll = null;
 
        public GCodeListControl()
        {
            InitializeComponent();

            ctxMenu.DataContext = this;
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
            grdGCode.DataContext = GCode.File.Data;
            if (DataContext is GrblViewModel)
            {
                (DataContext as GrblViewModel).PropertyChanged += GCodeListControl_PropertyChanged;
                ApplyGrouping((DataContext as GrblViewModel).IsFolderView);
            }
        }

        private void GCodeListControl_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is GrblViewModel) switch (e.PropertyName)
            {
                case nameof(GrblViewModel.ScrollPosition):
                    int sp = ((GrblViewModel)sender).ScrollPosition;
                    if (sp == 0)
                        scroll.ScrollToTop();
                    else
                        scroll.ScrollToVerticalOffset(sp);
                    break;

                case nameof(GrblViewModel.IsFolderView):
                    ApplyGrouping(((GrblViewModel)sender).IsFolderView);
                    break;
            }
        }

        // Group the gcode list by toolpath section when a folder is loaded (outline view),
        // ungrouped flat list otherwise. The DataGrid uses the default view of GCode.File.Data.
        private void ApplyGrouping(bool grouped)
        {
            var view = CollectionViewSource.GetDefaultView(GCode.File.Data);
            if (view == null)
                return;

            // Fresh outline starts fully collapsed; also avoids carrying stale section names forward.
            groupExpanded.Clear();

            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Clear();
                if (grouped)
                    view.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
            }
        }

        // Remembers which toolpath sections are expanded so scrolling (which recycles the virtualized
        // group containers) restores their state instead of snapping back to collapsed. Default
        // (unseen section) is collapsed; remembered once toggled.
        private static readonly Dictionary<string, bool> groupExpanded = new Dictionary<string, bool>();

        public static bool IsToolpathGroupExpanded(string name)
        {
            bool v;
            return name != null && groupExpanded.TryGetValue(name, out v) && v;
        }

        private void ToolpathGroup_Expanded(object sender, RoutedEventArgs e)
        {
            RecordGroup(sender, true);
        }

        private void ToolpathGroup_Collapsed(object sender, RoutedEventArgs e)
        {
            RecordGroup(sender, false);
        }

        private static void RecordGroup(object sender, bool expanded)
        {
            var name = ((sender as FrameworkElement)?.DataContext as CollectionViewGroup)?.Name as string;
            if (name != null)
                groupExpanded[name] = expanded;
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

    /// <summary>Maps a toolpath section name to its remembered expanded/collapsed state.</summary>
    public class ToolpathGroupExpandedConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return GCodeListControl.IsToolpathGroupExpanded(value as string);
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new System.NotSupportedException();
        }
    }
}
