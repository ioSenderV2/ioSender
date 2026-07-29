/*
 * UiPump.cs - part of CNC Controls library
 *
 * The WPF implementation of CNC.Core's nested message pump (EventUtils.DoEvents), which CNC.Core can no
 * longer contain: DispatcherFrame / Dispatcher.PushFrame are WPF types.
 *
 * Register() installs BOTH the pump and CNC.Core's UI synchronization context, from the one call, on
 * purpose: roughly 70 call sites rely on DoEvents to keep the UI alive while a blocking controller
 * handshake waits for replies, so a host with a context but no pump would appear to hang at connect.
 *
 * The context is built explicitly from the dispatcher rather than taken from SynchronizationContext.Current.
 * A DispatcherSynchronizationContext built this way marshals at DispatcherPriority.Normal, matching the
 * Dispatcher.Invoke this replaced. The ambient context must NOT be used: it inherits the priority of the
 * enclosing dispatcher operation, which during connect is ApplicationIdle - below the Background priority
 * DoEvents exits its frame at, so the marshalled callback is never dispatched and connect hangs. That was
 * a real regression; see the CNC.Core Threading.cs header.
 */

using System.Windows.Threading;
using CNC.Core;

namespace CNC.Controls
{
    public static class UiPump
    {
        /// <summary>Call once from the host's UI thread at startup, before any view model is built.</summary>
        public static void Register()
        {
            UiContext.Register(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
