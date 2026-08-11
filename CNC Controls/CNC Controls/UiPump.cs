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

        // Every caller of this is a "wait for the controller while keeping the UI alive" loop:
        //
        //     while (res == null) EventUtils.DoEvents();
        //
        // with no delay of any kind. Each pass allocates a DispatcherFrame and a DispatcherOperation, and
        // an unblocked UI thread runs that loop millions of times a second - so a reply that is merely
        // SLOW (grblHAL is silent for a whole homing cycle) turns into hundreds of megabytes a second of
        // garbage. Twice on 2026-08-11 that reached the 32-bit process ceiling and killed the app with an
        // OutOfMemoryException at ~3.2GB, both times with the stack in this method, under
        // GrblSDCard.Load's $CWD= wait. It is the same spin already recorded against
        // MacroRunner.StreamProgram.
        //
        // The 1ms yield makes the loop cost bounded (~1000 pumps/second instead of millions) without
        // changing what any caller sees: they are all waiting on a controller that answers in
        // milliseconds at best, and the UI is still pumped far faster than a human or a status report can
        // notice. Fixing it here rather than at the call sites deliberately - there are dozens of these
        // loops, and the next one written will get the fix for free.
        private static void DoEvents()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(ExitFrame), frame);
            Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(1);
        }

        private static object ExitFrame(object f)
        {
            ((DispatcherFrame)f).Continue = false;

            return null;
        }
    }
}
