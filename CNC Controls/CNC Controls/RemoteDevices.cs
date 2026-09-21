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

        // ---- the device's friendly name, by two routes ------------------------------------------------
        //
        // Route 1 is the HID product string. It is the obvious one and it is what a USB HID device
        // answers with - but a BLUETOOTH LE HID child frequently has none, which is exactly what happened
        // here: the first build logged "(unnamed)" for the operator's PICO, 2026-09-21.
        //
        // Route 2 is the one that matches what Windows actually shows. "PICO V0.1:079B5C11FFF" in the
        // Bluetooth list is the BLE device's OWN name, and it lives on the PARENT device node, not on the
        // HID collection underneath it. So: turn the interface path into a device instance ID, locate the
        // node, walk up one level, and read the parent's friendly name (falling back to its description).
        //
        // Both are tried and WHICH ONE ANSWERED IS LOGGED, because picking one on reasoning alone is how
        // the first attempt got it wrong.

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property,
                                                                    out uint regDataType, StringBuilder buffer,
                                                                    ref uint length, uint flags);

        private const int CR_SUCCESS = 0;
        private const uint CM_DRP_DEVICEDESC = 1;
        private const uint CM_DRP_FRIENDLYNAME = 13;

        /// <summary>
        /// The name Windows shows for this device, or null when it cannot be read - an ordinary outcome,
        /// not a failure: the remote may be asleep, unpaired or simply not answering, and the caller falls
        /// back to the path. Never throws.
        /// </summary>
        public static string ProductName(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            string name = HidProductString(devicePath);
            if (!string.IsNullOrEmpty(name))
            {
                DebugLog.Write("remote", "name: HID product string = " + name);
                return name;
            }

            name = ParentFriendlyName(devicePath);
            if (!string.IsNullOrEmpty(name))
            {
                DebugLog.Write("remote", "name: parent device node = " + name);
                return name;
            }

            DebugLog.Write("remote", "name: neither the HID product string nor the parent node gave one");
            return null;
        }

        private static string HidProductString(string devicePath)
        {
            IntPtr h = INVALID_HANDLE;
            try
            {
                h = CreateFile(devicePath, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID_HANDLE)
                {
                    DebugLog.Write("remote", "name: CreateFile on the device failed, error " + Marshal.GetLastWin32Error());
                    return null;
                }

                var sb = new StringBuilder(256);
                if (!HidD_GetProductString(h, sb, sb.Capacity * 2))
                {
                    DebugLog.Write("remote", "name: HidD_GetProductString said no");
                    return null;
                }

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                DebugLog.Write("remote", "name: " + ex.Message);
                return null;
            }
            finally
            {
                if (h != INVALID_HANDLE && h != IntPtr.Zero)
                    CloseHandle(h);
            }
        }

        // WALK THE WHOLE ANCESTRY, not just one level. The tree under a Bluetooth LE remote is three deep:
        //
        //     HID\{...}                       the HID collection - this is what raw input names
        //       parent  BTHLEDEVICE\{...}      "Bluetooth Low Energy GATT compliant HID device"
        //         parent  BTHLE\Dev_<addr>     "PICO V0.1:079B5C11FFF"   <- the one a human recognises
        //
        // Taking the immediate parent got the middle one, and since that node HAS a description it looked
        // like a success: "Bound to Bluetooth Low Energy GATT compliant HID device". Right mechanism, one
        // level short. A description is a device CLASS talking about itself; a friendly name is the thing
        // the operator named or the device calls itself, so friendly names win at every level and a
        // description is only a last resort.
        private const int MaxAncestry = 6;

        private static string ParentFriendlyName(string devicePath)
        {
            try
            {
                string id = InstanceIdFrom(devicePath);
                if (id == null)
                    return null;

                uint node;
                if (CM_Locate_DevNodeW(out node, id, 0) != CR_SUCCESS)
                {
                    DebugLog.Write("remote", "name: could not locate a device node for " + id);
                    return null;
                }

                string firstDescription = null;

                for (int level = 0; level < MaxAncestry; level++)
                {
                    uint parent;
                    if (CM_Get_Parent(out parent, node, 0) != CR_SUCCESS)
                        break;   // reached the root
                    node = parent;

                    string friendly = NodeProperty(node, CM_DRP_FRIENDLYNAME);
                    string description = NodeProperty(node, CM_DRP_DEVICEDESC);

                    // Logged per level: if this ever lands on the wrong node again, the chain is right
                    // here rather than something to go and re-derive.
                    DebugLog.Write("remote", string.Format(CultureInfo.InvariantCulture,
                        "name: ancestor {0} friendly=[{1}] desc=[{2}]",
                        level + 1, friendly ?? "", description ?? ""));

                    if (!string.IsNullOrEmpty(friendly))
                        return friendly;

                    if (firstDescription == null && !string.IsNullOrEmpty(description))
                        firstDescription = description;
                }

                return firstDescription;
            }
            catch (Exception ex)
            {
                DebugLog.Write("remote", "name: " + ex.Message);
                return null;
            }
        }

        private static string NodeProperty(uint devInst, uint property)
        {
            uint type, len = 512;
            var sb = new StringBuilder((int)len);
            if (CM_Get_DevNode_Registry_PropertyW(devInst, property, out type, sb, ref len, 0) != CR_SUCCESS)
                return null;
            string s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// Interface path to device instance ID: drop the leading prefix, drop the trailing
        /// interface-class GUID, and the remaining '#' separators become backslashes.
        /// </summary>
        private static string InstanceIdFrom(string devicePath)
        {
            string p = devicePath;
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
                p = p.Substring(4);

            int guid = p.LastIndexOf("#{", StringComparison.Ordinal);
            if (guid > 0)
                p = p.Substring(0, guid);

            p = p.Replace('#', '\\');
            return p.Length == 0 ? null : p;
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

        /// <summary>
        /// The binding press also settles WHICH BUTTON IS WHICH. The two are not labelled up and down -
        /// on the operator's PICO the iOS button is the bigger one on top, so it reads as "up" while
        /// sending VOLUME DOWN - so the button used to bind becomes the primary one and the mapping is
        /// derived from that rather than assumed. Called by the hook, which is the only thing that knows
        /// the key.
        /// </summary>
        public static void SetPrimaryFromBindPress(bool wasVolumeUp)
        {
            if (AppConfig.Settings?.Base == null)
                return;

            AppConfig.Settings.Base.ShutterRemoteSwapButtons = !wasVolumeUp;
            DebugLog.Write("remote", "primary button = the one just pressed (" +
                                      (wasVolumeUp ? "volume up" : "volume down") + ")");
        }

        /// <summary>
        /// Is this key the primary button? Everything above the hook is written in terms of primary and
        /// second, never up and down, because up and down are not written on the remote.
        /// </summary>
        public static bool IsPrimary(bool volumeUp)
        {
            bool swapped = AppConfig.Settings?.Base != null && AppConfig.Settings.Base.ShutterRemoteSwapButtons;
            return volumeUp != swapped;
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
