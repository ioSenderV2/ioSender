/*
 * ControllerMapper.cs - part of CNC Core library for Grbl
 *
 * Maps Xbox controller buttons (from ControllerService) to named machine actions
 * and executes them. The button->action map is editable (Controller tab) and
 * dispatch is gated to safe controller/grbl states. Analog stick jogging is handled
 * separately.
 *
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CNC.Core
{
    public enum ControllerAction
    {
        None,
        CycleStart,
        FeedHold,
        Reset,
        Unlock,
        Home,
        SpindleStop,
        JogXPlus,
        JogXMinus,
        JogYPlus,
        JogYMinus,
        JogZPlus,
        JogZMinus,
        JogStepIncrease,
        JogStepDecrease
    }

    public class ControllerMapper
    {
        private readonly GrblViewModel grbl;
        private readonly ControllerService service;
        private readonly Dictionary<XInputButton, ControllerAction> map = new Dictionary<XInputButton, ControllerAction>();

        /// <summary>When false, controller input is ignored (e.g. while remapping in the editor).</summary>
        public bool Enabled { get; set; } = true;

        public ControllerService Service { get { return service; } }

        /// <summary>Buttons exposed for mapping in the Controller tab (analog sticks/triggers handled elsewhere).</summary>
        public static readonly XInputButton[] MappableButtons = new XInputButton[]
        {
            XInputButton.DPadUp, XInputButton.DPadDown, XInputButton.DPadLeft, XInputButton.DPadRight,
            XInputButton.A, XInputButton.B, XInputButton.X, XInputButton.Y,
            XInputButton.LeftShoulder, XInputButton.RightShoulder,
            XInputButton.Back, XInputButton.Start,
            XInputButton.LeftThumb, XInputButton.RightThumb
        };

        public ControllerMapper(GrblViewModel model, ControllerService controllerService)
        {
            grbl = model;
            service = controllerService;
            LoadDefaults();
            service.ButtonPressed += OnButtonPressed;
            service.Connected += (s, e) => service.Rumble(20000, 20000, 150);   // brief "found it" buzz
        }

        public ControllerAction GetAction(XInputButton button)
        {
            ControllerAction a;
            return map.TryGetValue(button, out a) ? a : ControllerAction.None;
        }

        public void SetAction(XInputButton button, ControllerAction action)
        {
            if (action == ControllerAction.None)
                map.Remove(button);
            else
                map[button] = action;
        }

        private static readonly Dictionary<XInputButton, ControllerAction> defaults = new Dictionary<XInputButton, ControllerAction>
        {
            { XInputButton.A, ControllerAction.CycleStart },
            { XInputButton.B, ControllerAction.FeedHold },
            { XInputButton.X, ControllerAction.SpindleStop },
            { XInputButton.Y, ControllerAction.Home },
            { XInputButton.Back, ControllerAction.Reset },
            { XInputButton.Start, ControllerAction.Unlock },
            { XInputButton.DPadRight, ControllerAction.JogXPlus },
            { XInputButton.DPadLeft, ControllerAction.JogXMinus },
            { XInputButton.DPadUp, ControllerAction.JogYPlus },
            { XInputButton.DPadDown, ControllerAction.JogYMinus },
            { XInputButton.LeftShoulder, ControllerAction.JogStepDecrease },
            { XInputButton.RightShoulder, ControllerAction.JogStepIncrease }
        };

        public static ControllerAction DefaultAction(XInputButton button)
        {
            ControllerAction a;
            return defaults.TryGetValue(button, out a) ? a : ControllerAction.None;
        }

        public void LoadDefaults()
        {
            map.Clear();
            foreach (var kv in defaults)
                map[kv.Key] = kv.Value;
        }

        private bool IsConnected
        {
            get { return Comms.com != null && grbl.GrblState.State != GrblStates.Unknown; }
        }

        private bool CanJog
        {
            get
            {
                var s = grbl.GrblState.State;
                return s == GrblStates.Idle || s == GrblStates.Jog || s == GrblStates.Tool;
            }
        }

        private void OnButtonPressed(object sender, ControllerButtonEventArgs e)
        {
            if (!Enabled || !IsConnected)
                return;

            Execute(GetAction(e.Button));
        }

        private void Execute(ControllerAction action)
        {
            switch (action)
            {
                case ControllerAction.CycleStart:
                    Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
                    break;
                case ControllerAction.FeedHold:
                    Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
                    break;
                case ControllerAction.Reset:
                    Comms.com.WriteByte(GrblConstants.CMD_RESET);
                    break;
                case ControllerAction.SpindleStop:
                    Comms.com.WriteByte(GrblConstants.CMD_SPINDLE_OVR_STOP);
                    break;
                case ControllerAction.Home:
                    Home();
                    break;
                case ControllerAction.Unlock:
                    grbl.ExecuteCommand(GrblConstants.CMD_UNLOCK);
                    break;
                case ControllerAction.JogXPlus: JogStep("X", 1); break;
                case ControllerAction.JogXMinus: JogStep("X", -1); break;
                case ControllerAction.JogYPlus: JogStep("Y", 1); break;
                case ControllerAction.JogYMinus: JogStep("Y", -1); break;
                case ControllerAction.JogZPlus: JogStep("Z", 1); break;
                case ControllerAction.JogZMinus: JogStep("Z", -1); break;
                case ControllerAction.JogStepIncrease: AdjustStep(10d); break;
                case ControllerAction.JogStepDecrease: AdjustStep(0.1d); break;
            }
        }

        private void Home()
        {
            var s = grbl.GrblState.State;
            if (s == GrblStates.Idle || s == GrblStates.Alarm)
                grbl.ExecuteCommand(GrblConstants.CMD_HOMING);
        }

        private void AdjustStep(double factor)
        {
            double step = grbl.JogStep <= 0d ? 1d : grbl.JogStep * factor;
            grbl.JogStep = Math.Max(0.001d, Math.Min(1000d, step));
        }

        private void JogStep(string axis, int dir)
        {
            if (!CanJog)
                return;

            double dist = grbl.JogStep;
            if (dist <= 0d)
                dist = 1d;
            if (dir < 0)
                dist = -dist;

            double feed = grbl.Keyboard != null ? grbl.Keyboard.JogFeedrates[(int)KeypressHandler.JogMode.Step] : 0d;
            if (feed <= 0d)
                feed = 500d;

            // Use the same path the on-screen jog buttons use (GrblViewModel.ExecuteCommand), not a raw
            // stream write - otherwise the jog line never reaches the controller.
            string cmd = string.Format(CultureInfo.InvariantCulture, "$J=G91G21{0}{1}F{2}", axis, dist, Math.Ceiling(feed));
            grbl.ExecuteCommand(cmd);
        }
    }
}
