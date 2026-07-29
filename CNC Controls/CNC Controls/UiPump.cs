/*
 * UiPump.cs - part of CNC Controls library
 *
 * The WPF implementation of CNC.Core's nested message pump (EventUtils.DoEvents), which CNC.Core can no
 * longer contain: DispatcherFrame / Dispatcher.PushFrame are WPF types.
 *
 * Register() is called from the host's startup alongside UiContext.Register(). Both must be installed,
 * and installing them together is deliberate - roughly 70 call sites across the app rely on DoEvents to
 * keep the UI alive while a blocking controller handshake waits for replies, so a host that registered
 * a UI context but no pump would appear to hang during connect or a settings read.
 */

using System.Windows.Threading;
using CNC.Core;

namespace CNC.Controls
{
    public static class UiPump
    {
        public static void Register()
        {
            EventUtils.Pump = DoEvents;
        }

        private static void DoEvents()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(ExitFrame), frame);
            Dispatcher.PushFrame(frame);
        }

        private static object ExitFrame(object f)
        {
            ((DispatcherFrame)f).Continue = false;

            return null;
        }
    }
}
