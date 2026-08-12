/*
 * JobRunner.cs - part of CNC Core library
 *
 * The streaming state machine: it feeds a g-code program to the controller, tracks what the machine is
 * doing while it does, and decides what the operator may do right now - whether Run, Feed Hold, Stop and
 * Rewind are available, and whether the Stop button is currently offering Stop or Pause.
 *
 * It arrived here in four steps, on purpose, because this is the code most able to hurt a machine:
 *
 *   4a  the send/ack pump moved out first (StreamPump), on its own.
 *   4b  this class was created holding ONLY the run-control state above - the ~49 writes the machine makes
 *       across its six handlers, which used to go straight into WPF DependencyProperties. No logic moved.
 *       That proved the mirror on real hardware before any streaming logic depended on it.
 *   4c  the three things that made Run un-moveable were taken out of it one at a time: the Generate-vs-Run
 *       policy, the run-mode armed intents, and the engine's calls back into the view.
 *   4c-4 the machine itself moved, unchanged.
 *
 * The host mirrors the run-control state into whatever its widgets bind to (ioSender: JobControl's existing
 * DependencyProperties, so the XAML and every binding are untouched), and registers the seams in the "Host
 * seams" region below for everything the engine needs from an operator's machine - words, a wait cursor, a
 * timer, thread marshals. Every seam is optional and every unset default is the headless one.
 *
 * Semantics are preserved exactly as the state machine had them, deliberately including the parts that
 * look redundant:
 *
 *  - FeedHoldArmed and CanFeedHold are SEPARATE. The machine tracks "a hold would be meaningful here"
 *    (armed) and the view gates it again on GrblViewModel.FeedHoldDisabled, which the controller reports
 *    via M53 in the parser state and can change without the machine running at all. Deriving CanFeedHold
 *    from the two here would have moved that second gate into Core, changing when it is evaluated - not
 *    a refactor of the one control that stops a moving machine.
 *  - StopShowsPause is state, not text. The client renders "Stop"/"Pause" from its own resources; Core
 *    does not know the words. (See ControllerValidator for why Core never localizes: this solution has
 *    three LibStrings classes over two dictionaries, and a FindResource in the wrong assembly compiles
 *    clean and silently resolves nothing.)
 *
 * Setters dedupe, matching DependencyProperty semantics - assigning the value it already holds raises
 * nothing, exactly as SetValue would not have re-run IsRunEnabled's change callback.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CNC.Core
{
    public class JobRunner : ViewModelBase
    {
        // --- Host policy: what to run -------------------------------------------------------------------
        // "Cycle Start" does not always mean "stream the loaded job". A tool tab (Start Job, Surface
        // Spoilboard, Odd Jobs, ...) can be focused with its own program, and one that has not generated yet
        // must generate first. Which program that is, and whether a tab is even focused, is entirely a client
        // concept - so the engine asks rather than knows.
        //
        // Each returns true if it handled the press, false to fall through to the ordinary behaviour.
        // Unset means no host policy: always fall through, which is exactly right for a headless run.
        //
        // The engine keeps the GATING (is this idle? is this a resume?) because that is streaming state, and
        // it calls these at two specific points whose ORDER is load-bearing - RunActiveProgram in particular
        // sits after the hold/tool-change/timer resume branches, so pressing Run while paused resumes the job
        // instead of launching a wizard program.

        /// <summary>The focused tool tab still has to build its program; Run should generate, not stream.</summary>
        public System.Func<bool> GenerateActiveProgram;

        /// <summary>Run the focused tool tab's own program rather than the loaded job.</summary>
        public System.Func<bool> RunActiveProgram;

        public bool TryGenerateActiveProgram()
        {
            return GenerateActiveProgram != null && GenerateActiveProgram();
        }

        public bool TryRunActiveProgram()
        {
            return RunActiveProgram != null && RunActiveProgram();
        }

        // --- Host policy: preparing a run ---------------------------------------------------------------
        /// <summary>
        /// Host work that must happen before a run starts, at the very top of Run. Return false to abort it.
        /// ioSender uses this for the run-mode selector's "Simulate", which has to switch the CONNECTION to
        /// the simulator - launching and connecting it synchronously - and repaint the run button. All of
        /// that is host business; the engine only needs to know whether to carry on.
        /// Unset = nothing to prepare, run proceeds.
        /// </summary>
        public System.Func<bool> PrepareRun;

        public bool PrepareForRun()
        {
            return PrepareRun == null || PrepareRun();
        }

        // --- Run-mode state -----------------------------------------------------------------------------
        // Armed by the host's run-mode selector, consumed by the engine. Deliberately plain state rather
        // than an argument to Run: arming and pressing Run are two separate operator actions, and the
        // intent has to survive between them.

        /// <summary>
        /// "Check Run" is armed. The engine sends $C at the next idle start and clears this. Not applied
        /// mid-run: a hold or tool-change resume is not "starting a check run", so it stays armed for the
        /// next genuine fresh start.
        /// </summary>
        public bool CheckModeArmed { get; set; }

        /// <summary>
        /// This run switched the connection to the simulator, so the end of the run has to switch back.
        /// Stays false when the session was already on the simulator - nothing was disturbed.
        /// </summary>
        public bool SimulateActive { get; set; }

        // Step 7 (unified streaming engine): the NEXT Run() is a macro run started programmatically by
        // MacroProcessor.Run - the macro was just pushed/loaded as the job and Run(0, false) follows
        // immediately. One-shot, consumed (and cleared) at the top of Run() itself so no early-return
        // path can leave it armed for a later, unrelated Cycle Start. Effects while consumed:
        //   - macro: the dry-run G92 Z-clearance preamble is skipped. Per-line spindle/coolant/M6
        //     suppression still applies in the pump (gated on IsDryRunMode alone) - that pair is exactly
        //     the protection macro runs had under the retired MacroRunner engine, where dry run
        //     neutralised lines but never shifted Z (a Z shift corrupts a probing macro's positioning).
        //   - unattended: the (PROMPT) field dialog takes each field's declared default, and the pump
        //     auto-answers (MBOX)/bare-(PROMPT) holds OK - MacroRunner.Run's own 'unattended' contract.
        private bool macroRunPending, unattendedRunPending;
        public void ArmMacroRun(bool unattended)
        {
            macroRunPending = true;
            unattendedRunPending = unattended;
        }

        // --- Host policy: is a tool tab's program the thing Run would run? ------------------------------
        // Both are pure client bookkeeping (which tab is focused, whether it has generated), so the engine
        // asks rather than reads. Unset = no tool tab in play, which is what a headless host wants.

        /// <summary>A tool tab's own program is the active one, whether or not a job is also loaded.</summary>
        public System.Func<bool> HasActiveProgram;

        /// <summary>A Generate-first tab is focused and has not built its program yet.</summary>
        public System.Func<bool> GenerateModeBlocking;

        public bool AnyActiveProgram
        {
            get { return HasActiveProgram != null && HasActiveProgram(); }
        }

        public bool IsGenerateBlocking
        {
            get { return GenerateModeBlocking != null && GenerateModeBlocking(); }
        }

        private bool _activeProgramReady = false, _controlsEnabled = false;

        /// <summary>
        /// A tool tab's program is loaded and the machine is idle: it is ready to run on the next Run press.
        /// The host paints its own "press Run" cue from this and writes the matching status line - Core does
        /// not know the words (see the header).
        /// </summary>
        public bool ActiveProgramReady
        {
            get { return _activeProgramReady; }
            set { if (_activeProgramReady != value) { _activeProgramReady = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// The run controls are usable at all - false while the controller is under MPG (pendant) control,
        /// where the sender must not drive it. Distinct from the individual Can* flags: this is "is this
        /// surface live", they are "is this particular action available right now".
        /// </summary>
        public bool ControlsEnabled
        {
            get { return _controlsEnabled; }
            set { if (_controlsEnabled != value) { _controlsEnabled = value; OnPropertyChanged(); } }
        }

        private bool _canRun = false, _canStop = false, _canRewind = false;
        private bool _feedHoldArmed = false, _canFeedHold = false, _stopShowsPause = false;

        /// <summary>A program can be started (or resumed) right now.</summary>
        public bool CanRun
        {
            get { return _canRun; }
            set { if (_canRun != value) { _canRun = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// The state machine's own view: a feed hold would be meaningful in the current streaming state.
        /// NOT the same as CanFeedHold - see the header.
        /// </summary>
        public bool FeedHoldArmed
        {
            get { return _feedHoldArmed; }
            set { if (_feedHoldArmed != value) { _feedHoldArmed = value; OnPropertyChanged(); } }
        }

        /// <summary>Feed hold is offered to the operator (armed, and not disabled by the controller).</summary>
        public bool CanFeedHold
        {
            get { return _canFeedHold; }
            set { if (_canFeedHold != value) { _canFeedHold = value; OnPropertyChanged(); } }
        }

        /// <summary>Stop is available.</summary>
        public bool CanStop
        {
            get { return _canStop; }
            set { if (_canStop != value) { _canStop = value; OnPropertyChanged(); } }
        }

        /// <summary>Rewind is available (a completed or partially-streamed program can be re-run).</summary>
        public bool CanRewind
        {
            get { return _canRewind; }
            set { if (_canRewind != value) { _canRewind = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// The Stop control is currently offering Pause rather than Stop. State only - the host picks the
        /// word from its own localized resources.
        /// </summary>
        public bool StopShowsPause
        {
            get { return _stopShowsPause; }
            set { if (_stopShowsPause != value) { _stopShowsPause = value; OnPropertyChanged(); } }
        }


        // ================================================================================================
        // The streaming state machine.
        //
        // Everything below this line ran in JobControl.xaml.cs - a WPF UserControl code-behind - until step
        // 4c-4. It is the code that actually feeds a program to the controller and decides what the machine
        // is allowed to do while it does: the six Streaming* handlers and their dispatch table,
        // ResponseReceived's flow-control accounting, SendNextLine, GrblStateChanged, and Run itself.
        //
        // It moved unchanged. The seams immediately below are the whole of what was rewritten - every other
        // line is byte-identical to the version that has been running on real hardware, deliberately,
        // because a state machine that starts and stops machine motion is not the place to also improve the
        // code. Anything worth changing here gets changed afterwards, on its own, where it can be reviewed
        // as a change rather than lost inside a move.
        //
        // Each seam is unset by default and each default is the headless one: no words, no cursor, no
        // stock declaration, no simulator to restore, nothing to marshal through. A server that registers
        // none of them still streams correctly - it just has no operator to tell.
        // ================================================================================================

        #region Host seams

        /// <summary>
        /// Resolve one of the engine's status messages ("Checking", "DryRun", "TransferComplete") to text.
        /// Core does NOT own these strings - they live in the client's own resources (ioSender: JobControl.xaml,
        /// localized per-locale through x:Uid like every other UI string). Same split as StopShowsPause: the
        /// engine says WHICH message, the host says what it reads. Unset = no message, which is right for a
        /// host with nobody to show it to.
        /// </summary>
        public System.Func<string, string> Localizer;

        private string Localize(string key)
        {
            return Localizer == null ? string.Empty : (Localizer(key) ?? string.Empty);
        }

        /// <summary>
        /// Extra Z clearance for a dry run, from the loaded job's DECLARED stock height (a (STOCK ...) comment
        /// in the program), in mm. Unset, or no declaration, = 0 - no declaration means no extra clearance, not
        /// a guess. Deliberately not the general "stock size" property, which falls back to the machine's whole
        /// Z travel when undeclared - conservative elsewhere, wildly wrong here.
        /// </summary>
        public System.Func<double> DeclaredStockZ;

        /// <summary>
        /// A busy indicator for the rewind, which walks the whole program. Disposed when it ends. Unset = no
        /// indicator; a host with no cursor to change simply does not register one.
        /// </summary>
        public System.Func<IDisposable> BusyCursor;

        /// <summary>
        /// The two marshals the StreamPump reports through, kept separate on purpose - see StreamPump's own
        /// header. Control flow (job finished / error) must not queue behind display work, and the coalesced
        /// per-line status markers must not compete with the streaming. Unset = run inline on the pump thread,
        /// which is what a host with no UI thread wants.
        /// </summary>
        public System.Action<System.Action> ControlMarshal, DisplayMarshal;

        /// <summary>
        /// Whether the host wants g-code comments sent to the controller, and whether the connected device is
        /// the bundled simulator (which parses (TOOL ...) comments for material removal, so it always gets
        /// them). Read LIVE on every line rather than captured, so changing the setting mid-session applies to
        /// the next line - these are ioSender's Settings toggles. Unset = false: no comments, no simulator.
        /// </summary>
        public System.Func<bool> GetSendComments, GetStartSimulator;

        private bool SendComments { get { return GetSendComments != null && GetSendComments(); } }
        private bool StartSimulator { get { return GetStartSimulator != null && GetStartSimulator(); } }

        /// <summary>
        /// Undo whatever PrepareRun did, once the run is over - the mirror of that seam. ioSender uses it to
        /// switch the connection back off the simulator after a "Simulate" run. Called from every job-end path
        /// (finish, error, abort) and only when SimulateActive says there is something to undo.
        /// </summary>
        public System.Action RestoreAfterRun;

        /// <summary>
        /// The program to stream when nothing has been pointed at explicitly - the host's "the loaded job".
        /// Resolved lazily on every read so it never forces that program into existence during startup.
        /// Unset = nothing loaded, and IsLoaded is false throughout, which is what a fresh server has.
        /// </summary>
        public System.Func<IProgramSource> DefaultSource;

        /// <summary>
        /// The host's run controls are the surface the operator is actually looking at. ioSender's run bar is
        /// visible on every tab but only "active" on the Job tab, so machine-state transitions are processed
        /// when this is set OR a tool tab's program is the active one OR a job is genuinely in flight (see
        /// GrblStateChanged).
        /// <para>
        /// Starts FALSE, matching the tab that has not been activated yet - a host with no tab concept must
        /// set it once at startup or the engine ignores machine-state transitions outside a running job.
        /// Defaulting it true would have been friendlier to a headless host and wrong here: this control's
        /// flag was false until the Job tab first activated, which is before the first status report arrives.
        /// </para>
        /// </summary>
        public bool IsActive = false;

        /// <summary>Start or stop status polling. Unset = the host polls on its own schedule.</summary>
        public System.Action<bool> SetPolling;

        /// <summary>
        /// The pump-stall watchdog's timer, which belongs to the host: OnIdleKick has to run a short while
        /// after the controller goes idle, and at a priority that cannot preempt streaming or operator input
        /// (ioSender: a Background-priority DispatcherTimer). Arm it, cancel it, and call OnIdleKick when it
        /// fires. Unset = no watchdog; the engine is correct without one, just slower to notice a stall.
        /// </summary>
        public System.Action RequestIdleKick, CancelIdleKick;

        #endregion

        #region Streaming state

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
            public System.Func<StreamingState, bool, bool> Call;
        }

        private struct JobData
        {
            public int CurrBlock, LastExecuting, PendingLine, PgmEndLine, ToolChangeLine, ACKPending, serialUsed;
            public bool Started, Transferred, Complete, IsSDFile, IsChecking, HasError, Stopped, ToolChanged;
            // Set the first time the controller's reported line number actually matches a block of THIS
            // program - i.e. proof that execution-driven progress works here. See OnLineNumberChanged.
            public bool LineNumbersTracking;
            public GCodeBlock CurrentRow, NextRow;
        }

        private int serialSize = 128;
        private bool useBuffering = false;

        // Probe-streaming throttle: once a probe (G38) has been streamed, cap look-ahead (ProbeLookahead lines)
        // and never send past an in-flight probe until it completes - so a streamed probe macro can't race lines
        // into the controller's RX during a probe. Self-scoping: normal cutting jobs (no G38) are untouched.
        private bool jobHasProbe = false, probePending = false;
        // Set when the currently running job had dry-run mode applied at start (G92 Z-offset queued, M5/M9
        // preamble sent, and per-line M3/M4/M7/M8 suppression armed for the streamers). Cleared (G92.1 sent)
        // at every job-end path so the temporary offset never survives past the run. See Run,
        // OnPumpJobFinished, OnPumpError and AbortPump.
        private bool dryRunActive = false;

        private volatile StreamingState streamingState = StreamingState.NoFile;
        private GrblState grblState;
        private GrblViewModel model;
        private JobData job;
        private int missed = 0;

        // Background send/ack pump - owns flow control off the caller's thread. When pumpActive,
        // ResponseReceived's accounting is skipped (the pump owns it).
        private StreamPump pump;
        private volatile bool pumpActive = false;

        private StreamingHandlerFn[] streamingHandlers = new StreamingHandlerFn[(int)StreamingHandler.Max];
        private StreamingHandlerFn streamingHandler;

        // The program the streamer reads. Defaults to the host's loaded job (DefaultSource); a tool can point
        // it at its own in-memory program for a run (so the run never touches the job buffer), then reset it
        // to null.
        private IProgramSource _source;
        public IProgramSource Source
        {
            get { return _source ?? (DefaultSource == null ? null : DefaultSource()); }
            set { _source = value; }
        }

        public delegate void StreamingStateChangedHandler(StreamingState state, bool MPGMode);
        public event StreamingStateChangedHandler StreamingStateChanged;

        /// <summary>The streaming state machine's current state - what the engine believes it is doing.</summary>
        public StreamingState StreamingState { get { return streamingState; } }

        /// <summary>A job is part-streamed: the block the stream is currently at, 0 when nothing has run.</summary>
        public int CurrentBlock { get { return job.CurrBlock; } }

        /// <summary>The program ran to its end. Cleared by a rewind.</summary>
        public bool JobComplete { get { return job.Complete; } }

        public JobRunner()
        {
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
        }

        /// <summary>
        /// Bind the engine to the machine it drives. The host owns the model's lifetime and its other
        /// subscribers; this takes only what the state machine itself needs - the command responses it does
        /// flow control on, and (via the host, which still routes property changes) the state transitions.
        /// </summary>
        public void Attach(GrblViewModel model)
        {
            if (this.model != null)
                this.model.OnCommandResponseReceived -= ResponseReceived;

            this.model = model;

            if (model != null)
                model.OnCommandResponseReceived += ResponseReceived;
        }

        /// <summary>Drive the state machine directly - the host's Stop/Rewind buttons and tab changes.</summary>
        public bool CallHandler(StreamingState state, bool always)
        {
            return streamingHandler.Call(state, always);
        }

        /// <summary>
        /// Wipe everything that lets a later Cycle Start silently continue a job from where it left off.
        /// The host calls this after a controller reboot or a completed homing cycle.
        /// </summary>
        public void ResetJobData()
        {
            job = new JobData();
        }

        /// <summary>The machine is in a state where jogging is allowed.</summary>
        public bool CanJog { get { return grblState.State == GrblStates.Idle || grblState.State == GrblStates.Tool || grblState.State == GrblStates.Jog; } }

        /// <summary>The MPG (pendant) has control - the sender must not drive the machine.</summary>
        public bool MPGActive { get { return grblState.MPG; } set { grblState.MPG = value; } }

        /// <summary>The machine state the engine last acted on.</summary>
        public GrblStates MachineState { get { return grblState.State; } }

        /// <summary>Feed hold availability re-gated by the controller's own M53 report - see the header.</summary>
        public void RefreshFeedHoldGate(bool feedHoldDisabled)
        {
            CanFeedHold = !feedHoldDisabled && FeedHoldArmed;
        }

        /// <summary>The host's watchdog timer fired: nudge a pump that has stalled with the controller idle.</summary>
        public void OnIdleKick()
        {
            PumpLog.W(string.Format("IDLEKICK timer fire  pumpActive={0} state={1}", pumpActive, grblState.State));
            if (pumpActive && grblState.State == GrblStates.Idle)
                pump?.KickIdle();
        }

        /// <summary>
        /// How many bytes of the controller's receive buffer the streamer may fill. Set once the controller
        /// has reported its own buffer size; it must stay under the hardware handshake high-water mark.
        /// </summary>
        public int SerialSize { get { return serialSize; } set { serialSize = value; } }

        /// <summary>Whether to keep the controller's receive buffer full - re-read when the setting changes.</summary>
        public bool UseBuffering { get { return useBuffering; } set { useBuffering = value; } }

        #endregion

        #region Host-driven transitions
        // The machine reports through the host's view model, and the host still owns that subscription - it
        // has its own display work to do on the same notifications. These are the engine's half of it, one
        // entry point per notification, called at exactly the point in each case the inline code used to sit.
        // Splitting it this way rather than subscribing twice is deliberate: for some of these the display
        // work runs before the engine's and for others after, and a second subscriber cannot express that.

        /// <summary>The controller reported the line number it is executing: catch the sent markers up to it.</summary>
        public void OnLineNumberChanged(int lineNum)
        {
            if (job.CurrBlock > 0)
            {
                int found = 0;
                var block = job.CurrBlock;
                do
                {
                    if (Source.Data[block].LineNum == lineNum)
                    {
                        found = block - 1;
                        Source.Data[block].Sent = "@";
                        // This report matched a real block, so execution-driven progress demonstrably works
                        // for this program on this controller - the ack handler can stop marking "ok" ahead
                        // of the tool from here on. Proved rather than assumed: a program with no N words,
                        // or a controller not reporting Ln:, never gets here and keeps the old behaviour.
                        job.LineNumbersTracking = true;
                        if (pump != null)
                            pump.ExecutionDrivenProgress = true;   // the pump does the acking on the live path
                        break;
                    }
                } while (--block > job.LastExecuting);
                while (job.LastExecuting < found)
                {
                    Source.Data[++job.LastExecuting].Sent = "ok";
                }

                // Follow the TOOL once we own the progress markers. Left to the ack handler this scrolls to
                // whatever the controller has swallowed, which is the whole planner buffer ahead - so the
                // green executing line ends up off-screen above a block of unmarked rows.
                if (job.LineNumbersTracking && job.CurrBlock > 5)
                    model.ScrollPosition = Math.Max(0, job.LastExecuting - 5);
            }
        }

        /// <summary>
        /// The link dropped mid-job. The state machine would otherwise sit waiting on acks that can never
        /// arrive, leaving the job "running" with the connection already gone.
        /// </summary>
        public void OnConnectionLost()
        {
            // Whatever was queued for MDI referred to the machine on the other end of a link that is now
            // gone; the dispatcher re-taps the new link when the next command arrives (MdiDispatcher.Send).
            mdi.Abort();

            if (model.IsJobRunning || JobTimer.IsRunning)
            {
                AbortPump();
                streamingHandler.Call(StreamingState.Stop, true);
            }
        }

        /// <summary>The pendant took or released control.</summary>
        public void OnMPGChanged(bool mpg)
        {
            grblState.MPG = mpg;
            SetPolling?.Invoke(!grblState.MPG);
            streamingHandler.Call(grblState.MPG ? StreamingState.Disabled : StreamingState.Idle, false);
        }

        /// <summary>The controller reached the end of a program (M2/M30).</summary>
        public void OnProgramEnd()
        {
            if (!Source.IsLoaded)
                streamingHandler.Call(model.IsSDCardJob ? StreamingState.JobFinished : StreamingState.NoFile, model.IsSDCardJob);
            else if (JobTimer.IsRunning && !job.Complete)
                streamingHandler.Call(StreamingState.JobFinished, true);
            if (!model.IsParserStateLive)
                SendCommand(GrblConstants.CMD_GETPARSERSTATE);
        }

        /// <summary>
        /// Raised when a program is warned about at load time - it needs a tool reference the machine has not
        /// set, or it parks at a predefined position on an unhomed machine. Core states the condition; the
        /// host words the warning and decides whether anyone is there to read it.
        /// </summary>
        public System.Action<JobLoadWarning> LoadedJobWarning;

        public enum JobLoadWarning
        {
            /// <summary>The program changes tools but no tool length offset reference has been set.</summary>
            ToolReferenceNotSet,
            /// <summary>The program goes to a predefined position (G28/G30) on a machine that is not homed.</summary>
            NotHomedForPredefinedPosition
        }

        /// <summary>A different program was loaded (or the loaded one cleared): reset the line accounting.</summary>
        public void OnFileNameChanged()
        {
            job.IsSDFile = false;
            if (string.IsNullOrEmpty(model.FileName))
                job.NextRow = null;
            else
            {
                job.ToolChangeLine = -1;
                job.ToolChanged = false;
                job.CurrBlock = job.PendingLine = job.ACKPending = model.BlockExecuting = 0;
                job.PgmEndLine = Source.Blocks - 1;
                if (model.IsPhysicalFileLoaded)
                {
                    if (Source.ToolChanges > 0 && GrblSettings.HasSetting(grblHALSetting.ToolChangeMode)
                        && GrblSettings.GetInteger(grblHALSetting.ToolChangeMode) > 0 && !model.IsTloReferenceSet)
                        LoadedJobWarning?.Invoke(JobLoadWarning.ToolReferenceNotSet);
                    if (Source.HasGoPredefinedPosition && model.IsGrblHAL && model.HomedState != HomedState.Homed)
                        LoadedJobWarning?.Invoke(JobLoadWarning.NotHomedForPredefinedPosition);
                    streamingHandler.Call(Source.IsLoaded ? StreamingState.Idle : StreamingState.NoFile, false);
                }
            }
        }

        /// <summary>The controller was soft-reset: tear the run down. The host still discards its own program.</summary>
        public void OnGrblReset()
        {
            AbortPump();
            JobTimer.Stop();
            // A soft reset answers nothing that was outstanding and executes nothing that was queued -
            // drop both rather than let a stale $J record (or a stale queued jog) survive the reset.
            // The dispatcher restarts itself on the next command.
            mdi.Abort();
            streamingHandler.Call(StreamingState.Stop, true);
            ResetJobData();
        }

        /// <summary>The operator stopped the job.</summary>
        public void Stop()
        {
            AbortPump();
            JobTimer.Stop();
            job.Stopped = true;
            streamingHandler.Call(StreamingState.Stop, true);
        }

        /// <summary>Abort a run without marking it operator-stopped - the run-bar Stop button.</summary>
        public void Abort()
        {
            AbortPump();
            streamingHandler.Call(StreamingState.Stop, true);
        }

        /// <summary>Rewind to the start and re-enter the current state, so the enables settle again.</summary>
        public void Rewind()
        {
            RewindFile();
            streamingHandler.Call(streamingState, true);
        }

        /// <summary>
        /// A program is ready to start: a loaded job, or a tool tab's own program (so a physical Cycle Start
        /// runs a wizard's program too). False once a job is actually running.
        /// </summary>
        public bool JobPending { get { return ((Source?.IsLoaded ?? false) || AnyActiveProgram) && !JobTimer.IsRunning; } }

        #endregion

        public void Run(int fromBlock, bool honorActiveProgram = true)
        {
            // One-shot macro-run intent (Step 7): consumed HERE, before any early return can strand it
            // armed for the next unrelated Cycle Start. See ArmMacroRun.
            bool macroRun = macroRunPending, unattendedRun = unattendedRunPending;
            macroRunPending = unattendedRunPending = false;

            // Entry and every early return below are logged. This method has a dozen ways to decline
            // without starting anything, several of them correct and silent by design - which leaves an
            // operator whose Cycle Start "did nothing" with no way to find out which one it was.
            DebugLog.Write("run", string.Format("JobRunner.Run: fromBlock={0} honorActive={1} macroRun={2} unattended={3} state={4} loaded='{5}'",
                fromBlock, honorActiveProgram, macroRun, unattendedRun, grblState.State, model.FileName));

            // Host work first - switching the connection to the simulator when "Simulate" was armed. That
            // whole step is client business (it launches the simulator and repaints the run button), so it
            // lives in RegisterActiveProgramPolicy below; false means it could not prepare and the run is off.
            if (!PrepareForRun())
            {
                DebugLog.Write("run", "JobRunner.Run: STOPPED - PrepareForRun() refused (host could not prepare, e.g. Simulate switch)");
                return;
            }

            // A Generate-first tool tab is focused and hasn't built its program yet: the button reads
            // "Generate" (see UpdateRunButtonLabel) - pressing it only generates, it does NOT also run. A
            // second press, once IsProgramGenerated flips true and the button reads "Run", falls through to
            // the honorActiveProgram/ActiveRun branch below like any other wizard tab.
            // Which program is "active", and whether it still needs generating, is client policy - it lives in
            // RegisterActiveProgramPolicy below. The gates stay here because they are streaming state.
            if (honorActiveProgram && grblState.State == GrblStates.Idle && TryGenerateActiveProgram())
            {
                DebugLog.Write("run", "JobRunner.Run: STOPPED - a Generate-first tab generated instead of running (this press only generates)");
                return;
            }

            // The dropdown's "Check Run" only arms the intent (see checkModeArmed's own comment) - this is
            // where it actually takes effect, right before the run it was meant to gate would otherwise start.
            // Idle-gated same as the old immediate-send behavior (StartMode_Click used to require this too);
            // if not idle when Run() fires, silently skip for now (stays armed - a Hold/Tool resume etc. isn't
            // "starting a check run" anyway, and the next genuine fresh start will pick it up).
            if (CheckModeArmed && grblState.State == GrblStates.Idle)
            {
                CheckModeArmed = false;
                model.ExecuteCommand(GrblConstants.CMD_CHECK);
            }

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
            // A wizard tab is active and the machine is idle: run its program (generate-and-run, with its
            // prompts/flow control) rather than the loaded job. It routes back here with honorActiveProgram:
            // false to stream. Idle-gated so a Run mid-run can never re-trigger it.
            // Position in this chain is load-bearing and unchanged: it sits AFTER the hold / SD-rewind /
            // tool-change / running-timer branches, so pressing Run while paused resumes the job rather than
            // launching a wizard program. Returning false falls through to the loaded-job branch below,
            // exactly as an unmatched else-if did.
            else if (honorActiveProgram && grblState.State == GrblStates.Idle && TryRunActiveProgram())
            {
            }
            else if (Source.IsLoaded)
            {
                DebugLog.Write("run", "JobRunner.Run: branch = loaded job");
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
                    // Dry run cannot protect an SD-card job: the controller runs it directly off its own SD
                    // card (CMD_SDCARD_RUN below) - the sender never sees or streams individual lines, so
                    // there is nothing for the per-line M3/M4/M7/M8 suppression to intercept, and the initial
                    // M5/M9 preamble would be a false sense of safety if the program turns the spindle back
                    // on moments later. Refuse rather than silently run unprotected while the toggle is
                    // checked - see GrblViewModel.IsDryRunMode.
                    if (model.IsDryRunMode)
                    {
                        UserPrompt.Show("Dry run is not supported for SD card jobs - the controller runs them directly, so the sender cannot intercept spindle/coolant commands. Turn dry run off, or load the file into the sender instead.",
                            "ioSender", PromptButtons.OK, PromptIcon.Warning);
                        return;
                    }
                    Comms.com.WriteCommand(GrblConstants.CMD_SDCARD_RUN + model.FileName.Substring(7));
                }
                else
                {
                    // (PREREQ ...) gate for a LOADED program (unified streaming engine Step 5): a
                    // directive-bearing program - Work Order's generated text, or a .macro opened via
                    // plain Load File - declares its own preconditions, and they are checked HERE,
                    // before any motion, exactly as MacroRunner.Run has always done for macros. One
                    // shared evaluator (EvaluatePrereqLines) so the two paths cannot drift. Evaluated
                    // on every Cycle Start including a mid-program "start from this toolpath" run -
                    // prerequisites are program-level facts (homed, G30 set, build options), not
                    // positional ones. Programs without PREREQ rows skip all of this at the cost of
                    // one flag scan. Not applied to SD-card jobs above: the sender never sees their
                    // lines, so there is nothing to scan - the same visibility rule as dry run.
                    if (Source.Data.Any(b => b.Directive == "PREREQ"))
                    {
                        var unmet = MacroRunner.EvaluatePrereqLines(model,
                            Source.Data.Where(b => b.Directive == "PREREQ").Select(b => (string)b.Data));
                        DebugLog.Write("run", string.Format("JobRunner: PREREQ evaluated - {0}",
                            unmet.Count == 0 ? "all met" : "UNMET: " + string.Join(" | ", unmet)));
                        if (unmet.Count > 0)
                        {
                            DebugLog.Write("run", "JobRunner: REFUSED - prerequisites unmet, nothing started");
                            UserPrompt.Show(string.Format("Cannot start this program:\r\n\r\n• {0}", string.Join("\r\n• ", unmet)),
                                "ioSender", PromptButtons.OK, PromptIcon.Warning);
                            return;
                        }
                    }
                    else
                        DebugLog.Write("run", "JobRunner: no PREREQ rows in this program");

                    // (PROMPT ...) field collection for a loaded program (Step 4b) - one combined dialog
                    // before any motion, exactly as MacroRunner.Run does for macros, through the same
                    // collector and FieldPrompt seam. Cancel = a silent refusal (the operator changed
                    // their mind - not an error). Check mode runs unattended: declared defaults, no
                    // dialog - a syntax check has no operator to ask. The values land two ways, same
                    // hybrid as always: #<_name>=value assignments ride the existing Source.Commands
                    // preamble (only when the controller reports EXPR - MacroRunner's own refusal rule
                    // for #-syntax on the wire), and StreamPump substitutes references at send time.
                    List<MacroRunner.PromptField> promptFields = null;
                    if (Source.Data.Any(b => b.Directive == "PROMPT"))
                    {
                        promptFields = MacroRunner.CollectPromptFields(
                            Source.Data.Where(b => b.Directive == "PROMPT").Select(b => (string)b.Data));
                        if (promptFields.Count > 0)
                        {
                            // Unattended macro run: no operator to ask - each field keeps its declared
                            // default (PromptField.Value is seeded with it), same as check mode below.
                            if (!CheckModeArmed && model.GrblState.State != GrblStates.Check && !unattendedRun &&
                                !MacroRunner.ShowFieldPrompt(model.FileName ?? "Program", promptFields))
                                return;   // cancelled - not an error, just not running
                            if (GrblInfo.ExpressionsSupported)
                                foreach (var field in promptFields)
                                    Source.Commands.Enqueue(field.Param + "=" + field.Value);
                        }
                    }

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

                    // Dry run has no effect in check mode - the controller doesn't move regardless of any
                    // offset, so skip it there rather than leaving stray preamble commands queued for
                    // nothing. Queued as a preamble on Source.Commands (mirrors "Start from this toolpath"'s
                    // modal-reset prolog): it survives PurgeQueue below and is drained ahead of the first
                    // program line, by SendNextLine (legacy path) or StreamPump.Start's own preamble drain
                    // (buffered path). M5/M9 go first as a DEFENSIVE measure - the spindle/coolant might
                    // already be on from a previous operation, and per-line suppression in the streamers
                    // (GCodeBlock.HasSpindleOrCoolantOn) only neutralises commands IN the program, not
                    // whatever state the machine is already in when dry run starts.
                    //
                    // Also gated on !(Source is a transient macro/tool run) and !macroRun (Step 7): dry-run
                    // is a loaded-job-only toggle the operator arms from the Run dropdown on the Job tab -
                    // its Z-clearance G92 must never leak into a probing/wizard macro. A stray G92 Z offset
                    // there corrupts the macro's own positioning (e.g. a spoilboard probe search starting
                    // ~10mm+ too high and timing out) even though the macro never armed dry run itself - it
                    // was simply still checked from an earlier, unrelated loaded-job test. Macro runs used
                    // to arrive as transient sources (RunStreamedJobInPlace); under the unified engine they
                    // arrive as the pushed loaded job, so the macroRun one-shot carries the same exemption.
                    // Per-line spindle/coolant/M6 suppression still applies to them in the pump.
                    if ((dryRunActive = model.IsDryRunMode && model.GrblState.State != GrblStates.Check
                                          && !macroRun
                                          && !((Source as GCodeProgram)?.IsTransient ?? false)))
                    {
                        // DeclaredStock (NOT the .Stock property) - .Stock falls back to the machine's FULL
                        // Z travel range as a conservative default when the program has no (STOCK ...)
                        // comment, which is right for other features but wildly wrong here. No declaration
                        // = 0 extra clearance, not the whole machine.
                        double stockZ = DeclaredStockZ == null ? 0d : DeclaredStockZ();
                        double offset = 10d + stockZ;
                        Source.Commands.Enqueue("M5");
                        Source.Commands.Enqueue("M9");
                        // G21 first: offset is always computed in mm (stockZ from DeclaredStock.Z, which the
                        // Fusion post always declares in mm) - without forcing units here, this preamble runs
                        // in WHATEVER modal state the controller happens to be in at Run (leftover
                        // from an earlier G20 command, a previous job, etc.), and a G20 (inch) controller
                        // reads "G92Z-17" as -17 IN (~432mm), not -17mm - a massive, silent overshoot instead
                        // of the intended small clearance.
                        Source.Commands.Enqueue("G21");

                        // "G92 Zk" does NOT set an absolute offset - it makes WHEREVER THE MACHINE CURRENTLY
                        // IS read as work-Z=k. The bug this replaces just sent "G92Z-<offset>" unconditionally,
                        // which only gives the intended clearance if the machine happens to already be sitting
                        // at the stock surface when Run runs - it never was (typically wherever the
                        // last job/macro parked, e.g. Start Job's G30). Confirmed on real hardware: a machine
                        // parked ~67mm above the true stock plus the intended 17mm clearance gave a 84mm gap,
                        // not 17mm - exactly this bug's arithmetic.
                        //
                        // Fix: compute the k that ACTUALLY produces "work-zero is offset mm above the true
                        // stock", using where the machine really is right now (MachinePosition, live) and
                        // where work-zero really is right now (WorkPositionOffset, live - assumes G92 is 0
                        // here, which ClearDryRunOffset's G92.1 guarantees between runs). Derivation: G92 Zk
                        // sets WCO_new = MachinePosition.Z - k; we want WCO_new = WorkPositionOffset.Z + offset
                        // (true work-zero, shifted up by the clearance) => k = MachinePosition.Z -
                        // (WorkPositionOffset.Z + offset).
                        double k = model.MachinePosition.Z - (model.WorkPositionOffset.Z + offset);
                        Source.Commands.Enqueue("G92Z" + k.ToInvariantString());
                    }

                    Comms.com.PurgeQueue();
                    DebugLog.Write("run", string.Format("JobRunner.Run: STARTING - '{0}', {1} block(s), dryRun={2}, checkMode={3}",
                        model.FileName, Source.Data?.Count ?? -1, model.IsDryRunMode, model.GrblState.State == GrblStates.Check));

                    JobTimer.Start();
                    streamingHandler.Call(StreamingState.Send, false);
                    if ((job.IsChecking = model.GrblState.State == GrblStates.Check))
                        model.Message = Localize("Checking");
                    else if (dryRunActive)
                        model.Message = Localize("DryRun");

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

                    // The send/ack flow control always runs on the dedicated background thread (StreamPump) so
                    // UI load can never stall motion - including Check mode ($C), which used to fall back to
                    // the legacy UI-thread streamer because it reports EVERY line's error and keeps going,
                    // where the pump used to abort on the first error. StreamPump.continueOnError now
                    // reproduces that same keep-going-and-report-every-error behavior (OnPumpCheckError below),
                    // so Check mode no longer needs a separate streamer.
                    if (pump == null)
                        // Two marshals, and the difference is deliberate - the priorities stay here in the
                        // WPF host rather than inside the now-portable pump. Control flow (job finished /
                        // error) at Normal, because the state machine must not wait behind display work;
                        // the coalesced per-line status markers at Background, because they must never
                        // compete with the streaming itself or with operator input.
                        pump = new StreamPump(model, ControlMarshal, DisplayMarshal);
                    pumpActive = true;
                    pump.Start(Source, job.CurrBlock, job.PgmEndLine, serialSize, useBuffering,
                               SendComments, StartSimulator,
                               OnPumpJobFinished, OnPumpError,
                               continueOnError: job.IsChecking, onCheckError: OnPumpCheckError,
                               // (MBOX) Cancel = the operator declining to continue = the STOP BUTTON's
                               // routine, which is Abort() - NOT Stop(). Found on real hardware
                               // 2026-08-08: Stop() sets job.Stopped=true first, and StreamingIdle's
                               // Stop case sends CMD_STOP only when !job.Stopped - so wiring Stop()
                               // here suppressed the stop byte and Z kept moving to the end of its
                               // 5mm move after Cancel. Abort() is what btnStop_Click actually calls;
                               // it leaves job.Stopped false and the CMD_STOP goes out.
                               onOperatorCancel: Abort,
                               promptFields: promptFields, unattended: unattendedRun);
                }
            }
        }

        // Resets the run-mode selection (Dry Run / Check Run) back to plain Run once the job that used it ends -
        // normal finish, error, or stop/alarm/connection-lost (all of which route through AbortPump, so this
        // fires from OnPumpJobFinished/OnPumpError/AbortPump, the same three paths). Neither mode is a sticky
        // setting the operator meant to leave armed for the NEXT, unrelated job - re-arming either for another
        // run is one click; staying silently armed (or, for check mode, silently STUCK - see below) across
        // unrelated runs is exactly the kind of state an operator can lose track of.
        private void ResetRunModeAfterJob()
        {
            if (model == null)
                return;

            CheckModeArmed = false;   // belt-and-suspenders - Run() should already have cleared this before $C ever went out

            if (dryRunActive)
            {
                dryRunActive = false;
                // Deliberately does NOT re-send M5/M9 here - the run already forced them at start, and
                // re-issuing them on every job end (including ordinary non-dry-run jobs, since AbortPump is
                // the shared stop path) would fight a job that legitimately wants to leave the spindle running
                // (M5 is not modal-safe to send blind).
                Comms.com.WriteCommand("G92.1");
                model.IsDryRunMode = false;
            }

            // Check mode ($C) has no auto-exit of its own - grblHAL stays in the Check state after the checked
            // program finishes until an explicit soft reset (see StartMode_Click, which uses the same
            // mechanism to leave it deliberately). Without this, both the controller AND btnStart's label
            // (model.IsCheckMode is a live read of GrblState, not a separate flag - see UpdateRunButtonLabel)
            // would still show Check Run for the NEXT job too - and since that's the CONTROLLER'S own state,
            // not something ioSender caches, it would look "stuck" even across an app restart if the operator
            // closed ioSender before ever leaving check mode.
            if (model.GrblState.State == GrblStates.Check)
                Grbl.Reset();

            // A "Simulate" run switched the live connection to the bundled simulator (see the top of Run()) -
            // switch back now that it's over, same finish/error/abort coverage as dryRunActive's G92.1 cleanup
            // above. Unconditional on WHY the job ended - a simulated run that errors or gets Stopped still
            // needs its real controller back, same as user answer #2 (still reconnect on a mid-run abort).
            if (SimulateActive)
            {
                SimulateActive = false;
                RestoreAfterRun?.Invoke();
            }
        }

        // Pump -> UI signals (marshalled onto the UI thread by the pump). The state machine and display stay here.
        private void OnPumpJobFinished()
        {
            PumpLog.W("OnPumpJobFinished -> JobFinished, state=" + grblState.State);
            pumpActive = false;
            streamingHandler.Count = false;   // pump owned flow control; stop legacy line accounting so a late/trailing response can't re-enter it
            ResetRunModeAfterJob();
            streamingHandler.Call(StreamingState.JobFinished, true);
        }

        private void OnPumpError(string response)
        {
            pumpActive = false;
            streamingHandler.Count = false;
            job.HasError = model.IsGrblHAL;
            ResetRunModeAfterJob();
            streamingHandler.Call(StreamingState.Error, true);
        }

        // Check mode (StreamPump.continueOnError): fires on EVERY error line, not just the first - the run
        // keeps streaming afterward (pump does not abort), so this must not tear the run down the way
        // OnPumpError does. The actual per-line "Sent" text (the error response) is already written by
        // StreamPump's own MarkSent/Drain, same path every other line's status uses - this only drives the
        // state-machine/UI bookkeeping the legacy check-mode streamer used to do inline (ResponseReceived's
        // old isError branch: streamingHandler.Call(StreamingState.Error, true) + job.HasError).
        private void OnPumpCheckError()
        {
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
            ResetRunModeAfterJob();
            CancelIdleKick?.Invoke();
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

        // Ack-paced dispatch for typed/programmatic commands, on the same primitive the job pump uses
        // (docs/Architecture-MDI-Dispatch-Unification.md). Replaced JobRunner's own SendMDI pacing -
        // the private queue, the streamingState==SendMDI busy flag, the synthetic "go" kick and the
        // cancel-flushed $J release, all of which had to learn the firmware's ack quirks separately
        // from the pump. The dispatcher taps replies itself, so nothing here has to drive it.
        private readonly MdiDispatcher mdi = new MdiDispatcher();

        public void SendCommand(string command)
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
                    Source.Parser.ParseBlock(ref c, true);   // keep the source's modal state in step (UI thread)
                    mdi.Send(command);
                }
                catch (Exception ex)
                {
                    // Diagnostic only, 2026-08-08: this catch used to swallow everything silently - a
                    // parse or enqueue failure here dropped the command with zero trace anywhere.
                    if (DebugLog.Enabled)
                        DebugLog.Write("jobrunner", string.Format("SendCommand THREW on \"{0}\" (streamingState={1}): {2}", command, streamingState, ex));
                }
            }
            // Diagnostic only, 2026-08-08: this branch used to not exist - a command (jog or MDI) arriving
            // while streamingState is outside the allowed list above was, and still is, silently dropped.
            // No behavior change here (still a no-op), but a real incident on real hardware looked exactly
            // like a hung controller with zero trace anywhere - repeated jog clicks got logged to the
            // console (GrblViewModel.ExecuteCommand accepted them) but never reached the wire, and nothing
            // recorded WHY. This makes the drop itself observable: enable with -debuglog=jobrunner (or a
            // bare -debuglog) and a repro will show the exact command and the streamingState that ate it,
            // which is the fact needed to fix this correctly rather than guess at it.
            else if (DebugLog.Enabled)
                DebugLog.Write("jobrunner", string.Format("SendCommand DROPPED \"{0}\" - streamingState={1} is not in the allowed set", command, streamingState));
        }

        public void RewindFile()
        {
            job.Complete = false;

            if (Source.IsLoaded)
            {
                using (BusyCursor?.Invoke())
                {
                    CanRun = false;

   //                 grdGCode.DataContext = null;

                    Source.ClearStatus();

                    //                  grdGCode.DataContext = Source.Data.DefaultView;
                    model.ScrollPosition = 0;
                    job.ToolChangeLine = -1;
                    job.CurrBlock = job.LastExecuting = job.PendingLine = job.ACKPending = model.BlockExecuting = 0;
                    job.LineNumbersTracking = false;   // re-proved per job; a program without N words never sets it
                    job.PgmEndLine = Source.Blocks - 1;

                    CanRun = true;
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
                    CanRun = true;
                    CanFeedHold = (FeedHoldArmed = false);
                    CanStop = true;
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
                        CanRun = true;
                        CanFeedHold = (FeedHoldArmed = false);
                        if ((CanStop = model.IsJobRunning || model.IsSDCardJob) && !GrblInfo.IsGrblHAL)
                            StopShowsPause = false;
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
                            CanRun = !GrblInfo.IsGrblHAL; // BAD! ?
                            CanFeedHold = (FeedHoldArmed = false);
                            CanStop = true;
                            SetStreamingHandler(StreamingHandler.AwaitAction);
                        }
                        else
                            changed = false; // ignore
                        break;

                    case StreamingState.Send:
                        if (!model.IsJobRunning)
                            model.IsJobRunning = true;
                        CanRun = false;
                        CanFeedHold = (FeedHoldArmed = true) && !model.FeedHoldDisabled;
                        CanStop = true;
                        CanRewind = false;
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        CanFeedHold = (FeedHoldArmed = false);
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
                        CanRun = !GrblInfo.IsGrblHAL;
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
                        CanRun = false;
                        CanFeedHold = (FeedHoldArmed = false);
                        CanRun = true;
                        CanStop = true;
                        StopShowsPause = false;
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
                        CanRun = !GrblInfo.IsGrblHAL;
                        CanFeedHold = (FeedHoldArmed = false);
                        CanStop = true;
                        break;

                    case StreamingState.Send:
                        CanRun = false;
                        CanFeedHold = (FeedHoldArmed = true) && !model.FeedHoldDisabled;
                        CanStop = true;
                        CanRewind = false;
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
                        ControlsEnabled = false;
                        break;

                    case StreamingState.JobFinished:
                        if(model.IsSDCardJob && grblState.State == GrblStates.Check)
                            SetStreamingHandler(StreamingHandler.SendFile);
                        break;

                    case StreamingState.Idle:
                    case StreamingState.NoFile:
                        ControlsEnabled = !grblState.MPG;
                        // Also enabled when a wizard tab is up (its program is the active program Run runs),
                        // even with no job loaded. Re-evaluated on every idle status report, so it tracks tab changes.
                        CanRun = Source.IsLoaded || AnyActiveProgram || (model.IsSDCardJob && model.SDRewind);
                        CanStop = model.IsSDCardJob && model.SDRewind;
                        CanFeedHold = (FeedHoldArmed = !grblState.MPG) && !model.FeedHoldDisabled;
                        CanRewind = !grblState.MPG && Source.IsLoaded && job.CurrBlock != 0;
                        model.IsJobRunning = JobTimer.IsRunning;
                        ActiveProgramReady = AnyActiveProgram && CanRun && !IsGenerateBlocking;
                        break;

                    case StreamingState.Send:
                        ActiveProgramReady = false;   // running now - drop the "press Run" cue
                        if (!string.IsNullOrEmpty(model.FileName) && !grblState.MPG)
                            model.IsJobRunning = true;
                        if (JobTimer.IsRunning)
                            SetStreamingHandler(StreamingHandler.SendFile);
                        else
                        {
                            CanStop = true;
                            CanFeedHold = (FeedHoldArmed = !grblState.MPG) && !model.FeedHoldDisabled;
                        }
                        break;

                    case StreamingState.Start: // Streaming from SD Card
                        job.IsSDFile = true;
                        break;

                    case StreamingState.Error:
                    case StreamingState.Halted:
                        CanRun = !grblState.MPG;
                        CanFeedHold = (FeedHoldArmed = false);
                        CanStop = !grblState.MPG;
                        break;

                    case StreamingState.FeedHold:
                        SetStreamingHandler(StreamingHandler.FeedHold);
                        break;

                    case StreamingState.ToolChange:
                        SetStreamingHandler(StreamingHandler.ToolChange);
                        break;

                    case StreamingState.Stop:
                        CanFeedHold = (FeedHoldArmed = !(grblState.MPG || grblState.State == GrblStates.Alarm)) && !model.FeedHoldDisabled;
                        CanRun = FeedHoldArmed && Source.IsLoaded; //!GrblInfo.IsGrblHAL;
                        CanStop = false;
                        CanRewind = false;
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

        public void GrblStateChanged(GrblState newstate)
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
                CancelIdleKick?.Invoke();
            else if (pumpActive)
                RequestIdleKick?.Invoke();

            // An alarm must release the pump (and, critically, Comms.com.AckSink) IMMEDIATELY, regardless of
            // which tab currently has focus - this is a comms-safety concern, not a UI-state one. Confirmed
            // as a real hang 2026-08-01: an alarm during a Check-mode run landed while a different tab was
            // active, the isActive-gated switch below skipped AbortPump() entirely (its own case, further
            // down), and the pump's AckSink stayed hijacked - every subsequent response (including jog acks
            // sent from the other tab) silently vanished into the abandoned, undrained pump instead of
            // reaching the app, and the pump's own still-queued lines from the aborted check file later got
            // flushed mid-jog, producing a bogus "error:9 locked out during alarm or jog state".
            if (newstate.State == GrblStates.Alarm)
                AbortPump();

            // Process state transitions when the Grbl tab is active OR a wizard program is the active source: the
            // fixed bottom bar drives that program from the wizard tab, so its enables must track the machine
            // there too (Idle re-enables Run after a run, Hold/Tool/Alarm behave as on the Grbl tab).
            // Also while a job/stream is actually running (JobTimer): a stay-put run (Load Stock) finishes on a
            // non-Grbl tab and parks in AwaitIdle waiting for the controller's final Idle - if its active program
            // was already torn down, neither flag above is set and that Idle would be dropped, leaving the bar
            // stuck "running" until Stop is pressed. JobTimer is live for exactly that finishing window.
            if (IsActive || AnyActiveProgram || JobTimer.IsRunning) switch(newstate.State)
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
                        CanRun = !grblState.MPG;
                        CanFeedHold = (FeedHoldArmed = false);
                    }
                    else if (grblState.Substate == 1)
                    {
                        CanRun = false;
                        CanFeedHold = (FeedHoldArmed = !grblState.MPG) && !model.FeedHoldDisabled;
                    }
                    if (!GrblInfo.IsGrblHAL)
                        StopShowsPause = true;
                    break;

                case GrblStates.Tool:
                    if (grblState.State != GrblStates.Jog)
                    {
                        // In pump mode read the pump's progress mirror, and suspend it so jog/MDI acks during the
                        // tool change aren't consumed as job-line acks (resumed from Run's Tool branch).
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
                    SetPolling?.Invoke(true);
                    break;

                case GrblStates.Door:

                    //if (newstate.Substate == 1)
                    //    Comms.com.WriteByte(GrblConstants.CMD_TOOL_ACK);
                    //else if (newstate.Substate == 5)
                    //    streamingHandler.Call(StreamingState.ToolChange, true);

                    //if (newstate.Substate != 5 && streamingState == StreamingState.Send)
                    //    streamingHandler.Call(StreamingState.FeedHold, false);
                    //else
                    //    IsRunEnabled = newstate.Substate != 5;

                    if (newstate.Substate > 0)
                    {
                        if (streamingState == StreamingState.Send)
                            streamingHandler.Call(StreamingState.FeedHold, false);
                        else
                            CanRun = false;
                    } else
                        CanRun = true;
                    break;

                case GrblStates.Alarm:
                    // AbortPump() already ran unconditionally above, regardless of this gate.
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

            // Jog acks are not this handler's business, and counting them here corrupts real accounting:
            // "missed" would climb on every jog. Jogging while a macro streams is a live path (the fixture
            // dialog is non-modal precisely so jogging stays reachable during one). This filter used to
            // live in GrblViewModel, which suppressed responses for EVERY consumer while jogging - see the
            // comment there for why it had to move: it was starving JogGate of its ack.
            // It no longer gates MDI dispatch: MdiDispatcher taps replies directly off the comms read
            // thread, so an ack arriving while the controller reports Jog reaches it either way. That
            // early return WAS how a command sent during a jog could sit in the queue indefinitely.
            if (model != null && model.GrblState.State == GrblStates.Jog)
                return;

            // When the background pump is driving the job it owns all flow-control accounting (off the UI
            // thread). Skip the accounting here; the MDI/Reset switch below still runs on the UI thread.
            // Check mode ($C) now also always sets pumpActive (see Run()) - the job.IsChecking branches
            // below this point are consequently unreachable for the checking case (kept rather than
            // pruned: they're still live for job.IsSDFile, which shares the same conditionals, and this
            // is real-time streaming code where a conservative diff beats a clever one).
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
                        // An "ok" means the controller PARSED and BUFFERED this line, not that it cut it.
                        // With a full planner buffer that runs the marker a hundred-odd lines ahead of the
                        // tool - on a program of short segments, the whole file shows complete while the
                        // spindle is still in the first shape (reported 2026-08-06, a 10-square test).
                        // Once OnLineNumberChanged has proved the controller's Ln: reports match this
                        // program, IT owns the "ok" markers and the scroll, because it reports what is
                        // actually EXECUTING. Until then - and forever, for a program with no N words or a
                        // controller that never reports Ln: - this stays exactly as it was.
                        // Errors are never suppressed: an error:N is not progress, it is the reason the job
                        // just stopped, and it must appear on its line the moment it arrives.
                        if (!(job.LineNumbersTracking && response == "ok"))
                        {
                            Source.Data[job.PendingLine].Sent = response;

                            if (job.PendingLine > 5)
                                model.ScrollPosition = job.PendingLine - 5;
                        }
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
                    model.Message = Localize("TransferComplete");
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
                if ((bool)job.NextRow.IsComment && !SendComments && !StartSimulator)
                {
                    line = "()";
                    job.NextRow.Length = line.Length + 1;
                }

                // Dry-run/verify mode: neutralise spindle-on (M3/M4) and coolant-on (M7/M8) so the operator
                // can watch the toolpath move without the spindle or coolant ever actually activating,
                // regardless of what the loaded program contains - the Z-offset alone is NOT a safety
                // feature, it only avoids hitting stock. HasSpindleOrCoolantOn is precomputed at load time
                // from the real G-code parser's tokens (GCodeJob.ParseFileLines/AddBlock), not a regex
                // re-check here. Mirrors StreamPump.SendNext's buffered-path equivalent.
                else if (model.IsDryRunMode && job.NextRow.HasSpindleOrCoolantOn)
                {
                    line = "()";
                    job.NextRow.Length = line.Length + 1;
                }

                // Dry-run mode: also skip the program's own tool changes (M6) entirely - see
                // StreamPump.SendNext's buffered-path equivalent for the full reasoning.
                else if (model.IsDryRunMode && job.NextRow.HasToolChange)
                {
                    line = "()";
                    job.NextRow.Length = line.Length + 1;
                }

                if (job.serialUsed < (serialSize - (int)job.NextRow.Length)
                     && (!jobHasProbe || job.ACKPending < StreamPump.ProbeLookahead))   // cap look-ahead once probing
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
