/*
 * ControllerMapper.cs - part of CNC Core library for Grbl
 *
 * Translates Xbox controller button events (from ControllerService) into machine
 * actions: realtime commands, homing/unlock and step jogging. Dispatch is gated to
 * safe controller/grbl states. Analog stick jogging is handled separately.
 *
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CNC.Core
{
    public class ControllerMapper
    {
        private readonly GrblViewModel grbl;
        private readonly ControllerService service;
        private Dictionary<XInputButton, System.Action> map;

        /// <summary>When false, controller input is ignored (e.g. while remapping in the editor).</summary>
        public bool Enabled { get; set; } = true;

        public ControllerService Service { get { return service; } }

        public ControllerMapper(GrblViewModel model, ControllerService controllerService)
        {
            grbl = model;
            service = controllerService;
            BuildDefaultMap();
            service.ButtonPressed += OnButtonPressed;
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

            System.Action action;
            if (map.TryGetValue(e.Button, out action))
                action();
        }

        private void BuildDefaultMap()
        {
            map = new Dictionary<XInputButton, System.Action>
            {
                { XInputButton.A, CycleStart },
                { XInputButton.B, FeedHold },
                { XInputButton.X, SpindleStop },
                { XInputButton.Y, Home },
                { XInputButton.Back, Reset },
                { XInputButton.Start, Unlock },
                { XInputButton.DPadRight, () => JogStep("X", 1) },
                { XInputButton.DPadLeft,  () => JogStep("X", -1) },
                { XInputButton.DPadUp,    () => JogStep("Y", 1) },
                { XInputButton.DPadDown,  () => JogStep("Y", -1) }
            };
        }

        private void CycleStart()
        {
            Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
        }

        private void FeedHold()
        {
            Comms.com.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
        }

        private void Reset()
        {
            Comms.com.WriteByte(GrblConstants.CMD_RESET);
        }

        private void SpindleStop()
        {
            Comms.com.WriteByte(GrblConstants.CMD_SPINDLE_OVR_STOP);
        }

        private void Home()
        {
            var s = grbl.GrblState.State;
            if (s == GrblStates.Idle || s == GrblStates.Alarm)
                grbl.ExecuteCommand(GrblConstants.CMD_HOMING);
        }

        private void Unlock()
        {
            grbl.ExecuteCommand(GrblConstants.CMD_UNLOCK);
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

            string cmd = string.Format(CultureInfo.InvariantCulture, "$J=G91G21{0}{1}F{2}", axis, dist, feed);
            Comms.com.WriteCommand(cmd);
        }
    }
}
