/*
 * OffsetView.xaml.cs - part of CNC Controls library
 *
 * v0.44 / 2023-09-16 / Io Engineering (Terje Io)
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
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CNC.Core;
using CNC.GCode;
using System.Threading;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for OffsetView.xaml
    /// </summary>
    public partial class OffsetView : UserControl, ICNCView
    {
        private GrblViewModel parameters = new GrblViewModel();
        private volatile bool awaitCoord = false;
        private Action<string> GotPosition;
        // Which row a pending "Get MPos" click is for - each row now edits/acts on itself directly (no
        // separate staging panel to the grid's right), so the async wait-for-status result has to land back
        // on the SPECIFIC row that asked for it rather than one shared field.
        private CoordinateSystem getPosTargetRow;
        // "Startup" baseline (CoordinateSystem.CaptureStartup) is captured once per app session on the
        // FIRST Activate, not every time this tab is revisited - this view instance lives for the app's
        // lifetime (built once by TabRegistry), so this flag is safe as a plain field.
        private bool startupCaptured;

        public OffsetView()
        {
            InitializeComponent();

            parameters.WorkPositionOffset.PropertyChanged += Parameters_PropertyChanged;
            if(!GrblInfo.IsGrblHAL)
                parameters.PropertyChanged += Parameters_PropertyChanged;
        }

        public AxisFlags AxisEnabledFlags { get { return GrblInfo.AxisFlags; } }

        #region Methods and properties required by CNCView interface

        public ViewType ViewType { get { return ViewType.Offsets; } }
        public bool CanEnable { get { return !(DataContext as GrblViewModel).IsGCLock; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (activate)
            {
                Comms.com.DataReceived += parameters.DataReceived;

                GrblWorkParameters.Get(parameters);

                parameters.AxisEnabledFlags = GrblInfo.AxisFlags;

                dgrOffsets.ItemsSource = GrblWorkParameters.CoordinateSystems;

                if (!startupCaptured)
                {
                    startupCaptured = true;
                    foreach (var row in GrblWorkParameters.CoordinateSystems)
                        row.CaptureStartup();
                }
            }
            else
            {
                // Belt-and-suspenders for switching to a DIFFERENT main tab: the interactive commit path
                // (NumericField_LostFocus) is entirely LostFocus-driven, and a tab switch doesn't reliably
                // give its deferred (Dispatcher.BeginInvoke) check a chance to run before this Deactivate
                // path clears ItemsSource below - confirmed on real hardware as a staged-but-never-committed
                // Get MPos edit silently surviving in memory but never reaching the controller. Flush any
                // still-pending row here, synchronously, before the grid detaches.
                FlushPendingEdits();

                Comms.com.DataReceived -= parameters.DataReceived;
                Comms.com.PurgeQueue();
                dgrOffsets.ItemsSource = null;
            }
        }

        // Commits every row with an unsaved edit (row.IsEdited). Used as a reliable fallback when leaving
        // the tab entirely (see Activate above) - the interactive per-row LostFocus path stays for the
        // common case (moving between rows without switching tabs), this is just insurance against that not
        // completing. Deliberately NO confirmation dialog here, unlike the interactive row-leave case:
        // AppDialogs.Show is modal/blocking, and showing it synchronously inside Activate(false) (itself
        // called from TabControl.SelectionChanged) pumps a nested message loop mid-tab-switch - confirmed on
        // real hardware to corrupt GrblWorkParameters.CoordinateSystems while background status/data
        // processing kept running underneath the blocked dialog (values reset to 0, rows vanished from the
        // grid except the one row involved). Leaving the tab is treated as an implicit "yes, save it" -
        // the interactive confirm (still shown for a same-tab row-to-row move) remains the real safety net.
        private void FlushPendingEdits()
        {
            if (dgrOffsets.ItemsSource == null)
                return;

            foreach (CoordinateSystem row in GrblWorkParameters.CoordinateSystems)
            {
                if (!row.IsEdited)
                    continue;

                saveOffset(row, "All");
            }
        }

        public void CloseFile()
        {
        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
        }

        #endregion

        private void Parameters_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(GrblViewModel.MachinePosition):
                    if (!(awaitCoord = double.IsNaN(parameters.MachinePosition.Values[0])))
                    {
                        getPosTargetRow?.Set(parameters.MachinePosition);
                        parameters.SuspendPositionNotifications = true;
                        parameters.Clear();
                        parameters.MachinePosition.Clear();
                        parameters.SuspendPositionNotifications = false;
                    }
                    break;

                case nameof(GrblViewModel.GrblError):
                    GrblWorkParameters.Get(parameters);
                    break;
            }
        }

        #region UIEvents
        void OffsetControl_Load(object sender, EventArgs e)
        {
            if (GrblInfo.LatheModeEnabled)
                dgrOffsets.Columns[1].Header = string.Format("X offset ({0})", GrblWorkParameters.LatheMode == LatheMode.Radius ? "R" : "D");
        }

        // G10 L1 P- axes <R- I- J- Q-> Set Tool Table
        // L10 - ref G5x + G92 - useful for probe (G38)
        // L11 - ref g59.3 only
        // Q: 1 - 8: 1: 135, 2: 45, 3: 315, 4: 225, 5: 180, 6: 90, 7: 0, 8: 270

        // row IS the target now (its own X/Y/Z/etc are whatever's currently in the grid's editable fields -
        // typed, staged via Get MPos, or zeroed via Clear) - no separate staging object to read from anymore.
        // axis is always "All" or "ClearAll" - the old per-axis Set button is gone along with the staging
        // panel; the row-action columns / row-leave commit act on the whole row.
        void saveOffset(CoordinateSystem row, string axis)
        {
            bool mChanged;
            string cmd = string.Empty;
            bool isPredefined = row.Code == "G28" || row.Code == "G30";
            var grbl = DataContext as GrblViewModel;

            Position newpos = new Position(row);

            newpos.X = GrblWorkParameters.ConvertX(GrblWorkParameters.LatheMode, GrblParserState.LatheMode, row.X);

            GrblParserState.Get();
            if ((mChanged = GrblParserState.IsMetric != grbl.IsMetric))
                cmd = grbl.IsMetric ? "G21" : "G20";

            grbl.Message = String.Empty;

            // G28.1/G30.1 capture WHEREVER THE MACHINE PHYSICALLY IS RIGHT NOW - they take no coordinate
            // parameters, so there is no "rapid to the typed target first" step any more (there used to be,
            // via G53 G0 - removed on request: the intended workflow is jog there by hand, click Get to
            // reflect it in the row, THEN leave the row/confirm - never an app-driven move).
            if (isPredefined && axis != "ClearAll")
            {
                WriteCommandAndWait(row.Code + ".1");
                if (mChanged)
                    WriteCommandAndWait(grbl.IsMetric ? "G20" : "G21");
                row.MarkCommitted();
                return;
            }

            // G92 has no "capture current position as a stored reference" primitive like G28.1/G30.1 - it
            // only knows how to declare "the machine's current physical position IS this work coordinate".
            // What $# reports back for G92 is the resulting OFFSET (MPos - the value you gave G92), not the
            // value you gave it - the same way G54-G59.3's stored value is an offset, not a work position.
            // So "G92 X<row's captured MPos>" (re-declaring wherever we already are) drives the offset to
            // ~0 (confirmed via -debuglog=offsets), while "G92 X0 Y0 Z0" - the conventional "zero the work
            // origin here" - makes the offset equal to MPos, which is what the row is showing after Get.
            // That's the one that actually leaves $# reporting back (approximately) what the row displays.
            // Row-leave/save on G92 therefore always sends "G92 X0 Y0 Z0" regardless of the row's typed/
            // Get-MPos values; the row itself is left alone (not forced to 0) since the real committed
            // offset the controller will echo back on the next $# is close to whatever MPos was at save time.
            if (row.Code == "G92" && axis != "ClearAll")
            {
                WriteCommandAndWait("G92X0Y0Z0");
                if (mChanged)
                    WriteCommandAndWait(grbl.IsMetric ? "G20" : "G21");
                row.MarkCommitted();
                return;
            }

            if (row.Id == 0)
            {
                string code = row.Code == "G28" || row.Code == "G30" ? row.Code + ".1" : row.Code;

                if (axis == "ClearAll" || isPredefined)
                    cmd += row.Code == "G43.1" ? "G49" : row.Code + ".1";
                else
                    cmd += string.Format("G90{0}{1}", code, newpos.ToString(axis == "All" ? GrblInfo.AxisFlags : GrblInfo.AxisLetterToFlag(axis)));
            } else
                cmd += string.Format("G90G10L2P{0}{1}", row.Id, newpos.ToString(axis == "All" || axis == "ClearAll" ? GrblInfo.AxisFlags : GrblInfo.AxisLetterToFlag(axis)));

            WriteCommandAndWait(cmd);
            if(mChanged)
                WriteCommandAndWait(grbl.IsMetric ? "G20" : "G21");
            row.MarkCommitted();
        }

        // Comms.com.WriteCommand only ENQUEUES the send - it returns immediately, before the controller has
        // actually processed the line. Block (same Thread+DoEvents pump idiom as GrblWorkParameters.Get and
        // btnRowGetMPos_Click, both already in this file) until the controller actually acknowledges with
        // "ok" before letting the caller proceed to MarkCommitted() or a subsequent re-query - otherwise a
        // later re-query (e.g. GrblWorkParameters.Get on the next Activate(true) after switching tabs away
        // and back) could race ahead of a write that hadn't reached the controller yet.
        private bool WriteCommandAndWait(string cmd)
        {
            bool? res = null;
            CancellationToken cancellationToken = new CancellationToken();

            new Thread(() =>
            {
                res = WaitFor.AckResponse<string>(
                    cancellationToken,
                    null,
                    a => parameters.OnResponseReceived += a,
                    a => parameters.OnResponseReceived -= a,
                    1500, () => Comms.com.WriteCommand(cmd));
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            return res == true;
        }

        private static CoordinateSystem RowOf(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as CoordinateSystem;
        }

        // Walks up from a hit/focused visual to the DataGridRow's own item (a CoordinateSystem) - used to
        // tell "moved to another field in the SAME row" (X -> Y, no commit yet) apart from "actually left
        // the row" (commit now), since NumericField's own LostFocus fires on every such move, not just the
        // ones that matter.
        private static CoordinateSystem RowOfVisual(DependencyObject d)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.DataContext is CoordinateSystem cs)
                    return cs;
                d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // G28/G30/G92 always confirm before committing (never auto-silent) - saveOffset no longer issues any
        // motion command for these (see its own comment), but they're still important reference positions,
        // so an accidental edit shouldn't commit silently just because focus happened to move on.
        private static readonly HashSet<string> ConfirmBeforeCommitCodes = new HashSet<string> { "G28", "G30", "G92" };

        // Fires on every X/Y/Z field's LostFocus, but only actually commits once focus has left the ROW
        // entirely (not just moved from X to Y within it) AND something in the row actually changed
        // (row.IsEdited) - re-checked one dispatcher tick later so Keyboard.FocusedElement reflects where
        // focus actually landed, not where it's leaving from.
        private void NumericField_LostFocus(object sender, RoutedEventArgs e)
        {
            var row = RowOf(sender);
            if (row == null)
                return;

            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (!row.IsEdited)
                    return;   // nothing changed, or already committed by a sibling field's own LostFocus this tick

                var focused = Keyboard.FocusedElement as DependencyObject;
                if (focused != null && RowOfVisual(focused) == row)
                    return;   // still within the same row - not a row-leave yet

                if (ConfirmBeforeCommitCodes.Contains(row.Code))
                {
                    if (AppDialogs.Show(
                            string.Format("Save the current fields as {0}?", row.Code),
                            "Set " + row.Code, MessageBoxButton.YesNo, MessageBoxImage.Question, id: "offset.confirmRowLeave") != MessageBoxResult.Yes)
                        return;
                }

                saveOffset(row, "All");
            }), DispatcherPriority.Input);
        }

        // "Restore to value at startup" (row context menu) - resets the displayed/staged fields only, same
        // as the Grbl settings tree's own revert; the normal row-leave commit takes it from there.
        private void offsetRestoreStartup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is CoordinateSystem row)
                row.RestoreStartup();
        }

        // Zeroes this row's fields and commits immediately - same "Clear" semantics as before (an immediate
        // write, not just a display reset).
        private void btnRowClear_Click(object sender, RoutedEventArgs e)
        {
            var row = RowOf(sender);
            if (row == null)
                return;
            for (var i = 0; i < row.Values.Length; i++)
                row.Values[i] = 0d;
            saveOffset(row, "ClearAll");
        }

        private void RequestStatus ()
        {
            parameters.WorkPositionOffset.Z = double.NaN;
            if (double.IsNaN(parameters.WorkPosition.X) || true) // If not NaN then MPG is polling
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_STATUS_REPORT_ALL));
        }

        // Stages this row's fields from the live machine position - NOT written to the controller (that's
        // what the row's own Set position button is for).
        private void btnRowGetMPos_Click(object sender, RoutedEventArgs e)
        {
            var row = RowOf(sender);
            if (row == null)
                return;

            getPosTargetRow = row;

            bool? res = null;
            CancellationToken cancellationToken = new CancellationToken();

            awaitCoord = true;

            parameters.OnRealtimeStatusProcessed += DataReceived;

            new Thread(() =>
            {
                res = WaitFor.AckResponse<string>(
                    cancellationToken,
                    null,
                    a => GotPosition += a,
                    a => GotPosition -= a,
                    1000, () => RequestStatus());
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            parameters.OnRealtimeStatusProcessed -= DataReceived;
            getPosTargetRow = null;
        }

        #endregion

        private void DataReceived(string data)
        {
            if (awaitCoord)
            {
                Thread.Sleep(50);
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_STATUS_REPORT));
            } else
                GotPosition?.Invoke("ok");
        }
    }
}
