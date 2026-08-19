/*
 * AppDialogs.cs - part of CNC Core library
 *
 * One funnel for the app's message boxes. In a normal run it is exactly MessageBox.Show. When the UI test
 * server is active it first offers the prompt to the harness (WpfUiTestServer.UiTestServer.Prompt); if the
 * harness has armed/captures an answer, no modal appears and automation doesn't stall - and the prompt's text
 * is recorded so the harness can read back what was shown, even for an info box.
 *
 * The overloads mirror System.Windows.MessageBox.Show (with an optional trailing `id` the harness can target),
 * so migrating a call is just MessageBox.Show(...) -> AppDialogs.Show(...). For a real user nothing changes:
 * Prompt returns null when the server isn't running, so the real MessageBox is shown as before.
 *
 * Moved here from CNC Core. It used to live there because "CNC Core itself shows message boxes and cannot
 * reference the higher layer" - Core now raises CNC.Core.UserPrompt instead, and Register() below points that
 * at this class. That is what let CNC.Core drop its WpfUiTestServer package reference (whose nuspec declares
 * WPF frameworkAssemblies that NuGet injects into every consumer) on the way to a .NET 8 target.
 */

using System;
using System.Windows;
using CNC.Core;

namespace CNC.Controls
{
    public static class AppDialogs
    {
        /// <summary>
        /// Route CNC.Core's portable prompts (CNC.Core.UserPrompt) through this class, so a prompt raised
        /// from inside Core looks and behaves exactly like one raised from the UI - same test-server hook,
        /// same UiScale-aware window. Called from AppMessageBox.Register() at startup.
        /// </summary>
        public static void RegisterCorePrompts()
        {
            UserPrompt.Handler = (message, caption, buttons, icon, defaultResult, id) =>
            {
                // A Core prompt can be raised from a WORKER thread - e.g. GCodeJob.ParseFileLines's
                // per-line load-error dialog, which runs inside BackgroundLoad's Task.Run since the
                // background-load refactor. OwnerWindow()/the custom message box touch UI-owned
                // objects, so that used to throw "the calling thread cannot access this object"
                // INSIDE the error dialog (found 2026-08-08 loading a #-expression file). Marshal the
                // whole thing synchronously: the caller blocks for the answer either way - that's the
                // prompt's contract - so Invoke preserves the semantics exactly.
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    return dispatcher.Invoke(() => ShowCorePrompt(message, caption, buttons, icon, defaultResult, id));
                return ShowCorePrompt(message, caption, buttons, icon, defaultResult, id);
            };
        }

        private static PromptResult ShowCorePrompt(string message, string caption, PromptButtons buttons,
            PromptIcon icon, PromptResult defaultResult, string id)
        {
            var owner = OwnerWindow();
            return ToPromptResult(owner != null
                ? Show(owner, message, caption, ToMessageBoxButton(buttons), ToMessageBoxImage(icon),
                       ToMessageBoxResult(defaultResult), id)
                : Show(message, caption, ToMessageBoxButton(buttons), ToMessageBoxImage(icon),
                       ToMessageBoxResult(defaultResult), id));
        }

        /// <summary>
        /// The window a dialog should be owned by, or null if the main window is not shown yet (the owner
        /// overload requires a non-null window, so callers fall back to the ownerless one).
        /// An owned dialog centres on its owner and is forced ABOVE it. That second part is the point:
        /// without an owner a modal box can end up BEHIND a Topmost window, and a hidden modal box blocks
        /// the app and looks exactly like a hang. Hence preferring a visible Topmost auxiliary window over
        /// the main one. The live case is MacroProcessor's own hold prompt, which is deliberately Topmost so
        /// it stays visible while the operator jogs - a message raised while one is up (an alarm abort, a
        /// failed WAITIDLE, a load error) would otherwise open behind it. (The commit that moved this here
        /// cited the floating run-control panel instead; that panel was retired when the run control moved to
        /// the fixed bottom bar - MacroProcessor.RunControlPanel is now declared, never set and never
        /// invoked. The hazard is real, the example was stale.)
        /// Moved up from MacroProcessor so every prompt gets it, including the ones CNC.Core raises through
        /// UserPrompt (which went to the ownerless overload until now).
        /// </summary>
        public static Window OwnerWindow()
        {
            if (Application.Current == null)
                return null;

            Window main = Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible
                ? Application.Current.MainWindow
                : null;

            foreach (Window w in Application.Current.Windows)
                if (w != main && w.IsVisible && w.Topmost)
                    return w;

            return main;
        }

        private static MessageBoxButton ToMessageBoxButton(PromptButtons b)
        {
            switch (b)
            {
                case PromptButtons.OKCancel: return MessageBoxButton.OKCancel;
                case PromptButtons.YesNo: return MessageBoxButton.YesNo;
                case PromptButtons.YesNoCancel: return MessageBoxButton.YesNoCancel;
                default: return MessageBoxButton.OK;
            }
        }

