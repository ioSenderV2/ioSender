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
using System.Threading;
using System.Xml.Serialization;

namespace CNC.Core
{
    [XmlType("ControllerMapping")]
    [XmlRoot("ControllerMap")]
    public class ControllerMapFile
    {
        public List<ControllerMapEntry> Buttons = new List<ControllerMapEntry>();
        public bool AnalogJogEnabled = true;
        public double FeedScale = 2.0;
        public int DeadzonePercent = 21;
        public bool InvertX, InvertY, InvertZ;
    }

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
        JogStepDecrease,
        JogDistanceIncrease,
        JogDistanceDecrease
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

            // Analog stick/trigger jogging runs on its own background thread rather than off the UI-thread
            // ButtonService poll timer - see the comment above AnalogJogLoop for why.
            analogThread = new Thread(AnalogJogLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "AnalogJogPump" };
            analogThread.Start();
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

        // ---- persistence ------------------------------------------------------------------------
        //
        // The controller map is stored as the "Controller" section of App.config (folded in from the old
        // standalone ControllerMap.xml). CNC.Core can't reference the config store in CNC.Controls, so the wiring
        // is a small static hand-off: AppConfig sets PersistHook (to save App.config) and keeps SectionConfig in
        // sync with the section's payload. When PersistHook is null (e.g. tools that don't load AppConfig) the
        // code falls back to the legacy ControllerMap.xml file.

        private bool mapLoaded = false;
        private static string MapPath { get { return Resources.ConfigPath + "ControllerMap.xml"; } }

        // App.config "Controller" section payload (get/set by the ConfigStore section) + the save hook.
        public static ControllerMapFile SectionConfig;
        public static System.Action PersistHook;
        private static bool UseSection { get { return PersistHook != null; } }

        /// <summary>Load the saved map once (config path isn't available when the mapper is constructed).</summary>
        public void EnsureLoaded()
        {
            if (mapLoaded)
                return;
            mapLoaded = true;
            LoadMap();
        }

        // Apply a saved map onto the live state. Only called when there IS a saved map, so an absent map leaves
        // the constructor's defaults in place.
        private void Apply(ControllerMapFile file)
        {
            if (file == null)
                return;

            map.Clear();
            if (file.Buttons != null)
                foreach (var e in file.Buttons)
                    if (e.Action != ControllerAction.None)
                        map[e.Button] = e.Action;

            AnalogJogEnabled = file.AnalogJogEnabled;
            FeedScale = file.FeedScale;
            DeadzonePercent = file.DeadzonePercent;
            InvertX = file.InvertX;
            InvertY = file.InvertY;
            InvertZ = file.InvertZ;
        }

        // Snapshot the live state (for the section serializer / SaveMap).
        public ControllerMapFile Export()
        {
            return new ControllerMapFile
            {
                Buttons = map.Select(kv => new ControllerMapEntry { Button = kv.Key, Action = kv.Value }).ToList(),
                AnalogJogEnabled = AnalogJogEnabled,
                FeedScale = FeedScale,
                DeadzonePercent = DeadzonePercent,
                InvertX = InvertX,
                InvertY = InvertY,
                InvertZ = InvertZ
            };
        }

