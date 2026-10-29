/*
 * UserPrompt.cs - part of CNC Core library
 *
 * Portable (non-WPF) prompt abstraction for CNC.Core.
 *
 * CNC.Core occasionally has to ask the user something mid-operation - a g-code parse error it can
 * continue past, a serial port it could not open. That was done by calling AppDialogs.Show()
 * directly, which is System.Windows.MessageBox plus the WpfUiTestServer hook, and it was the reason
 * AppDialogs had to live in CNC.Core at all ("CNC Core itself shows message boxes and cannot
 * reference the higher layer"). It is also the reason CNC.Core referenced the WpfUiTestServer
 * package, whose nuspec declares PresentationCore/PresentationFramework/WindowsBase/System.Xaml as
 * <frameworkAssemblies> - NuGet injects those into every consumer, so no amount of editing
 * <Reference> items could get WPF out of Core while that package reference remained.
 *
 * Core now raises an intent ("ask the user this") and a host decides how to present it. The WPF
 * client registers AppDialogs; a headless .NET 8 server registers nothing and gets SafeDefault(),
 * which is the correct behaviour for a server - never block on a modal nobody can see.
 *
 * The enums deliberately do NOT mirror System.Windows names, so a file can use both namespaces
 * without ambiguity.
 */

using System;

namespace CNC.Core
{
    public enum PromptButtons
    {
        OK,
        OKCancel,
        YesNo,
        YesNoCancel
    }

    public enum PromptIcon
    {
        None,
        Information,
        Question,
        Warning,
        Error
    }

    public enum PromptResult
    {
        None,
        OK,
        Cancel,
        Yes,
        No
    }

    public delegate PromptResult UserPromptHandler(string message, string caption, PromptButtons buttons,
        PromptIcon icon, PromptResult defaultResult, string id);

    public static class UserPrompt
    {
        /// <summary>
        /// Set by the host. The WPF client points this at AppDialogs (see AppMessageBox.Register).
        /// Left null - a headless server, or a unit test - every prompt resolves to SafeDefault()
        /// without blocking.
        /// </summary>
        public static UserPromptHandler Handler { get; set; }

        public static PromptResult Show(string message, string caption = "",
            PromptButtons buttons = PromptButtons.OK, PromptIcon icon = PromptIcon.None,
            PromptResult defaultResult = PromptResult.None, string id = null)
        {
            var handler = Handler;

            if (handler == null)
                return defaultResult == PromptResult.None ? SafeDefault(buttons) : defaultResult;

            return handler(message, caption, buttons, icon, defaultResult, id);
        }

        /// <summary>
        /// The answer used when nobody can answer: the least destructive choice.
        /// Same policy AppDialogs applies when the test harness captures a prompt but times out.
        /// </summary>
        public static PromptResult SafeDefault(PromptButtons buttons)
        {
            switch (buttons)
            {
                case PromptButtons.OKCancel: return PromptResult.Cancel;
                case PromptButtons.YesNo: return PromptResult.No;
                case PromptButtons.YesNoCancel: return PromptResult.Cancel;
                default: return PromptResult.OK;
            }
        }
    }
}
