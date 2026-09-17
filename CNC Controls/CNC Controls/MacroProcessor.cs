/*
 * MacroProcessor.cs - part of CNC Controls library
 *
 * The desktop face of macro / generated-program running. The engine - the directive loop, prerequisite
 * evaluation, the flow-controlled streamer, the idle/alarm waits - is CNC.Core.MacroRunner. What is left
 * here is what talks to the operator, plus the Run-bar state that is pure client bookkeeping:
 *
 *   - the active-program / Generate-mode surface (ActiveRun, SupportsGenerateMode, IsProgramGenerated, ...)
 *     that the shared Run bar and each wizard tab coordinate through;
 *   - the dialogs: the (PROMPT) parameter form and the (MBOX) hold prompt;
 *   - PublishGenerated, which drives a tab's own ProgramView.
 *
 * The (MBOX) hold in particular cannot move: it is a deliberately non-modal, ShowActivated=false window
 * pumped with its own DispatcherFrame, and it forwards jog keys to the KeypressHandler so the operator can
 * jog to a corner while it is up. That is WPF by design, not by accident.
 *
 * Run is the unified-engine ENTRY since Step 7 (load the macro as the job, start it, pop-restore at the
 * terminal); EmitGotoG30 / CoordinateSystemDefined / SaveGeneratedCopy stay here as forwarders so none
 * of the ~50 call sites across the app had to move.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public static class MacroProcessor
    {
        // Optional hook to surface the floating run-control panel (status / feed hold / override / MDI) while a
        // generated program runs. Set by the shell (ioSender XL) since the panel lives in that assembly; callers
        // in this library (e.g. the Surface Spoilboard generator) invoke it before Run so they don't need a
        // direct reference to it.
        public static System.Action<GrblViewModel> RunControlPanel;

        // The active program's run action: a tool registers its "generate-and-run" here when its tab is shown and
        // clears it when the tab is left. Cycle Start, when idle, runs this instead of streaming the loaded job -
        // so one Cycle Start runs whatever program is active (the loaded file on the Job tab, or a wizard on its tab)
        // and tools no longer need their own Run button. Null = no tool active: Cycle Start streams the job.
        // Setting it raises ActiveProgramChanged so program views can re-mark which one is the configured source.
        private static System.Action _activeRun;
        public static System.Action ActiveRun
        {
            get { return _activeRun; }
            set { _activeRun = value; ActiveProgramChanged?.Invoke(); }
        }

        // Raised when the active program changes (a wizard tab set/cleared, or a job loaded). Program views
        // subscribe to re-evaluate the "configured input source" highlight (the mint background).
        public static event System.Action ActiveProgramChanged;

        // Generate-mode plumbing: a "Generate first" tool (Start Job, Stepper Calibration, Auto Square,
        // Surface Spoilboard) no longer owns its own standalone Generate button - it registers itself here
        // while its tab is active so the shared Run bar (JobControl) can show "Generate" (disabled until
        // ready) in ActiveRun's place, and hide the Dry Run/Check Run dropdown (neither is meaningful before
        // something is generated). All are set together on Activate(true) and cleared together on
        // Activate(false), same lifecycle as ActiveRun/ActiveProgramName. Changes reuse ActiveProgramChanged
        // (JobControl's one subscriber for all active-program state) rather than adding a second event.
        private static bool _supportsGenerateMode;
        public static bool SupportsGenerateMode
        {
            get { return _supportsGenerateMode; }
            set
            {
                _supportsGenerateMode = value;
                // Tab teardown clears the blocked reason with everything else. These statics are SHARED by
                // every Generate-first tab, so a reason left behind by the tab you just left would otherwise
                // sit on the next tab's disabled button describing the wrong thing entirely.
                if (!value)
                    GenerateBlockedReason = string.Empty;
                ActiveProgramChanged?.Invoke();
            }
        }

        // Opt-in for a Generate-first tab whose generated program IS a real cutting program worth Dry
        // Running/Check Running (Odd Jobs' job wizards - Pocket etc), as opposed to the setup/probing-macro
        // tabs (Start Job, Stepper Calibration, Auto Square, Surface Spoilboard) where those modes don't mean
        // anything. False (dropdown stays hidden, same as before this existed) unless a tab sets it alongside
        // SupportsGenerateMode. Only takes effect once IsProgramGenerated is true - see UpdateRunButtonLabel.
        public static bool AllowRunModesWhenGenerated;

        // Opt-in "Generate and Run" mode-dropdown entry for a Generate-first tab whose own Generate/Run
        // steps are routine enough (re-run often with the same answers) to be worth a one-click unattended
        // path - Start Job is the first (its own confirmation dialogs + the generated program's (MBOX) probe-
        // install prompts add up to 3 clicks every single run). ActiveGenerateAndRun is the tab's own
        // combined "build the program, then MacroProcessor.Run(..., unattended: true) it" action - the tab
        // itself decides which of ITS OWN confirmations are routine-safe to skip (see StartJobView.
        // GenerateAndRun) vs genuine safety gates that must still prompt even here.
        public static bool SupportsGenerateAndRun;
        public static System.Action ActiveGenerateAndRun;

        // Live "are this tab's current inputs enough to generate" gate - the tab re-sets this on every input
        // change (the same checks that used to drive its own Generate button's IsEnabled).
        private static bool _isGenerateReady;
        public static bool IsGenerateReady
        {
            get { return _isGenerateReady; }
            set
            {
                _isGenerateReady = value;
                // "Ready" can never carry a reason it is not ready - clearing it HERE rather than asking
                // every caller to remember means a gate that flips back to ready cannot leave its old
                // explanation on the button. Callers therefore only ever need to set the reason on the
                // blocking branch, and must set it AFTER IsGenerateReady, not before.
                if (value)
                    GenerateBlockedReason = string.Empty;
                ActiveProgramChanged?.Invoke();
            }
        }

        /// <summary>
        /// Why <see cref="IsGenerateReady"/> is false, in the operator's words - shown on the disabled Run
        /// bar so a greyed-out button says what it wants. Empty when there is no specific reason.
        ///
        /// A tab that blocks generation knows exactly why (a validation warning, usually), and that reason
        /// used to go only into the tab's own warnings panel. If the panel had scrolled, or the operator was
        /// looking at the button rather than the panel, the button simply did nothing and explained nothing.
        /// Set it wherever IsGenerateReady is set, or leave it empty and the bar falls back to its generic
        /// "nothing to run yet" text.
        /// </summary>
        public static string GenerateBlockedReason { get; set; } = string.Empty;

        // False = nothing generated yet (or it was discarded) - Run bar reads "Generate". True = a program is
        // built and ActiveRun will stream it - Run bar reads "Run". The tab flips this true right after a
        // successful ActiveGenerate, and false again whenever it discards the program (an input changed, or
        // JobControl calls DiscardGenerated after the run ends).
        private static bool _isProgramGenerated;
        public static bool IsProgramGenerated
        {
            get { return _isProgramGenerated; }
            set { _isProgramGenerated = value; ActiveProgramChanged?.Invoke(); }
        }

        // The Generate-only action (build + PublishGenerated, no run) - what pressing the Run bar while it
        // reads "Generate" actually does. Distinct from ActiveRun (which streams an already-generated program).
        public static System.Action ActiveGenerate;

        // Called by JobControl right after a run this tab owned finishes, to drop the in-memory program and
        // revert the Run bar back to "Generate". Null-safe to invoke; a tab that has nothing to discard beyond
        // IsProgramGenerated itself can leave this unset.
        public static System.Action DiscardGenerated;

        // Display name of the active program (set when a wizard registers it), used in the "ready - press Cycle
        // Start" status prompt. Null when no wizard program is active.
        public static string ActiveProgramName;

        // Optional one-liner about how the active program was built ("4777 lines in 9.6 s"), appended to the
        // "ready - press Cycle Start" prompt. Exists because that prompt lands right after Generate's own
        // completion message and OVERWROTE it - the compile result was on screen for a frame. Set by whoever
        // builds a program (Work Order does), cleared by PublishGenerated when a caller has none to report.
        public static string ActiveProgramStats;

        // Bumped every time new program text reaches the program view (PublishGenerated, below - the one
        // place that happens). It exists so a consumer can tell "a different program" from "the same
        // program again": JobControl announces "<name> ready - press Run to run." once per program rather
        // than on every false->true edge of the ready cue, since that cue also flips with tab activation
        // and re-reading the same sentence on every visit to the Job tab is noise, not news.
        public static int ActiveProgramVersion { get; private set; }


        // Step 7 seam (unified streaming engine): start the just-loaded job as a macro run - the shell
        // (ioSender XL) points this at its run bar's JobControl.RunMacro, since the JobControl INSTANCE
        // lives in that assembly. Same idiom as SwitchToTab below. The bool is 'unattended'. Null means
        // no streamer is wired (headless/degenerate host) - Run() refuses rather than sending motion
        // without flow control, exactly as the retired MacroRunner.Flush did.
        public static System.Action<bool> StartLoadedJob;

        // Set by the shell: switches the main tab strip to the given tab. Used by Work Order's Run - hands
        // its generated program off to the Job tab ("one mental model of running a program" regardless of
        // source), then switches BACK to Work Order once the Job tab's borrowed program is done with (a
        // failed prereq, or the run's own true terminal - see WorkOrderView.Run/WatchForRunEnd). Switching
        // straight back also sidesteps a WPF quirk found 2026-08-01: the Job tab's docked list didn't
        // visually repaint its outline grouping after GCode.Pop restored a large file WHILE that tab stayed
        // in view - but a genuine tab switch always forces a correct repaint, so leaving (and not looking at
        // the stale frame) beats fighting to force one in place.
        public static System.Action<ViewType> SwitchToTab;

        // Common tail of every tab's Generate button: save the diagnostic copy, then hand the program text to
        // that tab's own preview ProgramView. Every Generate handler (Start Job, Auto square, Stepper
        // calibration, Surface spoilboard) builds its program text its own way - that part stays at the call
        // site - but once built, all four do the exact same four steps with it; only this tail was duplicated
        // four times. ensureProgramView/getProgramView are the caller's own lazy-init method/field (each tab
        // owns its ProgramView independently, so there's no shared base to hang a field on) - getProgramView is
        // read AFTER ensureProgramView() runs, so it sees the just-created instance on first call.
        public static void PublishGenerated(string name, string program, System.Action ensureProgramView, System.Func<ProgramView> getProgramView, string stats = null)
        {
            ActiveProgramStats = stats;
            ActiveProgramVersion++;
            SaveGeneratedCopy(name, program);
            ensureProgramView();
            var view = getProgramView();
            view.SetProgramText(program);
            view.Connect();
        }

        // --- The generated-program handoff: ONE mechanism for every Generate-first tab ------------------
        //
        // Every Generate-first tab (Work Order, Setup, and the three Calibration wizards) now ends its
        // Generate the same way: the program it just built becomes THE LOADED JOB on the Job tab, the
        // operator is taken there to look at it before anything moves, and the run bar keeps pointing back
        // at the tab that built it. At the run's true terminal the borrowed program is popped, whatever was
        // loaded before comes back, and the operator is returned to the tab they started from.
        //
        // This was three separate implementations - WorkOrderView.Generate/WatchForRunEnd, StartJobView's
        // HandOffToJobTab/ReleaseBorrowedProgram/EndHandoff, and for the three wizards no handoff at all,
        // just a floating preview that left the Job tab showing someone else's program. Two of the three
        // had to learn the same two guards the hard way, and the third never did:
        //
        //   - never push a SECOND snapshot over a program that is already ours (observed live 2026-08-08:
        //     "Push: depth now 2" with every watcher trace doubled);
        //   - never pop when the loaded job is no longer ours - the operator generated, then loaded their
        //     own file instead of running, and popping yanks it out from under them.
        //
        // Keeping the bookkeeping in ONE record is the point: "exactly one push is outstanding" becomes a
        // thing that can be checked in one place rather than a property three copies each maintain.

        // What a Generate-first tab has currently handed to the Job tab. Null name = nothing borrowed.
        private static string _borrowedName;
        private static ViewType _borrowedOrigin;
        private static bool _borrowedWatcherArmed;
        // True ONLY for the duration of HandOffToJobTab's own SwitchToTab call - see IsHandingOffFrom.
        private static bool _handoffSwitching;
        private static System.ComponentModel.PropertyChangedEventHandler _borrowedHandler;
        // Filled in by Run() when the tab finally starts the borrowed program - the handoff watcher owns
        // the terminal, so it is the one that has to make the caller's completion callback.
        private static System.Action<bool> _borrowedOnDone;
        // The originating tab's teardown for the moment the handoff is over - see HandOffToJobTab's own
        // parameter docs. Held with the rest of the record so it is cleared by exactly the same paths.
        private static System.Action _borrowedOnEnd;

        /// <summary>The name a Generate-first tab's program is currently loaded under, or null when
        /// nothing is borrowed.</summary>
        public static string BorrowedProgramName { get { return _borrowedName; } }

        /// <summary>
        /// True when <paramref name="name"/> is a program a Generate-first tab handed to the Job tab AND it
        /// is still the loaded job - i.e. reloading it in place is safe and pushing again is not. The LOADED
        /// JOB is half the test on purpose: the record alone would still say "ours" after the operator
        /// loaded a file of their own over it.
        /// </summary>
        public static bool IsHandedOff(GrblViewModel model, string name)
        {
            return _borrowedName != null && _borrowedName == name && model != null && model.FileName == name;
        }

        // These two look alike and are NOT interchangeable. They were ONE method for a day, and that bug is
        // worth keeping written down: a tab used it to mean "this Activate(false) is my own handoff" while
        // it actually answered "is a borrow outstanding". Those coincide only until the operator walks BACK
        // to the tab - after which leaving it again for somewhere unrelated still looked like a handoff, so
        // the tab skipped its teardown and went on owning the run bar from off-screen, borrowed program and
        // all.

        /// <summary>
        /// TRANSIENT: true only while <see cref="HandOffToJobTab"/> is performing its own tab switch - so an
        /// Activate(false) arriving right now is OUR OWN handoff switching away, not the operator leaving
        /// the tab for good. False at every other moment, including the whole time the borrow is held.
        /// </summary>
        /// <remarks>
        /// The window is exact because WPF tab selection is not deferred: the outgoing tab's Activate(false)
        /// runs synchronously inside the SwitchToTab call this brackets. A Generate-first tab must not tear
        /// down its run-bar registration or clear its own `program` field on that one deactivation - the bar
        /// keeps pointing back at it across the handoff, which is the whole point: pressing Run on the Job
        /// tab still runs the program AS that tab's run, with its confirmation, its completion hook and its
        /// result parsing. (Clearing the field and reading it back across the switch is separately what
        /// shipped a blank Job tab on 2026-08-11, 0c457451.)
        /// </remarks>
        public static bool IsHandingOffFrom(ViewType originTab)
        {
            return _handoffSwitching && _borrowedName != null && _borrowedOrigin == originTab;
        }

        /// <summary>
        /// DURABLE: true for as long as <paramref name="originTab"/>'s generated program is the borrowed
        /// loaded job - from its Generate until the run's terminal, a discard, or Esc. This is the one that
        /// answers "is the run bar still mine", which stays true while the operator is over on the Job tab.
        /// </summary>
        public static bool HoldsHandoffFrom(ViewType originTab)
        {
            return _borrowedName != null && _borrowedOrigin == originTab;
        }

        /// <summary>
        /// Operator cancel: discard a generated program that was handed to the Job tab and never started,
        /// give the previous program back and return to the tab that built it. Bound to Esc.
        /// </summary>
        /// <returns>
        /// True when there was a handoff to cancel - the caller consumes the key. False otherwise, so Esc
        /// falls through to whatever else wants it; a gesture that silently does nothing must not also
        /// silently swallow the key.
        /// </returns>
        public static bool CancelHandoff(GrblViewModel model)
        {
            // A run in flight owns the program outright - its watcher is what pops, and Esc is not a Stop.
            if (_borrowedName == null || model == null || model.IsJobRunning)
                return false;

            string name = _borrowedName;
            var origin = _borrowedOrigin;
            DebugLog.Write("run", string.Format("CancelHandoff: Esc - discarding '{0}' and returning to {1}", name, origin));

            ReleaseHandoff(model);          // pop the previous job back, disarm the watcher, clear the record
            DiscardGenerated?.Invoke();     // the tab drops its own program text, so the bar reads "Generate"
            SwitchToTab?.Invoke(origin);    // ...back where it was built
            model.Message = string.Format("{0} discarded - the previous program is back.", name);
            return true;
        }

        /// <summary>
        /// Generate's tail for every Generate-first tab: make <paramref name="program"/> the loaded job,
        /// take the operator to the Job tab to look at it, and arm the watcher that gives the previous job
        /// back and returns them to <paramref name="originTab"/> once the run is over.
        /// </summary>
        /// <param name="onHandoffEnd">
        /// The originating tab's own teardown, run at the terminal just before <c>onDone</c>. It exists for
        /// the ABORT path: a clean finish switches back to that tab and its Activate(true) re-registers
        /// everything, but a stopped or alarmed run deliberately leaves the operator on the Job tab - and
        /// the run bar would go on pointing at an off-screen tab, so the next Cycle Start over a file of
        /// their own would run the generated program instead of it. Every implementation no-ops when its
        /// tab is the active one, which is exactly the clean-finish case.
        /// </param>
        public static void HandOffToJobTab(GrblViewModel model, string name, string program, ViewType originTab,
                                           string stats = null, System.Action onHandoffEnd = null)
        {
            if (model == null || string.IsNullOrWhiteSpace(program))
            {
                // Handing off nothing used to be indistinguishable from a successful handoff, which is
                // exactly how a blank Job tab shipped once already (2026-08-11, 0c457451).
                DebugLog.Write("run", string.Format("HandOffToJobTab: REFUSED - nothing to hand off (model={0}, program={1} chars)",
                    model == null ? "null" : "ok", program == null ? 0 : program.Length));
                return;
            }

            // Sanitize HERE, before anything else sees the text: the diagnostic copy, the Job tab's docked
            // list and the run then all show the same program. Run sanitizes again on its way to the wire
            // (harmless - it is idempotent), but by then this text has already been on screen, and a prompt
            // that reads as gibberish in the list is one the operator distrusts before it is ever shown.
            program = MacroRunner.SanitizeProgram(program);

            ActiveProgramStats = stats;
            ActiveProgramVersion++;
            SaveGeneratedCopy(name, program);

            // Capture BEFORE the switch. Selecting another tab runs the originating tab's Activate(false)
            // SYNCHRONOUSLY (WPF tab selection is not deferred), and that is where a tab clears its own
            // `program` field - reading it back across the switch is the trap that shipped the blank Job
            // tab above. One read, here, and the local is what gets loaded.
            string toLoad = program;
            // Program_FileChanged clears IsDryRunMode by design on every load - re-arm it around LoadText.
            bool dryRunArmed = model.IsDryRunMode;

            // Decide this BEFORE overwriting the record, and set the record BEFORE the switch: the
            // originating tab's own Activate(false), fired synchronously below, reads it to tell this
            // handoff apart from a genuine tab-leave.
            bool replacingOurOwn = IsHandedOff(model, name);
            _borrowedName = name;
            _borrowedOrigin = originTab;
            _borrowedOnEnd = onHandoffEnd;

            // Bracketed, not just called: the originating tab's Activate(false) runs synchronously inside
            // this, and IsHandingOffFrom is how that tab tells this deactivation from a real tab-leave.
            _handoffSwitching = true;
            try { SwitchToTab?.Invoke(ViewType.GRBL); }   // the Job tab
            finally { _handoffSwitching = false; }

            // Don't push a SECOND slot over our own still-loaded program - a previous Generate the operator
            // looked at and never ran. LoadText replaces it in place.
            if (!replacingOurOwn)
                GCode.File.Push();
            GCode.File.LoadText(name, toLoad);
            model.IsDryRunMode = dryRunArmed;

            WatchHandoffEnd(model);

            // The Esc half of this is not discoverable on its own - there is no button for it - so the one
            // status line the operator is already reading is where it has to be said.
            model.Message = string.Format("{0} loaded{1} - press Cycle Start when ready, or Esc to discard it.",
                name, string.IsNullOrEmpty(stats) ? string.Empty : " (" + stats + ")");

            DebugLog.Write("run", string.Format("HandOffToJobTab: '{0}' ({1} chars) is the loaded job; origin={2}, pushed={3}",
                name, toLoad.Length, originTab, !replacingOurOwn));
        }

        /// <summary>
        /// Hand the previous job back WITHOUT having run the borrowed program - an input changed, the tab
        /// was left for good, or the run was refused up front. Everything that drops a generated program
        /// has to come through here, or the pushed snapshot is stranded and the Job tab keeps showing a
        /// program nothing will ever run.
        /// </summary>
        /// <remarks>
        /// The LOADED JOB is the test, never the record on its own: after a real run the handoff watcher
        /// has already popped by the time a tab's DiscardGenerated reaches here, so this correctly does
        /// nothing. A run still in flight owns the pop outright - leave it to the watcher.
        /// </remarks>
        public static void ReleaseHandoff(GrblViewModel model)
        {
            if (_borrowedName == null)
                return;
            if (model != null && model.IsJobRunning)
                return;

            string name = _borrowedName;
            _borrowedName = null;
            _borrowedOnDone = null;
            _borrowedOnEnd = null;
            if (_borrowedHandler != null && model != null)
            {
                model.PropertyChanged -= _borrowedHandler;
                _borrowedHandler = null;
                _borrowedWatcherArmed = false;
            }

            if (model != null && model.FileName == name)
            {
                DebugLog.Write("run", string.Format("ReleaseHandoff: dropping '{0}' without running it - popping the previous job back", name));
                GCode.File.Pop();
            }
        }

        /// <summary>
        /// Pop the borrowed program back and return the operator to the tab that generated it, once the run
        /// reaches its TRUE terminal (Idle/NoFile after a genuine Send of OUR program).
        /// </summary>
        /// <remarks>
        /// Armed at GENERATE time, not at Run time. Cycle Start may be minutes away, and arming late is how
        /// a work order once finished, parked at G30, and simply stayed loaded forever (2026-08-06): this
        /// watcher only arms by OBSERVING a Send transition, so arriving after one has already gone past
        /// means no terminal will ever fire it.
        /// </remarks>
        private static void WatchHandoffEnd(GrblViewModel model)
        {
            if (_borrowedWatcherArmed)
            {
                DebugLog.Write("run", "WatchHandoffEnd: already armed - not arming a second watcher");
                return;
            }
            _borrowedWatcherArmed = true;
            bool started = false, jobFinished = false, sawError = false;
            _borrowedHandler = (s, e) =>
            {
                if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                var st = model.StreamingState;
                // Send ONLY (never SendMDI), and only OUR OWN program's Send. A single jog between Generate
                // and Cycle Start reaches SendMDI then Idle; an unrelated macro run streams under its own
                // name having pushed ours aside. Either one, latched, pops the program before it ever runs -
                // both were live incidents, 2026-08-08.
                if (st == StreamingState.Send && model.FileName == _borrowedName)
                    started = true;
                if (st == StreamingState.JobFinished)
                    jobFinished = true;
                // Latch a failed run: the terminal only arrives at the Idle that follows the operator's
                // reset/unlock, by which time StreamingState no longer says anything went wrong.
                if (st == StreamingState.Error || st == StreamingState.Halted)
                    sawError = true;
                if (!started || (st != StreamingState.Idle && st != StreamingState.NoFile))
                    return;

                model.PropertyChanged -= _borrowedHandler;
                _borrowedHandler = null;
                _borrowedWatcherArmed = false;
                string name = _borrowedName;
                var origin = _borrowedOrigin;
                var onDone = _borrowedOnDone;
                var onEnd = _borrowedOnEnd;
                _borrowedName = null;
                _borrowedOnDone = null;
                _borrowedOnEnd = null;

                // Self-disarm without popping when the loaded job is no longer ours - the operator
                // generated, then loaded a different file instead of running. Popping now would yank THEIR
                // file out from under THEIR run. (The pushed slot is left unconsumed in that path -
                // accepted; the alternative is worse.)
                if (model.FileName != name)
                    DebugLog.Write("run", string.Format("WatchHandoffEnd: terminal but loaded job is '{0}', not '{1}' - disarming without pop",
                        model.FileName, name));
                else
                {
                    // st is in the line because its absence once cost two passes over the same symptom:
                    // "jobFinished=False" says the discard did not happen, never which terminal got there
                    // first. JobFinished comes from OnProgramEnd (the controller's own "[MSG:Pgm End]"), so
                    // a program whose final acks land before the motion finishes terminates on Idle instead.
                    DebugLog.Write("run", string.Format("WatchHandoffEnd: '{0}' terminal={1} (jobFinished={2} sawError={3}) - popping the borrowed program",
                        name, st, jobFinished, sawError));
                    GCode.File.Pop();
                }

                // A CLEAN finish has nothing actionable left to look at: drop the preview, drop the
                // program, and put the operator back on the tab that built it.
                //
                // A FAILED run keeps the operator HERE on the Job tab, and keeps the originating tab's own
                // program text so Run can re-run it without rebuilding. Note what it does NOT keep: the pop
                // above is unconditional, so the previous program is back either way. That is not a
                // contradiction - this terminal only arrives at the Idle FOLLOWING the operator's reset or
                // unlock, so the failed program sits on screen with its stopped line marked for as long as
                // the machine is alarmed, and gives way once they clear it. The switch-back is gated here
                // for the same reason the discard is, and that is a deliberate change from Work Order's old
                // unconditional switch-back, which moved you off the failure. (User decision 2026-09-14.)
                if (!sawError)
                {
                    // NOT the loaded job's own view. GCode.File.Pop() above ends in RaiseFileChanged, which
                    // reconnects jobProgramView SYNCHRONOUSLY (MainWindow.OnJobFileChanged) - so by the time we
                    // reach here the active view IS the restored job, and disconnecting it detached the Job tab's
                    // own view from the program it had just put back. What this line is for is a tool's TRANSIENT
                    // preview; the loaded job is never that.
                    if (ProgramView.Active != null && !ProgramView.Active.IsLoadedJob)
                        ProgramView.Active.Disconnect();
                    if (jobFinished && SupportsGenerateMode)
                        DiscardGenerated?.Invoke();
                    SwitchToTab?.Invoke(origin);
                }

                // BEFORE onDone, not after: a tab's onDone may queue follow-on work of its own (Setup's
                // height-map pass), and that has to run against a tab whose run-bar ownership has already
                // been settled one way or the other.
                onEnd?.Invoke();
                onDone?.Invoke(jobFinished);
            };
            model.PropertyChanged += _borrowedHandler;

            // The state AT ARM TIME is the one thing the handler above can never report afterwards - see
            // the remark on arming early. "armed while already Send" identifies that fault immediately.
            DebugLog.Write("run", string.Format("WatchHandoffEnd: armed for '{0}' (origin {1}) with StreamingState={2}",
                _borrowedName, _borrowedOrigin, model.StreamingState));
        }

        // NOTE: ConfirmRun and ShowMessage used to live here. Every message the engine raises now goes
        // through CNC.Core.UserPrompt, which AppDialogs.RegisterCorePrompts routes back to this assembly -
        // and since a10ce1e that path picks the same dialog owner ShowMessage used to, so nothing about
        // where a macro's message boxes appear changed. Leaving the pair behind would have left two
        // plausible-looking message paths where only one is live.

        // Show one dialog with an editable, numeric-validated input box per field.
        // Returns false if the user cancelled; on OK each field's Value holds the entry.
        private static bool ShowPromptDialog(string title, List<MacroRunner.PromptField> fields)
        {
            var win = new Window {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                MinWidth = 300
            };

            win.Owner = AppDialogs.OwnerWindow();
            win.WindowStartupLocation = win.Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

            var root = new StackPanel { Margin = new Thickness(12) };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var boxes = new List<TextBox>();
            for (int i = 0; i < fields.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock {
                    Text = fields[i].Label + ":",
                    Margin = new Thickness(0, 4, 8, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(label, i);
                Grid.SetColumn(label, 0);

                var box = new TextBox {
                    Text = fields[i].Value,
                    MinWidth = 120,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                Grid.SetRow(box, i);
                Grid.SetColumn(box, 1);

                grid.Children.Add(label);
                grid.Children.Add(box);
                boxes.Add(box);
            }
            root.Children.Add(grid);

            var buttons = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            win.Content = root;
            DialogScaling.Apply(win);

            ok.Click += (s, e) => {
                for (int i = 0; i < boxes.Count; i++)
                {
                    double v;
                    if (!double.TryParse(boxes[i].Text.Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v))
                    {
                        AppDialogs.Show(win, string.Format("\"{0}\" is not a valid number.", fields[i].Label), title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        boxes[i].Focus();
                        boxes[i].SelectAll();
                        return;
                    }
                }
                for (int i = 0; i < boxes.Count; i++)
                    fields[i].Value = double.Parse(boxes[i].Text.Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

                win.DialogResult = true;
            };

            if (boxes.Count > 0)
                boxes[0].Loaded += (s, e) => { boxes[0].Focus(); boxes[0].SelectAll(); };

            return win.ShowDialog() == true;
        }

        // A modeless "hold" prompt: pauses the macro until the operator clicks, but - unlike a modal MessageBox -
        // leaves the MAIN window fully usable and does NOT steal keyboard focus, so the operator can jog (incl.
        // keyboard jog), change the jog step and zero the DRO while it is up (needed for "jog to the corner and
        // set work zero" style prompts). PushFrame keeps the UI pumping while the macro waits here.
        private static bool ShowHoldPrompt(string title, string message, bool cancellable, bool yesNo)
        {
            bool result = !cancellable;   // closing the window [X] = OK when there is no Cancel
            var frame = new System.Windows.Threading.DispatcherFrame();

            var win = new Window
            {
                Title = string.IsNullOrEmpty(title) ? "ioSender" : title,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                ShowActivated = false,   // don't steal focus -> keyboard jogging stays live on the main window
                Topmost = true,
                Owner = AppDialogs.OwnerWindow(),
                WindowStartupLocation = AppDialogs.OwnerWindow() != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };

            var root = new StackPanel { Margin = new Thickness(16), MaxWidth = 480 };
            root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var okBtn = new Button { Content = yesNo ? "Yes" : "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            okBtn.Click += (s, e) => { result = true; frame.Continue = false; };
            bar.Children.Add(okBtn);
            if (cancellable)
            {
                var cancelBtn = new Button { Content = yesNo ? "No" : "Cancel", MinWidth = 80, IsCancel = true };
                cancelBtn.Click += (s, e) => { result = false; frame.Continue = false; };
                bar.Children.Add(cancelBtn);
            }
            root.Children.Add(bar);
            win.Content = root;
            DialogScaling.Apply(win);
            win.Closed += (s, e) => frame.Continue = false;

            // Keep keyboard jogging live while the prompt is up. The prompt is a separate top-level window, so
            // it owns keyboard focus and the main window's jog forwarding never sees these keys (and the macro
            // may have been launched from a non-Job tab anyway, where that forwarding is disabled). Forward
            // jog-relevant keys straight to the keypress handler; leave Enter/Esc/Tab/Space for the buttons.
            var kbd = CNC.Core.Grbl.GrblViewModel?.Keyboard as KeypressHandler;
            Window mainForJog = Application.Current?.MainWindow;
            System.Windows.Input.KeyEventHandler forwardJog = null;
            if (kbd != null)
            {
                forwardJog = (s, e) =>
                {
                    if (e.Handled)
                        return;   // already handled (e.g. the Job view's own jog handler when it is the current view)
                    switch (e.Key)
                    {
                        case System.Windows.Input.Key.Enter:
                        case System.Windows.Input.Key.Escape:
                        case System.Windows.Input.Key.Tab:
                        case System.Windows.Input.Key.Space:
                            return;   // reserved for the OK / Cancel buttons
                    }
                    if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                        return;   // focus is in a text box (typing) - don't jog
                    e.Handled = kbd.ProcessKeypress(e, true);
                };
                // Forward jog keys from the prompt window (when it has focus) AND the main window. The prompt is
                // shown ShowActivated=false so it never steals focus, and these tools run from a non-Job tab where
                // the main window's own jog forwarding (CurrentView is JobView) is inactive - so without the
                // main-window hook, no window would jog while the prompt is up. Unsubscribed when the frame ends.
                win.PreviewKeyDown += forwardJog;
                win.PreviewKeyUp += forwardJog;
                if (mainForJog != null && mainForJog != win)
                {
                    mainForJog.PreviewKeyDown += forwardJog;
                    mainForJog.PreviewKeyUp += forwardJog;
                }
            }

            win.Show();
            System.Windows.Threading.Dispatcher.PushFrame(frame);   // pumps the UI (jog/DRO live) until a button closes the frame
            if (forwardJog != null && mainForJog != null)
            {
                mainForJog.PreviewKeyDown -= forwardJog;
                mainForJog.PreviewKeyUp -= forwardJog;
            }
            try { win.Close(); } catch { }

            return result;
        }

        // --- The unified-engine entry (Step 7) ---------------------------------------------------------
        // Run used to forward to CNC.Core.MacroRunner.Run - a second engine that interpreted directives
        // itself and streamed transient bursts through RunStreamedJobInPlace, holding the caller in
        // DoEvents wait loops (the proven 32-bit OOM allocator). Now a macro IS a job: push the loaded
        // program aside, load the directive-bearing text as the job, and start it through the ordinary
        // streaming path. The pump handles (WAITIDLE)/(MBOX)/bare-(PROMPT) inline and JobRunner.Run gates
        // (PREREQ)/(PROMPT ...) up front - the same hardware-verified path Work Order runs on since Step 6.
        // Deliberately silent presentation (user decision 2026-08-08): no tab switch, no floating run
        // view - the operator stays where they are; the run bar drives Feed Hold/Stop from any tab and
        // the docked Job list shows progress if they look. The previous job pops back at the terminal.

        /// <summary>Run a macro through the unified streaming engine. Returns false if it was refused up
        /// front (busy, prerequisite unmet, or user cancelled); true once the run has started (the run
        /// itself is asynchronous - pass onDone to sequence work after it).</summary>
        /// <param name="unattended">Skip every routine confirmation this macro would otherwise pop (the
        /// confirm-before-run prompt, bare mid-body (PROMPT) run-confirmations, and (MBOX) holds - all
        /// auto-answered OK/Yes) and take an unanswered (PROMPT param, default, ...) input's own default
        /// rather than asking. For a "Generate and Run" action that a tab offers explicitly (see
        /// SupportsGenerateAndRun) - NOT a general silencing knob. PREREQ failures still apply and still
        /// stop the run; this only skips prompts that exist purely to ask "are you sure" / "ready?".</param>
        /// <param name="onDone">Invoked on the UI thread at the run's true terminal, after the previous
        /// job is restored. The argument is true only for a genuine program end (JobFinished) - false for
        /// a Stop/alarm recovery. Not invoked when Run returns false (nothing started).</param>
        /// <param name="startDelayMs">
        /// Pause between loading the program and starting it. For callers that hand the operator off to
        /// another tab first (see StartJobView's Run) - landing on the Job tab to find motion already
        /// under way is not the same as being shown what is about to run.
        /// </param>
        /// <param name="alreadyPushed">
        /// The caller has ALREADY pushed the loaded job aside and made this program the loaded one - a tab
        /// whose Generate hands the program to the Job tab so the operator can look at it before pressing
        /// Run (StartJobView since 2026-08-12; WorkOrderView.Generate does the same thing without coming
        /// through here). Suppresses only the Push, never the LoadText: the text loaded at Generate time is
        /// the raw build, and the comment sanitizing above happens HERE - reloading in place is what puts
        /// the sanitized text on the wire. Exactly one push is then outstanding, so the watcher's pop and
        /// the not-started pop below stay balanced. Pushing a second slot instead stacked snapshots and
        /// doubled watchers when Work Order hit this same shape (observed live 2026-08-08).
        /// </param>
        public static bool Run(GrblViewModel model, string name, string code, bool confirm = false, bool unattended = false, System.Action<bool> onDone = null, int startDelayMs = 0, bool alreadyPushed = false)
        {
            // Returning TRUE here said "ran fine" for doing nothing at all, and that is precisely how an
            // empty program went unnoticed: a caller whose `program` field had been cleared by a tab switch
            // got success, no log, no load, and a Job tab that came up blank with no error anywhere.
            // Nothing to run is not a successful run.
            if (model == null || string.IsNullOrEmpty(code))
            {
                DebugLog.Write("run", string.Format("Run: REFUSED - nothing to run (model={0}, code={1} chars)",
                    model == null ? "null" : "ok", code?.Length ?? 0));
                return false;
            }

            if (string.IsNullOrEmpty(name))
                name = "Macro";

            // Every exit from this method is logged. A run that declines to start is indistinguishable
            // from one that never was asked to, unless it says which gate stopped it - and 2026-08-12 a
            // Generate-and-Run stopped somewhere in here with nothing said at all.
            DebugLog.Write("run", string.Format("Run: '{0}' confirm={1} unattended={2} delay={3}ms alreadyPushed={4} lines={5}",
                name, confirm, unattended, startDelayMs, alreadyPushed, code.Split('\n').Length));

            if (StartLoadedJob == null)
            {
                DebugLog.Write("run", "Run: REFUSED - no streamer wired (StartLoadedJob == null)");
                // No streamer wired - refuse rather than flood (Feed Hold / Stop would not work).
                UserPrompt.Show("Cannot run this program safely: the job streamer is not available, so motion would be sent without flow control and Feed Hold / Stop would be unresponsive.",
                    "ioSender", PromptButtons.OK, PromptIcon.Error);
                return false;
            }

            // Busy guard: a macro run means "load this as the job and press Cycle Start" - doing that
            // while a job is streaming would collide with it, and doing it while held/jogging/tool-
            // changing would make the programmatic Cycle Start a RESUME of that state instead of a
            // start. The retired engine's deferred Background-priority start merely broke quietly in
            // these states; the unified immediate start must refuse them explicitly.
            var grblState = model.GrblState.State;
            if (model.IsJobRunning || JobTimer.IsRunning || grblState == GrblStates.Run || grblState == GrblStates.Hold ||
                grblState == GrblStates.Jog || grblState == GrblStates.Tool || grblState == GrblStates.Door)
            {
                DebugLog.Write("run", string.Format("Run: REFUSED - machine busy (state={0} IsJobRunning={1} JobTimer={2})",
                    grblState, model.IsJobRunning, JobTimer.IsRunning));
                UserPrompt.Show(string.Format("Cannot run macro \"{0}\": the machine is busy (a job is running, held or jogging). Let it finish or stop it first.", name),
                    "ioSender", PromptButtons.OK, PromptIcon.Warning);
                return false;
            }

            // A macro whose body is a single "@<path>" line is a reference to an external file - load and
            // run that file's current contents (re-read every run, so the macro can be developed by
            // editing the file - no copy/paste back into ioSender).
            if (!MacroRunner.ResolveFileReference(ref code, name))
                return false;

            MacroRunner.SaveGeneratedCopy(name, code);

            var lines = code.Replace("\r", string.Empty).Split('\n');

            // O-word/#-expression lines can only be streamed verbatim when the controller evaluates
            // expressions itself; there is no safe fallback (MDI is reserved for typed text), so refuse
            // outright rather than send it unfiltered. Same rule the retired Flush applied per burst.
            if (!GrblInfo.ExpressionsSupported)
            {
                foreach (var l in lines)
                    if (l.IndexOf("O<", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf('#') >= 0)
                    {
                        DebugLog.Write("run", string.Format("Run: REFUSED - EXPR not reported and the program needs it (first offending line: {0})", l.Trim()));
                        UserPrompt.Show("This macro uses O-word/parameter (#) syntax, which needs the controller to support NGC expressions (EXPR). This controller does not report that support, so ioSender cannot run it.",
                            "ioSender", PromptButtons.OK, PromptIcon.Error);
                        return false;
                    }
            }

            // Sanitize comments per line - see MacroRunner.SanitizeProgram for the rule, and for why a
            // directive row is exempt from the length limit but NOT from paren flattening. This loop used
            // to live here and skipped directive rows outright, which is what garbled an (MBOX) prompt.
            code = MacroRunner.SanitizeProgram(code);
            lines = code.Replace("\r", string.Empty).Split('\n');

            // Confirm-before-run - but an input prompt's OK/Cancel is itself the run confirmation, so
            // when the macro has (PROMPT param, ...) fields the field dialog (shown by JobRunner.Run's
            // up-front gate) does the confirming and a separate box here would be redundant. Same rule
            // the retired engine applied.
            if (confirm && !unattended && MacroRunner.CollectPromptFields(lines).Count == 0 &&
                UserPrompt.Show(string.Format("Run {0} macro?", name), "ioSender",
                    PromptButtons.YesNo, PromptIcon.Question) != PromptResult.Yes)
            {
                DebugLog.Write("run", "Run: declined - operator answered No to the run confirmation");
                return false;
            }

            // Make the macro the loaded job, previous job pushed aside. Program_FileChanged clears
            // IsDryRunMode by design on every load - re-arm it (Work Order's Generate idiom): the
            // retired engine kept an armed dry run active across macro runs (per-line neutralisation
            // only - the pump still applies exactly that; the Z-shift preamble is skipped for macro
            // runs, see JobRunner.ArmMacroRun).
            bool dryRunArmed = model.IsDryRunMode;
            // Don't push a SECOND slot when the caller already made this program the loaded job at Generate
            // time (see alreadyPushed) - the LoadText below replaces it in place, and its own pushed
            // snapshot is the one the watcher pops. Gated on the loaded job actually still being ours, not
            // on the caller's word alone: if something else was loaded in between, skipping the push would
            // load over THAT file with nothing left to restore it. Pushing is the safe direction.
            // A Generate-first tab whose Generate already handed this program to the Job tab is the same
            // case, established from the shared record rather than the caller's word - see IsHandedOff.
            // Its watcher, armed back at Generate time, owns the pop, the discard and the return to the
            // originating tab, so this run must add neither a push nor a second watcher below.
            bool handedOff = IsHandedOff(model, name);
            if (handedOff || (alreadyPushed && model.FileName == name))
                DebugLog.Write("run", string.Format("Run: '{0}' is already the loaded job{1} - reloading in place, not pushing a second slot",
                    name, handedOff ? " (handed off from " + _borrowedOrigin + ")" : string.Empty));
            else
            {
                if (alreadyPushed)
                    DebugLog.Write("run", string.Format("Run: caller reported '{0}' already pushed, but the loaded job is '{1}' - pushing anyway", name, model.FileName));
                GCode.File.Push();
            }
            // The (PROMPT) field dialog now fires inside LoadText (GCodeProgram.CollectLoadPrompts). An
            // unattended run has no operator to ask - suppress it and the fields keep their declared
            // defaults, the same contract JobRunner's own unattended path always had.
            GCodeProgram.SuppressLoadPrompt = unattended;
            try { GCode.File.LoadText(name, code); }
            finally { GCodeProgram.SuppressLoadPrompt = false; }
            model.IsDryRunMode = dryRunArmed;

            // Watch the run to its TRUE terminal (Idle/NoFile after a genuine Send) and pop the borrowed
            // job back - WorkOrderView.WatchForRunEnd's proven pattern, armed BEFORE the start below so
            // even a run that finishes inside the start call's own event pumping cannot be missed.
            // Left in place through an Error/Halted (alarm) on purpose: the pop then happens on the
            // Idle that follows the operator's reset/unlock, so they can see what failed first.
            bool started = false, jobFinished = false, sawError = false;
            System.ComponentModel.PropertyChangedEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                var st = model.StreamingState;
                // Only THIS macro's Send latches the watcher (same gate, same incident class as
                // WorkOrderView.WatchForRunEnd): a refused start (e.g. PREREQ) leaves this armed, and
                // without the FileName gate the next unrelated run would latch it and burn the watcher
                // on a foreign terminal - disarming without popping this macro's pushed slot.
                if (st == StreamingState.Send && model.FileName == name)
                    started = true;
                if (st == StreamingState.JobFinished)
                    jobFinished = true;
                // Latch a failed run: this watcher only completes at the Idle/NoFile that follows the
                // operator's reset/unlock, so without the latch an alarmed run would be treated exactly
                // like a clean one by the view-dismissal below.
                if (st == StreamingState.Error || st == StreamingState.Halted)
                    sawError = true;
                if (!started || (st != StreamingState.Idle && st != StreamingState.NoFile))
                    return;
                model.PropertyChanged -= handler;
                // A handed-off program's teardown belongs to the handoff watcher, which was armed first and
                // has therefore already run by the time this fires: it popped, dismissed the view, discarded
                // the program, switched back, and made our own onDone call (Run passed it along below).
                // Everything past this point would be a second helping of exactly that.
                if (handedOff)
                {
                    DebugLog.Write("run", string.Format("Run watcher: '{0}' terminal={1} - the handoff watcher owns the teardown", name, st));
                    return;
                }
                // Self-disarm without popping if the loaded job is no longer ours (a different file got
                // loaded before the terminal was seen) - popping would yank that file out from under the
                // operator. Same guard as WatchForRunEnd.
                if (model.FileName != name)
                    DebugLog.Write("macro", string.Format("Run watcher: terminal but loaded job is '{0}', not '{1}' - disarming without pop", model.FileName, name));
                else
                {
                    // st is in here because it was not, and its absence cost two passes over the same
                    // symptom: "jobFinished=False" says the discard did not happen, never which terminal
                    // got there first. JobFinished comes from OnProgramEnd (the controller's own
                    // "[MSG:Pgm End]"), so a program whose final acks land before the motion finishes
                    // terminates on Idle instead and the discard is silently skipped.
                    DebugLog.Write("macro", string.Format("Run watcher: '{0}' terminal={1} (jobFinished={2}) - popping the borrowed program", name, st, jobFinished));
                    GCode.File.Pop();
                }
                // The run is over and there is nothing actionable left to look at: dismiss the expanded
                // program view rather than leaving it sitting open showing wherever the last executed
                // line happened to land - RestoreSourceOnEnd's old clean-finish behavior, found missing
                // on the first Step 7 hardware test (Setup ran fine, its preview overlay stayed up).
                // Works uniformly for a tool's own preview pane (Setup, Stepper Calibration, ...) - all
                // go through the same ProgramView.Active/Disconnect mechanism. On a failed run (see the
                // sawError latch above) the view is left up on purpose, so the operator can see
                // where/what failed - same polarity as the old code's Error/Halted branch.
                // NOT the loaded job's own view. GCode.File.Pop() above ends in RaiseFileChanged, which
                // reconnects jobProgramView SYNCHRONOUSLY (MainWindow.OnJobFileChanged) - so by the time we
                // reach here the active view IS the restored job, and disconnecting it detached the Job tab's
                // own view from the program it had just put back. What this line is for is a tool's TRANSIENT
                // preview; the loaded job is never that.
                if (!sawError && ProgramView.Active != null && !ProgramView.Active.IsLoadedJob)
                    ProgramView.Active.Disconnect();
                // A Generate-first tool tab's run just finished cleanly: drop the in-memory program and
                // revert the Run bar to "Generate" - the operator re-generates for the next job rather
                // than re-running a stale program. RestoreSourceOnEnd's clean-finish behavior, preserved
                // with its exact condition: NOT on error/halt or a Feed Hold + Stop (jobFinished false),
                // so the operator can still inspect/resume the SAME generated program.
                if (jobFinished && SupportsGenerateMode)
                    DiscardGenerated?.Invoke();
                onDone?.Invoke(jobFinished);
            };
            model.PropertyChanged += handler;
            // The handoff watcher reaches the terminal first (it subscribed back at Generate time) and is
            // the one that pops, so it is also the one that has to make this call - handing it over here
            // rather than letting the handler above do it is what keeps onDone firing exactly once, AFTER
            // the previous job is back.
            if (handedOff)
                _borrowedOnDone = onDone;

            // Give the operator a beat before motion when the caller has just moved them to another tab.
            // The program is already loaded and drawn by this point, so the pause is spent looking at the
            // toolpath that is about to be run rather than at an empty view - and it is the difference
            // between arriving on the Job tab and finding a probe cycle already under way. Pumped rather
            // than slept: the load, the 3D view and the block list all still need the UI thread.
            if (startDelayMs > 0)
            {
                model.Message = string.Format("{0} loaded - starting in {1} s...", name, (startDelayMs + 999) / 1000);
                var until = DateTime.Now.AddMilliseconds(startDelayMs);
                while (DateTime.Now < until)
                    EventUtils.DoEvents();
            }

            DebugLog.Write("run", string.Format("Run: calling StartLoadedJob(unattended={0}) - loaded '{1}', {2} block(s)",
                unattended, model.FileName, GCode.File.Data?.Count ?? -1));

            StartLoadedJob(unattended);

            DebugLog.Write("run", string.Format("Run: StartLoadedJob returned - started={0} StreamingState={1} GrblState={2}",
                started, model.StreamingState, model.GrblState.State));

            // JobRunner.Run's own up-front gates (PREREQ unmet, the field dialog's Cancel) return without
            // starting anything - no terminal will ever fire the watcher, so detect it here: no Send seen
            // means nothing started. Undo the push and report the refusal. (A macro so short it already
            // FINISHED inside the start call still set 'started' on its way through Send - the watcher
            // has then popped and completed normally, and this is not taken.)
            if (!started)
            {
                model.PropertyChanged -= handler;
                // A handed-off program's slot belongs to the handoff record - popping it directly would
                // leave that record claiming a borrow it no longer has, and the still-armed handoff watcher
                // waiting for a terminal that will never come. ReleaseHandoff is the one undo for both.
                if (handedOff)
                    ReleaseHandoff(model);
                else
                    GCode.File.Pop();
                DebugLog.Write("macro", string.Format("Run: '{0}' did not start (gate refused/cancelled) - popped the borrowed program", name));
                return false;
            }

            return true;
        }

        // --- The frame every generated program is built inside -----------------------------------------
        //
        // Nine builders across five tabs (Work Order, Setup x4, both stepper-calibration wizards, Auto
        // Square) each open and close a program the same way, and each had written it out longhand. The
        // duplication was not harmless: the modal line was spelled in two different orders for no reason,
        // Work Order hand-rolled the Z lift that EmitGotoG30 exists to own, and Auto Square simply forgot
        // to park at all - it finished over the last hole it drilled, which is the post-condition class
        // that destroyed a toolsetter on 2026-09-14.
        //
        // Three methods rather than one "emit the whole prologue", because the ORDER is not shared. Setup
        // and the probe wizard put the modal line straight after the gate and park much later, after a
        // pile of parameter assignments; Work Order puts its tool declarations in between; the scratch
        // wizard parked BEFORE establishing units at all. Folding those into one call would silently
        // reorder machine-moving g-code, which a refactor does not get to do. Each piece goes in at the
        // position its caller already uses.

        /// <summary>
        /// A program's opening: its identifying comment(s), then the prerequisite gate.
        /// </summary>
        /// <param name="prereq">
        /// The condition list INSIDE <c>(PREREQ, ...)</c>. Deliberately passed whole rather than assembled
        /// from flags: "connected, homed" is the only condition all nine share, and the rest genuinely
        /// differ per program (EXPR, noalarm, tlo, G30, G59.3, ATC=1, a named WCS). A builder that needs a
        /// condition states it; nothing is added behind its back.
        /// </param>
        public static void EmitProgramHeader(System.Action<string> L, string prereq, params string[] comments)
        {
            foreach (var c in comments)
                if (!string.IsNullOrEmpty(c))
                    L(c.StartsWith("(") ? c : "(" + c + ")");
            L("(PREREQ, " + prereq + ")");
        }

        /// <summary>
        /// The modal state every generated program establishes before it moves: millimetres, absolute
        /// distance, feed per minute, XY plane.
        /// </summary>
        /// <param name="cancelToolOffset">
        /// Emit <c>G49</c> as well. NOT the default, and not a tidy-up: on this machine a tool length
        /// offset is what makes one work Z0 mean the same thing for every tool (tc.macro applies a G43.1 on
        /// every M6, against the machine-wide baseline), so cancelling it leaves Z0 referenced to whatever
        /// tool last had an offset. See the scratch wizard, which says at length why it does NOT pass this.
        /// </param>
        public static void EmitModalDefaults(System.Action<string> L, bool cancelToolOffset = false)
        {
            // One spelling. The two that existed - "G21 G90 G94 G17" and "G90 G94 G17 G21" - are the same
            // four modal groups in a different order, so this settles a cosmetic split, not a behavioural one.
            L("G21 G90 G94 G17");
            if (cancelToolOffset)
                L("G49");
        }

        /// <summary>
        /// A program's close: stop the spindle, park, end.
        /// </summary>
        /// <param name="parkAtG30">
        /// Park at G30 rather than finishing wherever the last cut left the tool. Effectively always true -
        /// a program must hand the machine back somewhere the NEXT one expects to find it, and every
        /// hardware failure in the 2026-09-13/14 run was a program that did not. It stays a parameter only
        /// because a caller must be able to say so deliberately.
        /// </param>
        /// <param name="endWord">
        /// <c>M30</c> or <c>M2</c>. NOT unified - M30 rewinds and resets modal state where M2 does not, so
        /// which one a program ends with is the program's business, not the frame's.
        /// </param>
        public static void EmitProgramFooter(System.Action<string> L, bool stopSpindle, bool parkAtG30, string endWord)
        {
            if (stopSpindle)
                L("M5");
            // The park has to be the last thing that MOVES, and it has to move at all. A program whose
            // final lines are non-motion (a parameter assignment, a PRINT, M30) reaches Idle before the
            // controller's own "[MSG:Pgm End]" arrives, and the run watcher has unsubscribed 49 ms before
            // JobFinished turns up - measured 2026-09-14 11:15:43.251 vs .300. The program then never gets
            // discarded and the Run bar stays stuck on "Run".
            if (parkAtG30)
                EmitGotoG30(L);
            L(endWord);
        }

        public static void SaveGeneratedCopy(string name, string code)
        {
            MacroRunner.SaveGeneratedCopy(name, code);
        }

        public static void EmitGotoG30(System.Action<string> L)
        {
            MacroRunner.EmitGotoG30(L);
        }

        /// <summary>
        /// Emit a <c>G10 L2</c> write against the ACTIVE coordinate system, then repair the parser position
        /// it corrupts. Use this for every such write - never the bare G10 L2 line.
        /// </summary>
        /// <remarks>
        /// grblHAL (gcode.c, NonModal_Settings ~4259-4280) converts gc_state.position machine -> work, applies
        /// the new coordinate data, then converts work -> machine. The two halves are guarded INDEPENDENTLY
        /// when they have to be paired:
        ///
        ///     to-work    runs if OLD rotation != 0 AND old != new
        ///     to-machine runs if NEW rotation != 0
        ///
        /// so whenever the active system carries a rotation, some write runs one half alone and the parser is
        /// left holding coordinates in the wrong frame. The next move that leaves an axis UNNAMED then holds
        /// that axis at the corrupted value - and a "G53 G0 Z0" lift is exactly that.
        ///
        /// It is NOT only rotation writes. Observed on hardware 2026-09-16 by the Squareness (probe) tool:
        /// "G10 L2 P1 X0 Y0 Z0" - no R word at all - against a G54 holding a 0.10 deg rotation left the
        /// rotation untouched (old == new != 0), so only the to-machine half ran. The machine was standing at
        /// 20.001,-20.003 with WCO 128.392,-662.315; the very next line, a bare "G53 G0 Z0", rapided to
        /// 148.4,-682.3 - MPos + WCO to a thousandth of a millimetre, 662 mm of unplanned Y at 16824 mm/min,
        /// with a probe in the spindle. That commit (860656e5) repaired the rotation-write door; this is the
        /// offset-write door beside it, and every "G10 L2 P1 X0 Y0 Z0" in the app was standing in it.
        ///
        /// The repair: after the write, command an ABSOLUTE move that NAMES X and Y, so the parser's position
        /// is overwritten with a target rather than carried forward. Naming them from #&lt;_abs_x&gt;/#&lt;_abs_y&gt; is
        /// what makes it self-correcting - those read the STEPPER position (ngc_params.c _absolute_pos), not
        /// the parser's, so they are true however corrupt gc_state.position is.
        ///
        /// The G4 P0 is load-bearing, not politeness: those parameters are read at PARSE time, which runs
        /// ahead of motion, so mid-stream they would answer with a position the machine has not reached yet
        /// and the "no-op" move would drive BACKWARDS to it. mc_dwell calls protocol_buffer_synchronize
        /// unconditionally, so after G4 P0 parse time == real position.
        ///
        /// Z is deliberately NOT named: only the plane axes are corrupted, and naming Z here would turn a
        /// repair into a plunge.
        ///
        /// The real fix belongs in the firmware (pair the guards) and is tracked separately.
        /// </remarks>
        public static void EmitWcsWrite(System.Action<string> L, string g10Line)
        {
            L(g10Line);
            L("G4 P0");                                    // drain the queue - #<_abs_*> are read at parse time
            L("G53 G0 X[#<_abs_x>] Y[#<_abs_y>]");         // no-op move; resyncs the parser from the steppers
        }

        // ---- tool length offset for the tool ALREADY in the spindle ------------------------------------
        //
        // Moved here from StartJobView 2026-09-14, unchanged, so the stepper-calibration wizards can use the
        // same sequence instead of growing a second copy of it. Setup is the proven caller; these three are
        // its implementation, not a reimplementation of it.
        //
        // The three are a SET and are used in order - baseline, reference, restore. What they solve is
        // "the right bit is already fitted, so no M6 will run, so nothing gives it a tool length offset".
        // That is not a theoretical gap: per StartJobView's own account it cut a spoilboard on 2026-08-06,
        // when a second run with the same endmill already fitted emitted no M6, nothing re-applied the
        // offset, and the job rapided to a work Z0 15.432mm inside the material. The offset was never
        // stale - it was discarded.

        /// <summary>
        /// Load the machine-wide TLO baseline, saving whatever <c>#&lt;_tlo_ref&gt;</c> held.
        /// </summary>
        /// <remarks>
        /// <paramref name="tloAlreadyReferenced"/> comes from the controller's own $TLR report
        /// (GrblViewModel.IsTloReferenceSet) and is decided in C#, NOT with an O-word IF in the streamed
        /// program: a bare ELSE/ENDIF line is silently dropped by the streaming pipeline, leaving the
        /// controller's o-word engine waiting for an ENDIF that never comes - it keeps acking lines and
        /// stops queuing motion for the rest of the session. Confirmed on real hardware 2026-08-01.
        /// </remarks>
        public static void EmitTloBaseline(System.Action<string> L, bool tloAlreadyReferenced, double baseline)
        {
            L(tloAlreadyReferenced ? "#<_tlo_saved> = #<_tlo_ref>" : "#<_tlo_saved> = 0");
            L(string.Format("#<_tlo_ref> = {0}", N(baseline)));
        }

        /// <summary>
        /// Give the tool currently in the spindle a tool length offset by touching the puck - no tool
        /// change, so no operator prompt and no swap.
        /// </summary>
        /// <summary>
        /// Give the tool currently in the spindle a tool length offset by touching the puck - no tool
        /// change, so no operator prompt and no swap.
        /// </summary>
        /// <param name="toolId">
        /// What is in the spindle. 8 is the 3D probe stylus, which probes the MAIN input because it must
        /// not bear down on the puck; anything else is a rigid cutting tool and pushes the puck's own
        /// switch on the TOOLSETTER input. tlo.macro makes that choice - do not pre-decide it here.
        /// </param>
        /// <remarks>
        /// This USED to emit the whole sequence inline, as a copy of tc.macro's puck section. The copy had
        /// drifted: it probed Z-80 where the macro had been raised to Z-90 after a short V-bit threw
        /// Alarm:5 five mm short of the puck on real hardware; it restored a hardcoded G54 where the macro
        /// restores the caller's own WCS and says in as many words that it must not be hardcoded; and it
        /// selected the toolsetter input unconditionally where the macro branches three ways.
        ///
        /// The last two could never have been right in a streamed program - both need o-word branching, and
        /// o-word flow control cannot be streamed to grblHAL (a bare ELSE/ENDIF is dropped by the line
        /// pipeline and wedges the controller's o-word engine). So the copy was not merely at risk of
        /// drifting, it was structurally incapable of matching. It is a CALL now, like pcorner.
        ///
        /// G92.2/G92.3 are tlo.macro's own, so a caller needs no coordinate-frame ceremony around this.
        ///
        /// LEAVES THE MACHINE PARKED AT G30, IN <paramref name="returnWcs"/>. That is a guarantee, not a
        /// convenience: tlo.macro itself ends standing on the puck in G59.3, and a caller that continues
        /// from there in a work frame drives into the toolsetter. Do not "optimise away" either line.
        /// </remarks>
        public static void EmitTloReference(System.Action<string> L, int toolId, string returnWcs)
        {
            L("(--- reference the loaded tool at the puck - see tlo.macro ---)");
            L(string.Format("#<_tlo_toolid> = {0}", toolId));
            // BOTH of tlo.macro's inputs, every call. #<_tc_touchplate> is the one this used to leave to
            // chance, and tlo.macro's own header is where the trap was written down: "Set by tc.macro's own
            // header; read here so both callers see one rule" - true of the READ, and only one of the two
            // callers was setting it. Named parameters do not survive a controller reset, so a generated
            // program that called tlo before any tool change this boot hit "o150 IF [#<_tc_touchplate> EQ 1]"
            // against an UNDEFINED parameter: grblHAL error 2, "Missing the expected G-code word value".
            //
            // It hid for as long as it did because a tool change earlier in the same session leaves the
            // parameter set, so only the FIRST reference after a reboot fails - and on 2026-09-14 that was a
            // Setup run 90 seconds after a reset, which is exactly the sequence nobody tries twice.
            //
            // HasToolSetter, not a copy of tc.macro's literal 0: the variable asks "does toolsetter hardware
            // exist" and this is the controller's own answer to that question. A second hardcoded constant
            // beside tc.macro's is what tlo.macro exists to stop - read its header.
            L(string.Format("#<_tc_touchplate> = {0}", GrblInfo.HasToolSetter ? 0 : 1));
            L("O<tlo> CALL");
            // Immediately after the CALL, before anything else this caller emits. The program streams as ONE
            // job, so without a sync point here a puck probe that alarms does not actually stop it - the
            // controller halts and the sender keeps feeding lines that quietly error. Same reason Setup has
            // one after every corner probe.
            L("(WAITIDLE)");

            // ---- SAFE POST-CONDITION, AND IT IS NOT THE CALLER'S TO REMEMBER --------------------------
            // This emitter leaves the machine PARKED AT G30 IN THE CALLER'S OWN WCS. Both halves are here
            // because both were got wrong on 2026-09-14 and each cost real hardware:
            //
            // The WCS. tlo.macro selects G59.3 to reach the puck and restores the caller's frame from
            // #5220 on the way out. On this machine that restore did not take - the wire log has WCS:G59.3
            // from the moment tlo.macro first ran and never anything else again - so the next program line
            // that was not a G53 addressed the puck's frame instead of the job's. pcorner's face seek is
            // exactly such a line, and it asked for a target 816mm outside travel: Alarm:2. Naming the WCS
            // here does not replace the macro's restore, it makes the macro's restore not load-bearing.
            //
            // The park. tlo.macro deliberately ends AT THE PUCK, lifted 10mm, and I made that a documented
            // post-condition and then removed the caller-side G30 from a program as "a pointless round
            // trip". It was the only thing standing the spindle off the puck. What followed was S18000 M3
            // and a G0 to a WORK Z that sits far below the puck, so a 60-degree V-bit at 15000 rpm was
            // driven into the toolsetter and destroyed it. A few seconds of travel is not a cost worth
            // weighing against that, and no caller should have to know it is standing on the puck.
            if (!string.IsNullOrEmpty(returnWcs))
                L(returnWcs);
            EmitGotoG30(L);
            L("(WAITIDLE)");
        }

        /// <summary>
        /// Re-apply the measured offset and put <c>#&lt;_tlo_ref&gt;</c> back.
        /// </summary>
        /// <remarks>
        /// G43.1 sets the offset absolutely, so re-emitting it costs nothing if it somehow survived - and
        /// it does not survive a pcorner call, whose absolute G53 moves need true machine coordinates and
        /// so cancel it. Only covers a CLEAN finish; an aborted run leaves #&lt;_tlo_ref&gt; at the baseline
        /// this run loaded rather than the true prior value - safe, since the baseline is itself a trusted
        /// reference, just not a perfect restore. Known, accepted gap.
        /// </remarks>
        public static void EmitTloRestore(System.Action<string> L)
        {
            L("(--- restore the tool length offset the probe measured ---)");
            L("G43.1 Z[#<_probe_z> - #<_tlo_ref>]");
            L("(PRINT, LS_TLO_RESTORED tlo=[#<_probe_z> - #<_tlo_ref>])");
            L("#<_tlo_ref> = #<_tlo_saved>");
        }

        private static string N(double v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }

        public static bool CoordinateSystemDefined(string code)
        {
            return MacroRunner.CoordinateSystemDefined(code);
        }

        public static string StoredPositionUnreachable(string code)
        {
            return MacroRunner.StoredPositionUnreachable(code);
        }

        /// <summary>
        /// Point the engine's operator seams at this assembly's dialogs. Called once at startup, same idiom
        /// as AppDialogs.RegisterCorePrompts. Without it the engine still runs - it just takes each (PROMPT)
        /// field's declared default and treats every (MBOX) as acknowledged, which is what an unattended run
        /// does on purpose.
        /// </summary>
        public static void RegisterPrompts()
        {
            MacroRunner.FieldPrompt = ShowPromptDialog;
            MacroRunner.HoldPrompt = ShowHoldPrompt;
        }
    }
}
