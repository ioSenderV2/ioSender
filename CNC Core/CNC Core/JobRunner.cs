/*
 * JobRunner.cs - part of CNC Core library
 *
 * What the streaming state machine has decided the operator may do right now: whether Run, Feed Hold,
 * Stop and Rewind are available, and whether the Stop button is currently offering Stop or Pause.
 *
 * This class is deliberately introduced BEFORE the state machine itself moves. The machine lives in
 * JobControl.xaml.cs and writes these ~49 times across its six handlers; every one of those writes used
 * to go straight into a WPF DependencyProperty, which is what tied the machine to the view. Repointing
 * them here first - with no logic relocated at all - establishes and proves the seam on its own, so the
 * large and genuinely dangerous move that follows is a move of LOGIC into a class whose UI surface has
 * already been exercised on real hardware, rather than both at once.
 *
 * The host mirrors these into whatever its widgets bind to (ioSender: JobControl's existing
 * DependencyProperties, so the XAML and every binding are untouched).
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
    }
}
