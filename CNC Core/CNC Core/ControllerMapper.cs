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
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace CNC.Core
{
    [XmlType("ControllerMapping")]
    public class ControllerMapEntry
    {
        public XInputButton Button;
        public ControllerAction Action;
    }

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
            service.Polled += OnPolled;   // analog stick / trigger jogging
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

        // ---- persistence (ControllerMap.xml alongside the KeyMap files) -------------------------

        private bool mapLoaded = false;
        private static string MapPath { get { return Resources.ConfigPath + "ControllerMap.xml"; } }

        /// <summary>Load the saved map once (config path isn't available when the mapper is constructed).</summary>
        public void EnsureLoaded()
        {
            if (mapLoaded)
                return;
            mapLoaded = true;
            LoadMap();
        }

        public bool LoadMap()
        {
            mapLoaded = true;

            try
            {
                if (!File.Exists(MapPath))
                    return false;

                var xs = new XmlSerializer(typeof(List<ControllerMapEntry>), new XmlRootAttribute("ControllerMap"));
                using (var reader = new StreamReader(MapPath))
                {
                    var list = (List<ControllerMapEntry>)xs.Deserialize(reader);
                    map.Clear();
                    foreach (var e in list)
                        if (e.Action != ControllerAction.None)
                            map[e.Button] = e.Action;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool SaveMap()
        {
            try
            {
                var list = map.Select(kv => new ControllerMapEntry { Button = kv.Key, Action = kv.Value }).ToList();
                var xs = new XmlSerializer(typeof(List<ControllerMapEntry>), new XmlRootAttribute("ControllerMap"));
                using (var fs = new FileStream(MapPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    xs.Serialize(fs, list);
                return true;
            }
            catch
            {
                return false;
            }
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
            EnsureLoaded();

            if (!Enabled)
                return;   // editor is open - intentionally silent

            ControllerAction action = GetAction(e.Button);
            if (action == ControllerAction.None)
                return;

            if (!IsConnected)
            {
                grbl.Message = "Controller: " + e.Button + " ignored - no controller-board connection.";
                return;
            }

            Execute(action);
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
            {
                grbl.Message = string.Format("Controller jog ignored - state is {0} (needs Idle/Jog).", grbl.GrblState.State);
                return;
            }

            // Mirror the on-screen jog panel's distance/feed when available.
            double dist = grbl.JogDistanceProvider != null ? grbl.JogDistanceProvider() : grbl.JogStep;
            if (dist <= 0d)   // 0, or continuous (-1) - fall back to a sane fixed step
                dist = grbl.JogStep > 0d ? grbl.JogStep : 1d;
            if (dir < 0)
                dist = -dist;

            double feed = grbl.JogFeedProvider != null ? grbl.JogFeedProvider() : 0d;
            if (feed <= 0d && grbl.Keyboard != null)
                feed = grbl.Keyboard.JogFeedrates[(int)KeypressHandler.JogMode.Step];
            if (feed <= 0d)
                feed = 500d;

            // Use the same path the on-screen jog buttons use (GrblViewModel.ExecuteCommand), not a raw
            // stream write - otherwise the jog line never reaches the controller.
            string cmd = string.Format(CultureInfo.InvariantCulture, "$J=G91G21{0}{1}F{2}", axis, dist, Math.Ceiling(feed));
            grbl.ExecuteCommand(cmd);
        }

        // ---- analog stick / trigger jogging -----------------------------------------------------

        private const short StickDeadzone = 7000;        // ~21% of full deflection
        private const byte TriggerDeadzone = 40;
        private const int JogSendEveryNthPoll = 4;       // ~15 Hz at a 60 Hz poll
        // Each send queues ~JogOverlap intervals of travel, so grbl always has 2-3 jog blocks to blend
        // into continuous motion. Jog-cancel on release flushes the queue, so this adds no overshoot
        // (only a small feed-change latency). Lower it if feed changes feel laggy; raise if it stutters.
        private const double JogOverlap = 3.0;
        private const double StartupBoost = 2.5;         // longer first move to bridge the planner fill
        private const double AnalogFeedScale = 2.0;      // analog max feed = 2x the jog panel feed (headroom)
        private const double DefaultMaxJogFeed = 1500d;  // used if the jog panel feed is unavailable

        private int pollCounter = 0;
        private bool analogJogging = false;

        // Left stick -> X/Y, triggers -> Z (RT up, LT down). Proportional feed, jog-cancel on release.
        private void OnPolled(object sender, EventArgs e)
        {
            EnsureLoaded();

            XInputGamepad pad = service.State;

            double x = ControllerService.Normalize(pad.sThumbLX, StickDeadzone);
            double y = ControllerService.Normalize(pad.sThumbLY, StickDeadzone);
            double z = ControllerService.NormalizeTrigger(pad.bRightTrigger, TriggerDeadzone)
                     - ControllerService.NormalizeTrigger(pad.bLeftTrigger, TriggerDeadzone);

            double mag = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));

            if (!Enabled || !IsConnected || !CanJog || mag <= 0d)
            {
                StopAnalogJog();
                return;
            }

            // Throttle the jog send rate and never queue on top of an un-drained serial line.
            if (pollCounter++ % JogSendEveryNthPoll != 0)
                return;
            if (Comms.com.OutCount != 0)
                return;

            double panelFeed = grbl.JogFeedProvider != null ? grbl.JogFeedProvider() : 0d;
            if (panelFeed <= 0d)
                panelFeed = DefaultMaxJogFeed;
            double maxFeed = panelFeed * AnalogFeedScale;   // headroom; feather the stick to slow near target

            double feed = maxFeed * mag;
            if (feed < 10d)
                feed = 10d;

            double interval = JogSendEveryNthPoll / 60d;             // seconds between sends
            double baseDist = maxFeed / 60d * interval * JogOverlap; // mm at max feed per send
            if (!analogJogging)
                baseDist *= StartupBoost;   // first move of a jog is longer to avoid the start-up jerk

            var sb = new StringBuilder("$J=G91G21");
            AppendAxis(sb, "X", x * baseDist);
            AppendAxis(sb, "Y", y * baseDist);
            AppendAxis(sb, "Z", z * baseDist);
            sb.Append("F").Append(((int)Math.Ceiling(feed)).ToString(CultureInfo.InvariantCulture));

            grbl.ExecuteCommand(sb.ToString());
            analogJogging = true;
        }

        private static void AppendAxis(StringBuilder sb, string axis, double dist)
        {
            if (Math.Abs(dist) < 0.0005d)
                return;
            sb.Append(axis).Append(dist.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void StopAnalogJog()
        {
            pollCounter = 0;
            if (analogJogging)
            {
                analogJogging = false;
                if (Comms.com != null)
                    Comms.com.WriteByte(GrblConstants.CMD_JOG_CANCEL);
            }
        }
    }
}
