/*
 * MacroProcessor.cs - part of CNC Controls library
 *
 * Runs a macro, interpreting the parenthesised ioSender macro directives before the
 * remaining lines are streamed to the controller as G-code:
 *
 *   (PREREQ, cond, ...)   Require machine state before the macro runs. If any condition
 *                         is not met the macro is aborted with a message and nothing is
 *                         sent - so a macro that runs is guaranteed its prerequisites held.
 *                         Conditions: homed, tlo, idle, noalarm, connected.
 *
 *   (MBOX, [buttons,] message)
 *                         Pop a Windows MessageBox. Streaming of the following lines is
 *                         held until it is dismissed. Optional buttons: OK (default),
 *                         OKCANCEL, YESNO - Cancel/No aborts the rest of the macro.
 *
 * Macros containing neither directive run exactly as before (GrblViewModel.ExecuteMacro).
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
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

            // Fast path: no directives -> identical to the previous behaviour.
            if (code.IndexOf("(PREREQ", StringComparison.OrdinalIgnoreCase) < 0 &&
                 code.IndexOf("(MBOX", StringComparison.OrdinalIgnoreCase) < 0)
            {
                model.ExecuteMacro(code);
                return true;
            }

            string[] lines = code.Replace("\r", string.Empty).Split('\n');

            // 1) Prerequisites - evaluated up front, before anything is streamed.
            var unmet = new List<string>();
            foreach (var raw in lines)
            {
                if (!IsDirective(raw, "PREREQ"))
                    continue;
                foreach (var arg in Body(raw, "PREREQ").Split(','))
                {
                    string fail = EvalPrereq(model, arg.Trim().ToLowerInvariant());
                    if (fail != null)
                        unmet.Add(fail);
                }
            }
            if (unmet.Count > 0)
            {
                MessageBox.Show(string.Format("Cannot run macro \"{0}\":\r\n\r\n• {1}", name, string.Join("\r\n• ", unmet)),
                    "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 2) Stream the G-code, holding at each (MBOX).
            var buffer = new StringBuilder();
            foreach (var raw in lines)
            {
                if (IsDirective(raw, "PREREQ"))
                    continue;

                if (IsDirective(raw, "MBOX"))
                {
                    Flush(model, buffer);
                    if (!ShowMBox(name, raw))
                        return false;   // Cancel / No - stop here
                    continue;
                }

                buffer.Append(raw).Append('\n');
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

        private static string EvalPrereq(GrblViewModel model, string cond)
        {
            switch (cond)
            {
                case "":
                    return null;
                case "homed":
                    return model.HomedState == HomedState.Homed ? null : "the machine is not homed";
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
                    return "unknown prerequisite '" + cond + "'";
            }
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
