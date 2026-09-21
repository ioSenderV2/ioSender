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
 * These remotes are HID keyboards and every one of them sends a MEDIA key. The operator's PICO has TWO
 * BUTTONS, not a mode switch - this file said "mode" for three months and was simply wrong, corrected by
 * the operator 2026-09-21. The button marked for Android sends VOLUME UP (0xAF), the one marked for iOS
 * sends VOLUME DOWN (0xAE). Neither sends Enter. Windows routes media keys to the shell and the audio endpoint - they never reach
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
 * Both volume keys are accepted rather than one: they are the remote's two BUTTONS, so the operator has
 * both under their thumb and either may be the one they press. WHICH key was pressed is passed on
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
        private static int _traced = 0;   // see the TEMPORARY TRACE in Callback

        // When the HOOK last acted on a press, so the raw-input path below does not act on the same one
        // twice. Both can deliver the same press on a machine where the hook does see media keys.
        private static double _hookActedAt = double.NegativeInfinity;
        private const double DuplicateWindowMs = 250d;

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

        /// <summary>
        /// A press that arrived by RAW INPUT rather than through the hook, already known to be from the
        /// bound device. Acts, but cannot swallow - raw input is a read path.
        ///
        /// This exists because the hook is not a reliable way to hear this class of remote. Measured on
        /// the operator's PICO 2026-09-21: every keystroke from the machine's own keyboard reached the
        /// hook, and the remote's buttons did not reach it at all, while raw input reported both the key
        /// and the device. A Bluetooth HID consumer control is delivered to the raw input stack and the
        /// shell; whether it is ALSO injected as a keystroke the hook can see varies - the same remote did
        /// reach the hook earlier the same day, before the device re-enumerated.
        ///
        /// So the hook is no longer the way in. It is kept for what only it can do - SWALLOWING a press so
        /// the volume does not move - and this is the path that makes the buttons work either way.
        /// </summary>
        public static void PressFromRawInput(bool volumeUp)
        {
            // The hook got there first on a machine where it does fire: one press, one action.
            if (RemoteDevices.ElapsedMs - _hookActedAt <= DuplicateWindowMs)
                return;

            if (RemoteDevices.PressJustBound())
            {
                DebugLog.Write("remote", "shutter remote: that press bound the device - not acted on");
                return;
            }

            var resolve = Resolve;
            System.Action action = resolve == null ? null : resolve(RemoteDevices.IsPrimary(volumeUp));

            if (action == null)
            {
                // Cannot be swallowed from here, so the volume WILL move. Say so rather than beeping into
                // a key that also turned the volume down - a beep would read as "handled".
                DebugLog.Write("remote", "shutter remote: nothing was waiting (raw input; the key still reaches Windows)");
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _last < Debounce)
            {
                DebugLog.Write("remote", "shutter remote: press ignored - within the debounce window");
                return;
            }
            _last = now;

            DebugLog.Write("remote", "shutter remote: press (raw input)");
            var dispatcher = _dispatcher;
            if (dispatcher != null)
                dispatcher.BeginInvoke(action);
            else
                action();
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

            // TEMPORARY TRACE. The remote's HID events were arriving and Windows was changing the volume,
            // yet no HOOK line appeared for them - and with the log only written for 0xAE/0xAF there is no
            // way to tell "the hook never fired" from "the key is not the one we filter for". Logging every
            // key the hook sees settles it: ordinary typing appearing proves the hook is alive, and a
            // remote press then either shows up with some other vk or does not show up at all.
            //
            // Capped so it cannot flood a long session, and behind the usual gate. Remove once the answer
            // is in - it is a question, not an instrument worth keeping.
            if (DebugLog.Enabled && _traced < 400)
            {
                _traced++;
                DebugLog.Write("remote", string.Format(CultureInfo.InvariantCulture,
                    "HOOKSAW   vk=0x{0:X2} {1}", vk, up ? "up" : "down"));
            }

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

            // Not the bound remote? Then this is somebody's ordinary volume key - most likely the machine's
            // own keyboard - and it must behave like one. Without this, BOTH volume keys mean Feed Hold
            // while a job runs, so reaching up to turn the music down holds the machine.
            //
            // Answered from the raw-input event that landed just before this callback (RemoteDevices), and
            // fails OPEN when nothing is bound, so an operator who has never bound anything is exactly
            // where they were.
            // The press that just bound the remote is consumed here and goes no further: the operator
            // pressed the button to identify the device, not to answer whatever was on screen at the time.
            if (RemoteDevices.PressJustBound())
            {
                // The same press settles which button is which: the one used to bind is the primary one.
                RemoteDevices.SetPrimaryFromBindPress(vk == VK_VOLUME_UP);
                _swallowed = true;
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                DebugLog.Write("remote", "shutter remote: that press bound the device - not acted on");
                return new IntPtr(1);
            }

            // Not the bound remote? Then it is not this operator's pendant, and ioSender ignores it -
            // the key goes to Windows and behaves exactly as it always did. That covers the machine's own
            // keyboard, which is the whole point: without it, both volume keys mean Feed Hold while a job
            // runs, so reaching up to turn the music down holds the machine.
            if (!RemoteDevices.PressIsFromBoundDevice())
            {
                DebugLog.Write("remote", "shutter remote: not the bound device - ignored, passed to Windows");
                _last = DateTime.MinValue;
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // What it means is decided HERE, synchronously, because the answer also decides whether the key
            // is swallowed - and that has to be settled before returning from the hook. Only the action it
            // hands back is posted; nothing slow runs on the hook.
            // Asked in terms of PRIMARY, not volume up: which physical button sends which key is the
            // remote's business and was settled at binding, and nothing above this line should have to
            // know that the bigger button happens to send volume down.
            var resolve = Resolve;
            System.Action action = resolve == null ? null : resolve(RemoteDevices.IsPrimary(vk == VK_VOLUME_UP));

            if (action == null)
            {
                // A BOUND remote's buttons are ours outright, even when the press means nothing right now:
                // beep so the operator knows it was heard and ignored, and discard it. A pendant that
                // sometimes nudges the system volume instead is just a broken pendant, and over a height
                // map it walks the volume to one end or the other.
                //
                // Only when bound, though. With nothing bound we cannot tell this remote from the machine's
                // own keyboard, and swallowing then would take the keyboard's volume keys away from an
                // operator who never asked for that. The binding is what earns the right.
                if (RemoteDevices.HasBinding)
                {
                    _swallowed = true;   // so the matching key-up is swallowed too, not left orphaned
                    try { System.Media.SystemSounds.Beep.Play(); } catch { }
                    DebugLog.Write("remote", "shutter remote: nothing was waiting - beeped and discarded");
                    return new IntPtr(1);
                }

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
                _hookActedAt = RemoteDevices.ElapsedMs;   // so the raw-input path skips this same press
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
