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
                ToPromptResult(Show(message, caption, ToMessageBoxButton(buttons), ToMessageBoxImage(icon),
                                    ToMessageBoxResult(defaultResult), id));
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

        public static MessageBoxResult Show(string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string id = null, string yesText = null, string noText = null)
        {
            string answer = Ask(id ?? caption, caption, message, buttons, defaultResult);
            if (answer != null)
                return ParseResult(answer, buttons);
            return CustomMessageBox != null
                ? CustomMessageBox(null, message, caption, buttons, icon, DefaultOrNone(defaultResult), yesText, noText)
                : MessageBox.Show(message, caption, buttons, icon, DefaultOrNone(defaultResult));
        }

        public static MessageBoxResult Show(Window owner, string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string id = null, string yesText = null, string noText = null)
        {
            string answer = Ask(id ?? caption, caption, message, buttons, defaultResult);
            if (answer != null)
                return ParseResult(answer, buttons);
            if (CustomMessageBox != null)
                return CustomMessageBox(owner, message, caption, buttons, icon, DefaultOrNone(defaultResult), yesText, noText);
            return owner != null
                ? MessageBox.Show(owner, message, caption, buttons, icon, DefaultOrNone(defaultResult))
                : MessageBox.Show(message, caption, buttons, icon, DefaultOrNone(defaultResult));
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
