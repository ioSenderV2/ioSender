/*
 * ControllerService.cs - part of CNC Core library for Grbl
 *
 * Polls an Xbox controller via XInput on the UI thread and surfaces button
 * edge events, connect/disconnect, analog state and rumble. The mapping layer
 * subscribes to these; this class contains no machine logic of its own.
 *
 */

using System;
using System.Windows.Threading;

namespace CNC.Core
{
    public class ControllerButtonEventArgs : EventArgs
    {
        public int Controller;
        public XInputButton Button;
    }

    public class ControllerService
    {
        private readonly DispatcherTimer timer;
        private int activeIndex = -1;   // XInput slot currently in use, -1 while searching
        private bool connected = false;
        private ushort prevButtons = 0;
        private XInputGamepad pad;
        private DispatcherTimer rumbleTimer;

        public event EventHandler<ControllerButtonEventArgs> ButtonPressed;
        public event EventHandler<ControllerButtonEventArgs> ButtonReleased;
        public event EventHandler Connected;
        public event EventHandler Disconnected;
        public event EventHandler Polled;   // fired each tick after state refresh - used by the analog jog logic

        public bool IsConnected { get { return connected; } }
        public bool IsRunning { get { return timer.IsEnabled; } }
        public int ControllerIndex { get { return activeIndex; } }

        /// <summary>Latest gamepad snapshot (valid while IsConnected).</summary>
        public XInputGamepad State { get { return pad; } }

        public ControllerService(int pollHz = 60)
        {
            timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(15, pollHz))
            };
            timer.Tick += Poll;
        }

        public void Start()
        {
            if (!timer.IsEnabled)
                timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            StopRumble();
            if (connected)
            {
                connected = false;
                activeIndex = -1;
                prevButtons = 0;
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private static readonly XInputButton[] AllButtons = new XInputButton[]
        {
            XInputButton.DPadUp, XInputButton.DPadDown, XInputButton.DPadLeft, XInputButton.DPadRight,
            XInputButton.Start, XInputButton.Back, XInputButton.LeftThumb, XInputButton.RightThumb,
            XInputButton.LeftShoulder, XInputButton.RightShoulder,
            XInputButton.A, XInputButton.B, XInputButton.X, XInputButton.Y
        };

        private void Poll(object sender, EventArgs e)
        {
            // While disconnected, scan all XInput slots for the first connected controller.
            if (activeIndex < 0)
            {
                for (int i = 0; i < XInput.MaxControllers; i++)
                {
                    XInputState s;
                    if (XInput.GetState(i, out s))
                    {
                        activeIndex = i;
                        connected = true;
                        pad = s.Gamepad;
                        prevButtons = s.Gamepad.wButtons;   // adopt held buttons so they don't fire on connect
                        Connected?.Invoke(this, EventArgs.Empty);
                        Polled?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }
                return;
            }

            XInputState state;
            if (!XInput.GetState(activeIndex, out state))
            {
                activeIndex = -1;
                connected = false;
                prevButtons = 0;
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            pad = state.Gamepad;
            ushort cur = state.Gamepad.wButtons;

            ushort changed = (ushort)(cur ^ prevButtons);
            if (changed != 0)
            {
                foreach (var b in AllButtons)
                {
                    ushort bit = (ushort)b;
                    if ((changed & bit) == 0)
                        continue;

                    var args = new ControllerButtonEventArgs { Controller = activeIndex, Button = b };
                    if ((cur & bit) != 0)
                        ButtonPressed?.Invoke(this, args);
                    else
                        ButtonReleased?.Invoke(this, args);
                }
                prevButtons = cur;
            }

            Polled?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Pulse the rumble motors (0..65535) for the given duration.</summary>
        public void Rumble(ushort leftMotor, ushort rightMotor, int milliseconds = 200)
        {
            if (!connected || activeIndex < 0)
                return;

            XInput.SetVibration(activeIndex, leftMotor, rightMotor);

            if (rumbleTimer == null)
            {
                rumbleTimer = new DispatcherTimer(DispatcherPriority.Background);
                rumbleTimer.Tick += (s, e) => StopRumble();
            }
            rumbleTimer.Stop();
            rumbleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
            rumbleTimer.Start();
        }

        private void StopRumble()
        {
            if (rumbleTimer != null)
                rumbleTimer.Stop();
            if (activeIndex >= 0)
                XInput.SetVibration(activeIndex, 0, 0);
        }

        // ---- analog helpers: normalize to -1..1 (sticks) / 0..1 (triggers) past a deadzone ----

        public static double Normalize(short value, short deadzone)
        {
            int v = value;
            int dz = Math.Abs((int)deadzone);
            if (Math.Abs(v) <= dz)
                return 0d;

            double mag = (Math.Abs(v) - dz) / (double)(XInput.ThumbMax - dz);
            if (mag > 1d)
                mag = 1d;

            return Math.Sign(v) * mag;
        }

        public static double NormalizeTrigger(byte value, byte threshold)
        {
            if (value <= threshold)
                return 0d;

            double mag = (value - threshold) / (double)(XInput.TriggerMax - threshold);
            return mag > 1d ? 1d : mag;
        }
    }
}
