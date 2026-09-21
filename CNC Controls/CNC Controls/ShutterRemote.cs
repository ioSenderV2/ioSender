/*
 * ShutterRemote.cs - part of CNC Controls library
 *
 * A Bluetooth camera shutter remote, used as a one-button "carry on" while the operator's hands are on
 * the machine rather than the keyboard.
 *
 * ---- Why a keyboard hook, of all things ----
 *
 * Probing a height map with a touch plate means: place the plate, press Continue, walk back, place the
 * plate, press Continue - sixteen times for a 4x4 grid, with the laptop across the shop each time. A
 * shutter remote solves it for a few pounds, but only if the app can hear it.
 *
 * These remotes are HID keyboards and every one of them sends a MEDIA key. Measured on the operator's own
 * PICO remote 2026-09-18: iOS mode sends VOLUME UP (0xAF), Android mode VOLUME DOWN (0xAE), and it has no
 * mode that sends Enter. Windows routes media keys to the shell and the audio endpoint - they never reach
 * an ordinary WPF window at all, so no amount of key binding inside the app can see one. A low-level
 * keyboard hook is the only thing that does.
 *
 * ---- What keeps that honest ----
 *
 * A global keyboard hook is an intrusive thing for an app to own, so this one is kept honest:
 *
 *   - It is opt-in. Nobody who has never heard of this gets their volume keys quietly intercepted.
 *   - It swallows a key only when the press MEANT something (RemoteActions.Resolve answered). With the
 *     machine idle and nothing waiting, the key goes straight on to Windows and the volume changes as it
 *     always did - so "the hook is installed" and "the volume keys are taken" are no longer the same
 *     thing. That is what lets it stay installed for a whole session now that a press means something in
 *     several places (a prompt, a running job, a hold) rather than only during a height-map hold.
 *   - When the press DOES mean something it is swallowed, because the alternative is the system volume
 *     marching to maximum over the course of a height map.
 *   - ONE ACTION PER PHYSICAL PRESS. The same measurement showed a held button auto-repeating every
 *     ~30 ms after a ~500 ms delay; a single tap gives exactly one event. Without that rule one press
 *     would release several holds in a row and the machine would move to the next point - and the one
 *     after - while a hand is still on the plate. It is not tuning, it is the safety of the thing.
 *
 *     It is enforced by the KEY-UP, not by a timer. A timer alone was not enough, and the gap it left is
 *     worth keeping written down: the debounce clock is reset whenever a press passes through to Windows,
 *     so that a key Windows handled does not debounce the next real one - and a single long press spans
 *     both states. Hold the button while the machine is moving: nothing is waiting, so the repeats pass
 *     through and keep clearing the clock. The machine arrives and holds. The very next repeat of that
 *     SAME press now finds something waiting and a cleared clock, and releases it. Reported from the
 *     machine 2026-09-20 as "I see it if I press the remote button and take too long to lift up".
 *
 *     So a press is now latched at its first key-down and stays latched until the key-up. Whatever
 *     changes while the button is down, that press has already had its say. The timer stays beside it to
 *     catch a remote bouncing its contacts into two complete press/release cycles, which the latch cannot
 *     see.
 *
 *     If a remote ever failed to send a key-up, its button would stop working rather than start repeating.
 *     That is the right way round: the operator notices at once and reaches for the keyboard, where the
 *     other failure moves the machine.
 *
 * Both volume keys are accepted rather than one: the two modes of the same remote send different ones,
 * and asking an operator which mode their remote is in - to answer a question they only care about
 * because of this file - is a worse design than accepting either. WHICH key was pressed is passed on
 * rather than discarded, since once the machine is safely held the two can mean different things - see
 * RemoteActions, which owns every decision about meaning. This file only hears.
 */

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CNC.Core;

namespace CNC.Controls
{
    public static class ShutterRemote
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_VOLUME_DOWN = 0xAE;
        private const int VK_VOLUME_UP = 0xAF;

        /// <summary>Presses closer together than this are the same press - see the header.</summary>
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private static IntPtr _hook = IntPtr.Zero;
        // Held in a static field on purpose: the delegate is what Windows calls back into, and a local
        // would be collected while the hook is still installed - a crash whose stack says nothing about
        // this file.
        private static HookProc _proc;
        private static Dispatcher _dispatcher;
        private static DateTime _last = DateTime.MinValue;

        /// <summary>
        /// True from the first key-down of a physical press until its key-up. ONE action per press, decided
        /// at that first key-down and never revisited, however long the button is held.
        ///
        /// The timer alone could not do this. It is reset whenever a press passes through - so that a key
        /// Windows handled does not debounce the next real one - and a held button spans both states: press
        /// while the machine is moving and nothing is waiting, so the repeats pass through and keep clearing
        /// the timer; the machine then arrives and holds; the very next repeat of the SAME press finds
        /// something waiting and a cleared timer, and releases it. Reported from the machine 2026-09-20:
        /// "I see it if I press the remote button and take too long to lift up."
        ///
        /// That is the hazard this file's header already names - the next point reached while a hand is
        /// still on the plate - arriving by the one route the debounce did not cover.
        /// </summary>
        private static bool _pressActive = false;