        private static MessageBoxImage ToMessageBoxImage(PromptIcon i)
        {
            switch (i)
            {
                case PromptIcon.Information: return MessageBoxImage.Information;
                case PromptIcon.Question: return MessageBoxImage.Question;
                case PromptIcon.Warning: return MessageBoxImage.Warning;
                case PromptIcon.Error: return MessageBoxImage.Error;
                default: return MessageBoxImage.None;
            }
        }

        private static MessageBoxResult ToMessageBoxResult(PromptResult r)
        {
            switch (r)
            {
                case PromptResult.OK: return MessageBoxResult.OK;
                case PromptResult.Cancel: return MessageBoxResult.Cancel;
                case PromptResult.Yes: return MessageBoxResult.Yes;
                case PromptResult.No: return MessageBoxResult.No;
                default: return MessageBoxResult.None;
            }
        }

        private static PromptResult ToPromptResult(MessageBoxResult r)
        {
            switch (r)
            {
                case MessageBoxResult.OK: return PromptResult.OK;
                case MessageBoxResult.Cancel: return PromptResult.Cancel;
                case MessageBoxResult.Yes: return PromptResult.Yes;
                case MessageBoxResult.No: return PromptResult.No;
                default: return PromptResult.None;
            }
        }

        // Set by CNC.Controls.AppMessageBox.Register() at startup so the real (non-test-server) fallback is
        // our own UiScale-aware window instead of the native, DPI/zoom-oblivious System.Windows.MessageBox.
        // Signature mirrors MessageBox.Show(owner, message, caption, buttons, icon, defaultResult) plus a
        // trailing yesText/noText pair for overriding the Yes/No button LABELS only (e.g. "Flash Firmware"/
        // "Cancel") - the test-server protocol below still only ever deals in generic Yes/No/OK/Cancel, so
        // automation is unaffected by what a button happens to say. Owner may be null. Left null this class
        // still works standalone (falls back to the native MessageBox, which can't customize button text -
        // yesText/noText are ignored there).
        public delegate MessageBoxResult CustomMessageBoxDelegate(Window owner, string message, string caption,
            MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult, string yesText, string noText);
        public static CustomMessageBoxDelegate CustomMessageBox;

