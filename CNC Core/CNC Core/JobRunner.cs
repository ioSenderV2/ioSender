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
