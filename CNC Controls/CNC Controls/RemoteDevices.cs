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
 * ---- The measurement this rests on ----
 *
 * The hook must answer swallow-or-pass SYNCHRONOUSLY, so per-device filtering is only possible if the
 * device is already known by the time it runs. Three outcomes were plausible from the documentation, and
 * one of them killed the design outright: a WH_KEYBOARD_LL hook sits upstream of raw input delivery, so a
 * swallowed key might produce no WM_INPUT at all - leaving the device of every press we ACT on as
 * precisely the one we could never learn.
 *
 * Measured on the operator's machine 2026-09-21 rather than argued about, and it came out favourable:
 *
 *     t=230644.285ms   RAWINPUT  hid  10 bytes  device=\?\HID#{00001812-...}   <- the remote
 *     t=230644.925ms   HOOK      vk=0xAE down
 *
 * The device event lands 0.64 ms BEFORE the hook. So the hook can simply ask what arrived last, and the
 * swallow question never arises - the HID event precedes the hook, leaving nothing for it to eat.
 *
 * Two things that measurement also settled, both of which would have broken a design built on assumption:
 *
 *   - The KEYBOARD event Windows synthesizes for a media key reports a device handle that cannot be
 *     named (it is not a real device), so it logs as "?". Identity must come from the CONSUMER-HID event.
 *     Registering only the keyboard page would have produced a device path of "?" and looked like a dead
 *     end rather than a wrong choice of usage page.
 *   - The remote and the machine's own keyboard are wholly distinct paths - a BLE HID device against
 *     ACPI#LEN0071 - so telling them apart needs no heuristics.
 *
 * Re-run it any time with -debuglog=remote: every raw-input event is still logged with its device and a
 * timestamp from the same clock the hook stamps against.
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

        // The friendly name. GetRawInputDeviceInfo gives the interface PATH - a wall of braces and hex
        // that means nothing to anyone - while Windows' own Bluetooth list shows "PICO V0.1:079B5C11FFF".
        // That string is the HID PRODUCT STRING, and the raw-input device name IS an interface path, so
        // it can be opened and asked.
        //
        // Opened with dwDesiredAccess ZERO on purpose. A HID keyboard is held by the class driver and
        // cannot be opened for read or write, but a zero-access handle is still good for metadata - which
        // is the documented way to get at product and manufacturer strings for a device in use.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
                                                uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(IntPtr handle, StringBuilder buffer, int length);

        private const uint FILE_SHARE_READ_WRITE = 3;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        /// <summary>
        /// The name Windows shows for this device, or null when it cannot be read - which is an ordinary
        /// outcome, not a failure: the remote may be asleep, unpaired or simply not answering, and the
        /// caller falls back to the path. Never throws.
        /// </summary>
        public static string ProductName(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            IntPtr h = INVALID_HANDLE;
            try
            {
                h = CreateFile(devicePath, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID_HANDLE)
                    return null;

                var sb = new StringBuilder(256);
                if (!HidD_GetProductString(h, sb, sb.Capacity * 2))
                    return null;

                string name = sb.ToString().Trim();
                return name.Length == 0 ? null : name;
            }
            catch
            {
                return null;   // a cosmetic label must never take the app down
            }
            finally
            {
                if (h != INVALID_HANDLE && h != IntPtr.Zero)
                    CloseHandle(h);
            }
        }

        // RAWINPUTHEADER is dwType + dwSize (4 each) then hDevice + wParam (pointer-sized), so its length
        // differs between 32- and 64-bit. Computed rather than declared, because getting it wrong reads
        // the keyboard payload from the wrong offset and produces plausible nonsense rather than a crash.
        private static int HeaderSize { get { return 8 + 2 * IntPtr.Size; } }

        private static HwndSource source;
        private static readonly Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();

        /// <summary>True while raw input is being received.</summary>
        public static bool Watching { get { return source != null; } }

        // ---- which device sent the last press -----------------------------------------------------
        //
        // Read by ShutterRemote's hook callback to decide whether a press is THE remote's. Both this and
        // the hook run on the UI thread (a low-level hook is called on the thread that installed it), so
        // these are plain fields rather than anything synchronised - and they must stay that way, because
        // the hook has microseconds to answer.
        //
        // Only the consumer-HID event is recorded. The keyboard event Windows synthesizes for a media key
        // reports a device handle that GetRawInputDeviceInfo cannot name - it is not a real device - so
        // binding against it would bind to "?" and match everything. Measured 2026-09-21.
        private static string lastDevice;
        private static double lastDeviceAt = double.NegativeInfinity;

        // The raw-input event for a press lands BEFORE the hook sees it - measured at 0.64 ms on the
        // operator's own machine, which is the fact this whole design rests on (see the header). 250 ms is
        // enormous next to that: it is sized to be unmistakably longer than the gap, not tuned to it,
        // because being wrong in the other direction means the remote silently stops working.
        private const double MatchWindowMs = 250d;

        /// <summary>
        /// Should this press be treated as coming from the bound remote?
        ///
        /// FAILS CLOSED, unlike most guards in this codebase, and deliberately: a device that is not the
        /// bound one is not this operator's pendant, and nothing it sends should move a machine. With
        /// NOTHING bound that means every device - the remote does nothing at all until one press has
        /// bound it, which is one press, and the alternative is a stray volume key from any keyboard in
        /// the building meaning Feed Hold.
        ///
        /// It cannot strand anyone: enabling with nothing bound arms the bind (RemoteActions.Sync), so
        /// the very first press is the one that binds, and every press after it works.
        /// </summary>
        public static bool PressIsFromBoundDevice()
        {
            string bound = AppConfig.Settings?.Base?.ShutterRemoteDevice;
            if (string.IsNullOrEmpty(bound))
                return false;

            if (lastDevice == null || ElapsedMs - lastDeviceAt > MatchWindowMs)
                return false;   // bound to something, and nothing recent says it was that

            return string.Equals(lastDevice, bound, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the press being handled right now is the one that just bound the device. The hook
        /// consumes it without acting: the operator pressed the button to say "this is my remote", not to
        /// answer whatever happened to be on screen at the time.
        /// </summary>
        public static bool PressJustBound()
        {
            return ElapsedMs - boundAt <= MatchWindowMs;
        }

        private static double boundAt = double.NegativeInfinity;

        /// <summary>
        /// Arm a one-shot bind: the next button press on ANY remote becomes the bound device. Ticking the
        /// enable calls this, so binding costs the operator one press and no dialog.
        /// </summary>
        public static void ArmBinding()
        {
            armed = true;
            DebugLog.Write("remote", "binding ARMED - press a button on the remote to bind it");
        }

        /// <summary>Forget the bound device, so the next arm binds whatever is pressed.</summary>
        public static void ClearBinding()
        {
            armed = false;
            lastDevice = null;
            if (AppConfig.Settings?.Base != null)
            {
                AppConfig.Settings.Base.ShutterRemoteDevice = string.Empty;
                AppConfig.Settings.Base.ShutterRemoteName = string.Empty;
            }
            DebugLog.Write("remote", "binding cleared");
        }

        /// <summary>True while waiting for the press that will bind.</summary>
        public static bool Binding { get { return armed; } }

        /// <summary>
        /// True when a specific remote is bound. Once one is, ITS buttons belong to ioSender outright -
        /// a press that means nothing here is beeped and discarded rather than passed to the volume
        /// control, because a dedicated pendant that sometimes changes the volume is just a broken
        /// pendant. Unbound, that would be stealing the machine keyboard's volume keys, so it is exactly
        /// the binding that earns the right.
        /// </summary>
        public static bool HasBinding
        {
            get { return !string.IsNullOrEmpty(AppConfig.Settings?.Base?.ShutterRemoteDevice); }
        }

        /// <summary>Raised on the UI thread when a device is bound, with its path.</summary>
        public static event System.Action<string> BoundTo;

        private static bool armed;

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
                    string name = NameOf(device);

                    // This is the event that carries a usable identity, and it arrives before the hook -
                    // so recording it here is what lets the hook, microseconds later, know what pressed.
                    lastDevice = name;
                    lastDeviceAt = at;

                    DebugLog.Write("remote", string.Format(CultureInfo.InvariantCulture,
                        "RAWINPUT  t={0:F3}ms  hid  {1} bytes  device={2}",
                        at, size - HeaderSize, name));

                    if (armed && !string.IsNullOrEmpty(name) && name != "?")
                    {
                        armed = false;
                        boundAt = at;

                        // Resolved and STORED now, not looked up when the settings page happens to be
                        // shown: the name can only be read while the device is present, and the page is
                        // most often opened when it is not.
                        string friendly = ProductName(name);

                        if (AppConfig.Settings?.Base != null)
                        {
                            AppConfig.Settings.Base.ShutterRemoteDevice = name;
                            AppConfig.Settings.Base.ShutterRemoteName = friendly ?? string.Empty;
                        }
                        DebugLog.Write("remote", "BOUND to " + (friendly ?? "(unnamed)") + "  " + name);
                        var handler = BoundTo;
                        if (handler != null)
                            handler(name);
                    }
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