        public bool LoadMap()
        {
            mapLoaded = true;

            if (UseSection)
            {
                if (SectionConfig == null)
                    return false;   // no saved map - keep defaults
                Apply(SectionConfig);
                return true;
            }

            try
            {
                if (!File.Exists(MapPath))
                    return false;

                var xs = new XmlSerializer(typeof(ControllerMapFile));
                using (var reader = new StreamReader(MapPath))
                    Apply((ControllerMapFile)xs.Deserialize(reader));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool SaveMap()
        {
            if (UseSection)
            {
                SectionConfig = Export();
                PersistHook();
                return true;
            }

            try
            {
                var xs = new XmlSerializer(typeof(ControllerMapFile));
                using (var fs = new FileStream(MapPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    xs.Serialize(fs, Export());
                return true;
            }
            catch
            {
                return false;
            }
        }

        // One-time importer for the "Controller" section: read the legacy ControllerMap.xml if present.
        public static ControllerMapFile ReadLegacyFile()
        {
            try
            {
                if (!File.Exists(MapPath))
                    return null;
                var xs = new XmlSerializer(typeof(ControllerMapFile));
                using (var reader = new StreamReader(MapPath))
                    return (ControllerMapFile)xs.Deserialize(reader);
            }
            catch
            {
                return null;
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
                // Bumpers step the on-screen UI jog speed preset (2x4 grid); fall back to the old step-size
                // multiplier only when no UI jog panel is loaded to provide the callback.
                case ControllerAction.JogStepIncrease:
                    if (grbl.CycleJogFeed != null) grbl.CycleJogFeed(1); else AdjustStep(10d);
                    break;
                case ControllerAction.JogStepDecrease:
                    if (grbl.CycleJogFeed != null) grbl.CycleJogFeed(-1); else AdjustStep(0.1d);
                    break;
                // Distance +/- step the on-screen UI jog distance preset (2x4 grid); fall back to the step-size
                // multiplier only when no UI jog panel is loaded to provide the callback.
                case ControllerAction.JogDistanceIncrease:
                    if (grbl.CycleJogDistance != null) grbl.CycleJogDistance(1); else AdjustStep(10d);
                    break;
                case ControllerAction.JogDistanceDecrease:
                    if (grbl.CycleJogDistance != null) grbl.CycleJogDistance(-1); else AdjustStep(0.1d);
                    break;
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
        //
        // Runs on its own background thread (AnalogJogLoop), NOT off ControllerService's UI-thread
        // DispatcherTimer. Keyboard's continuous jog is smooth because it sends ONE $J with an
        // effectively-infinite distance and lets grblHAL run it until jog-cancel; analog jogging can't do
        // that (direction/feed must track the stick continuously), so it re-sends a short $J segment
        // every ~67ms and relies on each new segment landing before the previous one finishes so grblHAL
        // blends them into continuous motion. On the UI thread, any dispatcher hitch (layout, rendering,
        // other UI-bound work) can delay a tick past that window - the current segment then runs out,
        // motion decelerates to a stop, and the next segment's arrival reads as a jerk. A dedicated
        // background thread with its own sleep-based cadence isn't subject to that contention. Sends bypass
        // GrblViewModel.ExecuteCommand (which touches ObservableCollections bound to the UI and isn't safe
        // to call off the UI thread) in favour of Comms.com.WriteCommand directly - the same low-level path
        // KeypressHandler.SendJogCommand already uses for keyboard jogging.

        private const short StickDeadzone = 7000;        // ~21% of full deflection
        private const byte TriggerDeadzone = 40;
        private const int AnalogSendIntervalMs = 67;      // ~15 Hz
        // Each send queues ~JogOverlap intervals of travel, so grbl always has 2-3 jog blocks to blend
        // into continuous motion. Jog-cancel on release flushes the queue, so this adds no overshoot
        // (only a small feed-change latency). Lower it if feed changes feel laggy; raise if it stutters.
        private const double JogOverlap = 3.0;
        private const double StartupBoost = 2.5;         // longer first move to bridge the planner fill
        private const double DefaultMaxJogFeed = 1500d;  // used if the jog panel feed is unavailable

        // User-configurable analog jog settings (Controller tab), persisted in ControllerMap.xml.
        public bool AnalogJogEnabled = true;
        public double FeedScale = 2.0;                   // analog max feed = FeedScale x the jog panel feed
        public int DeadzonePercent = 21;                 // left-stick deadzone, percent of full deflection
        public bool InvertX = false, InvertY = false, InvertZ = false;

        private readonly Thread analogThread;
        private bool analogJogging = false;

        private void AnalogJogLoop()
        {
            while (true)
            {
                try
                {
                    AnalogJogTick();
                }
                catch
                {
                    // never let a stray exception take down this background thread
                }
                Thread.Sleep(AnalogSendIntervalMs);
            }
        }

        // Left stick -> X/Y, triggers -> Z (RT up, LT down). Proportional feed, jog-cancel on release.
        // Polls XInput directly (not via ControllerService.State) so this thread never races the UI-thread
        // button-poll timer over the shared gamepad snapshot.
        private void AnalogJogTick()
        {
            EnsureLoaded();

            if (!AnalogJogEnabled || !service.IsConnected)
            {
                StopAnalogJog();
                return;
            }

            XInputState state;
            if (!XInput.GetState(service.ControllerIndex, out state))
            {
                StopAnalogJog();
                return;
            }
            XInputGamepad pad = state.Gamepad;

            short stickDeadzone = (short)Math.Max(0, Math.Min(32000, DeadzonePercent / 100.0 * XInput.ThumbMax));

            double x = ControllerService.Normalize(pad.sThumbLX, stickDeadzone) * (InvertX ? -1d : 1d);
            double y = ControllerService.Normalize(pad.sThumbLY, stickDeadzone) * (InvertY ? -1d : 1d);
            double z = (ControllerService.NormalizeTrigger(pad.bRightTrigger, TriggerDeadzone)
                     - ControllerService.NormalizeTrigger(pad.bLeftTrigger, TriggerDeadzone)) * (InvertZ ? -1d : 1d);

            double mag = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));

            if (!Enabled || !IsConnected || !CanJog || mag <= 0d)
            {
                StopAnalogJog();
                return;
            }

            // Never queue on top of an un-drained serial line.
            if (Comms.com.OutCount != 0)
                return;

            double panelFeed = grbl.JogFeedProvider != null ? grbl.JogFeedProvider() : 0d;
            if (panelFeed <= 0d)
                panelFeed = DefaultMaxJogFeed;
            double maxFeed = panelFeed * (FeedScale <= 0d ? 1d : FeedScale);   // feather the stick to slow near target

            double feed = maxFeed * mag;
            if (feed < 10d)
                feed = 10d;

            double interval = AnalogSendIntervalMs / 1000d;           // seconds between sends
            double baseDist = maxFeed / 60d * interval * JogOverlap;  // mm at max feed per send
            if (!analogJogging)
                baseDist *= StartupBoost;   // first move of a jog is longer to avoid the start-up jerk

            var sb = new StringBuilder("$J=G91G21");
            AppendAxis(sb, "X", x * baseDist);
            AppendAxis(sb, "Y", y * baseDist);
            AppendAxis(sb, "Z", z * baseDist);
            sb.Append("F").Append(((int)Math.Ceiling(feed)).ToString(CultureInfo.InvariantCulture));

            Comms.com.WriteCommand(sb.ToString());
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
            if (analogJogging)
            {
                analogJogging = false;
                if (Comms.com != null)
                    Comms.com.WriteByte(GrblConstants.CMD_JOG_CANCEL);
            }
        }
    }
}
