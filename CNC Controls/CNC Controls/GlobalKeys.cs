/*
 * GlobalKeys.cs - part of CNC Controls library
 *
 * Application-wide keyboard dispatch: jog keys and keyboard shortcuts, from EVERY window.
 *
 * The rule this implements, in one line: a jog key jogs and a shortcut fires, unless you are typing -
 * wherever you happen to be.
 *
 * What it replaces. Both dispatch paths used to be tied to a particular window or view:
 *
 *   - Jog keys were reachable only from four controls' own PreviewKeyDown (JobView, ProbingView,
 *     JogFlyoutControl, MacroProcessor), plus a MainWindow forwarder that ran only when
 *     `CurrentView is JobView`. Dead on every other tab, and behind every dialog.
 *   - Shortcuts (F1 help, tab-switch, ActionKeyBinder actions) were dispatched from MainWindow's own
 *     PreviewKeyDown, so they worked on the top-level tabs and nowhere else - in particular not while
 *     Machine Setup or Fixture Definition had the keyboard.
 *
 * Which of those gates was blocking at any given moment was not something a user could reasonably
 * predict, and predicting it is not their job.
 *
 * How. A CLASS handler on Window, so it fires for every window in the application - main window and
 * every dialog, including ones opened later, with nothing to remember to wire up per dialog. Class
 * handlers run before instance handlers on the same element, so this sees a key first; anything it does
 * not claim falls through untouched.
 *
 * Order is jog first, then shortcuts. A jog key that is also bound as a shortcut jogs - motion is the
 * more immediate intent, and it keeps "arrow keys move the machine" true without exception.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public static class GlobalKeys
    {
        private static bool hooked = false;

        /// <summary>
        /// F1 help, tab-switch and ActionKeyBinder dispatch, supplied by the host window because they are
        /// its business (they need the current view, the tab list, the menu items). Registered once at
        /// startup; invoked here for EVERY window so a shortcut is not silently main-window-only.
        ///
        /// Each of those dispatchers carries its own "don't steal a text-producing key from a text box"
        /// guard, which is why the broad input-field test below is applied to jogging only: a Ctrl+Alt
        /// shortcut SHOULD still fire while the caret is in a text box, and an unmodified one should not.
        /// Applying the blanket guard here would break the first half of that.
        /// </summary>
        public static Func<KeyEventArgs, bool> ShortcutDispatcher;

        public static void Hook()
        {
            if (hooked)
                return;

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

            if (keyboard != null && !FocusIsInInputField() && keyboard.ProcessKeypress(e, true))
            {
                e.Handled = true;
                return;
            }

            var shortcuts = ShortcutDispatcher;
            if (shortcuts != null && shortcuts(e))
                e.Handled = true;
        }

        // Key-up is forwarded UNCONDITIONALLY - no focus gate. A continuous jog (Shift/Ctrl+Shift tiers)
        // runs while the key is held and stops on release, so the release event is a STOP command for a
        // moving machine. If focus moves into a text box while an axis is jogging - a click, a dialog
        // opening, anything - a gated key-up would never arrive and the axis would keep going.
        // ProcessKeypress cancels the jog when allowJog is false, so passing the focus state through still
        // stops it; what must never happen is not calling at all.
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
        // TextBoxBase/PasswordBox are the literal typing case. The rest are controls where an arrow key
        // MEANS something - moving through a list, opening a combo, dragging a slider - and stealing it
        // would trade one unpredictable behaviour for another. Walks up from the focused element because
        // focus usually lands on an inner part (a TextBox inside a ComboBox, a cell inside a DataGrid)
        // rather than on the control itself.
        private static bool FocusIsInInputField()
        {
            var element = Keyboard.FocusedElement as DependencyObject;

            while (element != null)
            {
                if (element is TextBoxBase || element is PasswordBox || element is ComboBox
                     || element is Selector || element is Slider || element is MenuBase)
                    return true;

                element = GetParent(element);
            }

            return false;
        }

        // A parent walk that works for both a visual and a logical/content parent. Keyboard focus can sit
        // on an element that is not in the visual tree of the control that owns it (a ComboBox's popup
        // content lives in its own visual root), where VisualTreeHelper.GetParent alone returns null too
        // early and the walk above would wrongly conclude "not an input field".
        private static DependencyObject GetParent(DependencyObject child)
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
