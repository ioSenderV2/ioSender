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
 * Lives in CNC Core (not CNC Controls) because CNC Core itself shows message boxes and cannot reference the
 * higher layer. Every project already references CNC Core, so one WpfUiTestServer reference here covers all.
 */

using System;
using System.Windows;

namespace CNC.Core
{
    public static class AppDialogs
    {
        // Set by CNC.Controls.AppMessageBox.Register() at startup so the real (non-test-server) fallback is
        // our own UiScale-aware window instead of the native, DPI/zoom-oblivious System.Windows.MessageBox.
        // Signature mirrors MessageBox.Show(owner, message, caption, buttons, icon, defaultResult); owner may
        // be null. Left null this class still works standalone (falls back to the native MessageBox).
        public static Func<Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult> CustomMessageBox;

        public static MessageBoxResult Show(string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string id = null)
        {
            string answer = Ask(id ?? caption, caption, message, buttons, defaultResult);
            if (answer != null)
                return ParseResult(answer, buttons);
            return CustomMessageBox != null
                ? CustomMessageBox(null, message, caption, buttons, icon, DefaultOrNone(defaultResult))
                : MessageBox.Show(message, caption, buttons, icon, DefaultOrNone(defaultResult));
        }

        public static MessageBoxResult Show(Window owner, string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string id = null)
        {
            string answer = Ask(id ?? caption, caption, message, buttons, defaultResult);
            if (answer != null)
                return ParseResult(answer, buttons);
            if (CustomMessageBox != null)
                return CustomMessageBox(owner, message, caption, buttons, icon, DefaultOrNone(defaultResult));
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
