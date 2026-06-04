/*
 * XInput.cs - part of CNC Core library for Grbl
 *
 * Minimal P/Invoke wrapper around the Windows XInput API for Xbox controllers.
 * No third-party dependency - calls xinput1_4.dll directly (with a 9_1_0 fallback).
 *
 */

using System;
using System.Runtime.InteropServices;

namespace CNC.Core
{
    [Flags]
    public enum XInputButton : ushort
    {
        None = 0x0000,
        DPadUp = 0x0001,
        DPadDown = 0x0002,
        DPadLeft = 0x0004,
        DPadRight = 0x0008,
        Start = 0x0010,
        Back = 0x0020,
        LeftThumb = 0x0040,
        RightThumb = 0x0080,
        LeftShoulder = 0x0100,
        RightShoulder = 0x0200,
        A = 0x1000,
        B = 0x2000,
        X = 0x4000,
        Y = 0x8000
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputVibration
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    /// <summary>Thin managed wrapper over XInput. All methods are safe to call when no controller is present.</summary>
    public static class XInput
    {
        public const int MaxControllers = 4;             // XInput supports up to 4
        public const short ThumbMax = 32767;
        public const byte TriggerMax = 255;

        private const uint ERROR_SUCCESS = 0;

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState14(uint dwUserIndex, out XInputState pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, out XInputState pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState14(uint dwUserIndex, ref XInputVibration pVibration);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState910(uint dwUserIndex, ref XInputVibration pVibration);

        // xinput1_4 ships with Windows 8+. Probe once and fall back to the 9_1_0 redist if absent.
        private static bool? use14 = null;
        private static bool Use14
        {
            get
            {
                if (use14 == null)
                {
                    try
                    {
                        XInputState s;
                        XInputGetState14(0, out s);
                        use14 = true;
                    }
                    catch (DllNotFoundException)
                    {
                        use14 = false;
                    }
                }
                return use14.Value;
            }
        }

        /// <summary>True if XInput is available at all on this machine.</summary>
        public static bool IsAvailable
        {
            get
            {
                try { return Use14 || Probe910(); }
                catch { return false; }
            }
        }

        private static bool Probe910()
        {
            try { XInputState s; XInputGetState910(0, out s); return true; }
            catch (DllNotFoundException) { return false; }
        }

        /// <summary>Reads controller <paramref name="index"/> (0-3). Returns false if not connected.</summary>
        public static bool GetState(int index, out XInputState state)
        {
            state = default(XInputState);

            if (index < 0 || index >= MaxControllers)
                return false;

            try
            {
                uint result = Use14 ? XInputGetState14((uint)index, out state)
                                    : XInputGetState910((uint)index, out state);
                return result == ERROR_SUCCESS;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        /// <summary>Sets the rumble motors (0..65535). No-op if the controller is gone.</summary>
        public static void SetVibration(int index, ushort leftMotor, ushort rightMotor)
        {
            if (index < 0 || index >= MaxControllers)
                return;

            var v = new XInputVibration { wLeftMotorSpeed = leftMotor, wRightMotorSpeed = rightMotor };

            try
            {
                if (Use14)
                    XInputSetState14((uint)index, ref v);
                else
                    XInputSetState910((uint)index, ref v);
            }
            catch (DllNotFoundException)
            {
            }
        }
    }
}
