/*
 * MacroProcessor.cs - part of CNC Controls library
 *
 * Runs a macro, interpreting the parenthesised ioSender macro directives before the
 * remaining lines are streamed to the controller as G-code:
 *
 *   (PREREQ, cond, ...)   Require machine state before the macro runs. If any condition
 *                         is not met the macro is aborted with a message and nothing is
 *                         sent - so a macro that runs is guaranteed its prerequisites held.
 *                         Conditions: homed, tlo, idle, noalarm, connected; stored positions
 *                         / work offsets g28, g30, g92, g54..g59, g59.1, g59.2, g59.3 (each
 *                         "set" if any axis offset is non-zero, from the $# report); and any
 *                         other string is required to be a controller $I build option (NEWOPT),
 *                         matched exactly and case-sensitively (e.g. EXPR, TC, THC).
 *
 *   (MBOX, [buttons,] message)
 *                         Pop a Windows MessageBox. Streaming of the following lines is
 *                         held until it is dismissed. Optional buttons: OK (default),
 *                         OKCANCEL, YESNO - Cancel/No aborts the rest of the macro.
 *
 *   (WAITIDLE)            Hold streaming of the following lines until the controller has
 *                         finished what was sent so far and returned to the Idle state.
 *                         Needed after a controller-side job such as $F=<file> on an SD
 *                         card, which acks immediately and then drops sender input while
 *                         it runs - so a line sent right after would otherwise be lost.
 *                         Aborts the macro if the controller alarms or the link is lost.
 *
 *   (PROMPT param, default [, label])
 *                         Ask the user for an input value before the macro runs. All such
 *                         prompts are collected into a single dialog shown up front (after
 *                         PREREQ); each row shows 'label' (default: the parameter name) with
 *                         'default' pre-filled and editable. Cancel aborts the macro. The
 *                         entered value is bound to a global named parameter both ways:
 *                         it is assigned on the controller (so $F=<file> jobs can read it)
 *                         and substituted into the streamed body (so inline references work
 *                         on any controller). 'param' is normalised to #<_name> form.
 *                         A bare (PROMPT) with no arguments is just a run confirmation.
 *
 *   @<path>               If the macro body is a single line starting with '@', it is a
 *                         reference to an external file: that file's current contents are
 *                         loaded and run in its place, re-read on every run - so a macro can be
 *                         developed by editing the file directly. The loaded contents are then
 *                         processed normally (they may use the directives above).
 *
 * Macros containing none of these directives run exactly as before (GrblViewModel.ExecuteMacro).
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

        // Hook to run the just-loaded in-memory program through the real job streamer: the host (ioSender XL)
        // brings the Grbl (Job) tab forward and calls JobControl.CycleStart(0). Set by the shell because the
        // streamer/job control live there. Called after a large flush has been loaded into GCode.File.
        public static System.Action<GrblViewModel> RunStreamedJob;

        // Hook to preview a tool's freshly generated program in the shell's program-view overlay (raw text):
        // args are (tool name, program). Set by ioSender XL since the overlay lives there; tools call it from
        // Generate so the program is shown - and the overlay popped open - without a direct shell reference.
        public static System.Action<string, string> ProgramPreview;

        // Hook for the "active program" the program-view button shows: a tool sets itself as the active program
        // (name, current program text - empty before Generate) when its tab is shown. The overlay then persists
        // this program (it changes only when another tool sets it, or a job file is loaded). Unlike ProgramPreview
        // this does NOT pop the overlay open - it just sets what it WOULD show. Set by ioSender XL.
        public static System.Action<string, string> SetActiveProgram;

        // Hook to stream a generated program through the job streamer WITHOUT leaving the current tab, then
        // restore the user's previously loaded job when it finishes: args are (model, name, lines). Set by the
        // shell. Used when Run is called with stayPut:true (Load Stock) so a tool's program runs with full flow
        // control (Feed Hold/Stop live) yet never takes over the job view. Falls back to RunStreamedJob if unset.
        public static System.Action<GrblViewModel, string, string[]> RunStreamedJobInPlace;

        // Set per-run by Run(..., stayPut:true); consumed by StreamProgram to route to RunStreamedJobInPlace.
        private static bool _stayPut = false;

        // Above this many g-code lines a single flush is streamed through the flow-controlled job streamer
        // (GCode.File + Cycle Start) instead of the MDI path. The MDI path has no character-counting flow
        // control, so a large program overruns the controller's serial buffer (hanging it) and blocks the
        // UI thread. Kept high enough that the small interactive macros (Load Stock etc.) stay on MDI.
        private const int StreamLineThreshold = 40;

        // Name given to the in-memory program when a flush is streamed (set per run).
        private static string _streamName = "Macro";

        /// <summary>Run a macro. Returns false if it was aborted (prerequisite unmet or user cancelled).</summary>
        public static bool Run(GrblViewModel model, string name, string code, bool confirm = false, bool stayPut = false)
        {
            if (model == null || string.IsNullOrEmpty(code))
                return true;

            if (string.IsNullOrEmpty(name))
                name = "Macro";

            _streamName = name;
            _stayPut = stayPut;   // route any streamed flush through the in-place (stay-on-tab + restore) path

            // A macro whose body is a single "@<path>" line is a reference to an external file;
            // load and run that file's current contents (re-read every run, so the macro can be
            // developed by editing the file - no copy/paste back into ioSender).
            if (!ResolveFileReference(ref code, name))
                return false;

            // Fast path: no directives -> identical to the previous behaviour (confirm if asked).
            if (code.IndexOf("(PREREQ", StringComparison.OrdinalIgnoreCase) < 0 &&
                 code.IndexOf("(MBOX", StringComparison.OrdinalIgnoreCase) < 0 &&
                  code.IndexOf("(WAITIDLE", StringComparison.OrdinalIgnoreCase) < 0 &&
                   code.IndexOf("(PROMPT", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (confirm && !ConfirmRun(name))
                    return false;
                model.ExecuteMacro(code);
                return true;
            }

            string[] lines = code.Replace("\r", string.Empty).Split('\n');

            // 1) Prerequisites - evaluated up front, before anything is streamed.
            var conditions = new List<string>();
            foreach (var raw in lines)
            {
                if (!IsDirective(raw, "PREREQ"))
                    continue;
                foreach (var arg in Body(raw, "PREREQ").Split(','))
                {
                    string cond = arg.Trim();   // original case kept - build options match case-sensitively
                    if (cond.Length > 0)
                        conditions.Add(cond);
                }
            }

            // The homed state and stored positions / work coordinate systems all come from the $#
            // report - fetch it once up front if any such prerequisite is present so they are read
            // fresh from the controller (the status-report H: field is change-based and goes stale).
            if (conditions.Any(c => c.Equals("homed", StringComparison.OrdinalIgnoreCase) || CoordinateSystemCodes.Contains(c.ToUpperInvariant())))
                GrblWorkParameters.Get(model);

            var unmet = new List<string>();
            foreach (var cond in conditions)
            {
                string fail = EvalPrereq(model, cond);
                if (fail != null)
                    unmet.Add(fail);
            }
            if (unmet.Count > 0)
            {
                ShowMessage(string.Format("Cannot run macro \"{0}\":\r\n\r\n• {1}", name, string.Join("\r\n• ", unmet)),
                    "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 2) Input prompts - gather every (PROMPT param, default [, label]) into a single
            //    dialog shown up front, then bind the entered values two ways (hybrid):
            //      - assign the globals on the controller (so $F=<file> jobs can read them), and
            //      - substitute the references in the streamed body (so inline use works on any
            //        controller and ioSender's own parser stays consistent).
            var fields = new List<PromptField>();
            foreach (var raw in lines)
            {
                if (!IsDirective(raw, "PROMPT"))
                    continue;
                var field = ParsePromptField(raw);
                if (field != null && !fields.Any(f => f.Inner.Equals(field.Inner, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(field);
            }
            // An input prompt's OK/Cancel is itself the run confirmation, so a separate "Prompt to
            // run" box would be redundant - only show that when there are no input prompts to gate on.
            if (fields.Count > 0)
            {
                if (!ShowPromptDialog(name, fields))
                    return false;   // cancelled

                var assignments = new StringBuilder();
                foreach (var field in fields)
                    assignments.Append(field.Param).Append('=').Append(field.Value).Append('\n');
                model.ExecuteMacro(assignments.ToString());
            }
            else if (confirm && !ConfirmRun(name))
                return false;

            // 3) Stream the G-code, holding at each (MBOX)/(WAITIDLE) and substituting prompt values.
            var buffer = new StringBuilder();
            foreach (var raw in lines)
            {
                if (IsDirective(raw, "PREREQ"))
                    continue;

                if (IsDirective(raw, "PROMPT"))
                {
                    // Input prompts were collected up front; a bare (PROMPT) is just a run confirmation.
                    if (Body(raw, "PROMPT").Trim().Length == 0)
                    {
                        Flush(model, buffer);
                        if (ShowMessage(string.Format("Run macro \"{0}\"?", name), "ioSender",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                            return false;
                    }
                    continue;
                }

                if (IsDirective(raw, "MBOX"))
                {
                    Flush(model, buffer);
                    if (!ShowMBox(name, raw))
                        return false;   // Cancel / No - stop here
                    continue;
                }

                if (IsDirective(raw, "WAITIDLE"))
                {
                    Flush(model, buffer);
                    if (!WaitForIdle(model))
                    {
                        ShowMessage(string.Format("Macro \"{0}\" aborted: the controller did not return to idle (alarm or connection lost).", name),
                            "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    continue;
                }

                buffer.Append(SanitizeComment(ApplySubstitutions(raw, fields))).Append('\n');
            }
            Flush(model, buffer);

            return true;
        }

        // grblHAL ends a g-code comment at the FIRST ')', so any '(' or ')' INSIDE a (comment) corrupts the
        // block - the text after the inner ')' is parsed as g-code (e.g. "1 depth pass(es)" -> stray ", DOC...").
        // Replace parens between the outer '(' .. ')' with '[' .. ']' so generated comments are always well-formed.
        // Applied to every streamed line (directives are consumed earlier, so only comments / g-code reach here).
        private static string SanitizeComment(string s)
        {
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open < 0 || close <= open + 1)
                return s;

            var sb = new StringBuilder(s.Length);
            sb.Append(s, 0, open + 1);
            for (int i = open + 1; i < close; i++)
                sb.Append(s[i] == '(' ? '[' : s[i] == ')' ? ']' : s[i]);
            sb.Append(s, close, s.Length - close);
            return sb.ToString();
        }

        // Send the accumulated g-code. A SMALL burst goes via the MDI path (as before) - fine between
        // directives and required for O-word/parameter macros the streamer's block parser can't handle.
        // A LARGE burst (a real cutting program) is run through the normal job streamer instead: it has
        // character-counting flow control and runs on a background thread, so it can't overrun the
        // controller's serial buffer or freeze the UI - and feed-hold/stop/overrides/progress all work.
        private static void Flush(GrblViewModel model, StringBuilder buffer)
        {
            if (buffer.Length == 0)
                return;

            string code = buffer.ToString();
            buffer.Clear();

            var lines = code.Replace("\r", string.Empty).Split('\n');
            int n = 0;
            bool hasOwordOrExpr = false, hasCall = false, hasFeed = false;
            foreach (var l in lines)
            {
                string t = l.Trim();
                if (t.Length == 0)
                    continue;
                n++;
                bool oword = t.IndexOf("O<", StringComparison.OrdinalIgnoreCase) >= 0;
                if (oword)
                    hasCall = true;                       // an O-word CALL moves the machine via the called macro
                if (oword || t.IndexOf('#') >= 0)
                    hasOwordOrExpr = true;
                // A feed/cut move (G1/G2/G3, incl. G01..). Its presence - not line count - is what makes a
                // burst dangerous to flood: see below.
                if (!hasFeed && Regex.IsMatch(t, @"(?<![0-9])[Gg]0*[123](?![0-9])"))
                    hasFeed = true;
            }

            // O-word/#-expression lines can only go through the streamer when the controller evaluates
            // expressions (the job loader now passes them verbatim in that case, unnumbered); otherwise they
            // must stay on the MDI path, which forwards them line-by-line for the controller to interpret.
            bool canStream = !hasOwordOrExpr || GrblInfo.ExpressionsSupported;

            // CRITICAL (safety): anything that actually MOVES the machine - a feed/cut move, or an O-word CALL
            // (which moves via the called macro) - or a large burst MUST go through the flow-controlled job
            // streamer, never the MDI path. The MDI path has no character-counting flow control, so a realtime
            // Feed Hold / Stop queues BEHIND the whole burst and Hold/Stop appear DEAD until it drains, and the
            // long synchronous send blocks the UI thread (the hourglass). The streamer keeps only ~one planner
            // buffer outstanding, so realtime commands take effect at once and the UI stays live. No-motion
            // setup bursts (a few G10/G54/#-set lines between prompts) stay on the quick MDI path.
            bool mustStream = canStream && (hasFeed || hasCall || n > StreamLineThreshold);

            if (mustStream)
            {
                if (RunStreamedJob == null)
                {
                    // No streamer wired - refuse rather than flood (Feed Hold / Stop would not work).
                    ShowMessage("Cannot run this program safely: the job streamer is not available, so motion would be sent without flow control and Feed Hold / Stop would be unresponsive.\r\n\r\nLoad the program in the Grbl tab and run it from there instead.",
                        "ioSender", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                StreamProgram(model, lines);
            }
            else
                model.ExecuteMacro(code);
        }

        // Load the accumulated g-code into the in-memory program and start it on the normal job streamer.
        private static void StreamProgram(GrblViewModel model, string[] lines)
        {
            var code = new List<string>();
            foreach (var l in lines)
            {
                string t = l.Trim();
                if (t.Length > 0)
                    code.Add(t);
            }
            if (code.Count == 0)
                return;

            // stay-put run: hand the lines to the host, which captures the current job, streams without leaving
            // the tab, and restores the job afterwards (Load Stock). Consume the flag so it can't leak.
            bool stayPut = _stayPut;
            _stayPut = false;
            if (stayPut && RunStreamedJobInPlace != null)
            {
                RunStreamedJobInPlace.Invoke(model, _streamName, code.ToArray());
                return;
            }

            GCode.File.AddBlock(_streamName, CNC.Core.Action.New);            // names + clears the program
            for (int i = 0; i < code.Count - 1; i++)
                GCode.File.AddBlock(code[i], CNC.Core.Action.Add);
            GCode.File.AddBlock(code[code.Count - 1], CNC.Core.Action.End);   // finalize (sets Model.Blocks)

            // Host brings the Job tab forward and starts the flow-controlled stream (JobControl.CycleStart(0)).
            RunStreamedJob.Invoke(model);
        }

        // The "Prompt to run" (confirm-before-run) gate. Shown by Run itself - not the call site -
        // so it can be skipped when an input (PROMPT) dialog already gates the run.
        private static bool ConfirmRun(string name)
        {
            return ShowMessage(string.Format("Run {0} macro?", name), "ioSender",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        // The window macro dialogs (MBOX, prompts, error/abort notices) should be owned by, so they
        // center on and stay above ioSender instead of popping up as an independent top-level window
        // that can fall behind the main window. Returns null if the main window is not (yet) shown.
        private static Window OwnerWindow()
        {
            if (Application.Current == null)
                return null;

            Window main = Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible
                ? Application.Current.MainWindow
                : null;

            // Prefer a visible Topmost auxiliary window (e.g. the floating run-control panel) as the dialog
            // owner: an owned dialog is forced ABOVE its owner, so this keeps message boxes from being hidden
            // behind a Topmost window - a hidden modal box blocks the app and looks exactly like a hang.
            foreach (Window w in Application.Current.Windows)
                if (w != main && w.IsVisible && w.Topmost)
                    return w;

            return main;
        }

        // MessageBox.Show with the main window as owner when available (the owner overload requires a
        // non-null window, so fall back to the ownerless overload before the main window exists).
        private static MessageBoxResult ShowMessage(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        {
            var owner = OwnerWindow();
            return owner != null ? MessageBox.Show(owner, text, caption, buttons, icon)
                                 : MessageBox.Show(text, caption, buttons, icon);
        }

        // If 'code' is a single "@<path>" reference, replace it with the referenced file's current
        // contents (re-read on every run). Relative paths resolve against the config folder.
        // Returns false (after a message) if the file cannot be read.
        private static bool ResolveFileReference(ref string code, string name)
        {
            string trimmed = code.TrimStart();
            if (!trimmed.StartsWith("@"))
                return true;

            string path = trimmed.Substring(1);
            int nl = path.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0)
                path = path.Substring(0, nl);
            path = path.Trim();

            try
            {
                if (!Path.IsPathRooted(path))   // throws on a path with illegal characters
                    path = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, path);
                code = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                ShowMessage(string.Format("Macro \"{0}\" references a file that could not be read:\r\n\r\n{1}\r\n\r\n{2}", name, path, ex.Message),
                    "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        // A single (PROMPT ...) input field.
        private class PromptField
        {
            public string Inner;    // parameter name inside the brackets, e.g. "_probe_radius"
            public string Label;    // text shown next to the input box
            public string Value;    // default, then the value the user entered

            public string Param { get { return "#<" + Inner + ">"; } }
        }

        // Parse "(PROMPT param, default [, label])" into a field. Returns null for a bare (PROMPT).
        private static PromptField ParsePromptField(string raw)
        {
            string body = Body(raw, "PROMPT").Trim();
            if (body.Length == 0)
                return null;

            string[] parts = body.Split(new[] { ',' }, 3);
            string inner = CanonInner(parts[0]);
            if (inner == null)
                return null;

            string label = parts.Length > 2 ? parts[2].Trim() : string.Empty;

            return new PromptField {
                Inner = inner,
                Label = label.Length > 0 ? label : inner,
                Value = parts.Length > 1 ? parts[1].Trim() : "0"
            };
        }

        // Normalise a parameter name to the inside of a global named parameter reference, e.g.
        // "#<_radius>" / "#_radius" / "_radius" / "radius" -> "_radius".
        private static string CanonInner(string s)
        {
            s = s.Trim();
            if (s.StartsWith("#"))
                s = s.Substring(1).Trim();
            if (s.StartsWith("<") && s.EndsWith(">"))
                s = s.Substring(1, s.Length - 2).Trim();
            if (s.Length == 0)
                return null;
            if (!s.StartsWith("_"))     // force global scope so the value survives the program / $F= files
                s = "_" + s;

            return s;
        }

        // Replace every #<_name> reference in the line with the value the user entered.
        private static string ApplySubstitutions(string line, List<PromptField> fields)
        {
            foreach (var field in fields)
                line = Regex.Replace(line, @"#<\s*" + Regex.Escape(field.Inner) + @"\s*>", field.Value, RegexOptions.IgnoreCase);

            return line;
        }

        // Show one dialog with an editable, numeric-validated input box per field.
        // Returns false if the user cancelled; on OK each field's Value holds the entry.
        private static bool ShowPromptDialog(string title, List<PromptField> fields)
        {
            var win = new Window {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                MinWidth = 300
            };

            win.Owner = OwnerWindow();
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

            ok.Click += (s, e) => {
                for (int i = 0; i < boxes.Count; i++)
                {
                    double v;
                    if (!double.TryParse(boxes[i].Text.Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v))
                    {
                        MessageBox.Show(win, string.Format("\"{0}\" is not a valid number.", fields[i].Label), title, MessageBoxButton.OK, MessageBoxImage.Warning);
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

        // Block (while keeping the UI pumped) until a controller-side job has started and then
        // returned to Idle. Returns false if the controller alarms or the link appears lost.
        // The wait runs on the UI thread, so it pumps the dispatcher the same way the rest of
        // the app does (see Grbl.WaitForIdle) - background threads observe controller responses
        // while EventUtils.DoEvents keeps status reports (and the UI) flowing.
        private static bool WaitForIdle(GrblViewModel model)
        {
            // A (WAITIDLE) reached while a flow-controlled job is streaming is a program/structure error: the
            // stream sequences itself, and pumping the UI here waiting on a job that drives its own completion
            // can wedge the UI. Fail loudly (abort the macro) instead of hanging.
            if (model.StreamingState == StreamingState.Send)
                return false;

            var token = new CancellationToken();

            // A $F= job acks immediately and only then starts running, so first wait briefly for
            // the controller to actually leave Idle before watching for it to return - otherwise
            // the very first status report could still show the pre-run Idle and we would finish early.
            var sw = Stopwatch.StartNew();
            bool started = model.GrblState.State != GrblStates.Idle;

            while (!started && sw.ElapsedMilliseconds < 2000)
            {
                PumpForReport(model, token, 500);

                if (model.GrblState.State == GrblStates.Alarm || model.GrblState.State == GrblStates.Unknown)
                    return false;

                started = model.GrblState.State != GrblStates.Idle;
            }

            if (!started)
                return true;    // job finished (or produced no motion) before we could observe it running

            // Wait for completion. Require two consecutive Idle reports since the planner can briefly
            // drain mid-job; bail out if status reports stop arriving (stalled or disconnected).
            int idleStreak = 0, silentReports = 0;

            while (true)
            {
                if (!PumpForReport(model, token, 5000))
                {
                    if (++silentReports >= 2)
                        return false;
                    continue;
                }
                silentReports = 0;

                switch (model.GrblState.State)
                {
                    case GrblStates.Alarm:
                    case GrblStates.Unknown:
                        return false;

                    case GrblStates.Idle:
                        if (++idleStreak >= 2)
                            return true;
                        break;

                    default:
                        idleStreak = 0;
                        break;
                }
            }
        }

        // Wait (pumping the UI) for the next response/status report from the controller.
        // Returns true if one arrived within msTimeout, false on timeout.
        private static bool PumpForReport(GrblViewModel model, CancellationToken token, int msTimeout)
        {
            bool? res = null;

            new Thread(() =>
            {
                res = WaitFor.SingleEvent<string>(
                    token,
                    null,
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    msTimeout);
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            return res == true;
        }

        private static string EvalPrereq(GrblViewModel model, string cond)
        {
            switch (cond.ToLowerInvariant())
            {
                case "":
                    return null;
                case "homed":
                    // Read fresh from the $# [HOME:...] mask (grblHAL sys.homed.mask), not the
                    // cached HomedState which can stay stale after a position-loss alarm. >0 means
                    // at least one axis is homed; 0 = unhomed; -1 = could not be read (fail closed).
                    return GrblWorkParameters.HomedMask > 0 ? null : "the machine is not homed";
                case "tlo":
                case "tloref":
                    return model.IsTloReferenceSet ? null : "the tool length offset reference is not set";
                case "idle":
                    return model.GrblState.State == GrblStates.Idle ? null : "the machine is not idle";
                case "noalarm":
                case "notalarm":
                    return model.GrblState.State != GrblStates.Alarm ? null : "the machine is in an alarm state";
                case "connected":
                    return model.GrblState.State != GrblStates.Unknown ? null : "the controller is not connected";
                default:
                    // A coordinate-system code (case-insensitive)?
                    string code = cond.ToUpperInvariant();
                    if (CoordinateSystemCodes.Contains(code))
                        return CoordinateSystemDefined(code) ? null : string.Format("{0} is not set", code);

                    // Otherwise require it to be a controller $I build option (NEWOPT), matched
                    // exactly and case-sensitively (e.g. EXPR, TC, THC).
                    return BuildOptionPresent(cond) ? null : string.Format("the controller build option '{0}' is not present", cond);
            }
        }

        // True if 'option' is one of the controller's $I build options (NEWOPT), matched exactly
        // and case-sensitively (e.g. EXPR, TC, THC).
        private static bool BuildOptionPresent(string option)
        {
            if (string.IsNullOrEmpty(GrblInfo.NewOptions))
                return false;

            foreach (var opt in GrblInfo.NewOptions.Split(','))
                if (opt == option)
                    return true;

            return false;
        }

        // grbl stored positions / work coordinate systems that PREREQ can require.
        private static readonly HashSet<string> CoordinateSystemCodes = new HashSet<string> {
            "G28", "G30", "G92", "G54", "G55", "G56", "G57", "G58", "G59", "G59.1", "G59.2", "G59.3"
        };

        // A stored position/offset is treated as "set" if any axis is non-zero. grbl has no explicit
        // "is defined" flag - these default to zero - so one deliberately left at machine zero would
        // read as unset. Values come from the $# report (GrblWorkParameters), fetched up front in Run.
        private static bool CoordinateSystemDefined(string code)
        {
            var cs = GrblWorkParameters.GetCoordinateSystem(code);
            if (cs == null)
                return false;

            for (int i = 0; i < GrblInfo.NumAxes; i++)
                if (!double.IsNaN(cs.Values[i]) && Math.Abs(cs.Values[i]) > 0.0001d)
                    return true;

            return false;
        }

        // Show the message box for an (MBOX...) line; returns false if the user cancelled (Cancel/No).
        private static bool ShowMBox(string name, string line)
        {
            string body = Body(line, "MBOX").Trim();   // "OKCANCEL, message" or "message"
            bool cancellable = false, yesNo = false;

            int comma = body.IndexOf(',');
            string head = (comma >= 0 ? body.Substring(0, comma) : body).Trim().ToUpperInvariant();
            if (head == "OK" || head == "OKCANCEL" || head == "YESNO")
            {
                cancellable = head == "OKCANCEL" || head == "YESNO";
                yesNo = head == "YESNO";
                body = comma >= 0 ? body.Substring(comma + 1).Trim() : string.Empty;
            }

            if (body == string.Empty)
                body = "(no message)";

            return ShowHoldPrompt(name, body, cancellable, yesNo);
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
                Owner = OwnerWindow(),
                WindowStartupLocation = OwnerWindow() != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
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
            win.Closed += (s, e) => frame.Continue = false;

            // Keep keyboard jogging live while the prompt is up. The prompt is a separate top-level window, so
            // it owns keyboard focus and the main window's jog forwarding never sees these keys (and the macro
            // may have been launched from a non-Job tab anyway, where that forwarding is disabled). Forward
            // jog-relevant keys straight to the keypress handler; leave Enter/Esc/Tab/Space for the buttons.
            var kbd = CNC.Core.Grbl.GrblViewModel?.Keyboard;
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

        // True if the trimmed line is the named directive, e.g. "(MBOX ...)" / "(PREREQ ...)".
        private static bool IsDirective(string line, string keyword)
        {
            string t = line.TrimStart();
            if (!t.StartsWith("("))
                return false;
            t = t.Substring(1).TrimStart();
            return t.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
                   (t.Length == keyword.Length || !char.IsLetter(t[keyword.Length]));
        }

        // The text inside the parentheses after the keyword (and the following comma/space), e.g.
        // "(MBOX, OKCANCEL, hi)" -> "OKCANCEL, hi".
        private static string Body(string line, string keyword)
        {
            string t = line.Trim();
            int close = t.LastIndexOf(')');
            string inner = (close >= 1 ? t.Substring(1, close - 1) : t.Substring(1)).TrimStart();
            inner = inner.Substring(Math.Min(keyword.Length, inner.Length));   // drop the keyword
            return inner.TrimStart(' ', ',');
        }
    }
}
