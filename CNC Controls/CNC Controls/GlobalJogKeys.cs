/*
 * GlobalJogKeys.cs - part of CNC Controls library
 *
 * Makes keyboard jogging work EVERYWHERE it sensibly can, instead of only where some view happened to
 * wire it up.
 *
 * The rule this implements, in one line: a jog key jogs unless you are typing.
 *
 * What it replaces. ProcessKeypress used to be reachable only from the PreviewKeyDown of four specific
 * controls (JobView, ProbingView, JogFlyoutControl, MacroProcessor), plus a forwarder in MainWindow that
 * ran only when `UIViewModel.CurrentView is JobView`. So keyboard jogging was silently dead on every
 * other tab, and dead again whenever a dialog was open - Machine Setup and Fixture Definition being
 * exactly the two places an operator most wants to nudge an axis while lining something up. Which of
 * those gates was blocking you at any moment was not something a user could reasonably predict, and
 * predicting it is not their job.
 *
 * How. A CLASS handler on Window, so it fires for every window in the application - the main window and
 * every dialog, including ones opened later, with nothing to remember to wire up per dialog. Class
 * handlers run before instance handlers on the same element, so this sees a key before MainWindow's own
 * PreviewKeyDown; that is harmless because ProcessKeypress returns false for any key that is not a jog
 * key, leaving F1/tab-switch/ActionKeyBinder dispatch exactly as it was.
 *
 * Deliberately NOT globalised: tab-switch and action shortcuts. Jogging behind an open Machine Setup
 * dialog is the point; switching the tab behind a modal dialog is not.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public static class GlobalJogKeys
    {
        private static bool hooked = false;

        public static void Hook()
        {
            if (hooked)
                return;

            // handledEventsToo: false - if something genuinely consumed the key first, let it.
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyUpEvent, new KeyEventHandler(OnPreviewKeyUp));
            hooked = true;
        }

        private static KeypressHandler Handler
        {
            get { return Grbl.GrblViewModel?.Keyboard as KeypressHandler; }
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled)
                return;

            var keyboard = Handler;
            if (keyboard == null || FocusIsInInputField())
                return;

            e.Handled = keyboard.ProcessKeypress(e, true);
        }

        // Key-up is forwarded UNCONDITIONALLY - no focus gate, no null-handler early-out beyond the
        // obvious one. A continuous jog (Shift/Ctrl+Shift tiers) runs while the key is held and stops on
        // release, so the release event is a STOP command for a moving machine. If focus moves into a text
        // box while an axis is jogging - a click, a dialog opening, anything - a gated key-up would never
        // arrive and the axis would keep going. ProcessKeypress cancels the jog when allowJog is false, so
        // passing the focus state through still stops it; what must never happen is not calling at all.
        private static void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            var keyboard = Handler;
            if (keyboard == null)
                return;

            if (keyboard.ProcessKeypress(e, !FocusIsInInputField()))
                e.Handled = true;
        }

        // "Are you typing, or otherwise using this key for something the control needs?"
        //
        // TextBoxBase/PasswordBox are the literal typing case the user named. The rest are controls where
        // an arrow key MEANS something - moving through a list, opening a combo, dragging a slider - and
        // stealing it would trade one unpredictable behaviour for another. Walks up from the focused
        // element because focus usually lands on an inner part (a TextBox inside a ComboBox, a cell inside
        // a DataGrid) rather than the control itself.
        private static bool FocusIsInInputField()
        {
            var element = Keyboard.FocusedElement as DependencyObject;

            while (element != null)
            {
                if (element is TextBoxBase || element is PasswordBox || element is ComboBox
                     || element is Selector || element is Slider || element is MenuBase)
                    return true;

                element = VisualTreeHelperEx.GetParent(element);
            }

            return false;
        }
    }

    // A parent walk that works for both a visual and a logical/content parent. Keyboard focus can sit on
    // an element that is not in the visual tree of the control that owns it (a ComboBox's popup content
    // lives in its own visual root), where VisualTreeHelper.GetParent alone returns null too early and the
    // walk above would wrongly conclude "not an input field".
    internal static class VisualTreeHelperEx
    {
        public static DependencyObject GetParent(DependencyObject child)
        {
            if (child is System.Windows.Media.Visual || child is System.Windows.Media.Media3D.Visual3D)
            {
                var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(child);
                if (visualParent != null)
                    return visualParent;
            }

            return LogicalTreeHelper.GetParent(child);
        }
    }
}
