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
using System.Windows.Threading;
using System.Threading;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public partial class JobControl : UserControl
    {
        private static bool keyboardMappingsOk = false;

        private bool initOK = false, isActive = false;

        // The streaming engine. It owns the job, the pump, the handler table and every decision about what
        // the machine does next; this control owns what the operator sees. The run-control enable state it
        // decides is mirrored into this control's DependencyProperties by Runner_PropertyChanged, so the XAML
        // and every binding are untouched.
        private readonly JobRunner runner = new JobRunner();

        private GrblViewModel model;

        public JobControl()
        {
            InitializeComponent();

            DataContextChanged += JobControl_DataContextChanged;
            runner.PropertyChanged += Runner_PropertyChanged;
            RegisterActiveProgramPolicy();
            RegisterEngineSeams();

            Loaded += JobControl_Loaded;

            // The run bar is fixed at the main-window bottom and visible on every tab, but its state machine is
            // only "active" on the Grbl tab. A wizard tab registers its program as a runnable source while the
            // Grbl tab is not active, so refresh the Run enable directly when the active program changes -
            // otherwise the bar's enables stay frozen and Run looks dead on the wizard tab.
            MacroProcessor.ActiveProgramChanged += OnActiveProgramChanged;
            ProgramView.ActiveChanged += OnActiveProgramChanged;   // a connected ProgramView is an active program too
        }

        // Tell the runner what "the active program" means here. MacroProcessor's active-program surface is
        // pure client bookkeeping - which tool tab is focused, whether it has generated yet - so the engine
        // asks these instead of reading it. Each returns whether it handled the press; false falls through.
        // Both conditions are the ones Run used to test inline, unchanged; only WHERE they are evaluated moved.
        private void RegisterActiveProgramPolicy()
        {
            runner.GenerateActiveProgram = () =>
            {
                if (!MacroProcessor.SupportsGenerateMode || MacroProcessor.IsProgramGenerated ||
                    MacroProcessor.ActiveGenerate == null)
                    return false;

                // Pressing "Generate" only generates - it does NOT also run. The second press, once
                // IsProgramGenerated flips true and the button reads "Run", falls through to
                // RunActiveProgram below like any other wizard tab.
                MacroProcessor.ActiveGenerate();
                return true;
            };

            runner.RunActiveProgram = () =>
            {
                if (MacroProcessor.ActiveRun == null)
                    return false;

                MacroProcessor.ActiveRun();
                return true;
            };

            // The run-mode selector's "Simulate" only arms the intent - the actual connection switch happens
            // here, right before the run it was meant to gate would otherwise start. Blocking (launches and
            // connects the simulator synchronously, a few seconds worst case) - the same cost every other
            // connect path in this app already pays, not something new. If the session is already on the
            // simulator there is nothing to switch, so SimulateActive stays false and ResetRunModeAfterJob
            // won't try to "restore" a connection that was never disturbed.
            // Returns false only when the switch was wanted and failed - that aborts the run.
            // Whether a tool tab's program is the active one, and whether it still has to generate first,
            // are read off MacroProcessor/ProgramView - client bookkeeping the engine used to reach into.
            // Same predicates as before, evaluated in the same place; only WHO evaluates them moved.
            runner.HasActiveProgram = () => HasActiveProgram;
            runner.GenerateModeBlocking = () => IsGenerateModeBlocking;

            runner.PrepareRun = () =>
            {
                if (!simulateArmed)
                    return true;

                simulateArmed = false;

                if (!SimulatorManager.IsSimulatorConnection())
                {
                    // MainWindow lives in the app project, which CNC Controls cannot reference directly (the
                    // dependency runs the other way) - SwitchToSimulatorForRun is a hook MainWindow registers
                    // at startup, same pattern as AppConfig.DeviceEnumerator.
                    bool switched = SimulatorManager.SwitchToSimulatorForRun?.Invoke() ?? false;
                    if (!switched)
                    {
                        model.Message = "Could not switch to the simulator - build one in Settings > Simulator first.";
                        UpdateRunButtonLabel();
                        return false;
                    }
                    runner.SimulateActive = true;
                }

                UpdateRunButtonLabel();
                return true;
            };
        }

        // Mirror the portable run-control state onto this control's DependencyProperties, which is what the
        // XAML actually binds to. One-way by design: the state machine decides, the view reflects.
        // Assignment-per-change (not a blanket refresh) preserves the DP semantics the machine already
        // relied on - notably that IsRunEnabled's PropertyChangedCallback fires UpdateRunButtonLabel only on
        // a real transition. JobRunner's setters dedupe for the same reason.
        private void Runner_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(JobRunner.CanRun):
                    IsRunEnabled = runner.CanRun;
                    break;

                case nameof(JobRunner.CanFeedHold):
                    IsFeedHoldEnabled = runner.CanFeedHold;
                    break;

                case nameof(JobRunner.CanStop):
                    IsStopEnabled = runner.CanStop;
                    break;

                // JobRunner.CanRewind is deliberately NOT mirrored - the Rewind button was removed (see
                // JobControl.xaml). The runner still maintains the state for any other host to surface.

                // State in, words out: Core does not know "Stop" from "Pause" - see JobRunner's header on
                // why nothing in Core resolves a localized resource.
                case nameof(JobRunner.StopShowsPause):
                    if (btnStop != null)
                        btnStop.Content = (string)FindResource(runner.StopShowsPause ? "JobPause" : "JobStop");
                    break;

                case nameof(JobRunner.ControlsEnabled):
                    IsEnabled = runner.ControlsEnabled;
                    break;

                // Same split again: the engine decides a tool tab's program is ready to run, this decides
                // what that looks like - the button cue, and the status line naming the program.
                case nameof(JobRunner.ActiveProgramReady):
                    SetActiveProgramReady(runner.ActiveProgramReady);
                    break;
            }
        }

        private void OnActiveProgramChanged()
        {
            if (model == null)
                return;

            // A wizard program is a runnable source even though the Grbl tab isn't active. Keep status reports
            // flowing so the bar's state machine (GrblStateChanged, relaxed below) stays live - otherwise its
            // enables freeze and Run re-disables after the first run.
            if (HasActiveProgram)
                EnablePolling(true);

            // Refresh Run now (only meaningful when idle; a running/held job manages its own enables).
            // Through the runner, NOT SetActiveProgramReady directly: the runner is the single source of
            // truth for this cue and the mirror is the only thing that writes the DependencyProperty.
            // Setting the DP behind the runner's back would let the two diverge - the engine sets ready
            // true, a tab change sets the DP false directly, and the engine's next identical assignment
            // then dedupes to nothing, leaving the cue stuck off with no way to recover.
            if (!JobTimer.IsRunning && runner.MachineState == GrblStates.Idle)
            {
                runner.CanRun = Source.IsLoaded || HasActiveProgram || (model.IsSDCardJob && model.SDRewind);
                runner.ActiveProgramReady = HasActiveProgram && runner.CanRun && !IsGenerateModeBlocking;
            }
            else
                runner.ActiveProgramReady = false;

            // IsRunEnabled's own DP callback only re-fires UpdateRunButtonLabel on an actual value CHANGE - but
            // Generate-mode readiness (MacroProcessor.IsGenerateReady/IsProgramGenerated) can flip without
            // IsRunEnabled itself changing (e.g. HasActiveProgram was already true from ActiveGenerate being
            // registered). This event covers both, so just always refresh here too.
            UpdateRunButtonLabel();
        }

        // An "active program" the streamer can run with no loaded job: the legacy MacroProcessor.ActiveRun (tools
        // not yet migrated) OR a connected ProgramView (stack top) OR a Generate-first tab registered but not
        // yet generated (ActiveGenerate) - that last case keeps IsRunEnabled true while such a tab is focused so
        // IsRunActionEnabled's extra IsGenerateReady gate (see UpdateRunButtonLabel) is what actually governs the
        // button, not this state-machine flag. Both coexist during the ProgramView migration; ProgramView.Active
        // is null until a view connects, so this is inert for tools still on ActiveRun.
        private static bool HasActiveProgram { get { return MacroProcessor.ActiveRun != null || MacroProcessor.ActiveGenerate != null || ProgramView.Active != null; } }

        // A Generate-first tab (Start Job etc.) registers MacroProcessor.ActiveGenerate as soon as it's
        // focused - well before the operator has actually pressed Generate - so HasActiveProgram alone goes
        // true too early for "ready to press Run" purposes. The "<name> ready - press Run to run." status
        // line means what it says - actually RUNNING - so it must stay quiet the whole time the button still
        // reads "Generate" (UpdateRunButtonLabel), even once IsGenerateReady is true: pressing it in that
        // state only generates, it does not run.
        private static bool IsGenerateModeBlocking { get { return MacroProcessor.SupportsGenerateMode && !MacroProcessor.IsProgramGenerated; } }

        // PropertyChangedCallback (not a manual call at every one of this DP's many "IsRunEnabled = ..."
        // assignment sites throughout this file) keeps btnStart's disabled-state tooltip in sync regardless of
        // which state-machine branch flips it - see UpdateRunButtonLabel.
        public static readonly DependencyProperty IsRunEnabledProperty = DependencyProperty.Register(nameof(IsRunEnabled), typeof(bool), typeof(JobControl),
            new PropertyMetadata(false, (d, e) => (d as JobControl)?.UpdateRunButtonLabel()));
        public bool IsRunEnabled
        {
            get { return (bool)GetValue(IsRunEnabledProperty); }
            set { SetValue(IsRunEnabledProperty, value); }
        }

        // The Run bar button's/dropdown's actual IsEnabled (XAML-bound) - IsRunEnabled ANDed with the
        // Generate-mode readiness gate (MacroProcessor.IsGenerateReady) while a Generate-first tab is focused
        // and hasn't generated yet. A separate DP rather than overloading IsRunEnabled itself: IsRunEnabled is
        // also read directly elsewhere (e.g. SetActiveProgramReady) as the plain "is there a runnable source"
        // state-machine signal, independent of per-tab Generate readiness. Recomputed in UpdateRunButtonLabel,
        // the single place that already reacts to every input that can change either side of the AND.
        public static readonly DependencyProperty IsRunActionEnabledProperty = DependencyProperty.Register(nameof(IsRunActionEnabled), typeof(bool), typeof(JobControl));
        public bool IsRunActionEnabled
        {
            get { return (bool)GetValue(IsRunActionEnabledProperty); }
            set { SetValue(IsRunActionEnabledProperty, value); }
        }

        // True when a wizard program is the active source and the machine is idle, ready to run on Run.
        // Drives the green highlight on the Run button (XAML) - a "press me to run" cue.
        public static readonly DependencyProperty IsActiveProgramReadyProperty = DependencyProperty.Register(nameof(IsActiveProgramReady), typeof(bool), typeof(JobControl));
        public bool IsActiveProgramReady
        {
            get { return (bool)GetValue(IsActiveProgramReadyProperty); }
            set { SetValue(IsActiveProgramReadyProperty, value); }
        }

        // Same green "press me" cue, but for the button while it still reads "Generate" (a Generate-first tab
        // focused, not yet generated). Deliberately a SEPARATE flag from IsActiveProgramReady: that one also
        // drives the "<name> ready - press Run to run." status line (SetActiveProgramReady), which must stay
        // quiet until the program has actually been generated - see IsGenerateModeBlocking's own comment. This
        // one only paints the button; set alongside IsRunActionEnabled in UpdateRunButtonLabel.
        public static readonly DependencyProperty IsGenerateActionReadyProperty = DependencyProperty.Register(nameof(IsGenerateActionReady), typeof(bool), typeof(JobControl));
        public bool IsGenerateActionReady
        {
            get { return (bool)GetValue(IsGenerateActionReadyProperty); }
            set { SetValue(IsGenerateActionReadyProperty, value); }
        }

        // Set the "ready to run the active program" cue. On the false->true edge, also drop a one-time status-line
        // prompt ("<name> ready - press Run to run."); the markers/scroll otherwise behave as on the job.
        private void SetActiveProgramReady(bool ready)
        {
            if (ready == IsActiveProgramReady)
                return;
            IsActiveProgramReady = ready;
            if (model == null)
                return;
            if (ready)
                model.Message = string.Format(LibStrings.FindResource("ReadyCycleStart"), MacroProcessor.ActiveProgramName ?? "Program", RunLabels.CycleStart);
            else
                // Drop the prompt along with the cue itself - previously only the (invisible) boolean flipped
                // here, leaving the "<name> ready - press Run to run." TEXT stale on screen through an entire
                // normal run (only Check/DryRun overwrite model.Message at run-start - see StreamingState.Send
                // just below this call site - a plain run never did), confirmed on real hardware as still
                // reading "ready to run" well after Run had already been pressed and the job was streaming.
                model.Message = string.Empty;
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

        private void JobControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                AppConfig.Settings.Base.PropertyChanged += Base_PropertyChanged;

                // Keyboard is the portable JogController unless the host registered the WPF handler
                // (see KeypressHandler.Register) - no handler, no key bindings to register.
                if (!keyboardMappingsOk && (DataContext as GrblViewModel)?.Keyboard is KeypressHandler keyboard)
                {
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

                    keyboard.AddHandler(Key.OemMinus, ModifierKeys.Control, FeedRateDown);
                    keyboard.AddHandler(Key.OemPlus, ModifierKeys.Control, FeedRateUp);
                    keyboard.AddHandler(Key.OemMinus, ModifierKeys.Shift | ModifierKeys.Control, FeedRateDownFine);
                    keyboard.AddHandler(Key.OemPlus, ModifierKeys.Shift | ModifierKeys.Control, FeedRateUpFine);
                }

                GCodeParser.IgnoreM6 = AppConfig.Settings.Base.IgnoreM6;
                GCodeParser.IgnoreM7 = AppConfig.Settings.Base.IgnoreM7;
                GCodeParser.IgnoreM8 = AppConfig.Settings.Base.IgnoreM8;

                runner.UseBuffering = AppConfig.Settings.Base.UseBuffering; // && GrblInfo.IsGrblHAL;
            }
        }

        private void Base_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            GCodeParser.IgnoreM6 = AppConfig.Settings.Base.IgnoreM6;
            GCodeParser.IgnoreM7 = AppConfig.Settings.Base.IgnoreM7;
            GCodeParser.IgnoreM8 = AppConfig.Settings.Base.IgnoreM8;
            GCodeParser.IgnoreG61G64 = AppConfig.Settings.Base.IgnoreG61G64;

            runner.UseBuffering = AppConfig.Settings.Base.UseBuffering; // && GrblInfo.IsGrblHAL;
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
                runner.Attach(model);   // the engine takes the command responses it does flow control on
                model.OnCycleStart += OnCycleStart;
                model.OnStop += OnStop;
                GCode.File.Model = model;   // wire the loaded job's model (job setup, not the streamed Source)
                UpdateRunButtonLabel();   // reflect whatever mode is already active (e.g. reattaching to a live controller)
            }
        }

        private void OnStop(object sender, EventArgs e)
        {
            runner.Stop();
        }

        private void OnCycleStart(object sender, EventArgs e)
        {
            if (isActive && runner.JobPending)
            {
                Run(0);
            }
        }

        private void RealtimeStatusProcessed(string response)
        {
            if (JobTimer.IsRunning && !JobTimer.IsPaused)
                model.RunTime = JobTimer.RunTime;
        }

        // The model is where the machine reports in, and both halves have work to do on it. This routes each
        // notification: the engine's half through the JobRunner entry point for that case, this control's half
        // (the run button's label, the load-time warnings) inline. Order within each case is the order the
        // single inline switch had before step 4c-4 moved the engine out - for GrblState the engine goes
        // first and the label follows, for a newly loaded file the warnings come before the engine settles
        // the enables. That ordering is why this stays one switch rather than becoming a second subscriber.
        private void OnDataContextPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is GrblViewModel) switch (e.PropertyName)
            {
                case nameof(GrblViewModel.LineNumber):
                    runner.OnLineNumberChanged((sender as GrblViewModel).LineNumber);
                    break;

                case nameof(GrblViewModel.GrblState):
                    runner.GrblStateChanged((sender as GrblViewModel).GrblState);
                    UpdateRunButtonLabel();   // IsCheckMode is derived from GrblState - no PropertyChanged of its own
                    break;

                case nameof(GrblViewModel.IsDryRunMode):
                    UpdateRunButtonLabel();
                    break;

                case nameof(GrblViewModel.IsConnectionLost):
                    // A mid-job socket drop (controller/simulator going away) leaves the streaming state
                    // machine waiting on 'ok' responses that will never arrive, so the job stays "running"
                    // with no indication while the link is actually gone. Stop the job so the UI reflects the
                    // lost connection (idle-time loss is already surfaced by the poller). Only fires while a
                    // job is active, so it cannot affect normal streaming.
                    if ((sender as GrblViewModel).IsConnectionLost)
                        runner.OnConnectionLost();
                    break;

                case nameof(GrblViewModel.MDI):
                    runner.SendCommand((sender as GrblViewModel).MDI);
                    break;

                case nameof(GrblViewModel.StartFromBlockNum):
                    // "Start from this toolpath/block" always streams the loaded job, never a wizard's program.
                    Run((sender as GrblViewModel).StartFromBlockNum, false);
                    break;

                case nameof(GrblViewModel.IsMPGActive):
                    runner.OnMPGChanged((sender as GrblViewModel).IsMPGActive == true);
                    break;

                case nameof(GrblViewModel.ProgramEnd):
                    runner.OnProgramEnd();
                    break;

                case nameof(GrblViewModel.FileName):
                    runner.OnFileNameChanged();   // raises LoadedJobWarning for the two load-time warnings
                    break;

                case nameof(GrblViewModel.FeedHoldDisabled):
                    runner.RefreshFeedHoldGate((sender as GrblViewModel).FeedHoldDisabled);
                    break;

                case nameof(GrblViewModel.GrblReset):
                    runner.OnGrblReset();
                    MacroProcessor.DiscardGenerated?.Invoke();
                    break;

                case nameof(GrblViewModel.HomedState):
                    // A homing cycle just re-established trusted position - the same moment "resume the same
                    // generated program" (see MacroProcessor.DiscardGenerated's own comment, and the
                    // GrblReset case above) stops making sense: a home doesn't continue anything, it re-zeros
                    // the very position reference the paused job's remaining lines were written against.
                    // Confirmed on real hardware 2026-07-29: after a real collision alarm and a controller
                    // power cycle, the surviving generated program silently resumed and ran to completion -
                    // including a toolsetter probe - the moment a stray Cycle Start signal arrived after a
                    // successful rehome, with no operator "Run" click at all.
                    if ((sender as GrblViewModel).HomedState == HomedState.Homed)
                        DiscardResumableJob();
                    break;
            }
        }

        // Wipes everything that lets a later Cycle Start (or Run) silently continue a job from wherever it
        // left off. Called after a controller reboot or a completed homing cycle - the two events that make
        // "resume the same run" unsafe regardless of which alarm, if any, preceded them. Deliberately
        // narrower than a plain Stop/error abort (see OnStop, and MainWindow's own DiscardGenerated call,
        // which stay resumable on purpose) - e.g. Alarm:5 (a probe search came up empty; nothing was ever
        // touched) is fine to unlock and continue right where it left off, without either of these two
        // events happening first.
        private void DiscardResumableJob()
        {
            runner.ResetJobData();
            MacroProcessor.DiscardGenerated?.Invoke();
        }

        public bool canJog { get { return runner.CanJog; } }
        // A job is ready to start: a loaded job, or an active wizard program (so the physical Run button
        // runs a wizard's program too, not just a loaded file). False once a job/stream is actually running.
        public bool JobPending { get { return runner.JobPending; } }

        public bool Activate(bool activate)
        {
            if (activate && !initOK)
            {
                initOK = true;
                runner.SerialSize = Math.Min(AppConfig.Settings.Base.MaxBufferSize, (int)(GrblInfo.SerialBufferSize * 0.9f)); // size should be less than hardware handshake HWM
                Source.Parser.Dialect = GrblInfo.IsGrblHAL ? Dialect.GrblHAL : Dialect.Grbl;
                Source.Parser.ExpressionsSupported = GrblInfo.ExpressionsSupported;

                if (GrblInfo.HasRTC)
                    runner.SendCommand("$RTC=" + DateTime.Now.ToLocalTime().ToString("s"));
            }

            EnablePolling(activate);

            isActive = activate;
            runner.IsActive = activate;

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
            runner.CallHandler(StreamingState.Stop, false);
            return true;
        }

        private bool StartJob(Key key)
        {
            Run(0);
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
            if (runner.MachineState != GrblStates.Idle)
                btnHold_Click(null, null);
            return runner.MachineState != GrblStates.Idle;
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

        #region UIevents

        void btnHold_Click(object sender, RoutedEventArgs e)
        {
            Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
        }

        void btnStop_Click(object sender, RoutedEventArgs e)
        {
            runner.Abort();
        }

        void btnStart_Click(object sender, RoutedEventArgs e)
        {
            Run(0);
        }

        // Armed by selecting "Check Run" from the dropdown - NOT the same as model.IsCheckMode (which reflects
        // the controller ACTUALLY being in Check state right now). $C isn't sent here: picking the mode should
        // only be a label/intent change (Home and other Idle-gated controls must stay enabled until the
        // operator actually presses Run) - the real $C fires from Run() itself, right before it would otherwise
        // start streaming. Cleared by picking a different mode, or once Run() actually sends $C.
        // "Check Run" armed intent now lives on the runner - see JobRunner.CheckModeArmed.

        // Armed by selecting "Simulate" from the dropdown, same deferred-until-Run() idiom as checkModeArmed -
        // the connection switch to the bundled simulator only happens once the operator actually presses Run,
        // not at selection time (picking a mode from the dropdown must stay a label/intent change only).
        private bool simulateArmed = false;

        // Sets the popup's MIN width explicitly at the moment it opens, reading startPanel's already-settled
        // ActualWidth - see the XAML comment on Popup.Opened for why a live Width binding clips content on
        // the first open (a WPF Popup-layout-timing quirk, not fixable by just binding harder). MinWidth, not
        // Width: the row's width varies with whichever mode is CURRENTLY shown (btnStart.Content), and "Run"
        // is shorter than "Dry Run"/"Check Run" - a fixed Width bound to the row while it reads "Run" clipped
        // the longer entries. MinWidth keeps the popup at least as wide as the row (the original "full width"
        // ask) while still letting it grow to fit its own widest item when the row itself is narrower.
        private void StartModePopup_Opened(object sender, EventArgs e)
        {
            startModePopup.MinWidth = startPanel.ActualWidth;
        }

        // Run's mode dropdown (replaces the old right-click context menu): Run (normal) / Dry
        // Run / Check Run, each a Button in the popup tagged with which one it is. Applies the underlying mode
        // exactly as the old checkable menu items did (grbl Reset for check mode, the sender-side IsDryRunMode
        // flag for dry run - see checkModeArmed's own comment for why $C itself is deferred), then relabels
        // btnStart to match via UpdateRunButtonLabel - so the button's own text is always a live reflection
        // of the current mode, not just "whatever was last clicked" (e.g. it correctly reverts to Run if check
        // mode exits some other way, like Reset).
        private void StartMode_Click(object sender, RoutedEventArgs e)
        {
            startModePopup.IsOpen = false;

            var m = DataContext as GrblViewModel;
            if (m == null || !(sender is Button btn))
                return;

            GrblStates state = m.GrblState.State;
            switch (btn.Tag as string)
            {
                case "check":
                    m.IsDryRunMode = false;
                    runner.CheckModeArmed = true;
                    simulateArmed = false;
                    break;

                case "dryrun":
                    runner.CheckModeArmed = false;
                    simulateArmed = false;
                    if (state == GrblStates.Check)
                        Grbl.Reset();
                    m.IsDryRunMode = true;
                    break;

                case "simulate":
                    runner.CheckModeArmed = false;
                    if (state == GrblStates.Check)
                        Grbl.Reset();
                    m.IsDryRunMode = false;
                    simulateArmed = true;
                    break;

                default:   // normal Run
                    runner.CheckModeArmed = false;
                    simulateArmed = false;
                    if (state == GrblStates.Check)
                        Grbl.Reset();
                    m.IsDryRunMode = false;
                    break;
            }
            UpdateRunButtonLabel();
        }

        // Reflects the CURRENT mode, not the last dropdown click - GrblViewModel.IsCheckMode is itself derived
        // from GrblState (see its own getter), so this must be re-run on every GrblState change too (e.g. a
        // Reset elsewhere exits check mode without going through StartMode_Click at all). Also drives btnStart's
        // tooltip: disabled -> guidance on what to do first (shown even while disabled - see
        // ToolTipService.ShowOnDisabled in XAML); enabled -> what THIS press will actually do, matching the
        // selected mode - a plain "Alt+R" static tip left an operator to discover Dry Run/Check Run's real
        // effect (Z offset, spindle/coolant forced off, etc.) only by reading the dropdown's own tooltips first.
        // Shared by both Generate-first early-return branches in UpdateRunButtonLabel (pre- and post-generate) -
        // a tab that opted in via MacroProcessor.SupportsGenerateAndRun gets the mode dropdown back just to
        // offer that one entry, in either state (pressing it before Generate has run just means "build it,
        // then run it" instead of the normal two clicks).
        private void UpdateGenerateAndRunVisibility()
        {
            bool show = MacroProcessor.SupportsGenerateAndRun && MacroProcessor.ActiveGenerateAndRun != null;
            if (btnStartMode != null)
                btnStartMode.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (pnlNormalModes != null)
                pnlNormalModes.Visibility = Visibility.Collapsed;
            if (pnlGenerateAndRun != null)
                pnlGenerateAndRun.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GenerateAndRun_Click(object sender, RoutedEventArgs e)
        {
            startModePopup.IsOpen = false;
            MacroProcessor.ActiveGenerateAndRun?.Invoke();
        }

        // "Cycle Start"/"Start" (and the dropdown item that shares the same text) and "Feed Hold"/"Pause" -
        // see RunLabels' own comment (the single source of truth every other user-facing reference to these
        // two actions reads from too - signal tooltips, key-binding names, status messages).
        private object NormalModeLabel()
        {
            return RunLabels.CycleStart;
        }

        private void UpdateRunButtonLabel()
        {
            if (model == null || btnStart == null)
                return;

            if (btnHold != null)
                btnHold.Content = RunLabels.FeedHold;
            if (btnStartModeNormalItem != null)
                btnStartModeNormalItem.Content = NormalModeLabel();

            // A Generate-first tool tab (Start Job, Stepper Calibration, Auto Square, Surface Spoilboard,
            // Odd Jobs' job wizards) is focused: it owns no standalone Generate button of its own any more
            // (see MacroProcessor's Generate-mode plumbing) - the Run bar itself reads "Generate" (gated on
            // IsGenerateReady) until the tab has built its program, then flips to plain "Run" (or, for a tab
            // that opted in via AllowRunModesWhenGenerated, the normal mode dropdown - see below).
            if (MacroProcessor.SupportsGenerateMode && !MacroProcessor.IsProgramGenerated)
            {
                btnStart.Content = FindResource("GenerateLabel");
                IsRunActionEnabled = IsRunEnabled && MacroProcessor.IsGenerateReady;
                IsGenerateActionReady = IsRunActionEnabled;
                btnStart.ToolTip = IsRunActionEnabled ? FindResource("GenerateTipReady") : FindResource("GenerateTipDisabled");
                UpdateGenerateAndRunVisibility();
                return;
            }
            // Program's generated (or this isn't a Generate-first tab at all). Most Generate-first tabs are
            // pure setup/probing macros where Dry Run/Check Run/Simulate don't mean anything, so they stay
            // hidden even now - only a tab that explicitly opted in (Odd Jobs' cutting wizards - a generated
            // program there IS real toolpath worth dry-running) falls through to the normal mode-dropdown
            // logic below instead.
            if (MacroProcessor.SupportsGenerateMode && !MacroProcessor.AllowRunModesWhenGenerated)
            {
                btnStart.Content = NormalModeLabel();
                IsRunActionEnabled = IsRunEnabled;
                IsGenerateActionReady = false;
                btnStart.ToolTip = FindResource("StartTipNormal");
                UpdateGenerateAndRunVisibility();
                return;
            }
            if (btnStartMode != null)
                btnStartMode.Visibility = Visibility.Visible;
            if (pnlNormalModes != null)
                pnlNormalModes.Visibility = Visibility.Visible;
            if (pnlGenerateAndRun != null)
                pnlGenerateAndRun.Visibility = Visibility.Collapsed;
            IsRunActionEnabled = IsRunEnabled;
            IsGenerateActionReady = false;

            // Neither mode is ever saved to config (IsDryRunMode is a plain in-memory GrblViewModel field,
            // always false on a fresh instance; IsCheckMode is a live read of GrblState.State - see their own
            // declarations) - so there is nothing here to "reset on startup". What LOOKED like the selection
            // surviving a restart was actually the CONTROLLER genuinely still sitting in its own real Check
            // state from before (grblHAL has no auto-exit for $C - see ResetRunModeAfterJob), which a fresh
            // reconnect would truthfully re-report. Belt-and-suspenders anyway: before a real connection
            // exists (GrblState still Unknown - the pre-connect default), neither mode is meaningful, so
            // always show plain Run regardless of whatever IsCheckMode/IsDryRunMode happen to read right now.
            bool connected = model.GrblState.State != GrblStates.Unknown;
            // checkModeArmed (picked from the dropdown, $C not sent yet - see its own comment) reads the same
            // as actually being in Check state (a real, already-running check) - both mean "Run will behave
            // as Check Run", just at different points before/after the operator actually presses it.
            bool showCheck = runner.CheckModeArmed || (connected && model.IsCheckMode);
            // simulateActive (the run already switched connections and is streaming against the sim right
            // now) reads the same as simulateArmed (picked but not yet pressed) - both mean "this run is/will
            // be against the simulator", matching checkModeArmed/model.IsCheckMode's own before/after pairing.
            bool showSimulate = simulateArmed || runner.SimulateActive;
            btnStart.Content = showCheck ? FindResource("StartModeCheck")
                              : showSimulate ? FindResource("StartModeSimulate")
                              : connected && model.IsDryRunMode ? FindResource("StartModeDryRun")
                              : NormalModeLabel();
            btnStart.ToolTip = !IsRunEnabled ? FindResource("StartTipDisabled")
                              : showCheck ? FindResource("StartTipCheck")
                              : showSimulate ? FindResource("StartTipSimulate")
                              : connected && model.IsDryRunMode ? FindResource("StartTipDryRun")
                              : FindResource("StartTipNormal");
        }

        #endregion

        // The streaming state machine itself lives in CNC.Core.JobRunner as of step 4c-4 - Run, the six
        // Streaming* handlers, ResponseReceived's flow control, SendNextLine and GrblStateChanged. What is
        // left here is the operator's side of it: the DependencyProperties the XAML binds to, the run button
        // and its mode dropdown, the keyboard shortcuts, and the handful of host services the engine asks for
        // (words, a wait cursor, a timer, the marshals). The forwarders below keep every existing caller -
        // MainWindow.RunControl.*, JobView - spelled exactly as before.

        // The pump-stall watchdog's timer. It stays on this side because its PRIORITY is the point: a
        // Background-priority DispatcherTimer can never preempt streaming or operator input, and it is a
        // watchdog - being late is harmless, being in the way is not. The engine owns the decision (is the
        // pump actually stalled?), this owns the clock.
        private DispatcherTimer idleKickTimer;

        // Tell the engine how to reach the operator. Every one of these is optional to the engine and each
        // unset default is the headless one - registering them is what makes it a desktop sender rather than
        // a server. See JobRunner's "Host seams" region for what each is for.
        private void RegisterEngineSeams()
        {
            // The engine names a message, this resolves it - the strings are JobControl.xaml's own resources,
            // localized per-locale like every other UI string. Core has no dictionary that contains them (it
            // has its OWN LibStrings, which is exactly how a FindResource in the wrong assembly resolves to
            // nothing and still compiles).
            runner.Localizer = key => FindResource(key) as string;

            // DeclaredStock, NOT the .Stock property - .Stock falls back to the machine's FULL Z travel range
            // as a conservative default when the program has no (STOCK ...) comment, which is right for other
            // features but wildly wrong as a dry-run clearance. No declaration = 0 extra clearance.
            runner.DeclaredStockZ = () => ProgramView.LoadedJob != null && ProgramView.LoadedJob.IsLoadedJob
                                            ? (ProgramView.LoadedJob.DeclaredStock?.Z ?? 0d) : 0d;

            runner.BusyCursor = () => new UIUtils.WaitCursor();

            // Two marshals, and the difference is deliberate - the priorities stay here in the WPF host rather
            // than inside the now-portable pump. Control flow (job finished / error) at Normal, because the
            // state machine must not wait behind display work; the coalesced per-line status markers at
            // Background, because they must never compete with the streaming itself or with operator input.
            runner.ControlMarshal = a => Dispatcher.BeginInvoke(a, DispatcherPriority.Normal);
            runner.DisplayMarshal = a => Dispatcher.BeginInvoke(a, DispatcherPriority.Background);

            // Read live, not captured - a Settings change applies to the next line streamed.
            runner.GetSendComments = () => AppConfig.Settings.Base.SendComments;
            runner.GetStartSimulator = () => AppConfig.Settings.Base.StartSimulator;

            // The mirror of PrepareRun: a "Simulate" run switched the live connection to the bundled
            // simulator, so the end of the run switches it back - whatever ended it. See SimulatorManager.
            runner.RestoreAfterRun = () => SimulatorManager.RestoreConnectionAfterSimulate?.Invoke();

            // Resolved lazily so it does not force GCode.File creation during early startup.
            runner.DefaultSource = () => GCode.File;

            // Unguarded on purpose: this is the "set the poll rate" primitive the engine drives (MPG taking
            // control stops polling outright). EnablePolling below is the guarded variant this control uses
            // for its own tab-activation bookkeeping.
            runner.SetPolling = enable => model?.Poller.SetState(enable ? AppConfig.Settings.Base.PollInterval : 0);

            runner.RequestIdleKick = ArmIdleKick;
            runner.CancelIdleKick = () => idleKickTimer?.Stop();

            // Core states the condition, this words it. Both are load-time warnings about the program that
            // was just opened, raised before the engine settles the run controls.
            runner.LoadedJobWarning = warning =>
            {
                switch (warning)
                {
                    case JobRunner.JobLoadWarning.ToolReferenceNotSet:
                        AppDialogs.Show(string.Format((string)FindResource("JobToolReference"), runner.Source.ToolChanges), "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                        break;

                    case JobRunner.JobLoadWarning.NotHomedForPredefinedPosition:
                        AppDialogs.Show((string)FindResource("JobG28G30"), "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                        break;
                }
            };
        }

        // (Re)arm the pump-stall watchdog: if the controller is still idle a short while from now while the
        // pump still thinks a job is in flight, the pump has stalled - nudge it to resume sending / finish.
        // One-shot; re-armed on each idle report and cancelled by any non-idle report (see the engine's
        // GrblStateChanged, which drives both through RequestIdleKick/CancelIdleKick).
        private void ArmIdleKick()
        {
            if (idleKickTimer == null)
            {
                idleKickTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(700)
                };
                idleKickTimer.Tick += (s, e) =>
                {
                    idleKickTimer.Stop();
                    runner.OnIdleKick();
                };
            }
            idleKickTimer.Stop();
            idleKickTimer.Start();
        }

        #region Engine forwarders
        // Kept so every existing caller - MainWindow.RunControl.Run/Source, JobView's RewindFile/CallHandler -
        // reads exactly as it did before the engine moved. Same approach as MacroProcessor.Run forwarding to
        // MacroRunner.Run: relocating the code should not churn 17 unrelated call sites.

        /// <summary>The program the streamer reads - a tool can point this at its own in-memory program.</summary>
        public IProgramSource Source
        {
            get { return runner.Source; }
            set { runner.Source = value; }
        }

        // honorActiveProgram: when a wizard tab is up it registers its program as the active program
        // (MacroProcessor.ActiveRun). A fresh (idle) Run then runs THAT instead of the loaded job - so one
        // Run runs whatever program is active, loaded file or wizard. The internal stream-starters that
        // already have a Source primed (the in-place run, StartLoadedJob) pass false so they don't re-enter it.
        public void Run(int fromBlock, bool honorActiveProgram = true)
        {
            runner.Run(fromBlock, honorActiveProgram);
        }

        public bool CallHandler(StreamingState state, bool always)
        {
            return runner.CallHandler(state, always);
        }

        public void RewindFile()
        {
            runner.RewindFile();
        }

        public void SendRTCommand(string command)
        {
            runner.SendRTCommand(command);
        }

        #endregion
    }
}
