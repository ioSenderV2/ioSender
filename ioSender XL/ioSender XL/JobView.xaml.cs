/*
 * JobView.xaml.cs - part of ioSender
 *
 * v0.47 / 2026-04-29 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2026, Io Engineering (Terje Io)
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
using System.ComponentModel;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Threading;
using CNC.Core;
using CNC.Controls;

namespace GCode_Sender
{
    /// <summary>
    /// Interaction logic for JobView.xaml
    /// </summary>
    public partial class JobView : UserControl, ICNCView
    {
        private bool? initOK = null;
        private bool isBooted = false, isCameraClaimed = false, holdActivated = false;

        // Set when the operator chooses to stay on a connection whose capabilities ($I) never loaded.
        // While true the controller is held in check mode - parsed, acknowledged, never executed - so a
        // connection ioSender cannot reason about can still be inspected but can never move the machine.
        // Cleared only by a connect that actually reads $I.
        private bool lockedInCheckMode = false;
        private GrblViewModel model;
        private bool jogConfigHooked = false;

        // Push the App jog config into the (CNC.Core) keyboard handler. That handler can't see AppConfig, so the
        // Controls layer owns this - called at startup and again whenever the App jog config changes (below), so
        // edits on Settings:App apply live to the Ctrl keyboard jog and the status-bar "Jog step" readout.
        private void ApplyJogConfig()
        {
            if (model == null)
                return;
            var jog = AppConfig.Settings.Jog;
            model.Keyboard.JogStepDistance = jog.StepDistance;
            model.Keyboard.JogDistances[(int)JogMode.Slow] = jog.SlowDistance;
            model.Keyboard.JogDistances[(int)JogMode.Fast] = jog.FastDistance;
            model.Keyboard.JogFeedrates[(int)JogMode.Step] = jog.StepFeedrate;
            model.Keyboard.JogFeedrates[(int)JogMode.Slow] = jog.SlowFeedrate;
            model.Keyboard.JogFeedrates[(int)JogMode.Fast] = jog.FastFeedrate;
            model.Keyboard.DefaultSpeedFast = jog.DefaultSpeedFast;
            model.Keyboard.IsJoggingEnabled = jog.KeyboardEnable;
        }

        // When the controller exposes firmware jog settings ($50-$55, HasFirmwareJog), mirror an edited App jog
        // value down to the matching controller setting - only when Idle, and only if it actually differs (avoids
        // redundant EEPROM writes and the read-back it triggers).
        private void WriteFirmwareJog(string prop)
        {
            if (model == null || !GrblInfo.HasFirmwareJog || model.GrblState.State != GrblStates.Idle)
                return;

            var jog = AppConfig.Settings.Jog;
            grblHALSetting setting;
            double val;
            switch (prop)
            {
                case nameof(JogConfig.StepFeedrate): setting = grblHALSetting.JogStepSpeed; val = jog.StepFeedrate; break;
                case nameof(JogConfig.SlowFeedrate): setting = grblHALSetting.JogSlowSpeed; val = jog.SlowFeedrate; break;
                case nameof(JogConfig.FastFeedrate): setting = grblHALSetting.JogFastSpeed; val = jog.FastFeedrate; break;
                case nameof(JogConfig.StepDistance): setting = grblHALSetting.JogStepDistance; val = jog.StepDistance; break;
                case nameof(JogConfig.SlowDistance): setting = grblHALSetting.JogSlowDistance; val = jog.SlowDistance; break;
                case nameof(JogConfig.FastDistance): setting = grblHALSetting.JogFastDistance; val = jog.FastDistance; break;
                default: return;
            }

            double cur = GrblSettings.GetDouble(setting);
            if (!double.IsNaN(cur) && Math.Abs(cur - val) < 1e-9)
                return;

            model.ExecuteCommand("$" + ((int)setting).ToString() + "=" + val.ToInvariantString());
        }
        private IInputElement focusedControl = null;
        private Controller Controller = null;
        private SidebarItem thcFlyout = null;
        // References to dynamically-placed panels that code-behind needs for keyboard-jog focus gating.
        private SpindleControl spindleControl = null;
        private WorkParametersControl workParametersControl = null;
        private DROControl _dro = null;            // captured when a DRO panel is placed (left or right); may be null
        private LimitsControl _limits = null;      // captured when a Program-limits panel is placed; may be null

        public JobView()
        {
            InitializeComponent();

            BuildMainPanels();
            BuildLeftPanels();

            DataContextChanged += View_DataContextChanged;
        }

        // Populate the six configurable main-page slots from Config.MainPanels (ioSender XL).
        // Panels not placed here are shown as flyouts (see MainWindow). Applied on restart.
        private const int MaxMainPanels = 8;   // keep in sync with MainPageEditor

        private void BuildMainPanels()
        {
            if (AppConfig.Settings.Base == null)   // config not loaded yet (JobView ctor runs before LoadConfig)
            {
                // Re-run before the first paint (priority above Render) so the configured layout is already in
                // place when the window first renders - avoids the empty/default-then-populated flash on launch.
                Dispatcher.BeginInvoke(new System.Action(BuildMainPanels), System.Windows.Threading.DispatcherPriority.DataBind);
                return;
            }

            mainSlotsLeft.Children.Clear();
            mainSlotsRight.Children.Clear();

            var names = AppConfig.Settings.Base.MainPanels;
            var placed = new System.Collections.Generic.HashSet<string>();
            var panels = new System.Collections.Generic.List<UserControl>();

            for (int i = 0; names != null && i < names.Count && panels.Count < MaxMainPanels; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name) || placed.Contains(name))
                    continue;

                var def = MainPanelRegistry.ByName(name);
                if (def == null || !def.CanBeMainPanel || def.CreateMainPanel == null)
                    continue;

                var ctl = def.CreateMainPanel();
                panels.Add(ctl);
                placed.Add(name);
                CaptureRefs(ctl);
            }

            mainPanels.Clear();
            mainPanels.AddRange(panels);
            mainSplit = -1;   // the columns were just cleared, so force a re-parent even if the split is unchanged
            DistributeMainPanels();
        }

        // The panels built by BuildMainPanels, held so the two columns can be re-flowed on a resize without
        // rebuilding them - recreating would discard each panel's live state and re-run CaptureRefs (which
        // subscribes DRO events), so creation happens once and only the parenting changes afterwards.
        private readonly System.Collections.Generic.List<UserControl> mainPanels = new System.Collections.Generic.List<UserControl>();
        private int mainSplit = -1;   // last applied split point; -1 = nothing distributed yet
        private bool postRenderSplitDone = false;   // the one deferred re-decide once the panels have rendered

        // Flow the panels down the FIRST column and start the second only when the next panel would not fit
        // in the height that is left.
        //
        // It used to balance the two columns by height - half the total each - which meant two short panels
        // always landed one per column, and since both columns carry MinWidth=250 the second reserved its
        // width whether or not it earned it. Two columns exist to avoid a scrollbar when a lot of panels are
        // assigned, not as a look; with a few panels (especially with the jog pad off, which hands this row
        // the pad's height) they belong in one column and the other should disappear so the workspace gets
        // the width back.
        private void DistributeMainPanels()
        {
            // Available height is only known after the first layout pass. Until then put everything in the
            // first column - MainScrollLeft_SizeChanged re-runs this the moment a real height exists, and
            // one column is the right answer far more often than not, so there is no visible reshuffle.
            // Less whatever the flyout clearance is holding at the top - that margin is inside the viewport,
            // so it is height the panels genuinely cannot use.
            double available = mainScrollLeft.ActualHeight - mainSlotsLeft.Margin.Top;
            double width = mainScrollLeft.ViewportWidth > 0d ? mainScrollLeft.ViewportWidth : 250d;

            // The split has to be decided from heights the panels ACTUALLY rendered at. Before the first
            // arrange every ActualHeight is 0 and the only figure available is a Measure() guess, which is
            // what got this wrong twice: it over-reports for content that wraps at the guessed width. So on
            // that first pass put everything in column one - always a legal arrangement, the ScrollViewer
            // copes - and re-decide once there are real numbers. postRenderSplitDone stops it re-deferring
            // for ever if a panel legitimately measures zero.
            bool rendered = mainPanels.All(p => p.ActualHeight > 0d);
            if (!rendered && !postRenderSplitDone)
            {
                postRenderSplitDone = true;
                Dispatcher.BeginInvoke(new System.Action(DistributeMainPanels), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            int split = mainPanels.Count;
            if (available > 0d && rendered)
            {
                double used = 0d;
                for (int i = 0; i < mainPanels.Count; i++)
                {
                    double height = PanelHeight(mainPanels[i], width);
                    // Spill when MOST of the panel would not fit, not when it fails to fit entirely. The
                    // strict test sent Goto to the second column for missing by 0.4px (measured: 526.0 into
                    // 525.6) and left a 143px hole behind it - which reads as "there was plenty of room",
                    // because there very nearly was. The column scrolls, so a panel hanging slightly over the
                    // bottom is a far better outcome than a gap here and a panel stranded over there.
                    //
                    // i > 0: the first panel always goes in the first column even if it is taller than the
                    // viewport on its own - moving it right would only leave the first column empty instead.
                    if (i > 0 && used + height / 2d > available) { split = i; break; }
                    used += height;
                }
            }

            if (CNC.Core.DebugLog.Enabled)
                CNC.Core.DebugLog.Write("layout", string.Format(
                    "DistributeMainPanels: avail={0:0.#} (viewportH={1:0.#} - clearance={2:0.#}) width={3:0.#} rendered={4} split={5}/{6}  heights=[{7}]",
                    available, mainScrollLeft.ActualHeight, mainSlotsLeft.Margin.Top, width, rendered,
                    split, mainPanels.Count,
                    string.Join(", ", mainPanels.Select(p => p.GetType().Name + "=" + PanelHeight(p, width).ToString("0.#")))));

            if (split != mainSplit)
            {
                mainSplit = split;

                mainSlotsLeft.Children.Clear();
                mainSlotsRight.Children.Clear();
                for (int i = 0; i < mainPanels.Count; i++)
                    (i < split ? mainSlotsLeft : mainSlotsRight).Children.Add(mainPanels[i]);

                // An empty column must take no width at all, not MinWidth's worth of blank.
                mainScrollRight.Visibility = mainSlotsRight.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            ApplyFlyoutClearance();
        }

        // Start the RIGHT-MOST occupied panel column below the flyout tab strip.
        //
        // The flyout panels are children of the main window's 22px sidebar canvas but render outside it
        // (Canvas.Right=22, ClipToBounds=False, ZIndex 1), so an open or pinned flyout floats over the
        // right-most panel column and hides whatever is at the top of it. The gap is reserved unconditionally
        // rather than appearing when a flyout opens: the panels would otherwise jump down and back every time
        // one is toggled, and a stack that moves under the pointer is worse than a fixed strip of blank.
        // Deferred to Loaded priority, which runs AFTER the arrange pass. Measuring inline was wrong in the one
        // case that matters: the column being measured has usually just been un-collapsed by the caller, and an
        // element that has not been arranged since still reports its OLD position - so the gap came out as
        // though that column started at the top of the window (roughly the whole strip height) instead of the
        // true overhang, and nothing ever recomputed it. Coalesced, so a burst of resize events measures once.
        private bool clearancePending = false;

        private void ApplyFlyoutClearance()
        {
            if (clearancePending)
                return;

            clearancePending = true;
            Dispatcher.BeginInvoke(new System.Action(MeasureFlyoutClearance), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void MeasureFlyoutClearance()
        {
            clearancePending = false;

            bool rightUsed = mainSlotsRight.Children.Count > 0;
            double gap = FlyoutClearance(rightUsed ? mainScrollRight : mainScrollLeft);

            // Setting a Thickness to an equal value is a no-op in WPF, and neither margin can change its
            // ScrollViewer's size (the Grid fixes those), so this cannot loop through layout.
            mainSlotsLeft.Margin = new Thickness(5d, rightUsed ? 0d : gap, 0d, 0d);
            mainSlotsRight.Margin = new Thickness(0d, rightUsed ? gap : 0d, 0d, 5d);
        }

        // How far the flyout strip's bottom edge sits below the top of host, or 0 when it is already above it
        // (the jog pad, when shown, can push the panels clear on its own).
        //
        // Measured against the HOST ScrollViewer, never the inner StackPanel: the margin this feeds moves the
        // StackPanel, so measuring against that would compute a gap, apply it, then measure zero and remove it
        // again, forever. The ScrollViewer's own position is fixed by the Grid and does not move.
        private double FlyoutClearance(FrameworkElement host)
        {
            var strip = MainWindow.SidebarFlyoutStrip;
            if (strip == null || !strip.IsVisible || host == null || !host.IsVisible || strip.ActualHeight <= 0d)
                return 0d;

            try
            {
                return System.Math.Max(0d, strip.TransformToVisual(host).Transform(new Point(0d, strip.ActualHeight)).Y);
            }
            catch (System.InvalidOperationException)
            {
                return 0d;   // not a common ancestor (yet) - a later pass will get it
            }
        }

        // How much vertical room a panel actually needs, margins included (a StackPanel stacks by desired size,
        // which counts margin).
        //
        // Prefers the height it RENDERED at over a fresh Measure. Measuring against a guessed 250px reported
        // several panels taller than they really are - anything whose content wraps at 250 but not at the real
        // column width - which pushed a panel that comfortably fitted into the second column (Goto, observed
        // 2026-08-03 with visible room left below it). Measure is only the fallback for a panel that has never
        // been arranged, and even then it uses the true viewport width rather than a constant.
        private static double PanelHeight(UserControl panel, double width)
        {
            double h = panel.ActualHeight;
            if (h <= 0d)
            {
                panel.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                h = panel.DesiredSize.Height;
            }
            return h + panel.Margin.Top + panel.Margin.Bottom;
        }

        // The first column's height changes both when the window resizes and when the jog pad above it is
        // toggled (its row is Auto, so collapsing it hands this row the height) - one signal covers both.
        private void MainScrollLeft_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.HeightChanged && mainPanels.Count > 0)
                DistributeMainPanels();
        }

        // The second column's own size settling (in particular the 0 -> real change when it is un-collapsed)
        // is the moment its position becomes measurable, so re-measure the clearance from here too.
        private void MainScrollRight_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyFlyoutClearance();
        }

        // Capture references to panels that have host wiring (focus gating, program-limits reveal) so they
        // work wherever the user places them (left, right, or not at all -> the reference stays null).
        private void CaptureRefs(UserControl ctl)
        {
            if (ctl is SpindleControl sc)
                spindleControl = sc;
            else if (ctl is WorkParametersControl wp)
                workParametersControl = wp;
            else if (ctl is DROControl dro)
            {
                _dro = dro;
                dro.DROEnabledChanged += DRO_DROEnabledChanged;
            }
            else if (ctl is LimitsControl lc)
            {
                _limits = lc;   // stays visible; shows machine limits with no program, program limits when loaded
            }
        }

        // Populate the area left of the 3D view from Config.LeftPanels (default = DRO + Program limits).
        // Signals/Status stay fixed below it (t2). Applied on restart.
        private void BuildLeftPanels()
        {
            if (AppConfig.Settings.Base == null)   // config not loaded yet (JobView ctor runs before LoadConfig)
            {
                // Build before first paint (above Render) so the configured left column is in place on launch.
                Dispatcher.BeginInvoke(new System.Action(BuildLeftPanels), System.Windows.Threading.DispatcherPriority.DataBind);
                return;
            }

            t1.Children.Clear();
            var placed = new System.Collections.Generic.HashSet<string>();

            foreach (var name in AppConfig.Settings.Base.LeftPanels)
            {
                if (string.IsNullOrEmpty(name) || placed.Contains(name))
                    continue;

                var def = MainPanelRegistry.ByName(name);
                if (def == null || !def.CanBeMainPanel || def.CreateMainPanel == null)
                    continue;

                var ctl = def.CreateMainPanel();
                ctl.HorizontalAlignment = HorizontalAlignment.Left;
                ctl.VerticalAlignment = VerticalAlignment.Top;
                t1.Children.Add(ctl);
                placed.Add(name);
                CaptureRefs(ctl);
            }
        }

        private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is GrblViewModel)
            {
                model = (GrblViewModel)e.NewValue;
                model.PropertyChanged += OnDataContextPropertyChanged;
                model.ReconnectInit += OnReconnectInit;
                DataContextChanged -= View_DataContextChanged;
                //          model.OnGrblReset += Model_OnGrblReset;
            }
        }

        private void OnDataContextPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is GrblViewModel) switch (e.PropertyName)
                {
                case nameof(GrblViewModel.GrblState):
                    // Enforce the check-mode lock taken when the operator chose to stay on a connection
                    // whose capabilities never loaded. Entering check mode once is not a safety guarantee -
                    // a soft reset, a stray $C, or the controller's own restart all clear it, and the
                    // machine would then be free to move on a connection ioSender cannot reason about.
                    // Re-assert it whenever the controller settles anywhere else. Cleared only by a
                    // reconnect that actually reads $I (see InitSystem / PrepareForReconnect).
                    if (lockedInCheckMode && Controller != null && !Controller.ResetPending &&
                        sender is GrblViewModel cm && !cm.IsCheckMode &&
                        cm.GrblState.State != GrblStates.Unknown && cm.GrblState.State != GrblStates.Alarm)
                    {
                        if (DebugLog.Enabled)
                            DebugLog.Write("connect", string.Format("check-mode lock: state went to {0}, re-asserting $C", cm.GrblState.State));
                        Comms.com.WriteCommand(GrblConstants.CMD_CHECK);
                    }

                    if (Controller != null && !Controller.ResetPending)
                    {
                        if (isBooted && initOK == false && (sender as GrblViewModel).GrblState.State != GrblStates.Alarm)
                            Dispatcher.BeginInvoke(new System.Action(() => InitSystem()), DispatcherPriority.ApplicationIdle);
                        else if ((sender as GrblViewModel).GrblState.State == GrblStates.Hold && !MainWindow.ui.JobRunning)
                        {
                            holdActivated = true;
                            MainWindow.ui.JobRunning = true;
                        }
                        else if ((sender as GrblViewModel).GrblState.State != GrblStates.Hold && holdActivated)
                            MainWindow.ui.JobRunning = holdActivated = false;
                        // Safety net: when the controller is idle and no job is streaming, un-strand any tab left
                        // disabled by a lock/hold/probe (e.g. Start Job's auto-probe) whose clear didn't line up.
                        else if ((sender as GrblViewModel).GrblState.State == GrblStates.Idle && !(sender as GrblViewModel).IsJobRunning)
                            MainWindow.ui.RefreshTabsIdle();
                    }
                    break;

                case nameof(GrblViewModel.IsGCLock):
                        MainWindow.ui.JobRunning = (sender as GrblViewModel).IsJobRunning;
           //             MainWindow.EnableView(!(sender as GrblViewModel).IsGCLock, ViewType.Probing);
                    break;

                case nameof(GrblViewModel.IsSleepMode):
                    EnableUI(!(sender as GrblViewModel).IsSleepMode);
                    break;

                case nameof(GrblViewModel.IsJobRunning):
                    MainWindow.ui.JobRunning = (sender as GrblViewModel).IsJobRunning;
                    if(GrblInfo.ManualToolChange)
                        GrblCommand.ToolChange = (sender as GrblViewModel).IsJobRunning ? "T{0}M6" : "M61Q{0}";
                    break;

                case nameof(GrblViewModel.IsToolChanging):
                    MainWindow.ui.JobRunning = (sender as GrblViewModel).IsToolChanging || (sender as GrblViewModel).IsJobRunning;
                    break;

                case nameof(GrblViewModel.Tool):
                if (GrblInfo.ManualToolChange && (sender as GrblViewModel).Tool != GrblConstants.NO_TOOL)
                    GrblWorkParameters.RemoveNoTool();
                break;

                case nameof(GrblViewModel.GrblReset):
                    // Controller is null mid-reconnect (PrepareForReconnect cleared it, Activate not yet re-run);
                    // a reset notification arriving in that window must be ignored - Activate re-runs the handshake.
                    if ((sender as GrblViewModel).IsReady)
                    {
                        if (Controller != null && !Controller.ResetPending && (sender as GrblViewModel).GrblReset)
                        {
                            initOK = null;
                            Dispatcher.BeginInvoke(new System.Action(() => Activate(true, ViewType.GRBL)), DispatcherPriority.ApplicationIdle);
                        }
                    }
                    break;

                case nameof(GrblViewModel.ParserState):
                    if (Controller != null && !Controller.ResetPending && (sender as GrblViewModel).GrblReset)
                    {
                        EnableUI(true);
                        (sender as GrblViewModel).GrblReset = false;
                    }
                    break;

                case nameof(GrblViewModel.FileName):
                    string filename = (sender as GrblViewModel).FileName;
                    MainWindow.ui.WindowTitle = filename;

                    if(string.IsNullOrEmpty(filename))
                        MainWindow.CloseFile();
                    else if ((sender as GrblViewModel).IsSDCardJob)
                    {
                        MainWindow.EnableView(false, ViewType.GCodeViewer);
                    }
                    else if (AppConfig.Settings.GCodeViewer.IsEnabled)
                    {
                        if (filename.StartsWith("Wizard:"))
                        {
                            //MainWindow.EnableView(true, ViewType.GCodeViewer);
                            workspace.ShowToolpath();
                        }
                        else if (!string.IsNullOrEmpty(filename))
                        {
                            //MainWindow.GCodeViewer.Open(GCode.File.Tokens);
                            //MainWindow.EnableView(true, ViewType.GCodeViewer);
                            MainWindow.ui.RunControl.EnablePolling(false);
                            workspace.ShowToolpath();
                            MainWindow.ui.RunControl.EnablePolling(true);
                        }
                    }
                    break;
            }
        }

        #region Methods and properties required by CNCView interface

        public ViewType ViewType { get { return ViewType.GRBL; } }
        public bool CanEnable { get { return true; } }

        // Reset controller state so a fresh Connect (e.g. switching simulators) re-runs the handshake.
        public void PrepareForReconnect()
        {
            initOK = null;
            isBooted = false;
            Controller = null;
        }

        // Auto-reconnect re-established the link (e.g. after a $REBOOT). Re-run the handshake so refreshed
        // capabilities ($I: ATC, tool count, ...) replace the pre-reboot values. Runs immediately regardless
        // of Alarm state - $I is explicitly Alarm-safe on the firmware side (build_info()'s own state guard
        // allows STATE_ALARM), and a boot after any reset commonly lands in Alarm (homing required), which
        // used to defer this until the GrblState handler below caught a later unlock - too late to surface
        // e.g. a hang-watchdog restart notice (GrblInfo.HangDetectedHook) promptly. The GrblState handler
        // still covers the case where InitSystem itself fails while stuck in Alarm (isBooted && initOK == false).
        private void OnReconnectInit()
        {
            initOK = false;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (initOK == false)
                    initOK = InitSystem();
            }), DispatcherPriority.ApplicationIdle);
        }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (activate)
            {
                MainWindow.ui.RunControl.RewindFile();
                MainWindow.ui.RunControl.CallHandler(model.IsSDCardJob ? StreamingState.Start : (GCode.File.IsLoaded ? StreamingState.Idle : StreamingState.NoFile), false);

                model.ResponseLogFilterOk = AppConfig.Settings.Base.FilterOkResponse;

                if (Controller == null)
                    Controller = new Controller(model);

                if (initOK != true)
                {
                    focusedControl = this;

                    Controller.RestartResult restartResult = Controller.Restart();

                    if (DebugLog.Enabled)
                        DebugLog.Write("connect", string.Format("Restart() -> {0}  (isBooted={1})", restartResult, isBooted));

                    switch (restartResult)
                    {
                        case Controller.RestartResult.Ok:
                            if (!isBooted)
                                Dispatcher.BeginInvoke(new System.Action(() => OnBooted()), DispatcherPriority.ApplicationIdle);
                            initOK = InitSystem();
                            if (DebugLog.Enabled)
                                DebugLog.Write("connect", string.Format("InitSystem() -> {0}, GrblInfo.IsLoaded={1}", initOK, GrblInfo.IsLoaded));
                            break;

                        case Controller.RestartResult.Close:
                            MainWindow.ui.Close();
                            break;

                        case Controller.RestartResult.Exit:
                            Environment.Exit(-1);
                            break;
                    }

                    // Close the loop opened by "Waiting for controller (...)". That notice pops the status log,
                    // so without a definite outcome the last thing on screen still reads as "waiting" long after
                    // the connect settled - which is exactly how a connect that worked and one that quietly
                    // didn't became indistinguishable.
                    //
                    // Precedence: a message Restart() set for itself wins (MsgHome on a homing-required boot says
                    // something actionable, a generic "Connected" does not). Success needs BOTH Ok and a good
                    // InitSystem() - Ok alone only means the controller answered. NoResponse used to fall through
                    // this switch unannounced (the GrblInfo.IsLoaded reconnect branches all blank the response
                    // rather than raising a dialog), and silence there is the worst of the three outcomes.
                    // A connection whose capabilities were never read is worse than no connection. GrblInfo
                    // empty does not read as "unknown" anywhere downstream - it reads as every capability
                    // ABSENT, so expressions, ATC, probing and the filesystem all quietly refuse, each with
                    // its own confident message about what this controller does not support. Observed
                    // 2026-08-12 on both hardware and the simulator: "$I" was never sent, and the Setup tab
                    // insisted the firmware lacked NGC expressions while that same firmware reported EXPR.
                    //
                    // So refuse the connection outright rather than carrying on blind, and deliberately
                    // WITHOUT a "continue anyway" button: there is nothing useful to continue into, and the
                    // alternative to guessing capabilities is not guessing them optimistically - assuming
                    // EXPR is present would stream o-word flow control to a controller that may not take it,
                    // which wedges grblHAL outright. Reconnecting is the recovery, and it works.
                    // Deliberately NOT gated on RestartResult.Ok. The condition that matters is "the link is
                    // open but the capabilities were never read", and NoResponse reaches that state too -
                    // it reports "did not complete the handshake" and then leaves the connection up anyway,
                    // which is the state observed 2026-08-12 at 01:12: jogging refused with "enable soft
                    // limits ($20=1) first" because every setting read as zero out of an empty GrblInfo,
                    // while the status line claimed not connected. Guarding only the Ok branch missed the
                    // very path that produced the report.
                    if ((restartResult == Controller.RestartResult.Ok || restartResult == Controller.RestartResult.NoResponse) &&
                        Comms.com != null && Comms.com.IsOpen && !GrblInfo.IsLoaded)
                    {
                        if (DebugLog.Enabled)
                            DebugLog.Write("connect", "connected but $I never loaded - offering disconnect or check-mode");

                        // A controller ALREADY in check mode is not a mystery to be reported - it is the
                        // whole explanation. Grbl answers every '$' query with error:8 while in check mode,
                        // so $I cannot possibly succeed, and neither can the reconnect the dialog offers:
                        // check mode survives it. Observed 2026-08-21 on a diode laser left in check mode by
                        // an earlier session - connect, fail, reconnect, fail, indefinitely.
                        //
                        // So leave check mode and try again, rather than asking a question whose answers
                        // both lead back here.
                        if (model.IsCheckMode)
                        {
                            if (DebugLog.Enabled)
                                DebugLog.Write("connect", "controller was ALREADY in check mode - $ queries return error:8 there; leaving it and reconnecting");

                            model.Message = "Controller was in check mode, which blocks $I - leaving check mode and reconnecting...";
                            Comms.com.WriteCommand(GrblConstants.CMD_CHECK);    // toggle: this LEAVES check mode

                            Dispatcher.BeginInvoke(new System.Action(() => MainWindow.ui.ReconnectAfterFailedHandshake()),
                                                   DispatcherPriority.ApplicationIdle);
                            return;
                        }

                        bool disconnect = AppDialogs.Show(
                            "Could not read this controller's capabilities ($I).\r\n\r\n" +
                            "Nothing is wrong with the controller - the query went unanswered during connect, and " +
                            "connecting again normally fixes it. Until then ioSender cannot tell what this " +
                            "controller supports, so every capability reads as absent.\r\n\r\n" +
                            "Reconnect now, or stay connected in CHECK MODE to look around? In check mode g-code is " +
                            "parsed but never executed - the machine cannot move until you reconnect.",
                            "ioSender - capabilities not read", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes;

                        if (disconnect)
                        {
                            if (DebugLog.Enabled)
                                DebugLog.Write("connect", "reconnecting at operator's choice");
                            model.Message = "Capabilities ($I) could not be read - reconnecting...";
                            // Actually reconnect, because that is what the prompt offers. Dropping the link
                            // and leaving the operator at "not connected" is not what "Reconnect now" says,
                            // and it was not what happened when they picked it.
                            //
                            // Deferred rather than called here: this runs inside the connect attempt that
                            // just failed, and re-entering the connect path from within it would nest the
                            // handshake inside itself. ApplicationIdle lets this one finish unwinding first -
                            // the same reason InitSystem is re-invoked that way above.
                            Dispatcher.BeginInvoke(new System.Action(() => MainWindow.ui.ReconnectAfterFailedHandshake()),
                                                   DispatcherPriority.ApplicationIdle);
                        }
                        else
                        {
                            // Staying connected is only safe if nothing can MOVE. Check mode is the
                            // controller's own guarantee of that - it parses and acknowledges but never
                            // executes motion - and it is enforced below rather than merely entered, because
                            // an unenforced safety mode is one stray toggle away from not being one.
                            // $C TOGGLES check mode; it does not set it. Sending it while the controller is
                            // already in check mode LEAVES check mode - and the enforcement below then sees
                            // that and sends $C again, putting it back. That oscillation is visible in the
                            // wire log as two $C a couple of seconds apart with nothing achieved between
                            // them (2026-08-21).
                            if (!model.IsCheckMode)
                                Comms.com.WriteCommand(GrblConstants.CMD_CHECK);

                            lockedInCheckMode = true;
                            if (DebugLog.Enabled)
                                DebugLog.Write("connect", "DEGRADED - staying connected, locking check mode until reconnect");
                            model.Message = "Capabilities ($I) unread - LOCKED IN CHECK MODE, no motion. Reconnect to restore normal operation.";
                        }
                    }
                    else if (!string.IsNullOrEmpty(Controller.Message))
                        model.Message = Controller.Message;
                    else if (restartResult == Controller.RestartResult.Ok && initOK == true)
                    {
                        model.Message = string.Format((string)FindResource("MsgConnected"), AppConfig.Settings.Base.PortParams);
                        LogConnectionDetail();
                    }
                    else if (restartResult == Controller.RestartResult.NoResponse)
                        model.Message = string.Format((string)FindResource("MsgNotConnected"), AppConfig.Settings.Base.PortParams);
                    else
                        // Controller.Message is empty unless Restart had something specific to say, so
                        // state the outcome rather than assigning nothing - silence here is precisely the
                        // "connected or not?" ambiguity the block above exists to close.
                        model.Message = string.IsNullOrEmpty(Controller.Message)
                            ? string.Format("Connect to {0} did not complete ({1})", AppConfig.Settings.Base.PortParams, restartResult)
                            : Controller.Message;
                }
                
                if (initOK == null)
                    initOK = false;

#if ADD_CAMERA
                if (MainWindow.UIViewModel.Camera != null && !isCameraClaimed)
                {
                    MainWindow.UIViewModel.Camera.MoveOffset += Camera_MoveOffset;
                    MainWindow.UIViewModel.Camera.IsVisibilityChanged += Camera_Opened;
                    MainWindow.UIViewModel.Camera.IsMoveEnabled = isCameraClaimed = true;
                }
#endif
                //if (viewer == null)
                //    viewer = new Viewer();

                if (GCode.File.IsLoaded)
                    MainWindow.ui.WindowTitle = ((GrblViewModel)DataContext).FileName;

                // Keyboard jogging is its own always-available input; KeyboardEnable (default on) is the master
                // switch. IsContinuousJoggingEnabled stays driven by controller capability (set in Grbl.cs).
                ApplyJogConfig();
                if (!jogConfigHooked)
                {
                    jogConfigHooked = true;
                    // Live: re-push to the keyboard handler on any App jog change (so Settings:App edits apply at
                    // once - Ctrl-jog + the status-bar readout), and - when the controller has firmware jog
                    // ($50-$55, HasFirmwareJog) - mirror the edited value down to the controller's setting too.
                    AppConfig.Settings.Jog.PropertyChanged += (s, e) => {
                        ApplyJogConfig();
                        WriteFirmwareJog(e.PropertyName);
                    };
                }

                model.IgnoreNextCycleStart = true;
            }
            else if(ViewType != ViewType.Shutdown)
            {
                if (_dro != null) _dro.IsFocusable = false;
#if ADD_CAMERA
                if (MainWindow.UIViewModel.Camera != null)
                {
                    MainWindow.UIViewModel.Camera.MoveOffset -= Camera_MoveOffset;
                    MainWindow.UIViewModel.Camera.IsMoveEnabled = isCameraClaimed = false;
                }
#endif
                focusedControl = focusedControl = AppConfig.Settings.Base.KeepMdiFocus &&
                                  Keyboard.FocusedElement is TextBox &&
                                   (Keyboard.FocusedElement as TextBox).Tag is string &&
                                    (string)(Keyboard.FocusedElement as TextBox).Tag == "MDI"
                                  ? Keyboard.FocusedElement
                                  : this;
            }

            if (MainWindow.ui.RunControl.Activate(activate)) {
                showProgramLimits();
                Task.Delay(500).ContinueWith(t => _dro?.EnableFocus());
                Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    // focusedControl is only ever assigned on the DEACTIVATE path above, where it records
                    // what had focus so it can be given back. On the activate path - including the very
                    // first activation, from CompleteStartup - nothing has assigned it and the field is
                    // still its null initialiser.
                    //
                    // It normally survives because RunControl.Activate returns false on that first call and
                    // this block is skipped; when it returns true instead, startup dies with a
                    // NullReferenceException before the window is usable (crash logs 2026-08-21, and the
                    // same method on 2026-07-14).
                    //
                    // Falling back to the view is what the deactivate path already does when there is no MDI
                    // box to restore - focus belongs somewhere, and here is the sensible somewhere.
                    (focusedControl ?? this).Focus();
                }), DispatcherPriority.Render);
            }
        }

        public void CloseFile()
        {
            workspace.ClearToolpath();
        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
        }

        #endregion

        // https://stackoverflow.com/questions/5707143/how-to-get-the-width-height-of-a-collapsed-control-in-wpf
        private void showProgramLimits()
        {
            // The Limits panel stays visible; refresh it so it shows program limits when a job is loaded,
            // or the machine soft-limit envelope when not.
            _limits?.Refresh();
            // The bottom panel of each column auto-hides on short windows (generalizes the old Coolant/Goto hiding).
            if (mainSlotsLeft.Children.Count > 0)
                mainSlotsLeft.Children[mainSlotsLeft.Children.Count - 1].Visibility = rhGrid.ActualHeight > 600 ? Visibility.Visible : Visibility.Collapsed;
            if (mainSlotsRight.Children.Count > 0)
                mainSlotsRight.Children[mainSlotsRight.Children.Count - 1].Visibility = rhGrid.ActualHeight > 575 ? Visibility.Visible : Visibility.Collapsed;
        }

#if ADD_CAMERA
        void Camera_Opened()
        {
            model.IsCameraVisible = MainWindow.UIViewModel.Camera.IsVisible;
            Focus();
        }

        void Camera_MoveOffset(CameraMoveMode Mode, double XOffset, double YOffset)
        {
            GrblParserState.Get();
            CNC.GCode.Units units = GrblParserState.Units;
            CNC.GCode.DistanceMode distanceMode = GrblParserState.DistanceMode;

            Comms.com.WriteString("G91G0\r"); // Enter relative metric G0 mode - set scale to 1.0?

            switch (Mode)
            {
                case CameraMoveMode.XAxisFirst:
                    Comms.com.WriteString(string.Format("X{0}\r", XOffset.ToInvariantString("F3")));
                    Comms.com.WriteString(string.Format("Y{0}\r", YOffset.ToInvariantString("F3")));
                    break;

                case CameraMoveMode.YAxisFirst:
                    Comms.com.WriteString(string.Format("Y{0}\r", YOffset.ToInvariantString("F3")));
                    Comms.com.WriteString(string.Format("X{0}\r", XOffset.ToInvariantString("F3")));
                    break;

                case CameraMoveMode.BothAxes:
                    ((GrblViewModel)DataContext).ExecuteCommand(string.Format("X{0}Y{1}", XOffset.ToInvariantString("F3"), YOffset.ToInvariantString("F3")));
                    break;
            }

            if (distanceMode != CNC.GCode.DistanceMode.Incremental)
                Comms.com.WriteString("G90\r");

            if (units != CNC.GCode.Units.Metric)
                Comms.com.WriteString("G20\r");
        }
#endif

        private void OnBooted()
        {
            isBooted = true;

            // Key mappings now live in the App.config "KeyMap" section (loaded at config-load); apply them now
            // that the handlers are registered.
            (model.Keyboard as KeypressHandler)?.LoadMappings();

            if (GrblInfo.NumAxes > 3)
                GCode.File.AddTransformer(typeof(GCodeWrapViewModel), "Wrap to rotary (WIP)", MainWindow.UIViewModel.TransformMenuItems);

            // Work Order compile cache, restore half (a 4-minute script-font compile per restart was the
            // motivating case): now that the controller is booted and its settings are loaded (the cache
            // fingerprint folds them in - this is the earliest moment it CAN be computed), reload the
            // cached compiled program as the job if it still matches the persisted work order. OnBooted
            // runs once per session (isBooted gate at the call site), never over an already-loaded job
            // (a file-open argument wins), and never in an automation instance.
            if (App.TestServerPort < 0)
                CNC.Controls.WorkOrderView.TryAutoRestoreCachedProgram(model);
        }

        /// <summary>
        /// What the connect actually established, written under the "Connected: ..." headline.
        ///
        /// status.log is the only log an operator sees, and it recorded a connect as a single line that
        /// looked identical whether the handshake had read everything or nothing. On 2026-08-24 a
        /// GrblSettings collection that came back EMPTY (see GrblHandshake) sat behind an ordinary-looking
        /// "Connected:" for hours - GrblInfo.MaxTravel derived as 0, the sender emitted a -9999 probe floor,
        /// and the machine alarmed. Finding it needed debug-only instrumentation the operator cannot see.
        ///
        /// So state the facts that decide whether this connection is usable: how many settings were read
        /// (0 is the failure, and it says so), what the firmware says it is and supports, and whether the
        /// controller is already sitting in an alarm that has to be cleared before anything will run.
        /// </summary>
        private void LogConnectionDetail()
        {
            int settings = GrblSettings.Settings.Count;
            model.LogDetail(settings > 0
                ? string.Format("- {0} controller settings read", settings)
                : "- NO controller settings were read - travel limits and soft-limit checks are unavailable. Reconnect.",
                settings == 0);

            var caps = string.IsNullOrWhiteSpace(GrblInfo.NewOptions) ? "(none reported)" : GrblInfo.NewOptions;
            model.LogDetail(string.Format("- {0} {1}{2} - options: {3}",
                string.IsNullOrWhiteSpace(GrblInfo.Identity) ? "controller" : GrblInfo.Identity,
                GrblInfo.Version,
                GrblInfo.Build > 0 ? " build " + GrblInfo.Build : string.Empty,
                caps));

            // An alarm latched before we connected is not reported anywhere else at connect time, and it
            // blocks g-code AND filesystem access ($F answers error:79) until it is cleared - which reads
            // as "the ATC macros are missing" rather than "the machine is in alarm".
            if (model.GrblState.State == GrblStates.Alarm)
                model.LogDetail("- Controller is in ALARM - <Reset> then <Unlock> before running anything", true);
        }

        private bool InitSystem()
        {
            initOK = true;
            int timeout = 5;

            // The shortcut for "already up, controller is mid-homing" must not also skip the one thing
            // this method exists for. Without the IsLoaded test it returns SUCCESS having never sent $I,
            // leaving GrblInfo empty - and an empty GrblInfo does not read as "unknown", it reads as
            // every capability ABSENT: no expressions, no ATC, no probes, no filesystem. That is how a
            // controller which reports EXPR perfectly well was told it lacks expression support.
            if (isBooted && model.GrblState.State == GrblStates.Home && GrblInfo.IsLoaded)
                return true;

            if (DebugLog.Enabled)
                DebugLog.Write("connect", string.Format("InitSystem: enter - isBooted={0} state={1} GrblInfo.IsLoaded={2}",
                    isBooted, model.GrblState.State, GrblInfo.IsLoaded));

            using (new UIUtils.WaitCursor())
            {
                MainWindow.ui.RunControl.EnablePolling(false);
                while (!GrblInfo.Get())
                {
                    if(--timeout == 0)
                    {
                        if (DebugLog.Enabled)
                            DebugLog.Write("connect", "InitSystem: FAILED - $I went unanswered after 5 attempts");
                        model.Message = (string)FindResource("MsgNoResponse");
                        return false;
                    }
                    if (DebugLog.Enabled)
                        DebugLog.Write("connect", string.Format("InitSystem: $I unanswered, retrying ({0} left)", timeout));
                    Thread.Sleep(500);
                }
                // $I answered, so this connection can be reasoned about again - release any check-mode
                // lock a previous degraded connect took. This is the ONLY thing that clears it.
                if (lockedInCheckMode)
                {
                    lockedInCheckMode = false;
                    if (DebugLog.Enabled)
                        DebugLog.Write("connect", "check-mode lock RELEASED - $I loaded on this connect");
                    if (model.IsCheckMode)
                        Comms.com.WriteCommand(GrblConstants.CMD_CHECK);   // toggle back out
                }

                GrblAlarms.Get();
                GrblErrors.Get();
                GrblSettings.Load();
                GrblSettings.WriteSnapshot();   // restore-point snapshot of the settings just read from the controller
                if (GrblInfo.IsGrblHAL)
                {
                    GrblParserState.Get();
                    GrblWorkParameters.Get();
                    GrblSpindles.Get();
                }
                else
                {
                    GrblSpindles.AddDefault();
                    GrblParserState.Get(true);
                }
                MainWindow.ui.RunControl.EnablePolling(true);
            }

            // GrblInfo (incl. the $I-reported IP) is loaded now - remember an IP to default the Connect
            // dialog's network tab to next launch, and (if "Prefer network" is set) migrate a serial link
            // to the network when the controller's telnet port answers.
            AppConfig.Settings.CaptureConnectedIp();
            MainWindow.ui.TryMigrateToNetwork();

            // The real machine is the truth: snapshot its reference points while they are in front of us,
            // and stamp the captured ones onto a simulator every time we connect to one. Exactly one of
            // these does anything per connection - see MachineOffsets.
            MachineOffsets.CaptureFromMachine(model);
            MachineOffsets.ApplyToSimulator(model);

            GrblCommand.ToolChange = GrblInfo.ManualToolChange ? "M61Q{0}" : (GrblInfo.HasATC ? "T{0}M6" : "T{0}");

            showProgramLimits();

            workspace.Set3DViewEnabled(AppConfig.Settings.GCodeViewer.IsEnabled);

            // Remove the main-page tabs this controller can't support (Lathe Tools / SD Card / Probing) and record
            // WHY under Edit Main Page > Unavailable. Each gated view owns its own prerequisite + reason
            // (IAvailabilityGated) - the single source the removal and the listing now share. Survivors are
            // (re)enabled by UpdateConnectionGatedViews on the connect transition. Height Map stays either way (it
            // can still load/apply a saved .map offline, gated at run time instead). Tools now goes too when the
            // controller supports none of the three tools it still hosts (2026-08-02) - it gates itself on its
            // own children, so this one call drops both the sub-tabs and, if nothing survives, the tab itself.
            ComponentAvailability.Note(MainWindow.ui.tabMode.PruneUnavailable());

            MainWindow.EnableView(true, ViewType.Tools);
            MainWindow.EnableView(true, ViewType.StartJob);   // front-door tool - always available

            MainWindow.EnableView(true, ViewType.Offsets);
            MainWindow.EnableView(true, ViewType.GRBLConfig);

            if (GrblInfo.THCMode && thcFlyout == null)
                MainWindow.UIViewModel.SidebarItems.Add(thcFlyout = new SidebarItem(MainWindow.ui.thcControl));

            // Keep the bundled simulator in step with THIS controller's build: derive its option signature and,
            // if the cached sim doesn't match, fetch/build a matching one so a later "connect to simulator" runs
            // a faithful copy. Real controllers only (skip when we're already talking to our own simulator).
            TriggerMatchedSimulatorCheck();

            return true;
        }

        // Fire-and-forget: read the connected controller's options and ensure the bundled simulator matches.
        // Runs on a background thread (network I/O), never blocks connect, and surfaces only a brief status line.
        // A first-time signature dispatches a CI build and then polls for it so the match is ready without a
        // reconnect; an already-built signature is installed from the local or remote cache immediately.
        private void TriggerMatchedSimulatorCheck()
        {
            if (!GrblInfo.IsGrblHAL || CNC.Controls.SimulatorManager.IsSimulatorRunning)
                return;   // meaningful only for a real grblHAL controller

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string sig, detail;
                    var r = CNC.Controls.SimulatorManager.EnsureMatchedSimulator(out sig, out detail);
                    switch (r)
                    {
                        case CNC.Controls.SimulatorManager.MatchResult.InstalledFromCache:
                        case CNC.Controls.SimulatorManager.MatchResult.InstalledFromRelease:
                            PostMessage("Simulator matched to controller (build " + sig + ").");
                            break;
                        case CNC.Controls.SimulatorManager.MatchResult.BuildTriggered:
                            PostMessage("Building a matching simulator (build " + sig + ")...");
                            if (CNC.Controls.SimulatorManager.PollForMatchedRelease(sig))
                                PostMessage("Matching simulator ready (build " + sig + ").");
                            break;
                        case CNC.Controls.SimulatorManager.MatchResult.Failed:
                            System.Diagnostics.Debug.WriteLine("Matched simulator: " + detail);
                            break;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Matched simulator: " + ex.Message); }
            });
        }

        private void PostMessage(string text)
        {
            try { Dispatcher.BeginInvoke((System.Action)(() => model.Message = text)); }
            catch { }
        }

        void EnableUI(bool enable)
        {
            // Status/Signals now live in the fixed bottom run-control bar (Phase 2c), not in JobView,
            // so there's no longer a status control to exclude here.
            foreach (UserControl control in UIUtils.FindFirstLogicalChildren<UserControl>(this))
                control.IsEnabled = enable;
            // disable ui components when in sleep mode
        }
        // Start the currently-loaded in-memory program through the real (flow-controlled) job streamer.
        // Used by generated-program runners (e.g. Surface Spoilboard) once their cut is loaded into GCode.File.
        public void StartLoadedJob()
        {
            if (GCode.File.IsLoaded)
                MainWindow.ui.RunControl.Run(0, false);   // stream the loaded job, don't re-enter ActiveRun
        }

#region UIevents

        void JobView_Load(object sender, EventArgs e)
        {
            MainWindow.ui.RunControl.CallHandler(StreamingState.Idle, true);

            // The flyout strip is populated during startup, after this view is built, and grows/shrinks as
            // flyouts are assigned - so the clearance has to be recomputed when it changes, not just once.
            var strip = MainWindow.SidebarFlyoutStrip;
            if (strip != null && !flyoutStripHooked)
            {
                flyoutStripHooked = true;
                strip.SizeChanged += (s, ev) => { if (ev.HeightChanged) ApplyFlyoutClearance(); };
            }
        }

        private bool flyoutStripHooked = false;

        private void JobView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                MainWindow.ui.RunControl.Focus();
        }

        private void JobView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (GrblInfo.IsLoaded)
                showProgramLimits();
        }

        private void outside_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
        }

        void DRO_DROEnabledChanged(bool enabled)
        {
            if (!enabled)
                Focus();
        }

        // GlobalKeys (a class handler on Window) sees every key before this does and dispatches jog keys
        // for the whole application. Both overrides below used to assign e.Handled unconditionally, which
        // would hand the SAME key to ProcessKeypress a second time - a double jog command. Bail out when it
        // has already been dealt with.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Handled)
                return;

            if (!(e.Handled = ProcessKeyPreview(e)))
            {
                if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                    Focus();

                base.OnPreviewKeyDown(e);
            }
        }
        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (e.Handled)
                return;

            if (!(e.Handled = ProcessKeyPreview(e)))
                base.OnPreviewKeyDown(e);
        }

        // Public so MainWindow can forward jog keys here when focus has drifted out of the Job view
        // (e.g. into a flyout or side panel) - otherwise OnPreviewKeyDown never fires and jogging "dies"
        // until the view is re-focused. The allowJog gate (focus in MDI/DRO/spindle/work-params) still applies.
        public bool ProcessKeyPreview(KeyEventArgs e)
        {
            // MDI now lives in the fixed bottom run-control bar (Phase 2c) - check its focus there.
            bool mdiFocused = MainWindow.ui.MdiControl?.IsFocused ?? false;

            if (!(model.Keyboard is KeypressHandler keyboard))
                return false;

            return keyboard.ProcessKeypress(e, !(mdiFocused || (_dro?.IsFocused ?? false) || (spindleControl?.IsFocused ?? false) || (workParametersControl?.IsFocused ?? false)), this);
        }

#endregion
    }
}
