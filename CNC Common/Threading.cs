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
 *
 * PRIORITY - the trap that broke connect, do not reintroduce it:
 * a WPF DispatcherSynchronizationContext carries a DispatcherPriority, and the AMBIENT one
 * (SynchronizationContext.Current) inherits the priority of the dispatcher operation it was created
 * under. This app defers controller I/O to low-priority operations on purpose, so during connect the
 * ambient context's priority is ApplicationIdle (2). Marshalling through it queues the callback at
 * ApplicationIdle, while EventUtils.DoEvents exits its frame at Background (4) - so every pump cycle
 * ended before the callback was ever dispatched and the handshake starved forever (observed: 146k pump
 * cycles, callback never run). The Dispatcher.Invoke this replaced always used Normal (9).
 *
 * So the context is never taken from SynchronizationContext.Current for UI marshalling: the host
 * supplies one built explicitly from its dispatcher (CNC.Controls.UiPump.Register), which defaults to
 * Normal priority, and SyncTarget prefers that registered context over the ambient one.
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
        /// Call once from the host's UI thread during startup, before any view model is built.
        /// A WPF host must pass a context built from its dispatcher rather than letting this capture the
        /// ambient one - see the priority note in the file header (CNC.Controls.UiPump.Register does it).
        /// A host with no UI thread simply never calls this.
        /// </summary>
        public static void Register(SynchronizationContext uiContext)
        {
            context = uiContext;
            threadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Register using the calling thread's ambient context. Only safe for a host whose ambient
        /// context has no priority semantics; a WPF host must use the overload above.</summary>
        public static void Register()
        {
            Register(SynchronizationContext.Current);
        }

        /// <summary>The context the host registered, if any.</summary>
        internal static SynchronizationContext RegisteredContext { get { return context; } }

        /// <summary>Managed thread id of the registered UI thread, or -1 when none was registered.</summary>
        internal static int ThreadId { get { return context == null ? -1 : threadId; } }

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

        /// <summary>
        /// Capture the calling thread as the target. When that thread is the registered UI thread, the
        /// registered context is used in preference to the ambient one: the ambient context inherits the
        /// priority of the enclosing dispatcher operation, which starves the callback (file header).
        /// </summary>
        public static SyncTarget Capture()
        {
            int id = Thread.CurrentThread.ManagedThreadId;

            return new SyncTarget(id == UiContext.ThreadId ? UiContext.RegisteredContext : SynchronizationContext.Current, id);
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