        // WPF refuses resource/component loads once Application shutdown has begun - constructing ANY
        // dialog then throws InvalidOperationException ("The Application object is being shut down").
        // Surfaced as a full crash report 2026-08-08 13:16: a slow startup's "no controller response"
        // prompt raced the operator closing the hung-looking window, and the app crash-exited (0xFA11)
        // instead of just closing. A dialog nobody can answer has exactly one sensible behavior: take
        // the caller's default (or the least-destructive button), log it, and let shutdown proceed.
        // Application.IsShuttingDown is the flag WPF itself consults before refusing; it is internal,
        // so it is read via reflection with the public signals as fallback if that ever breaks.
        private static readonly System.Reflection.PropertyInfo isShuttingDownProp =
            typeof(Application).GetProperty("IsShuttingDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        private static bool ApplicationShuttingDown
        {
            get
            {
                try
                {
                    if (isShuttingDownProp != null && (bool)isShuttingDownProp.GetValue(null, null))
                        return true;
                }
                catch { /* reflection failed - fall through to the public signals */ }
                var app = Application.Current;
                return app == null || app.Dispatcher.HasShutdownStarted;
            }
        }

        private static MessageBoxResult ShutdownAnswer(string message, MessageBoxButton buttons, MessageBoxResult defaultResult)
        {
            var result = defaultResult == MessageBoxResult.None ? SafeDefault(buttons) : defaultResult;
            CNC.Core.DebugLog.Write("app", string.Format(
                "AppDialogs.Show suppressed - app is shutting down, answering {0} to: {1}", result, message));
            return result;
        }

        /// <summary>
        /// Record a dialog and the answer it got.
        ///
        /// Everything else the operator is told lands in a log - console lines, wire traffic, alarms - but a
        /// message box did not, so the single most explicit statement the app ever makes ("nothing has been
        /// probed", "this will overwrite your work origin") was the one thing that could not be recovered
        /// afterwards. Diagnosing from logs meant reconstructing which dialogs must have appeared.
        ///
        /// The ANSWER matters as much as the question: "asked, and the operator said No" and "asked, and the
        /// operator said Yes" are different histories, and only one of them explains what happened next.
        ///
        /// Newlines are flattened so one dialog is one line - these are read by eye alongside timestamped
        /// traffic, where a multi-line entry hides the lines around it.
        /// </summary>
        private static MessageBoxResult Logged(string caption, string message, MessageBoxResult result)
        {
            try
            {
                CNC.Core.DebugLog.Write("dialog", string.Format("[{0}] {1} -> {2}",
                    string.IsNullOrEmpty(caption) ? "-" : caption,
                    (message ?? string.Empty).Replace("\r", " ").Replace("\n", " "),
                    result));
            }
            catch { }   // logging a dialog must never be the reason a dialog fails to appear
            return result;
        }

        public static MessageBoxResult Show(string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string id = null, string yesText = null, string noText = null)
        {
            string answer = Ask(id ?? caption, caption, message, buttons, defaultResult);
            if (answer != null)
                return Logged(caption, message, ParseResult(answer, buttons));
            if (ApplicationShuttingDown)
                return Logged(caption, message, ShutdownAnswer(message, buttons, defaultResult));
            try
            {
                return Logged(caption, message, CustomMessageBox != null
                    ? CustomMessageBox(null, message, caption, buttons, icon, DefaultOrNone(defaultResult), yesText, noText)
                    : MessageBox.Show(message, caption, buttons, icon, DefaultOrNone(defaultResult)));
            }
            // Belt and braces for the race the pre-check can lose (shutdown starting between the check
            // and the construction). Filtered on the shutdown flag ON PURPOSE: an unfiltered catch here
            // would also swallow genuine dialog bugs (e.g. a cross-thread "calling thread must be STA"
            // is the same exception type) that must keep failing loudly.
            catch (InvalidOperationException) when (ApplicationShuttingDown)
            {
                return Logged(caption, message, ShutdownAnswer(message, buttons, defaultResult));
            }
        }

        public static MessageBoxResult Show(Window owner, string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string id = null, string yesText = null, string noText = null)
        {
            string answer = Ask(id ?? caption, caption, message, buttons, defaultResult);
            if (answer != null)
                return Logged(caption, message, ParseResult(answer, buttons));
            if (ApplicationShuttingDown)
                return Logged(caption, message, ShutdownAnswer(message, buttons, defaultResult));
            try
            {
                if (CustomMessageBox != null)
                    return Logged(caption, message, CustomMessageBox(owner, message, caption, buttons, icon, DefaultOrNone(defaultResult), yesText, noText));
                return Logged(caption, message, owner != null
                    ? MessageBox.Show(owner, message, caption, buttons, icon, DefaultOrNone(defaultResult))
                    : MessageBox.Show(message, caption, buttons, icon, DefaultOrNone(defaultResult)));
            }
            catch (InvalidOperationException) when (ApplicationShuttingDown)   // see the parameterless overload
            {
                return Logged(caption, message, ShutdownAnswer(message, buttons, defaultResult));
            }
        }

        // Offer the prompt to the harness; null => not intercepted (caller shows the real MessageBox).
        private static string Ask(string id, string caption, string message, MessageBoxButton buttons, MessageBoxResult defaultResult)
        {
            string def = (defaultResult == MessageBoxResult.None ? SafeDefault(buttons) : defaultResult).ToString();
            return WpfUiTestServer.UiTestServer.Prompt(id, caption, message, ButtonLabels(buttons), def);
        }

        private static MessageBoxResult DefaultOrNone(MessageBoxResult r)
        {
            return r;   // MessageBox.Show accepts None as "no explicit default"
        }

        private static string[] ButtonLabels(MessageBoxButton b)
        {
            switch (b)
            {
                case MessageBoxButton.OKCancel: return new[] { "OK", "Cancel" };
                case MessageBoxButton.YesNo: return new[] { "Yes", "No" };
                case MessageBoxButton.YesNoCancel: return new[] { "Yes", "No", "Cancel" };
                default: return new[] { "OK" };
            }
        }

        // The answer used when the harness captures a prompt but times out: the least destructive choice.
        private static MessageBoxResult SafeDefault(MessageBoxButton b)
        {
            switch (b)
            {
                case MessageBoxButton.OKCancel: return MessageBoxResult.Cancel;
                case MessageBoxButton.YesNo: return MessageBoxResult.No;
                case MessageBoxButton.YesNoCancel: return MessageBoxResult.Cancel;
                default: return MessageBoxResult.OK;
            }
        }

        private static MessageBoxResult ParseResult(string answer, MessageBoxButton b)
        {
            if (answer != null)
            {
                if (answer.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return MessageBoxResult.Yes;
                if (answer.Equals("No", StringComparison.OrdinalIgnoreCase)) return MessageBoxResult.No;
                if (answer.Equals("OK", StringComparison.OrdinalIgnoreCase)) return MessageBoxResult.OK;
                if (answer.Equals("Cancel", StringComparison.OrdinalIgnoreCase)) return MessageBoxResult.Cancel;
            }
            return SafeDefault(b);   // unrecognised answer -> safe default
        }
    }
}
