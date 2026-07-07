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
            model.Keyboard.JogDistances[(int)KeypressHandler.JogMode.Slow] = jog.SlowDistance;
            model.Keyboard.JogDistances[(int)KeypressHandler.JogMode.Fast] = jog.FastDistance;
            model.Keyboard.JogFeedrates[(int)KeypressHandler.JogMode.Step] = jog.StepFeedrate;
            model.Keyboard.JogFeedrates[(int)KeypressHandler.JogMode.Slow] = jog.SlowFeedrate;
            model.Keyboard.JogFeedrates[(int)KeypressHandler.JogMode.Fast] = jog.FastFeedrate;
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

            // Flow across the two columns by measured height, in order: fill the left column to ~half the total
            // height, then the rest go right - so the columns stay about even. Done inline (before first paint)
            // using DesiredSize so the layout is final when the window renders, with no post-paint reshuffle.
            double total = 0d;
            var h = new double[panels.Count];
            for (int i = 0; i < panels.Count; i++)
            {
                panels[i].Measure(new System.Windows.Size(250d, double.PositiveInfinity));
                total += (h[i] = panels[i].DesiredSize.Height);
            }
            double half = total / 2d, leftH = 0d;
            int split = panels.Count;
            for (int i = 0; i < panels.Count; i++)
            {
                if (i > 0 && leftH + h[i] / 2d > half) { split = i; break; }
                leftH += h[i];
            }
            for (int i = 0; i < panels.Count; i++)
                (i < split ? mainSlotsLeft : mainSlotsRight).Children.Add(panels[i]);
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
        // capabilities ($I: ATC, tool count, ...) replace the pre-reboot values. Mark init as pending; the
        // GrblState handler re-runs InitSystem once the controller reports a non-Alarm state, and we also try
        // immediately in case it comes back idle (no state change to trigger the handler).
        private void OnReconnectInit()
        {
            initOK = false;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (initOK == false && model.GrblState.State != GrblStates.Alarm)
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

                    switch (Controller.Restart())
                    {
                        case Controller.RestartResult.Ok:
                            if (!isBooted)
                                Dispatcher.BeginInvoke(new System.Action(() => OnBooted()), DispatcherPriority.ApplicationIdle);
                            initOK = InitSystem();
                            break;

                        case Controller.RestartResult.Close:
                            MainWindow.ui.Close();
                            break;

                        case Controller.RestartResult.Exit:
                            Environment.Exit(-1);
                            break;
                    }

                    model.Message = Controller.Message;
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
                    focusedControl.Focus();
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
            model.Keyboard.LoadMappings();

            if (GrblInfo.NumAxes > 3)
                GCode.File.AddTransformer(typeof(GCodeWrapViewModel), "Wrap to rotary (WIP)", MainWindow.UIViewModel.TransformMenuItems);
        }

        private bool InitSystem()
        {
            initOK = true;
            int timeout = 5;

            if (isBooted && model.GrblState.State == GrblStates.Home)
                return true;

            using (new UIUtils.WaitCursor())
            {
                MainWindow.ui.RunControl.EnablePolling(false);
                while (!GrblInfo.Get())
                {
                    if(--timeout == 0)
                    {
                        model.Message = (string)FindResource("MsgNoResponse");
                        return false;
                    }
                    Thread.Sleep(500);
                }
                GrblAlarms.Get();
                GrblErrors.Get();
                GrblSettings.Load();
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

            GrblCommand.ToolChange = GrblInfo.ManualToolChange ? "M61Q{0}" : (GrblInfo.HasATC ? "T{0}M6" : "T{0}");

            showProgramLimits();

            workspace.Set3DViewEnabled(AppConfig.Settings.GCodeViewer.IsEnabled);

            if (GrblInfo.LatheModeEnabled)
                MainWindow.EnableView(true, ViewType.LatheWizards);
            else
                MainWindow.ShowView(false, ViewType.LatheWizards);

            if (GrblInfo.HasFS)   // any mounted filesystem (SD card and/or LittleFS), not just SD
                MainWindow.EnableView(true, ViewType.SDCard);
            else
                MainWindow.ShowView(false, ViewType.SDCard);

            // On an ATC controller, seed the ioSender-side "Start Job" macro so it is available to run. The
            // controller-side macros it CALLs (cal/probe_tfl/tc) are installed explicitly from the SD Card tab's
            // "Install ATC" button - provisioning is never automatic (controller-I/O timing proved too fragile).
            if (GrblInfo.HasFS && (GrblInfo.HasATC || GrblInfo.AtcMacrosRequired))
                CNC.Controls.AtcMacros.SeedStartJobMacro();

            // Tools is now a hub (tool table + stepper calibration / surface spoilboard / Trinamic / PID), so
            // keep it available even when the controller has no tool table (NumTools == 0) - only the tool-table
            // sub-tab is empty in that case. (Previously this hid the whole tab when NumTools == 0.)
            MainWindow.EnableView(true, ViewType.Tools);

            // Probing needs a probe AND probe-coordinate reporting; without them it can do nothing, so REMOVE it
            // (and it's listed, with the reason, in Edit Main Page > Unavailable) rather than leave it greyed.
            // Height Map stays (it can still load/apply a saved .map offline), gated at run time instead.
            if (GrblInfo.HasProbe && GrblSettings.ReportProbeCoordinates)
                MainWindow.EnableView(true, ViewType.Probing);
            else
                MainWindow.ShowView(false, ViewType.Probing);

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
                MainWindow.ui.RunControl.CycleStart(0, false);   // stream the loaded job, don't re-enter ActiveRun
        }

#region UIevents

        void JobView_Load(object sender, EventArgs e)
        {
            MainWindow.ui.RunControl.CallHandler(StreamingState.Idle, true);
        }

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

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!(e.Handled = ProcessKeyPreview(e)))
            {
                if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                    Focus();

                base.OnPreviewKeyDown(e);
            }
        }
        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
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
            return model.Keyboard.ProcessKeypress(e, !(mdiFocused || (_dro?.IsFocused ?? false) || (spindleControl?.IsFocused ?? false) || (workParametersControl?.IsFocused ?? false)), this);
        }

#endregion
    }
}