        /// <summary>
        /// Whether this press's key-down was swallowed, so its key-up can be swallowed too. An orphan
        /// key-up for a key whose key-down never arrived is the kind of thing that makes a volume control
        /// behave oddly later, and it costs one bool not to find out.
        /// </summary>
        private static bool _swallowed = false;

        /// <summary>
        /// What a press means right now: given true for VOLUME UP, returns what to do, or null when the
        /// press means nothing here and the key should go on to Windows. Answered on the UI thread (a
        /// low-level hook runs on the thread that installed it) and must be quick - it decides, the
        /// action it returns is what actually runs. Set by RemoteActions.
        /// </summary>
        public static Func<bool, System.Action> Resolve;

        /// <summary>True while the hook is installed.</summary>
        public static bool Listening { get { return _hook != IntPtr.Zero; } }

        /// <summary>
        /// Start listening. Idempotent, so a caller can simply assert the state it wants rather than
        /// tracking transitions.
        /// </summary>
        public static void Start()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            if (_hook != IntPtr.Zero)
                return;

            // Start from "no press in progress". These are static and survive a stop/start, and a latch
            // left set - the hook removed between a key-down and its key-up - would mean the remote
            // silently never worked again this session.
            _pressActive = _swallowed = false;
            _last = DateTime.MinValue;

            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

            DebugLog.Write("remote", _hook != IntPtr.Zero
                ? "shutter remote: listening for a volume key"
                : "shutter remote: the keyboard hook could not be installed - the remote will not work");
        }

        /// <summary>Stop listening. Idempotent, and safe to call from a teardown path that may run twice.</summary>
        public static void Stop()
        {
            if (_hook == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _proc = null;
            DebugLog.Write("remote", "shutter remote: stopped listening");
        }

        private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (!down && !up)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            int vk = Marshal.ReadInt32(lParam);
            if (vk != VK_VOLUME_UP && vk != VK_VOLUME_DOWN)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            // Stamped here, against RemoteDevices' clock, so the hook and the raw-input listener can be
            // ordered against each other afterwards - the question being whether a raw-input event arrives
            // at all for a key this hook swallows, and if so on which side of this callback. See
            // RemoteDevices' header. Measurement only; nothing below reads it.
            if (DebugLog.Enabled)
                DebugLog.Write("remote", string.Format(CultureInfo.InvariantCulture,
                    "HOOK      t={0:F3}ms  vk=0x{1:X2} {2}", RemoteDevices.ElapsedMs, vk, up ? "up" : "down"));

            // The key-up is what ends a press. Until it arrives, every key-down for this key is the SAME
            // press auto-repeating, whatever has changed in the meantime.
            if (up)
            {
                bool swallowUp = _swallowed;
                _pressActive = _swallowed = false;
                return swallowUp ? new IntPtr(1) : CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // Repeats of a press that has already been decided. Nothing can make this act - that is the
            // whole point - but it is still swallowed when something is waiting, so a held button does not
            // march the system volume while a hold is pending.
            if (_pressActive)
            {
                if (_swallowed)
                {
                    DebugLog.Write("remote", "shutter remote: auto-repeat ignored - button still held");
                    return new IntPtr(1);
                }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            _pressActive = true;

            // What it means is decided HERE, synchronously, because the answer also decides whether the key
            // is swallowed - and that has to be settled before returning from the hook. Only the action it
            // hands back is posted; nothing slow runs on the hook.
            var resolve = Resolve;
            System.Action action = resolve == null ? null : resolve(vk == VK_VOLUME_UP);

            if (action == null)
            {
                // Nothing is waiting on it: this is just a volume key, and Windows should have it. The
                // press stays marked active, so if something starts waiting while the button is still down
                // it is the NEXT press that acts on it, not this one.
                _last = DateTime.MinValue;   // a press that passed through must not debounce the next real one
                DebugLog.Write("remote", "shutter remote: passed to Windows - nothing was waiting on it");
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            _swallowed = true;

            // The timer still earns its place beside the press latch: it catches a remote that bounces its
            // contacts into two complete press/release cycles, which the latch alone cannot see.
            var now = DateTime.UtcNow;
            bool act = now - _last >= Debounce;
            _last = now;

            if (act)
            {
                var dispatcher = _dispatcher;
                if (dispatcher != null)
                    dispatcher.BeginInvoke(action);
                DebugLog.Write("remote", string.Format("shutter remote: press (vk 0x{0:X2})", vk));
            }
            else
                DebugLog.Write("remote", "shutter remote: press ignored - within the debounce window");

            // Swallowed either way - a repeat that is ignored must not reach the volume control either.
            return new IntPtr(1);
        }
    }
}
