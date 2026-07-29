/*
 * Threading.cs - part of CNC Core library
 *
 * Portable thread marshalling, replacing System.Windows.Threading.Dispatcher in CNC.Core.
 *
 * Two distinct needs were both being served by Dispatcher, and they are separated here:
 *
 *  UiContext  - "run this on the host's UI thread". Was Application.Current.Dispatcher, i.e. a global
 *               lookup of the WPF application object. The host now registers its context once at
 *               startup (see UiContext.Register). With no registration - a headless server, a console
 *               tool - everything runs inline, which is the correct behaviour when there is no UI
 *               thread to marshal to.
 *
 *  SyncTarget - "come back to the thread that started this operation". Was
 *               Dispatcher.CurrentDispatcher captured at the start of a request/response handshake and
 *               compared against the responding thread's dispatcher (see Grbl.cs's $I / spindle /
 *               settings readers, whose responses can arrive on a comms or worker thread).
 *
 * Thread identity is compared by managed thread id rather than by comparing SynchronizationContext
 * instances. That is both cheaper and more reliable: WPF installs a fresh DispatcherSynchronizationContext
 * around some callbacks, so two contexts for the same thread are not necessarily reference-equal, while
 * Dispatcher.CheckAccess - which this replaces - was always a plain thread-id comparison.
 */

using System;
using System.Threading;

namespace CNC.Core
{
    /// <summary>The host's UI thread, if it has one. Unregistered means "no UI thread" and everything runs inline.</summary>
    public static class UiContext
    {
        private static SynchronizationContext context;
        private static int threadId = -1;

        /// <summary>
        /// Call once from the host's UI thread during startup, before any view model is built
        /// (ioSender does this in App.OnStartup). A host with no UI thread simply never calls it.
        /// </summary>
        public static void Register()
        {
            context = SynchronizationContext.Current;
            threadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>True when it is safe to touch UI-affine state directly - i.e. we are on the registered
        /// thread, or no UI thread was ever registered. Equivalent to the old Dispatcher.CheckAccess().</summary>
        public static bool IsCurrent
        {
            get { return context == null || Thread.CurrentThread.ManagedThreadId == threadId; }
        }

        /// <summary>Run synchronously on the UI thread, blocking until it completes (was Dispatcher.Invoke).</summary>
        public static void Send(System.Action action)
        {
            if (action == null)
                return;

            if (IsCurrent)
                action();
            else
                context.Send(o => action(), null);
        }

        /// <summary>Queue on the UI thread and return immediately (was Dispatcher.BeginInvoke). Runs inline
        /// only when there is no UI thread at all - being already on it still queues, preserving ordering.</summary>
        public static void Post(System.Action action)
        {
            if (action == null)
                return;

            if (context == null)
                action();
            else
                context.Post(o => action(), null);
        }

        /// <summary>Inline when already on the UI thread, queued otherwise.</summary>
        public static void Run(System.Action action)
        {
            if (IsCurrent)
            {
                if (action != null)
                    action();
            }
            else
                Post(action);
        }
    }

    /// <summary>
    /// The thread that began an operation, captured so a response arriving on another thread can be
    /// marshalled back to it. Replaces capturing Dispatcher.CurrentDispatcher.
    /// </summary>
    public class SyncTarget
    {
        private readonly SynchronizationContext context;
        private readonly int threadId;

        private SyncTarget(SynchronizationContext context, int threadId)
        {
            this.context = context;
            this.threadId = threadId;
        }

        /// <summary>Capture the calling thread as the target.</summary>
        public static SyncTarget Capture()
        {
            return new SyncTarget(SynchronizationContext.Current, Thread.CurrentThread.ManagedThreadId);
        }

        /// <summary>True when the caller is already on the captured thread.</summary>
        public bool IsCurrent
        {
            get { return Thread.CurrentThread.ManagedThreadId == threadId; }
        }

        /// <summary>
        /// Run synchronously on the captured thread, blocking until it completes (was Dispatcher.Invoke).
        /// Runs inline if already there, or if that thread had no synchronization context to marshal through.
        /// </summary>
        public void Send(System.Action action)
        {
            if (action == null)
                return;

            if (IsCurrent || context == null)
                action();
            else
                context.Send(o => action(), null);
        }
    }
}
