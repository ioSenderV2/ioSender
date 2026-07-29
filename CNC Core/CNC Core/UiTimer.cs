/*
 * UiTimer.cs - part of CNC Core library
 *
 * Portable replacement for System.Windows.Threading.DispatcherTimer: a periodic timer whose Tick is
 * raised on the host's UI thread (or inline, if the host registered no UI thread).
 *
 * Re-entrancy: DispatcherTimer got serialisation for free - its ticks are dispatcher work items, so a
 * slow handler simply delays the next one, and handlers never overlap. A raw System.Timers.Timer has no
 * such guarantee: it will keep firing on threadpool threads regardless of whether the previous tick has
 * finished, and each one would post another work item. The `ticking` guard restores DispatcherTimer's
 * behaviour by dropping a tick that arrives while one is still outstanding. This matters most for the
 * gamepad poll (60 Hz, ControllerService), where a backlog would queue stale input.
 */

using System;
using System.Threading;

namespace CNC.Core
{
    public class UiTimer : IDisposable
    {
        private readonly System.Timers.Timer timer = new System.Timers.Timer();
        private int ticking;   // 0 = idle, 1 = a tick is outstanding

        public event EventHandler Tick;

        public UiTimer()
        {
            timer.AutoReset = true;
            timer.Elapsed += (s, e) => Fire();
        }

        public TimeSpan Interval
        {
            get { return TimeSpan.FromMilliseconds(timer.Interval); }
            set { timer.Interval = Math.Max(1d, value.TotalMilliseconds); }
        }

        public bool IsEnabled { get { return timer.Enabled; } }

        public void Start()
        {
            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            Interlocked.Exchange(ref ticking, 0);
        }

        private void Fire()
        {
            // Drop this tick if the previous one has not been handled yet (see the header note).
            if (Interlocked.CompareExchange(ref ticking, 1, 0) != 0)
                return;

            UiContext.Run(() =>
            {
                try
                {
                    if (timer.Enabled)
                        Tick?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    Interlocked.Exchange(ref ticking, 0);
                }
            });
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
        }
    }
}
