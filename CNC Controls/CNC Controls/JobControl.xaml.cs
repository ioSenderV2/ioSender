/*
 * JobControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.47 / 2026-02-22 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2026, Io Engineering (Terje Io)
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
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public partial class JobControl : UserControl
    {
        private enum StreamingHandler
        {
            Idle = 0,
            SendFile,
            FeedHold,
            ToolChange,
            AwaitAction,
            AwaitIdle,
            Previous,
            Max // only used for array instantiation
        }

        private struct StreamingHandlerFn
        {
            public StreamingHandler Handler;
            public bool Count;
            public Func<StreamingState, bool, bool> Call;
        }

        private struct JobData
        {
            public int CurrBlock, LastExecuting, PendingLine, PgmEndLine, ToolChangeLine, ACKPending, serialUsed;
            public bool Started, Transferred, Complete, IsSDFile, IsChecking, HasError, Stopped, ToolChanged;
            public GCodeBlock CurrentRow, NextRow;
        }

        private static bool keyboardMappingsOk = false;

        private int serialSize = 128;
        private bool initOK = false, isActive = false, useBuffering = false, feedHoldEnable = false;
        // Probe-streaming throttle: once a probe (G38) has been streamed, cap look-ahead (ProbeLookahead lines)
        // and never send past an in-flight probe until it completes - so a streamed probe macro can't race lines
        // into the controller's RX during a probe. Self-scoping: normal cutting jobs (no G38) are untouched.
        private bool jobHasProbe = false, probePending = false;
        internal const int ProbeLookahead = 10;
        private volatile StreamingState streamingState = StreamingState.NoFile;
        private GrblState grblState;
        private GrblViewModel model;
        private JobData job;
        private int missed = 0;

        // Background send/ack pump - owns flow control off the UI thread for cutting jobs (check mode keeps the
        // legacy streamer). When pumpActive, ResponseReceived's accounting is skipped (the pump owns it).
        private StreamPump pump;
        private volatile bool pumpActive = false;
        private System.Windows.Threading.DispatcherTimer idleKickTimer;   // nudges a stalled pump when the controller sits idle mid-run

        private StreamingHandlerFn[] streamingHandlers = new StreamingHandlerFn[(int)StreamingHandler.Max];
        private StreamingHandlerFn streamingHandler;

        // The program the streamer reads. Defaults to the loaded job (GCode.File); a tool can point it at its
        // own in-memory program for a run (so the run never touches the job buffer), then reset it to null.
        // Resolved lazily so it does not force GCode.File creation during early startup.
        private IProgramSource _source;
        public IProgramSource Source { get { return _source ?? GCode.File; } set { _source = value; } }

        //       private delegate void GcodeCallback(string data);

        public delegate void StreamingStateChangedHandler(StreamingState state, bool MPGMode);
        public event StreamingStateChangedHandler StreamingStateChanged;

        public JobControl()
        {
            InitializeComponent();

            DataContextChanged += JobControl_DataContextChanged;

            grblState.State = GrblStates.Unknown;
            grblState.Substate = 0;
            grblState.MPG = false;

            job.PgmEndLine = -1;

            streamingHandlers[(int)StreamingHandler.Idle].Call = StreamingIdle;
            streamingHandlers[(int)StreamingHandler.Idle].Count = false;

            streamingHandlers[(int)StreamingHandler.SendFile].Call = StreamingSendFile;
            streamingHandlers[(int)StreamingHandler.SendFile].Count = true;

            streamingHandlers[(int)StreamingHandler.ToolChange].Call = StreamingToolChange;
            streamingHandlers[(int)StreamingHandler.ToolChange].Count = false;

            streamingHandlers[(int)StreamingHandler.FeedHold].Call = StreamingFeedHold;
            streamingHandlers[(int)StreamingHandler.FeedHold].Count = true;

            streamingHandlers[(int)StreamingHandler.AwaitAction].Call = StreamingAwaitAction;
            streamingHandlers[(int)StreamingHandler.AwaitAction].Count = true;

            streamingHandlers[(int)StreamingHandler.AwaitIdle].Call = StreamingAwaitIdle;
            streamingHandlers[(int)StreamingHandler.AwaitIdle].Count = false;

            streamingHandler = streamingHandlers[(int)StreamingHandler.Previous] = streamingHandlers[(int)StreamingHandler.Idle];

            for (int i = 0; i < streamingHandlers.Length; i++)
                streamingHandlers[i].Handler = (StreamingHandler)i;

//            Thread.Sleep(100);

            Loaded += JobControl_Loaded;

            // The run bar is fixed at the main-window bottom and visible on every tab, but its state machine is
            // only "active" on the Grbl tab. A wizard tab registers its program as a runnable source while the
            // Grbl tab is not active, so refresh the Cycle Start enable directly when the active program changes -
            // otherwise the bar's enables stay frozen and Cycle Start looks dead on the wizard tab.
            MacroProcessor.ActiveProgramChanged += OnActiveProgramChanged;
            ProgramView.ActiveChanged += OnActiveProgramChanged;   // a connected ProgramView is an active program too
        }

        private void OnActiveProgramChanged()
        {
            if (model == null)
                return;

            // A wizard program is a runnable source even though the Grbl tab isn't active. Keep status reports
            // flowing so the bar's state machine (GrblStateChanged, relaxed below) stays live - otherwise its
            // enables freeze and Cycle Start re-disables after the first run.
            if (HasActiveProgram)
                EnablePolling(true);

            // Refresh Cycle Start now (only meaningful when idle; a running/held job manages its own enables).
            if (!JobTimer.IsRunning && grblState.State == GrblStates.Idle)
            {
                IsCycleStartEnabled = Source.IsLoaded || HasActiveProgram || (model.IsSDCardJob && model.SDRewind);
                SetActiveProgramReady(HasActiveProgram && IsCycleStartEnabled);
            }
            else
                SetActiveProgramReady(false);
        }

        // An "active program" the streamer can run with no loaded job: the legacy MacroProcessor.ActiveRun (tools
        // not yet migrated) OR a connected ProgramView (stack top). Both coexist during the ProgramView migration;
        // ProgramView.Active is null until a view connects, so this is inert for tools still on ActiveRun.
        private static bool HasActiveProgram { get { return MacroProcessor.ActiveRun != null || ProgramView.Active != null; } }

        public static readonly DependencyProperty IsCycleStartEnabledProperty = DependencyProperty.Register(nameof(IsCycleStartEnabled), typeof(bool), typeof(JobControl));
        public bool IsCycleStartEnabled
        {
            get { return (bool)GetValue(IsCycleStartEnabledProperty); }
            set { SetValue(IsCycleStartEnabledProperty, value); }
        }

        // True when a wizard program is the active source and the machine is idle, ready to run on Cycle Start.
        // Drives the green highlight on the Cycle Start button (XAML) - a "press me to run" cue.
        public static readonly DependencyProperty IsActiveProgramReadyProperty = DependencyProperty.Register(nameof(IsActiveProgramReady), typeof(bool), typeof(JobControl));
        public bool IsActiveProgramReady
        {
            get { return (bool)GetValue(IsActiveProgramReadyProperty); }
            set { SetValue(IsActiveProgramReadyProperty, value); }
        }

        // Set the "ready to run the active program" cue. On the false->true edge, also drop a one-time status-line
        // prompt ("<name> ready - press Cycle Start to run."); the markers/scroll otherwise behave as on the job.
        private void SetActiveProgramReady(bool ready)
        {
            if (ready == IsActiveProgramReady)
                return;
            IsActiveProgramReady = ready;
            if (ready && model != null)
                model.Message = string.Format(LibStrings.FindResource("ReadyCycleStart"), MacroProcessor.ActiveProgramName ?? "Program");
        }

        public static readonly DependencyProperty IsFeedHoldEnabledProperty = DependencyProperty.Register(nameof(IsFeedHoldEnabled), typeof(bool), typeof(JobControl));
        public bool IsFeedHoldEnabled
        {
            get { return (bool)GetValue(IsFeedHoldEnabledProperty); }
            set { SetValue(IsFeedHoldEnabledProperty, value); }
        }

        public static readonly DependencyProperty IsStopEnabledEnabledProperty = DependencyProperty.Register(nameof(IsStopEnabled), typeof(bool), typeof(JobControl));
        public bool IsStopEnabled
        {
            get { return (bool)GetValue(IsStopEnabledEnabledProperty); }
            set { SetValue(IsStopEnabledEnabledProperty, value); }
        }

        public static readonly DependencyProperty IsRewindEnabledProperty = DependencyProperty.Register(nameof(IsRewindEnabled), typeof(bool), typeof(JobControl));
        public bool IsRewindEnabled
        {
            get { return (bool)GetValue(IsRewindEnabledProperty); }
            set { SetValue(IsRewindEnabledProperty, value); }
        }

        private void JobControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                AppConfig.Settings.Base.PropertyChanged += Base_PropertyChanged;

                if (!keyboardMappingsOk && DataContext is GrblViewModel)
                {
                    KeypressHandler keyboard = (DataContext as GrblViewModel).Keyboard;

                    keyboardMappingsOk = true;

                    var parent = UIUtils.TryFindParent<UserControl>(this);

                    keyboard.AddHandler(Key.R, ModifierKeys.Alt, StartJob, parent);
                    keyboard.AddHandler(Key.S, ModifierKeys.Alt, StopJob, parent);
                    keyboard.AddHandler(Key.H, ModifierKeys.Control, Home, parent);
                    keyboard.AddHandler(Key.U, ModifierKeys.Control, Unlock);
                    keyboard.AddHandler(Key.R, ModifierKeys.Shift | ModifierKeys.Control, Reset);
                    keyboard.AddHandler(Key.None, ModifierKeys.None, ResetAndUnlock);   // unbound by default; assign in the Key Bindings editor
                    keyboard.AddHandler(Key.Space, ModifierKeys.None, FeedHold, parent);
                    keyboard.AddHandler(Key.F1, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F2, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F3, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F4, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F5, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F6, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F7, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F8, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F9, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F10, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F11, ModifierKeys.None, FnKeyHandler);
                    keyboard.AddHandler(Key.F12, ModifierKeys.None, FnKeyHandler);

                    // Assignable via Settings:App - Config.ActionShortcuts, not a fixed key (ActionKeyBinder
                    // dispatches these from MainWindow's PreviewKeyDown chain; defaults match the old fixed keys).
                    ActionKeyBinder.Register("JobFeedRateDown", FeedRateDown);
                    ActionKeyBinder.Register("JobFeedRateUp", FeedRateUp);
                    ActionKeyBinder.Register("JobFeedRateDownFine", FeedRateDownFine);
                    ActionKeyBinder.Register("JobFeedRateUpFine", FeedRateUpFine);
                }

                GCodeParser.IgnoreM6 = AppConfig.Settings.Base.IgnoreM6;
                GCodeParser.IgnoreM7 = AppConfig.Settings.Base.IgnoreM7;
                GCodeParser.IgnoreM8 = AppConfig.Settings.Base.IgnoreM8;

                useBuffering = AppConfig.Settings.Base.UseBuffering; // && GrblInfo.IsGrblHAL;
            }
        }

        private void Base_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            GCodeParser.IgnoreM6 = AppConfig.Settings.Base.IgnoreM6;
            GCodeParser.IgnoreM7 = AppConfig.Settings.Base.IgnoreM7;
            GCodeParser.IgnoreM8 = AppConfig.Settings.Base.IgnoreM8;
            GCodeParser.IgnoreG61G64 = AppConfig.Settings.Base.IgnoreG61G64;

            useBuffering = AppConfig.Settings.Base.UseBuffering; // && GrblInfo.IsGrblHAL;
        }

        private void JobControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue != null && e.OldValue is INotifyPropertyChanged)
                ((INotifyPropertyChanged)e.OldValue).PropertyChanged -= OnDataContextPropertyChanged;
            if (e.NewValue != null && e.NewValue is INotifyPropertyChanged)
            {
                model = (GrblViewModel)e.NewValue;
                model.PropertyChanged += OnDataContextPropertyChanged;
                model.OnRealtimeStatusProcessed += RealtimeStatusProcessed;
                model.OnCommandResponseReceived += ResponseReceived;
                model.OnCycleStart += OnCycleStart;
                model.OnStop += OnStop;
                GCode.File.Model = model;   // wire the loaded job's model (job setup, not the streamed Source)
            }
        }

        private void OnStop(object sender, EventArgs e)
        {
            AbortPump();
            JobTimer.Stop();
            job.Stopped = true;
            streamingHandler.Call(StreamingState.Stop, true);
        }

        private void OnCycleStart(object sender, EventArgs e)
        {
            if (isActive && JobPending)
            {
                CycleStart(0);
            }
        }

        private void RealtimeStatusProcessed(string response)
        {
            if (JobTimer.IsRunning && !JobTimer.IsPaused)
                model.RunTime = JobTimer.RunTime;
        }

        private void OnDataContextPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is GrblViewModel) switch (e.PropertyName)
            {
                case nameof(GrblViewModel.LineNumber):
                    if(job.CurrBlock > 0)
                    {
                            int found = 0;
                        var block = job.CurrBlock;
                        var lineNum = (sender as GrblViewModel).LineNumber;
                        do
                        {
                            if(Source.Data[block].LineNum == lineNum)
                            {
                                found = block - 1;
                                Source.Data[block].Sent = "@";
                                break;
                            }
                        } while (--block > job.LastExecuting);
                        while (job.LastExecuting < found)
                        {
                            Source.Data[++job.LastExecuting].Sent = "ok";
                        }
                    }
                    break;

                case nameof(GrblViewModel.GrblState):
                    GrblStateChanged((sender as GrblViewModel).GrblState);
                    break;

                case nameof(GrblViewModel.IsConnectionLost):
                    // A mid-job socket drop (controller/simulator going away) leaves the streaming state
                    // machine waiting on 'ok' responses that will never arrive, so the job stays "running"
                    // with no indication while the link is actually gone. Stop the job so the UI reflects the
                    // lost connection (idle-time loss is already surfaced by the poller). Only fires while a
                    // job is active, so it cannot affect normal streaming.
                    if ((sender as GrblViewModel).IsConnectionLost && (model.IsJobRunning || JobTimer.IsRunning))
                    {
                        AbortPump();
                        streamingHandler.Call(StreamingState.Stop, true);
                    }
                    break;

                case nameof(GrblViewModel.MDI):
                    SendCommand((sender as GrblViewModel).MDI);
                    break;

                case nameof(GrblViewModel.StartFromBlockNum):
                    // "Start from this toolpath/block" always streams the loaded job, never a wizard's program.
                    CycleStart((sender as GrblViewModel).StartFromBlockNum, false);
                    break;

                    case nameof(GrblViewModel.IsMPGActive):
                    grblState.MPG = (sender as GrblViewModel).IsMPGActive == true;
                    (sender as GrblViewModel).Poller.SetState(grblState.MPG ? 0 : AppConfig.Settings.Base.PollInterval);
                    streamingHandler.Call(grblState.MPG ? StreamingState.Disabled : StreamingState.Idle, false);
                    break;

                case nameof(GrblViewModel.ProgramEnd):
                    if (!Source.IsLoaded)
                        streamingHandler.Call(model.IsSDCardJob ? StreamingState.JobFinished : StreamingState.NoFile, model.IsSDCardJob);
                    else if(JobTimer.IsRunning && !job.Complete)
                        streamingHandler.Call(StreamingState.JobFinished, true);
                    if (!model.IsParserStateLive)
                        SendCommand(GrblConstants.CMD_GETPARSERSTATE);
                    break;

                case nameof(GrblViewModel.FileName):
                {
                    job.IsSDFile = false;
                    if(string.IsNullOrEmpty((sender as GrblViewModel).FileName))
                        job.NextRow = null;
                    else
                    {
                        job.ToolChangeLine = -1;
                        job.ToolChanged = false;
                        job.CurrBlock = job.PendingLine = job.ACKPending = model.BlockExecuting = 0;
                        job.PgmEndLine = Source.Blocks - 1;
                        if ((sender as GrblViewModel).IsPhysicalFileLoaded)
                        {
                            if (Source.ToolChanges > 0)
                            {
                                if (!GrblSettings.HasSetting(grblHALSetting.ToolChangeMode))
                                    AppDialogs.Show(string.Format((string)FindResource("JobToolChanges"), Source.ToolChanges), "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                                else if (GrblSettings.GetInteger(grblHALSetting.ToolChangeMode) > 0 && !model.IsTloReferenceSet)
                                    AppDialogs.Show(string.Format((string)FindResource("JobToolReference"), Source.ToolChanges), "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                            if (Source.HasGoPredefinedPosition && (sender as GrblViewModel).IsGrblHAL && (sender as GrblViewModel).HomedState != HomedState.Homed)
                                AppDialogs.Show((string)FindResource("JobG28G30"), "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                            streamingHandler.Call(Source.IsLoaded ? StreamingState.Idle : StreamingState.NoFile, false);
                        }
                    }
                    break;
                }

                case nameof(GrblViewModel.FeedHoldDisabled):
                    IsFeedHoldEnabled = !(sender as GrblViewModel).FeedHoldDisabled && feedHoldEnable;
                    break;

                case nameof(GrblViewModel.GrblReset):
                    AbortPump();
                    JobTimer.Stop();
                    streamingHandler.Call(StreamingState.Stop, true);
                    break;
            }
        }

        public bool canJog { get { return grblState.State == GrblStates.Idle || grblState.State == GrblStates.Tool || grblState.State == GrblStates.Jog; } }
        // A job is ready to start: a loaded job, or an active wizard program (so the physical Cycle Start button
        // runs a wizard's program too, not just a loaded file). False once a job/stream is actually running.
        public bool JobPending { get { return (Source.IsLoaded || HasActiveProgram) && !JobTimer.IsRunning; } }

        public bool Activate(bool activate)
        {
            if (activate && !initOK)
            {
                initOK = true;
                serialSize = Math.Min(AppConfig.Settings.Base.MaxBufferSize, (int)(GrblInfo.SerialBufferSize * 0.9f)); // size should be less than hardware handshake HWM
                Source.Parser.Dialect = GrblInfo.IsGrblHAL ? Dialect.GrblHAL : Dialect.Grbl;
                Source.Parser.ExpressionsSupported = GrblInfo.ExpressionsSupported;

                if (GrblInfo.HasRTC)
                    SendCommand("$RTC=" + DateTime.Now.ToLocalTime().ToString("s"));
            }

            EnablePolling(activate);

            isActive = activate;

            return isActive;
        }

        public void EnablePolling(bool enable)
        {
            if (enable)
                model.Poller.SetState(AppConfig.Settings.Base.PollInterval);
            else if (model.Poller.IsEnabled && model.GrblState.State != GrblStates.Home)
                model.Poller.SetState(0);
        }

        #region Keyboard shortcut handlers

        private bool FeedRateUpFine(Key key)
        {
            Comms.com.WriteByte((byte)GrblConstants.CMD_FEED_OVR_FINE_PLUS);
            return true;
        }

        private bool FeedRateDownFine(Key key)
        {
            Comms.com.WriteByte((byte)GrblConstants.CMD_FEED_OVR_FINE_MINUS);
            return true;
        }

        private bool FeedRateUp(Key key)
        {
            Comms.com.WriteByte((byte)GrblConstants.CMD_FEED_OVR_COARSE_PLUS);
            return true;
        }

        private bool FeedRateDown(Key key)
        {
            Comms.com.WriteByte((byte)GrblConstants.CMD_FEED_OVR_COARSE_MINUS);
            return true;
        }

        private bool StopJob(Key key)
        {
            streamingHandler.Call(StreamingState.Stop, false);
            return true;
        }

        private bool StartJob(Key key)
        {
            CycleStart(0);
            return true;
        }

        private bool Home(Key key)
        {
            model.ExecuteCommand(GrblConstants.CMD_HOMING);
            return true;
        }

        private bool Unlock(Key key)
        {
            model.ExecuteCommand(GrblConstants.CMD_UNLOCK);
            return true;
        }

        private bool Reset(Key key)
        {
            Comms.com.WriteByte((byte)GrblConstants.CMD_RESET);
            return true;
        }

        // Soft-reset, then clear the alarm ($X) once the controller has warm-restarted. One key for the common
        // "get me out of alarm" recovery (the same intent as the status-bar Reset+Unlock). Bindable, unbound by
        // default. The delay lets the controller finish its restart before $X, which it would otherwise drop.
        private bool ResetAndUnlock(Key key)
        {
            Comms.com.WriteByte((byte)GrblConstants.CMD_RESET);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += (s, e) => { timer.Stop(); model.ExecuteCommand(GrblConstants.CMD_UNLOCK); };
            timer.Start();
            return true;
        }

        private bool FeedHold(Key key)
        {
            if (grblState.State != GrblStates.Idle)
                btnHold_Click(null, null);
            return grblState.State != GrblStates.Idle;
        }

        private bool FnKeyHandler(Key key)
        {
            if(!model.IsJobRunning)
            {
                int fkey = int.Parse(key.ToString().Substring(1));
                var macro = AppConfig.Settings.Macros.FirstOrDefault(o => o.FKey == fkey);
                if (macro != null)
                {
                    if (MacroProcessor.Run(model, macro.Name, macro.Code, macro.ConfirmOnExecute))
                        AppConfig.Settings.RecordMacroRun(macro.Id);
                    return true;
                }
            }
            return false;
        }

        #endregion

        public bool CallHandler (StreamingState state, bool always)
        {
            return streamingHandler.Call(state, always);
        }

        #region UIevents

        void btnRewind_Click(object sender, RoutedEventArgs e)
        {
            RewindFile();
            streamingHandler.Call(streamingState, true);
        }

        void btnHold_Click(object sender, RoutedEventArgs e)
        {
            Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
        }

        void btnStop_Click(object sender, RoutedEventArgs e)
        {
            AbortPump();
            streamingHandler.Call(StreamingState.Stop, true);
        }

        void btnStart_Click(object sender, RoutedEventArgs e)
        {
            CycleStart(0);
        }

        // Cycle Start right-click -> toggle grbl check mode ($C). Relocated here from the status-bar Check
        // checkbox: enabled only when not running a job and not asleep. $C from Idle enters check mode; leaving
        // it (unchecking while in the Check state) needs a soft reset. IsChecked mirrors GrblViewModel.IsCheckMode.
        private void cycleStartMenu_Opened(object sender, RoutedEventArgs e)
        {
            var m = DataContext as GrblViewModel;
            miCheckMode.IsEnabled = m != null && !m.IsJobRunning && !m.IsSleepMode;
        }

        private void miCheckMode_Click(object sender, RoutedEventArgs e)
        {
            var m = DataContext as GrblViewModel;
            if (m == null)
                return;

            GrblStates state = m.GrblState.State;
            if (state == GrblStates.Check && !miCheckMode.IsChecked)
                Grbl.Reset();
            else if (state == GrblStates.Idle && miCheckMode.IsChecked)
                m.ExecuteCommand(GrblConstants.CMD_CHECK);
        }

        #endregion

        // honorActiveProgram: when a wizard tab is up it registers its program as the active program
        // (MacroProcessor.ActiveRun). A fresh (idle) Cycle Start then runs THAT instead of the loaded job - so one
        // Cycle Start runs whatever program is active, file/folder or wizard. The internal stream-starters that
        // already have a Source primed (the in-place run, StartLoadedJob) pass false so they don't re-enter it.
        public void CycleStart(int fromBlock, bool honorActiveProgram = true)
        {
            if (grblState.State == GrblStates.Hold || (grblState.State == GrblStates.Run && grblState.Substate == 1) || (grblState.State == GrblStates.Door && (grblState.Substate == 0 || grblState.Substate == 5)))
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
            else if(grblState.State == GrblStates.Idle && model.SDRewind) {
                streamingHandler.Call(StreamingState.Start, false);
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
            }
            else if (grblState.State == GrblStates.Tool)
            {
                model.Message = string.Empty;
                job.ToolChanged = false;
                job.ToolChangeLine = -1;
                if (pumpActive)
                    pump.Suspended = false;   // resume consuming acks for the buffered (and M6) lines
                Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
            }
            else if(JobTimer.IsRunning)
            {
                JobTimer.Pause = false;
                streamingHandler.Call(StreamingState.Send, false);
            }
            else if (honorActiveProgram && MacroProcessor.ActiveRun != null && grblState.State == GrblStates.Idle)
            {
                // A wizard tab is active and the machine is idle: run its program (generate-and-run, with its
                // prompts/flow control) rather than the loaded job. It routes back here with honorActiveProgram:
                // false to stream. Idle-gated so a Cycle Start mid-run can never re-trigger it.
                MacroProcessor.ActiveRun();
            }
            else if (Source.IsLoaded)
            {
                model.Message = model.RunTime = string.Empty;
                Source.StatusDirty = true;   // a run is about to mark block Sent status; let ClearStatus know there's something to clear
                if(job.ToolChanged)
                {
                    job.ToolChanged = false;
                    if (job.ToolChangeLine != -1)
                    {
                        job.ToolChangeLine = -1;
                        SendNextLine();
                    }
                }
                else if (model.IsSDCardJob)
                {
                    Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_RUN + model.FileName.Substring(7));
                }
                else
                {
                    job.ToolChangeLine = -1;
                    model.BlockExecuting = fromBlock;
                    job.CurrBlock = job.ACKPending = job.PendingLine = fromBlock;
                    // Bound the run: stop after RunToBlock when set ("Run just this toolpath"),
                    // otherwise run to program end. One-shot - consumed here.
                    job.PgmEndLine = model.RunToBlock >= 0 ? model.RunToBlock : Source.Blocks - 1;
                    model.RunToBlock = -1;
                    job.serialUsed = missed = 0;
                    probePending = jobHasProbe = false;
                    job.Started = job.Transferred = job.HasError = job.ToolChanged = false;
                    job.NextRow = Source.Data[job.CurrBlock];
                    Comms.com.PurgeQueue();
                    JobTimer.Start();
                    streamingHandler.Call(StreamingState.Send, false);
                    if ((job.IsChecking = model.GrblState.State == GrblStates.Check))
                        model.Message = (string)FindResource("Checking");

                    bool? res = null;
                    CancellationToken cancellationToken = new CancellationToken();

                    // Wait a bit for unlikely event before starting...
                    new Thread(() =>
                    {
                        res = WaitFor.SingleEvent<string>(
                        cancellationToken,
                        null,
                        a => model.OnGrblReset += a,
                        a => model.OnGrblReset -= a,
                       250);
                    }).Start();

                    while (res == null)
                        EventUtils.DoEvents();

                    // The send/ack flow control runs on a dedicated background thread (StreamPump) so UI load
                    // can never stall motion. Check mode ($C) keeps the legacy UI-thread streamer: it reports
                    // every line's error and keeps going (the pump stops on first error), and there is no motion
                    // to stutter, so the pump gives no benefit there.
                    if (!job.IsChecking)
                    {
                        if (pump == null)
                            pump = new StreamPump(model, Dispatcher);
                        pumpActive = true;
                        pump.Start(Source, job.CurrBlock, job.PgmEndLine, serialSize, useBuffering,
                                   AppConfig.Settings.Base.SendComments, AppConfig.Settings.Base.StartSimulator,
                                   OnPumpJobFinished, OnPumpError);
                    }
                    else
                        SendNextLine();
                }
            }
        }

        // Pump -> UI signals (marshalled onto the UI thread by the pump). The state machine and display stay here.
        private void OnPumpJobFinished()
        {
            PumpLog.W("OnPumpJobFinished -> JobFinished, state=" + grblState.State);
            pumpActive = false;
            streamingHandler.Count = false;   // pump owned flow control; stop legacy line accounting so a late/trailing response can't re-enter it
            streamingHandler.Call(StreamingState.JobFinished, true);
        }

        private void OnPumpError(string response)
        {
            pumpActive = false;
            streamingHandler.Count = false;
            job.HasError = model.IsGrblHAL;
            streamingHandler.Call(StreamingState.Error, true);
        }

        // Stop the background pump (Stop/Reset/Alarm/connection-lost). Idempotent.
        private void AbortPump()
        {
            if (pumpActive)
            {
                pumpActive = false;
                streamingHandler.Count = false;
                pump?.Abort();
            }
            idleKickTimer?.Stop();
        }

        // (Re)arm the pump-stall watchdog: if the controller is still idle a short while from now while the pump
        // still thinks a job is in flight, the pump has stalled - nudge it to resume sending / finish. One-shot;
        // re-armed on each idle report and cancelled by any non-idle report (see GrblStateChanged).
        private void ArmIdleKick()
        {
            if (idleKickTimer == null)
            {
                idleKickTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(700)
                };
                idleKickTimer.Tick += (s, e) =>
                {
                    idleKickTimer.Stop();
                    PumpLog.W(string.Format("IDLEKICK timer fire  pumpActive={0} state={1}", pumpActive, grblState.State));
                    if (pumpActive && grblState.State == GrblStates.Idle)
                        pump?.KickIdle();
                };
            }
            idleKickTimer.Stop();
            idleKickTimer.Start();
        }

        public void SendRTCommand(string command)
        {
            var b = Convert.ToInt32(command[0]);

            if(b > 255) switch(b)
            { 
                case 8222:
                    b = GrblConstants.CMD_SAFETY_DOOR;
                    break;

                case 8225:
                    b = GrblConstants.CMD_STATUS_REPORT_ALL;
                    break;

                case 710:
                    b = GrblConstants.CMD_OPTIONAL_STOP_TOGGLE;
                    break;

                case 8240:
                    b = GrblConstants.CMD_SINGLE_BLOCK_TOGGLE;
                    break;
            }

            if(b <= 255)
                Comms.com.WriteByte((byte)b);
        }

        private void SendCommand(string command)
        {
            if (command.Length == 1)
                SendRTCommand(command);
            else if (streamingState == StreamingState.Idle ||
                      streamingState == StreamingState.NoFile ||
                       streamingState == StreamingState.JobFinished ||
                        streamingState == StreamingState.ToolChange ||
                         streamingState == StreamingState.Stop ||
                          (command == GrblConstants.CMD_UNLOCK && streamingState != StreamingState.Send))
            {
                //                command = command.ToUpper();
                try
                {
                    string c = command;
                    Source.Parser.ParseBlock(ref c, true);
                    Source.Commands.Enqueue(command);
                    if (streamingState != StreamingState.SendMDI)
                    {
                        streamingState = StreamingState.SendMDI;
                        ResponseReceived("go");
                    }
                }
                catch
                {
                }
            }
        }

        public void RewindFile()
        {
            job.Complete = false;

            if (Source.IsLoaded)
            {
                using (new UIUtils.WaitCursor())
                {
                    IsCycleStartEnabled = false;

   //                 grdGCode.DataContext = null;

                    Source.ClearStatus();

                    //                  grdGCode.DataContext = Source.Data.DefaultView;
                    model.ScrollPosition = 0;
                    job.ToolChangeLine = -1;
                    job.CurrBlock = job.LastExecuting = job.PendingLine = job.ACKPending = model.BlockExecuting = 0;
                    job.PgmEndLine = Source.Blocks - 1;

                    IsCycleStartEnabled = true;
                }
            }
        }

        private void SetStreamingHandler(StreamingHandler handler)
        {
            if (handler == StreamingHandler.Previous)
                streamingHandler = streamingHandlers[(int)StreamingHandler.Previous];
            else if (streamingHandler.Handler != handler)
            {
                if (handler == StreamingHandler.Idle)
                    streamingHandler = streamingHandlers[(int)StreamingHandler.Previous] = streamingHandlers[(int)StreamingHandler.Idle];
                else {
                    streamingHandlers[(int)StreamingHandler.Previous] = streamingHandler;
                    streamingHandler = streamingHandlers[(int)handler];
                    if (handler == StreamingHandler.AwaitAction)
                        streamingHandler.Count = true;
                }
            }
        }

        public bool StreamingToolChange(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState;

            switch (newState)
            {
                case StreamingState.ToolChange:
                    model.IsJobRunning = false; // only enable UI if no ATC?
                    IsCycleStartEnabled = true;
                    IsFeedHoldEnabled = (feedHoldEnable = false);
                    IsStopEnabled = true;
                    if (JobTimer.IsRunning)
                        JobTimer.Pause = true;
                    break;

                case StreamingState.Idle:
                case StreamingState.Send:
                    if (JobTimer.IsRunning)
                    {
                        model.IsJobRunning = true;
                        JobTimer.Pause = false;
                        if (job.ToolChangeLine >= 0)
                            Source.Data[job.ToolChangeLine].Sent = "ok";
                        SetStreamingHandler(StreamingHandler.SendFile);
                    }
                    else
                        SetStreamingHandler(StreamingHandler.Previous);
                    job.ToolChanged = true;
                    break;

                case StreamingState.Error:
                    SetStreamingHandler(StreamingHandler.Previous);
                    break;

                case StreamingState.Stop:
                    SetStreamingHandler(StreamingHandler.Idle);
                    break;
            }

            if (streamingHandler.Handler != StreamingHandler.ToolChange)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingFeedHold(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState;

            if (always || changed)
            {
                switch (newState)
                {
                    case StreamingState.Halted:
                    case StreamingState.FeedHold:
                        IsCycleStartEnabled = true;
                        IsFeedHoldEnabled = (feedHoldEnable = false);
                        if ((IsStopEnabled = model.IsJobRunning || model.IsSDCardJob) && !GrblInfo.IsGrblHAL)
                            btnStop.Content = (string)FindResource("JobStop");
                        streamingHandler.Count = job.CurrentRow != null;
                        break;

                    case StreamingState.Send:
                    case StreamingState.Error:
                    case StreamingState.Idle:
                        SetStreamingHandler(StreamingHandler.Previous);
                        break;

                    case StreamingState.Stop:
                        SetStreamingHandler(StreamingHandler.Idle);
                        break;

                    case StreamingState.JobFinished:
                        SetStreamingHandler(StreamingHandler.SendFile);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.FeedHold)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingSendFile(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Idle:
                        if(streamingState == StreamingState.Error)
                        {
                            IsCycleStartEnabled = !GrblInfo.IsGrblHAL; // BAD! ?
                            IsFeedHoldEnabled = (feedHoldEnable = false);
                            IsStopEnabled = true;
                            SetStreamingHandler(StreamingHandler.AwaitAction);
                        }
                        else
                            changed = false; // ignore
                        break;

                    case StreamingState.Send:
                        if (!model.IsJobRunning)
                            model.IsJobRunning = true;
                        IsCycleStartEnabled = false;
                        IsFeedHoldEnabled = (feedHoldEnable = true) && !model.FeedHoldDisabled;
                        IsStopEnabled = true;
                        IsRewindEnabled = false;
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        IsFeedHoldEnabled = (feedHoldEnable = false);
                        break;

                    case StreamingState.FeedHold:
                        SetStreamingHandler(StreamingHandler.FeedHold);
                        break;

                    case StreamingState.ToolChange:
                        SetStreamingHandler(StreamingHandler.ToolChange);
                        break;

                    case StreamingState.JobFinished:
                        if (grblState.State == GrblStates.Idle || grblState.State == GrblStates.Check)
                            newState = StreamingState.Idle;
                        job.Complete = job.Transferred = true;
                        job.ACKPending = job.CurrBlock = 0;
                        job.CurrentRow = job.NextRow = null;
                        SetStreamingHandler(StreamingHandler.AwaitIdle);
                        break;

                    case StreamingState.Stop:
                        if (GrblInfo.IsGrblHAL)
                            SetStreamingHandler(StreamingHandler.Idle);
                        else
                        {
                            newState = StreamingState.Paused;
                            SetStreamingHandler(StreamingHandler.AwaitAction);
                        }
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.SendFile)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingAwaitAction(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState || newState == StreamingState.Idle;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Idle:
                        IsCycleStartEnabled = !GrblInfo.IsGrblHAL;
                        break;

                    case StreamingState.Stop:
                        if (GrblInfo.IsGrblHAL) {
                            if (!model.GrblReset)
                            {
                                Comms.com.WriteByte(GrblConstants.CMD_STOP);
                                if (!model.IsParserStateLive)
                                    SendCommand(GrblConstants.CMD_GETPARSERSTATE);
                            }
                        } else if(grblState.State == GrblStates.Run)
                            Comms.com.WriteByte(GrblConstants.CMD_RESET);
                        newState = StreamingState.Idle;
                        SetStreamingHandler(StreamingHandler.AwaitIdle);
                        break;

                    // Note: Only entered in legacy mode
                    case StreamingState.Paused:
                        IsCycleStartEnabled = false;
                        IsFeedHoldEnabled = (feedHoldEnable = false);
                        IsCycleStartEnabled = true;
                        IsStopEnabled = true;
                        btnStop.Content = (string)FindResource("JobStop");
                        if (job.ACKPending == 0)
                            streamingHandler.Count = false;
                        break;

                    case StreamingState.Send:
                        SetStreamingHandler(StreamingHandler.SendFile);
                        SendNextLine();
                        break;

                    case StreamingState.JobFinished:
                        SetStreamingHandler(StreamingHandler.SendFile);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.AwaitAction)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingAwaitIdle(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState || newState == StreamingState.Idle;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Idle:
                        model.RunTime = JobTimer.RunTime;
                        JobTimer.Stop();
                        RewindFile();
                        SetStreamingHandler(StreamingHandler.Idle);
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        IsCycleStartEnabled = !GrblInfo.IsGrblHAL;
                        IsFeedHoldEnabled = (feedHoldEnable = false);
                        IsStopEnabled = true;
                        break;

                    case StreamingState.Send:
                        IsCycleStartEnabled = false;
                        IsFeedHoldEnabled = (feedHoldEnable = true) && !model.FeedHoldDisabled;
                        IsStopEnabled = true;
                        IsRewindEnabled = false;
                        break;

                    case StreamingState.FeedHold:
                        SetStreamingHandler(StreamingHandler.FeedHold);
                        break;

                    case StreamingState.Stop:
                        SetStreamingHandler(StreamingHandler.Idle);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.AwaitIdle)
                return streamingHandler.Call(newState, true);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        public bool StreamingIdle(StreamingState newState, bool always)
        {
            bool changed = streamingState != newState || newState == StreamingState.Idle;

            if (changed || always)
            {
                switch (newState)
                {
                    case StreamingState.Disabled:
                        IsEnabled = false;
                        break;

                    case StreamingState.JobFinished:
                        if(model.IsSDCardJob && grblState.State == GrblStates.Check)
                            SetStreamingHandler(StreamingHandler.SendFile);
                        break;

                    case StreamingState.Idle:
                    case StreamingState.NoFile:
                        IsEnabled = !grblState.MPG;
                        // Also enabled when a wizard tab is up (its program is the active program Cycle Start runs),
                        // even with no job loaded. Re-evaluated on every idle status report, so it tracks tab changes.
                        IsCycleStartEnabled = Source.IsLoaded || HasActiveProgram || (model.IsSDCardJob && model.SDRewind);
                        IsStopEnabled = model.IsSDCardJob && model.SDRewind;
                        IsFeedHoldEnabled = (feedHoldEnable = !grblState.MPG) && !model.FeedHoldDisabled;
                        IsRewindEnabled = !grblState.MPG && Source.IsLoaded && job.CurrBlock != 0;
                        model.IsJobRunning = JobTimer.IsRunning;
                        SetActiveProgramReady(HasActiveProgram && IsCycleStartEnabled);
                        break;

                    case StreamingState.Send:
                        SetActiveProgramReady(false);   // running now - drop the "press Cycle Start" cue
                        if (!string.IsNullOrEmpty(model.FileName) && !grblState.MPG)
                            model.IsJobRunning = true;
                        if (JobTimer.IsRunning)
                            SetStreamingHandler(StreamingHandler.SendFile);
                        else
                        {
                            IsStopEnabled = true;
                            IsFeedHoldEnabled = (feedHoldEnable = !grblState.MPG) && !model.FeedHoldDisabled;
                        }
                        break;

                    case StreamingState.Start: // Streaming from SD Card
                        job.IsSDFile = true;
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        IsCycleStartEnabled = !grblState.MPG;
                        IsFeedHoldEnabled = (feedHoldEnable = false);
                        IsStopEnabled = !grblState.MPG;
                        break;

                    case StreamingState.FeedHold:
                        SetStreamingHandler(StreamingHandler.FeedHold);
                        break;

                    case StreamingState.ToolChange:
                        SetStreamingHandler(StreamingHandler.ToolChange);
                        break;

                    case StreamingState.Stop:
                        IsFeedHoldEnabled = (feedHoldEnable = !(grblState.MPG || grblState.State == GrblStates.Alarm)) && !model.FeedHoldDisabled;
                        IsCycleStartEnabled = feedHoldEnable && Source.IsLoaded; //!GrblInfo.IsGrblHAL;
                        IsStopEnabled = false;
                        IsRewindEnabled = false;
                        model.IsJobRunning = false;
                        job.CurrentRow = job.NextRow = null;
                        if (model.IsSDCardJob && !Source.IsLoaded)
                            model.FileName = string.Empty;
                        if (!grblState.MPG && !job.Stopped)
                        {
                            if (GrblInfo.IsGrblHAL && !(grblState.State == GrblStates.Home || grblState.State == GrblStates.Alarm))
                            {
                                if (!model.GrblReset)
                                {
                                    Comms.com.WriteByte(GrblConstants.CMD_STOP);
                                    if (!model.IsParserStateLive)
                                        SendCommand(GrblConstants.CMD_GETPARSERSTATE);
                                }
                            }
                            else if (grblState.State == GrblStates.Hold && !model.GrblReset)
                                Comms.com.WriteByte(GrblConstants.CMD_RESET);
                        }
                        job.Stopped = false;
                        if (JobTimer.IsRunning)
                        {
                            always = false;
                            model.StreamingState = streamingState = streamingState == StreamingState.Error ? StreamingState.Idle : newState;
                            SetStreamingHandler(StreamingHandler.AwaitIdle);
                        } else if(grblState.State != GrblStates.Alarm)
                            return streamingHandler.Call(StreamingState.Idle, true);
                        break;
                }
            }

            if (streamingHandler.Handler != StreamingHandler.Idle)
                return streamingHandler.Call(newState, always);
            else if (changed)
            {
                model.StreamingState = streamingState = newState;
                StreamingStateChanged?.Invoke(streamingState, grblState.MPG);
            }

            return true;
        }

        void GrblStateChanged(GrblState newstate)
        {
            if (grblState.State == GrblStates.Jog)
                model.IsJobRunning = false;

            // Pump-stall watchdog: a pump-streamed run (e.g. Load Stock's O-word/probe program) can deadlock
            // with the controller idle but the pump believing its buffer is full, so the tail (final G30 park +
            // M2) is never sent and the run never finalises. Arm a short timer whenever the controller goes idle
            // mid-pump; if it's still idle when it fires, nudge the pump (KickIdle) to resume/finish. Cancel on
            // any non-idle report so it never fires during real motion.
            if (pumpActive || JobTimer.IsRunning)
                PumpLog.W(string.Format("STATE {0} (sub {1})  pumpActive={2} streamingState={3}", newstate.State, newstate.Substate, pumpActive, streamingState));

            if (newstate.State != GrblStates.Idle)
                idleKickTimer?.Stop();
            else if (pumpActive)
                ArmIdleKick();

            // Process state transitions when the Grbl tab is active OR a wizard program is the active source: the
            // fixed bottom bar drives that program from the wizard tab, so its enables must track the machine
            // there too (Idle re-enables Cycle Start after a run, Hold/Tool/Alarm behave as on the Grbl tab).
            // Also while a job/stream is actually running (JobTimer): a stay-put run (Load Stock) finishes on a
            // non-Grbl tab and parks in AwaitIdle waiting for the controller's final Idle - if its active program
            // was already torn down, neither flag above is set and that Idle would be dropped, leaving the bar
            // stuck "running" until Stop is pressed. JobTimer is live for exactly that finishing window.
            if (isActive || HasActiveProgram || JobTimer.IsRunning) switch(newstate.State)
            {
                case GrblStates.Idle:
                    streamingHandler.Call(StreamingState.Idle, true);
                    break;

                case GrblStates.Jog:
                    model.IsJobRunning = !model.IsToolChanging;
                    break;

                //case GrblStates.Check
                //    streamingHandler.Call(StreamingState.Send, false);
                //    break;

                case GrblStates.Run:
                    if (JobTimer.IsPaused)
                        JobTimer.Pause = false;
                    if (model.StreamingState != StreamingState.Error)
                        streamingHandler.Call(StreamingState.Send, false);
                    if (newstate.Substate == 1)
                    {
                        IsCycleStartEnabled = !grblState.MPG;
                        IsFeedHoldEnabled = (feedHoldEnable = false);
                    }
                    else if (grblState.Substate == 1)
                    {
                        IsCycleStartEnabled = false;
                        IsFeedHoldEnabled = (feedHoldEnable = !grblState.MPG) && !model.FeedHoldDisabled;
                    }
                    if (!GrblInfo.IsGrblHAL)
                        btnStop.Content = (string)FindResource("JobPause");
                    break;

                case GrblStates.Tool:
                    if (grblState.State != GrblStates.Jog)
                    {
                        // In pump mode read the pump's progress mirror, and suspend it so jog/MDI acks during the
                        // tool change aren't consumed as job-line acks (resumed from CycleStart's Tool branch).
                        int pendingLine = pumpActive ? pump.PendingLine : job.PendingLine;
                        if (pumpActive)
                            pump.Suspended = true;
                        if (JobTimer.IsRunning && pendingLine > 0 && !model.IsSDCardJob)
                        {
                            job.ToolChangeLine = pendingLine - 1;
                            Source.Data[job.ToolChangeLine].Sent = "pending";
                        //      ResponseReceived("pending");
                        }
                        streamingHandler.Call(StreamingState.ToolChange, true);
                        if (!grblState.MPG)
                            Comms.com.WriteByte(GrblConstants.CMD_TOOL_ACK);
                    }
                    break;

                case GrblStates.Hold:
                    streamingHandler.Call(StreamingState.FeedHold, false);
                    break;

                case GrblStates.Home:
                    EnablePolling(true);
                    break;

                case GrblStates.Door:

                    //if (newstate.Substate == 1)
                    //    Comms.com.WriteByte(GrblConstants.CMD_TOOL_ACK);
                    //else if (newstate.Substate == 5)
                    //    streamingHandler.Call(StreamingState.ToolChange, true);

                    //if (newstate.Substate != 5 && streamingState == StreamingState.Send)
                    //    streamingHandler.Call(StreamingState.FeedHold, false);
                    //else
                    //    IsCycleStartEnabled = newstate.Substate != 5;

                    if (newstate.Substate > 0)
                    {
                        if (streamingState == StreamingState.Send)
                            streamingHandler.Call(StreamingState.FeedHold, false);
                        else
                            IsCycleStartEnabled = false;
                    } else
                        IsCycleStartEnabled = true;
                    break;

                case GrblStates.Alarm:
                    AbortPump();
                    grblState.State = newstate.State;
                    grblState.Substate = newstate.Substate;
                    streamingHandler.Call(StreamingState.Stop, false);
                    break;
            }

            grblState.State = newstate.State;
            grblState.Substate = newstate.Substate;
            grblState.MPG = newstate.MPG;
        }

        private void ResponseReceived(string response)
        {
            // ResponseReceived is raised by a specific comms instance, but the streaming switch below writes to
            // the static Comms.com. During a reconnect/teardown (startup simulator handshake, or the Restart
            // relaunch) the static can be null/replaced while an in-flight response from the old link still
            // arrives - writing then NREs (SendMDI/Reset cases). No link means nothing to send, so bail out.
            if (Comms.com == null)
                return;

            // When the background pump is driving the job it owns all flow-control accounting (off the UI
            // thread). Skip the accounting here; the MDI/Reset switch below still runs on the UI thread.
            if (pumpActive)
            {
            }
            else if (streamingHandler.Count)
            {
                //if(response == "pending")
                //{
                //    job.ToolChangeLine = job.PendingLine - 1;
                //    Source.Data.Rows[job.ToolChangeLine]["Sent"] = response;
                //    return;
                //}

                if (job.ACKPending > 0)
                    job.ACKPending--;

                // Probe barrier released once everything outstanding (including the G38, whose 'ok' arrives only
                // after the probe finishes) has been acked - then SendNextLine below resumes the stream.
                if (probePending && job.ACKPending == 0)
                    probePending = false;

                // A response can still arrive after the program finished/aborted, or after the streamer was
                // pointed back at the loaded job (a stay-put macro run - e.g. Load Stock probing one corner then
                // tearing down - leaves the job source empty when no file is loaded). The line accounting below
                // indexes Source.Data, so ignore a response whose PendingLine is past the current (possibly
                // empty) program rather than throwing IndexOutOfRange.
                if (job.PendingLine >= 0 && job.PendingLine < Source.Data.Count)
                {

                if (!job.IsSDFile && (job.IsChecking || (string)Source.Data[job.PendingLine].Sent == "*"))
                    job.serialUsed = Math.Max(0, job.serialUsed - (int)Source.Data[job.PendingLine].Length);

                //if (streamingState == StreamingState.Send || streamingState == StreamingState.Paused)
                //{
                bool isError = response.StartsWith("error");

                if (!(job.IsSDFile || job.IsChecking))
                {
                    if (!job.HasError)
                    {
                        Source.Data[job.PendingLine].Sent = response;

                        if (job.PendingLine > 5)
                            model.ScrollPosition = job.PendingLine - 5;
                    }

                    if(streamingHandler.Call == StreamingAwaitAction)
                        streamingHandler.Count = false;
                }

                if (isError)
                {
                    streamingHandler.Call(StreamingState.Error, true);
                    if(job.IsChecking && !job.HasError)
                    {
                        if (job.PendingLine > 5)
                            model.ScrollPosition = job.PendingLine - 5;
                        Source.Data[job.PendingLine].Sent = response;
                    }
                    job.HasError = model.IsGrblHAL;
                }
                else if (job.PgmEndLine == job.PendingLine)
                    streamingHandler.Call(StreamingState.JobFinished, true);
                else if (streamingHandler.Count && response == "ok")
                    SendNextLine();
                //}

                if (job.Transferred)
                {
                    job.Transferred = false;
                    model.BlockExecuting = 0;
                    model.Message = (string)FindResource("TransferComplete");
                }
                else if(job.PendingLine != job.PgmEndLine )
                {
                    job.PendingLine++;
                    if(!job.IsChecking || job.PendingLine % 250 == 0)
                        model.BlockExecuting = job.PendingLine;
                }

                }   // end PendingLine bounds guard
            }
            else if (response == "ok")
                missed++;

            switch (streamingState)
            {
                case StreamingState.Send:
                    if(response == "start")
                        SendNextLine();
                    break;

                case StreamingState.SendMDI:
                    if (Source.Commands.Count > 0)
                        Comms.com.WriteCommand(Source.Commands.Dequeue());
                    if (Source.Commands.Count == 0)
                        streamingState = StreamingState.Idle;
                    break;

                case StreamingState.Reset:
                    Comms.com.WriteCommand(GrblConstants.CMD_UNLOCK);
                    streamingState = StreamingState.AwaitResetAck;
                    break;

                case StreamingState.AwaitResetAck:
                    streamingHandler.Call(Source.IsLoaded ? StreamingState.Idle : StreamingState.NoFile, false);
                    break;
            }
        }

        void SendNextLine()
        {
            while (job.NextRow != null) {

                // Probe barrier: hold all lines while a streamed probe (G38) is in flight, until it completes
                // (every outstanding line acked). Stops post-probe lines piling into the controller's RX during
                // the probe - the fault that broke streamed Load Stock.
                if (probePending)
                    break;

                string line = (string)job.NextRow.Data; //  GCodeUtils.StripSpaces((string)currentRow["Data"]);

                // Send comment lines as empty comment when "Send comments" is off - except to the simulator,
                // which parses (TOOL T=n D=.. TYPE=..) comments for material removal, so it must always get
                // the full comment regardless of the setting.
                if ((bool)job.NextRow.IsComment && !AppConfig.Settings.Base.SendComments && !AppConfig.Settings.Base.StartSimulator)
                {
                    line = "()";
                    job.NextRow.Length = line.Length + 1;
                }

                if (job.serialUsed < (serialSize - (int)job.NextRow.Length)
                     && (!jobHasProbe || job.ACKPending < ProbeLookahead))   // cap look-ahead once probing
                {

                    if (Source.Commands.Count > 0)
                        Comms.com.WriteCommand(Source.Commands.Dequeue());
                    else
                    {
                        job.CurrentRow = job.NextRow;

                        if(!job.IsChecking)
                            job.CurrentRow.Sent = "*";

                        if (line == "%")
                        {
                            if (!(job.Started = !job.Started))
                                job.PgmEndLine = job.CurrBlock;
                        }
                        else if (job.CurrentRow.ProgramEnd)
                            job.PgmEndLine = job.CurrBlock;
                        job.NextRow = job.PgmEndLine == job.CurrBlock ? null : Source.Data[++job.CurrBlock];
                        //            ParseBlock(line + "\r");
                        job.serialUsed += (int)job.CurrentRow.Length;
                        Comms.com.WriteString(line + '\r');
                        if (job.CurrentRow.BreakAt)
                            Comms.com.WriteString("M0" + '\r');

                        // A probe move just went out: throttle this job from here on, and hold further lines
                        // until this probe completes (cleared when all outstanding lines are acked - see below).
                        if (line.IndexOf("G38", StringComparison.OrdinalIgnoreCase) >= 0)
                            probePending = jobHasProbe = true;
                    }
                    job.ACKPending++;

                    if (!useBuffering || probePending)
                        break;
                }
                else
                    break;
            }
        }
    }
}
