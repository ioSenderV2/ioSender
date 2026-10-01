/*
 * WindowFocusReturn.cs - part of CNC Controls library
 *
 * Closing a popup hands focus back to ioSender, not to whatever Windows picks next.
 *
 */

/*

The symptom: dismiss a popup - the "Status messages since launch" window, a settings dialog, a
popped-out view - and a DIFFERENT APPLICATION comes forward instead of the sender. The operator has
to alt-tab back to the machine they were driving.

Owning the popup is not the cure. ViewHostWindow discovered this first and wrote it down: with the
main window sitting behind a popup (and often behind a dialog owned by it in turn - Machine Setup ->
Fixture definitions), closing the last one still frequently activates another app. Windows picks the
next window in ITS z-order, which is not necessarily ours. The owner has to be activated explicitly.

So this attaches that one behaviour to every window the app opens, via a class handler on
Window.Loaded - which catches the ad-hoc `new Window { ... }` popups too, and anything added later,
which a sweep of Show() call sites cannot.

Deliberately NOT done here: setting Owner on windows that have none. That would fix the same symptom,
but it also forces a popup to sit above the main window forever and changes where it opens - real
behaviour changes for windows that were written without an owner on purpose. Activating the fallback
target on close gets the focus behaviour without re-parenting anything.

*/

using System.Windows;

namespace CNC.Controls
{
    public static class WindowFocusReturn
    {
        private static bool installed;

        /// <summary>
        /// Attach focus-return to every window this application opens from now on. Call once, before
        /// the first window is shown.
        /// </summary>
        public static void Install()
        {
            if (installed)
                return;
            installed = true;

            // Loaded is a Direct routed event, so a class handler on Window fires exactly once per
            // window that loads - never for a child element's own Loaded. It is also the first point
            // at which the window is a real, shown window, which is what Attach's capture needs.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                                              new RoutedEventHandler(OnWindowLoaded));
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Attach(sender as Window);
        }

        private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
            "FocusReturnAttached", typeof(bool), typeof(WindowFocusReturn), new PropertyMetadata(false));

        /// <summary>
        /// Idempotent: a window that is hidden and re-shown loads again, and a caller may attach
        /// explicitly as well (ViewHostWindow does, so it keeps the behaviour even in a host that
        /// never called Install).
        /// </summary>
        public static void Attach(Window win)
        {
            if (win == null || win == Application.Current?.MainWindow || (bool)win.GetValue(AttachedProperty))
                return;
            win.SetValue(AttachedProperty, true);

            // Traced because the whole design rests on a class handler for a Direct routed event firing
            // for every window - which is a claim about WPF, not about this code. -debuglog=ui turns the
            // claim into a log line per window.
            if (CNC.Core.DebugLog.Enabled)
                CNC.Core.DebugLog.Write("ui", string.Format("focus-return attached to {0} \"{1}\"",
                                                            win.GetType().Name, win.Title));

            // Both are read while the window is still alive: IsActive is already false by the time
            // Closed runs, and Owner is not reliable there either.
            bool wasActiveOnClose = false;
            Window ownerOnClose = null;

            win.Closing += (s, e) =>
            {
                wasActiveOnClose = win.IsActive;
                ownerOnClose = win.Owner;
            };

            win.Closed += (s, e) =>
            {
                // Guarded on this window actually having had focus as it closed: if the operator had
                // already switched to another app, pulling them back would be worse than the bug.
                if (!wasActiveOnClose)
                    return;

                // The owner if there is one - so a dialog over the Machine Setup wizard returns to the
                // wizard, not past it to the main window - otherwise the main window.
                Window target = ownerOnClose ?? Application.Current?.MainWindow;
                if (target == null || target == win || !target.IsLoaded)
                    return;

                if (target.WindowState == WindowState.Minimized)
                    target.WindowState = WindowState.Normal;
                target.Activate();

                if (CNC.Core.DebugLog.Enabled)
                    CNC.Core.DebugLog.Write("ui", string.Format("{0} \"{1}\" closed - activated {2} \"{3}\"",
                                                                win.GetType().Name, win.Title, target.GetType().Name, target.Title));
            };
        }
    }
}
