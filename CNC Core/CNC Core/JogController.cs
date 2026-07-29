/*
 * JogController.cs - part of CNC Core library
 *
 * Portable jog execution, split out of KeypressHandler.ProcessKeypress.
 *
 * The jog path used to run intent and execution together: ProcessKeypress decided which axes were
 * moving from the pressed keys AND, inline, clamped against soft limits, chose G91 vs G53, formatted
 * the "$J=" string and wrote it. The only available seam was in the middle of a WPF key handler.
 *
 * Now the client decides intent and hands over a JogCommand; this class owns execution. The division
 * is deliberate: axis letters, lathe orientation, travel limits and soft-limit clamping are machine
 * properties, so a client - a gamepad, a jog pad, eventually a browser - must not be the thing
 * deciding whether a move is safe. It also gives a structured packet that can cross a transport,
 * which a formatted "$J=" string never could.
 *
 * Behaviour is intended to be byte-identical to the old inline code, including the G53 branch's
 * command.Replace('-', ' ') quirk (targets are absolute machine coordinates there, so the sign
 * belongs to the value, not the word) - see BuildCommand.
 */

using System;
using CNC.GCode;

namespace CNC.Core
{
    public enum JogMode
    {
        Step = 0,
        Slow,
        Fast,
        None // must be last!
    }

    /// <summary>
    /// A jog request: which axes move, in which direction, how far and how fast.
    /// The client supplies Distance/Feedrate (normally from the controller's configured tier);
    /// the controller still owns clamping and rendering, so machine safety does not move client-side.
    /// </summary>
    public class JogCommand
    {
        /// <summary>Per-axis direction: -1, 0 (not moving) or +1. Indexed by axis.</summary>
        public double[] Directions;

        /// <summary>Requested distance for this jog, in current units.</summary>
        public double Distance;

        /// <summary>Requested feedrate for this jog.</summary>
        public double Feedrate;

        /// <summary>Speed/step tier this request came from - reported as the active jog mode.</summary>
        public JogMode Mode = JogMode.None;

        /// <summary>Cancel any in-flight jog before issuing this one (the old preCancel flag).</summary>
        public bool CancelFirst;

        public JogCommand(int axes)
        {
            Directions = new double[axes];
        }

        public bool IsMoving
        {
            get
            {
                for (int i = 0; i < Directions.Length; i++)
                    if (Directions[i] != 0d)
                        return true;
                return false;
            }
        }
    }

    public class JogController
    {
        protected readonly GrblViewModel grbl;
        private JogMode jogMode = JogMode.None;
        private JogMode notifiedJogMode = JogMode.None;
        private string[] axisCommands = new string[18];   // per axis, [i*2] = plus, [i*2+1] = minus
        private int N_AXIS = 3;

        public JogController(GrblViewModel model)
        {
            grbl = model;
            for (int i = 0; i < axisCommands.Length; i++)
                axisCommands[i] = string.Empty;
        }

        // ---- machine configuration (was KeypressHandler.Configure) ---------------------------------
        // Axis letters and lathe orientation are machine properties, so the format templates live here
        // rather than on the key bindings.
        public virtual void Configure(int numAxes, string axisLetters, bool lathe)
        {
            N_AXIS = numAxes;
            axisLetters = axisLetters.Replace("-", "");

            for (int i = 0; i < axisCommands.Length; i++)
                axisCommands[i] = string.Empty;

            for (int i = 0; i < numAxes; i++)
            {
                var k = lathe ? (i == 0 ? 2 : 0) : i;
                axisCommands[i * 2] = axisLetters.Substring(k, 1) + (lathe && i != 0 ? "-{0}" : "{0}");
                axisCommands[i * 2 + 1] = axisLetters.Substring(k, 1) + (lathe && i != 0 ? "{0}" : "-{0}");
            }
        }

        /// <summary>True once Configure has given this axis a command template (i.e. it can be jogged).</summary>
        public bool IsAxisConfigured(int jogKeyIndex)
        {
            return jogKeyIndex >= 0 && jogKeyIndex < axisCommands.Length && axisCommands[jogKeyIndex] != string.Empty;
        }

        public int AxisCount { get { return N_AXIS; } set { N_AXIS = value; } }

        // ---- configuration -------------------------------------------------------------------------
        public double[] JogDistances { get; set; } = new double[3] { 0.01, 500.0, 500.0 };
        public double[] JogFeedrates { get; set; } = new double[3] { 100.0, 200.0, 500.0 };
        public double JogStepDistance
        {
            get { return JogDistances[(int)JogMode.Step]; }
            set { grbl.JogStep = JogDistances[(int)JogMode.Step] = value; }
        }
        public double LimitSwitchesClearance { get; set; } = .5d;
        public bool SoftLimits { get; set; } = false;
        public bool IsJoggingEnabled { get; set; } = true;
        public bool IsContinuousJoggingEnabled { get; set; }

        // Default continuous-jog speed (from Config.Jog.DefaultSpeedFast): false = Slow (Shift -> Fast),
        // true = Fast (Shift -> Slow). Pushed in from the Controls layer since CNC.Core can't see AppConfig.
        public bool DefaultSpeedFast { get; set; } = false;

