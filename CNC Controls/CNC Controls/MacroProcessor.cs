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
        /// <summary>Run a macro. Returns false if it was aborted (prerequisite unmet or user cancelled).</summary>
        public static bool Run(GrblViewModel model, string name, string code)
        {
            if (model == null || string.IsNullOrEmpty(code))
                return true;

            if (string.IsNullOrEmpty(name))
                name = "Macro";

            // A macro whose body is a single "@<path>" line is a reference to an external file;
            // load and run that file's current contents (re-read every run, so the macro can be
            // developed by editing the file - no copy/paste back into ioSender).
            if (!ResolveFileReference(ref code, name))
                return false;

            // Fast path: no directives -> identical to the previous behaviour.
            if (code.IndexOf("(PREREQ", StringComparison.OrdinalIgnoreCase) < 0 &&
                 code.IndexOf("(MBOX", StringComparison.OrdinalIgnoreCase) < 0 &&
                  code.IndexOf("(WAITIDLE", StringComparison.OrdinalIgnoreCase) < 0 &&
                   code.IndexOf("(PROMPT", StringComparison.OrdinalIgnoreCase) < 0)
            {
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
                MessageBox.Show(string.Format("Cannot run macro \"{0}\":\r\n\r\n• {1}", name, string.Join("\r\n• ", unmet)),
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
            if (fields.Count > 0)
            {
                if (!ShowPromptDialog(name, fields))
                    return false;   // cancelled

                var assignments = new StringBuilder();
                foreach (var field in fields)
                    assignments.Append(field.Param).Append('=').Append(field.Value).Append('\n');
                model.ExecuteMacro(assignments.ToString());
            }

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
                        if (MessageBox.Show(string.Format("Run macro \"{0}\"?", name), "ioSender",
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
                        MessageBox.Show(string.Format("Macro \"{0}\" aborted: the controller did not return to idle (alarm or connection lost).", name),
                            "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    continue;
                }

                buffer.Append(ApplySubstitutions(raw, fields)).Append('\n');
            }
            Flush(model, buffer);

            return true;
        }

        private static void Flush(GrblViewModel model, StringBuilder buffer)
        {
            if (buffer.Length > 0)
            {
                model.ExecuteMacro(buffer.ToString());
                buffer.Clear();
            }
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

            if (!Path.IsPathRooted(path))
                path = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, path);

            try
            {
                code = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Macro \"{0}\" references a file that could not be read:\r\n\r\n{1}\r\n\r\n{2}", name, path, ex.Message),
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

            if (Application.Current != null && Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                win.Owner = Application.Current.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

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
                        MessageBox.Show(string.Format("\"{0}\" is not a valid number.", fields[i].Label), title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var buttons = MessageBoxButton.OK;

            int comma = body.IndexOf(',');
            string head = (comma >= 0 ? body.Substring(0, comma) : body).Trim().ToUpperInvariant();
            if (head == "OK" || head == "OKCANCEL" || head == "YESNO")
            {
                buttons = head == "OKCANCEL" ? MessageBoxButton.OKCancel
                        : head == "YESNO" ? MessageBoxButton.YesNo
                        : MessageBoxButton.OK;
                body = comma >= 0 ? body.Substring(comma + 1).Trim() : string.Empty;
            }

            if (body == string.Empty)
                body = "(no message)";

            var result = MessageBox.Show(body, name, buttons, MessageBoxImage.Information);
            return !(result == MessageBoxResult.Cancel || result == MessageBoxResult.No);
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
