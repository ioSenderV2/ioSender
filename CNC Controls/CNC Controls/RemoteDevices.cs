/*
 * RemoteDevices.cs - part of CNC Controls library
 *
 * WHICH physical device sent a key. ShutterRemote is the ear and RemoteActions the judgement; neither
 * can tell one keyboard from another, because a low-level keyboard hook is not told - KBDLLHOOKSTRUCT
 * carries a virtual key, a scan code and flags, and nothing whatsoever about where the press came from.
 *
 * That is a real wart, not a theoretical one: while a job is running BOTH volume keys mean Feed Hold, so
 * reaching up to turn the music down on the laptop holds the machine. Binding to one remote would fix it.
 *
 * ---- Why this file is, for now, ONLY AN INSTRUMENT ----
 *
 * Raw Input (WM_INPUT) does report the device, so identification is possible. What is NOT known - and is
 * the whole question - is whether a raw-input event still arrives for a key the HOOK SWALLOWED, and if so
 * whether it arrives before or after the hook callback runs.
 *
 * It matters because the hook has to answer swallow-or-pass SYNCHRONOUSLY. If WM_INPUT for a press lands
 * after the hook has already returned, the hook can never know the device in time, and per-device
 * filtering degrades to a "last device seen within N ms" heuristic. Worse: a WH_KEYBOARD_LL hook sits
 * upstream of raw input delivery, so a swallowed key may produce no WM_INPUT at all - in which case the
 * device of every press we ACT on is precisely the one we can never learn, and the design is dead.
 *
 * All three outcomes are plausible from the documentation. So this measures first and decides after:
 * every raw-input keystroke is logged with its device path and a timestamp from the SAME clock the hook
 * logs against, and the two sequences are read off against each other afterwards. Nothing here changes
 * what the remote does.
 *
 * Read the result with -debuglog=remote and press each device in turn - the remote, then the keyboard's
 * own volume key, first with nothing waiting (hook passes the key through) and then with a prompt on
 * screen (hook swallows it). The second case is the one that decides it.
 *
 * ---- Registration notes ----
 *
 * Registered with RIDEV_INPUTSINK so events arrive while the app is not focused, which is the entire
 * point of a remote used with hands on the machine. Both the keyboard usage page (01/06) and consumer
 * control (0C/01) are taken: a media key is a CONSUMER usage, and whether Windows presents it as one or
 * synthesizes a keyboard VK for it depends on the device, so listening to only one risks measuring
 * silence and concluding the wrong thing.
 *
 * Deliberately NOT using RIDEV_NOLEGACY. It suppresses legacy window messages for a usage page, which is
 * both more than this needs and no help at all for the thing people want suppressed - volume keys are
 * handled by the shell and the audio endpoint on a path that does not go through our window.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using CNC.Core;

namespace CNC.Controls
{
    public static class RemoteDevices
    {
        // ---- the shared clock -------------------------------------------------------------------------
        // The hook logs against this too. Comparing two DateTime.Now strings at millisecond resolution
        // cannot order two events a few hundred microseconds apart, which is exactly the gap in question.
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        public static double ElapsedMs { get { return clock.Elapsed.TotalMilliseconds; } }

        private const int WM_INPUT = 0x00FF;
        private const int RID_INPUT = 0x10000003;
        private const int RIDI_DEVICENAME = 0x20000007;
        private const int RIDEV_INPUTSINK = 0x00000100;
        private const int RIM_TYPEKEYBOARD = 1;
        private const int RIM_TYPEHID = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public int Flags;
            public IntPtr Target;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, int count, int size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetRawInputData(IntPtr hRawInput, int command, IntPtr data, ref int size, int headerSize);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetRawInputDeviceInfo(IntPtr hDevice, int command, StringBuilder data, ref int size);

        // RAWINPUTHEADER is dwType + dwSize (4 each) then hDevice + wParam (pointer-sized), so its length
        // differs between 32- and 64-bit. Computed rather than declared, because getting it wrong reads
        // the keyboard payload from the wrong offset and produces plausible nonsense rather than a crash.
        private static int HeaderSize { get { return 8 + 2 * IntPtr.Size; } }

        private static HwndSource source;
        private static readonly Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();

        /// <summary>True while raw input is being received.</summary>
        public static bool Watching { get { return source != null; } }

        /// <summary>
        /// Begin watching. Idempotent. Silent no-op when there is no main window handle yet - the caller
        /// asserts the state it wants and this makes it so if it can, exactly as ShutterRemote.Start does.
        /// </summary>
        public static void Start()
        {
            if (source != null)
                return;

            var window = Application.Current?.MainWindow;
            if (window == null)
                return;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                // No HWND yet (startup). Come back when there is one rather than failing silently forever.
                window.SourceInitialized += OnSourceInitialized;
                return;
            }

            Attach(handle);
        }

        private static void OnSourceInitialized(object sender, EventArgs e)
        {
            if (sender is Window w)
            {
                w.SourceInitialized -= OnSourceInitialized;
                Attach(new WindowInteropHelper(w).Handle);
            }
        }

        private static void Attach(IntPtr handle)
        {
            if (handle == IntPtr.Zero || source != null)
                return;

            source = HwndSource.FromHwnd(handle);
            if (source == null)
                return;

            source.AddHook(WndProc);

            var devices = new[]
            {
                new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Target = handle },
                new RAWINPUTDEVICE { UsagePage = 0x0C, Usage = 0x01, Flags = RIDEV_INPUTSINK, Target = handle }
            };

            bool ok = RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf(typeof(RAWINPUTDEVICE)));

            DebugLog.Write("remote", ok
                ? "device watch: registered for raw input (keyboard 01/06 + consumer 0C/01, INPUTSINK)"
                : "device watch: RegisterRawInputDevices FAILED, error " + Marshal.GetLastWin32Error());

            if (!ok)
                Stop();
        }

        /// <summary>Stop watching. Safe to call when not started.</summary>
        public static void Stop()
        {
            if (source == null)
                return;

            source.RemoveHook(WndProc);
            source = null;
            names.Clear();
            DebugLog.Write("remote", "device watch: stopped");
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_INPUT)
            {
                // Stamped FIRST, before any of the work below, so the number compared against the hook's
                // is when the message arrived rather than how long this took to decode it.
                double at = ElapsedMs;
                try { Report(lParam, at); }
                catch (Exception ex) { DebugLog.Write("remote", "device watch: " + ex.Message); }
            }

            return IntPtr.Zero;   // never handled - this observes, it does not consume
        }

        private static void Report(IntPtr hRawInput, double at)
        {
            int size = 0;
            if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, HeaderSize) != 0 || size <= 0)
                return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, HeaderSize) != size)
                    return;

                int type = Marshal.ReadInt32(buffer, 0);
                IntPtr device = Marshal.ReadIntPtr(buffer, 8);

                if (type == RIM_TYPEKEYBOARD)
                {
                    // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey (2 bytes each), then Message (4).
                    ushort flags = (ushort)Marshal.ReadInt16(buffer, HeaderSize + 2);
                    ushort vkey = (ushort)Marshal.ReadInt16(buffer, HeaderSize + 6);
                    bool up = (flags & 0x01) != 0;

                    DebugLog.Write("remote", string.Format(CultureInfo.InvariantCulture,
                        "RAWINPUT  t={0:F3}ms  keyboard  vk=0x{1:X2} {2}  device={3}",
                        at, vkey, up ? "up" : "down", NameOf(device)));
                }
                else if (type == RIM_TYPEHID)
                {
                    DebugLog.Write("remote", string.Format(CultureInfo.InvariantCulture,
                        "RAWINPUT  t={0:F3}ms  hid  {1} bytes  device={2}",
                        at, size - HeaderSize, NameOf(device)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Cached: the path never changes for a handle, and resolving it per keystroke would put a P/Invoke
        // that allocates on the path whose timing is being measured.
        private static string NameOf(IntPtr device)
        {
            string name;
            if (names.TryGetValue(device, out name))
                return name;

            int size = 0;
            name = "?";
            if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref size) == 0 && size > 0)
            {
                var sb = new StringBuilder(size + 1);
                if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, sb, ref size) > 0)
                    name = sb.ToString();
            }

            names[device] = name;
            return name;
        }
    }
}