        // ---- state ---------------------------------------------------------------------------------
        public bool CanJog2 { get { return grbl.GrblState.State == GrblStates.Idle || grbl.GrblState.State == GrblStates.Tool || grbl.GrblState.State == GrblStates.Jog; } }
        public bool CanJog { get { return AllowJog && (grbl.GrblState.State == GrblStates.Idle || grbl.GrblState.State == GrblStates.Tool || grbl.GrblState.State == GrblStates.Jog); } }
        public bool IsJogging { get { return jogMode != JogMode.None || grbl.GrblState.State == GrblStates.Jog; } }

        /// <summary>Set by the input layer per keypress pass; gates CanJog.</summary>
        public bool AllowJog { get; set; } = true;

        // Active jog mode (Step/Slow/Fast/None); the jog panel slider live-tracks this.
        public JogMode CurrentJogMode { get { return jogMode; } }

        public event System.Action JogModeChanged;

        /// <summary>Set the active tier. The input layer owns tier selection (modifier keys etc.).</summary>
        public void SetJogMode(JogMode mode)
        {
            jogMode = mode;
            NotifyJogModeChanged();
        }

        private void NotifyJogModeChanged()
        {
            if (notifiedJogMode != jogMode)
            {
                notifiedJogMode = jogMode;
                JogModeChanged?.Invoke();
            }
        }

        // ---- execution -----------------------------------------------------------------------------

        /// <summary>
        /// Execute a jog request: clamp to travel limits where required, render the "$J=" block and
        /// send it. Returns false if there was nothing to do.
        /// </summary>
        public bool Execute(JogCommand jog)
        {
            if (jog == null || jog.Mode == JogMode.None || !jog.IsMoving)
                return false;

            SetJogMode(jog.Mode);

            var command = BuildCommand(jog);

            if (string.IsNullOrEmpty(command))
                return false;

            Send(command, jog.CancelFirst);

            return true;
        }

        internal string BuildCommand(JogCommand jog)
        {
            string command = string.Empty;

            // grblHAL enforces its own soft limits during jogging, so a plain incremental move is safe;
            // otherwise clamp each axis to an absolute machine target ourselves.
            if (GrblInfo.IsGrblHAL || !SoftLimits)
            {
                var distance = jog.Distance.ToInvariantString();

                for (int i = 0; i < N_AXIS && i < jog.Directions.Length; i++)
                {
                    if (jog.Directions[i] != 0d)
                        command += string.Format(AxisCommand(i, jog.Directions[i]), distance);
                }

                return string.IsNullOrEmpty(command)
                    ? command
                    : "$J=G91G21" + command + string.Format("F{0}", jog.Feedrate.ToInvariantString());
            }

            for (int i = 0; i < N_AXIS && i < jog.Directions.Length; i++)
            {
                if (jog.Directions[i] == 0d)
                    continue;

                var target = grbl.MachinePosition.Values[i] + jog.Distance * jog.Directions[i];

                if (i == GrblConstants.A_AXIS && GrblInfo.MaxTravel.Values[GrblConstants.A_AXIS] == 0d)
                    continue;

                if (GrblInfo.ForceSetOrigin)
                {
                    if (!GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(i)))
                    {
                        if (target > 0)
                            target = 0;
                        else if (target < (-GrblInfo.MaxTravel.Values[i] + LimitSwitchesClearance))
                            target = (-GrblInfo.MaxTravel.Values[i] + LimitSwitchesClearance);
                    }
                    else
                    {
                        if (target < 0d)
                            target = 0d;
                        else if (target > (GrblInfo.MaxTravel.Values[i] - LimitSwitchesClearance))
                            target = GrblInfo.MaxTravel.Values[i] - LimitSwitchesClearance;
                    }
                }
                else
                {
                    if (target > -LimitSwitchesClearance)
                        target = -LimitSwitchesClearance;
                    else if (target < -(GrblInfo.MaxTravel.Values[i] - LimitSwitchesClearance))
                        target = -(GrblInfo.MaxTravel.Values[i] - LimitSwitchesClearance);
                }

                command += string.Format(AxisCommand(i, jog.Directions[i]), target.ToInvariantString());
            }

            if (string.IsNullOrEmpty(command))
                return command;

            // Replace('-', ' ') is carried over verbatim: these are absolute machine targets, so the
            // minus belongs to the value the template already emitted, not to the word.
            return "$J=G53G21" + string.Format(command.Replace('-', ' ') + "F{0}", jog.Feedrate.ToInvariantString());
        }

        private string AxisCommand(int axisIndex, double direction)
        {
            int i = axisIndex * 2 + (direction < 0d ? 1 : 0);
            return i < axisCommands.Length && axisCommands[i] != string.Empty ? axisCommands[i] : string.Empty;
        }

        // ---- raw transport -------------------------------------------------------------------------

        public void Cancel()
        {
            while (Comms.com.OutCount != 0) ;
            Comms.com.WriteByte(GrblConstants.CMD_JOG_CANCEL); // Cancel jog
            jogMode = JogMode.None;
            NotifyJogModeChanged();
        }

        /// <summary>Send an already-rendered jog block. Kept public for callers that build their own
        /// (e.g. ControllerMapper's gamepad jogging).</summary>
        public void Send(string command, bool cancelFirst)
        {
            if (IsJogging)
            {
                while (Comms.com.OutCount != 0) ;
                if (cancelFirst)
                    Comms.com.WriteByte(GrblConstants.CMD_JOG_CANCEL); // Cancel current jog
            }
            Comms.com.WriteCommand(command);
        }
    }
}
