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
 * Run / EmitGotoG30 / CoordinateSystemDefined / SaveGeneratedCopy stay here as forwarders so none of the
 * ~50 call sites across the app had to move.
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
            set { _supportsGenerateMode = value; ActiveProgramChanged?.Invoke(); }
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
            set { _isGenerateReady = value; ActiveProgramChanged?.Invoke(); }
        }

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
        public static void PublishGenerated(string name, string program, System.Action ensureProgramView, System.Func<ProgramView> getProgramView)
        {
            SaveGeneratedCopy(name, program);
            ensureProgramView();
            var view = getProgramView();
            view.SetProgramText(program);
            view.Connect();
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

        // --- Engine forwarders -------------------------------------------------------------------------
        // The engine is CNC.Core.MacroRunner; these keep every existing call site working unchanged.

        /// <summary>Run a macro. Returns false if it was aborted (prerequisite unmet or user cancelled).</summary>
        /// <param name="unattended">Skip every routine confirmation this macro would otherwise pop (the
        /// confirm-before-run prompt, bare mid-body (PROMPT) run-confirmations, and (MBOX) holds - all
        /// auto-answered OK/Yes) and take an unanswered (PROMPT param, default, ...) input's own default
        /// rather than asking. For a "Generate and Run" action that a tab offers explicitly (see
        /// SupportsGenerateAndRun) - NOT a general silencing knob. PREREQ failures and alarm-abort checks
        /// still apply and still stop the run; this only skips prompts that exist purely to ask "are you
        /// sure" / "ready?", not safety gates.</param>
        public static bool Run(GrblViewModel model, string name, string code, bool confirm = false, bool unattended = false, bool preferJobView = false)
        {
            return MacroRunner.Run(model, name, code, confirm, unattended, preferJobView);
        }

        public static void SaveGeneratedCopy(string name, string code)
        {
            MacroRunner.SaveGeneratedCopy(name, code);
        }

        public static void EmitGotoG30(System.Action<string> L)
        {
            MacroRunner.EmitGotoG30(L);
        }

        public static bool CoordinateSystemDefined(string code)
        {
            return MacroRunner.CoordinateSystemDefined(code);
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
