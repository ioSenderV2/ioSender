/*
 * StateMessages.cs - part of CNC Contracts
 *
 * The machine-state wire messages: how a server tells clients what the machine is doing. This file is
 * the protocol documentation - the semantics live in these comments, and a non-.NET client implements
 * against them.
 *
 * ---- The model ----
 *
 * One message type carries both the initial snapshot and every update: MachineDelta. `Changed` says
 * which fields of `State` are meaningful; the mirror applies exactly those and ignores the rest. A
 * full snapshot is simply a delta with every field flagged (MachineField.All) - so connect, resync
 * and steady-state updates are one code path on both ends.
 *
 * ---- Sequencing ----
 *
 * `Seq` increases by exactly 1 per delta on a connection. A client that receives Seq N+2 after N has
 * missed a delta and MUST NOT keep applying: it requests a fresh snapshot (which arrives with the
 * current Seq and All flagged) and resumes from there. This is the half-open-socket lesson applied to
 * state: a mirror must be able to KNOW it is stale, because nothing visible distinguishes a quiet
 * stream from a broken one.
 *
 * ---- Why NaN never crosses the wire ----
 *
 * Core uses NaN for "never reported" (positions, TLO reference, actual RPM, THC voltage). NaN is not
 * legal JSON, so the wire uses null for the same meaning and the boundary converts. Position arrays
 * are per-axis: a null ELEMENT means that axis has no reading (not homed / never reported); a null
 * ARRAY in a delta cannot occur for a flagged field - when a position field is flagged, the complete
 * array is present (positions parse from the controller as whole reports, so they change as a unit).
 */

using System;
using CNC.Core;
using CNC.GCode;

namespace CNC.Contracts
{
    /// <summary>
    /// Which fields of a <see cref="MachineDelta"/>'s State are meaningful. Fields not flagged have
    /// unspecified content and must be ignored by the receiver.
    /// </summary>
    [Flags]
    public enum MachineField : long
    {
        None = 0,
        GrblState = 1L << 0,
        MachinePosition = 1L << 1,
        WorkPosition = 1L << 2,
        Position = 1L << 3,
        WorkPositionOffset = 1L << 4,
        ToolOffset = 1L << 5,
        HomePosition = 1L << 6,
        ProbePosition = 1L << 7,
        AxisLetters = 1L << 8,
        AxisHomed = 1L << 9,
        Signals = 1L << 10,
        OptionalSignals = 1L << 11,
        AxisScaled = 1L << 12,
        SpindleState = 1L << 13,
        THCSignals = 1L << 14,
        WorkCoordinateSystem = 1L << 15,
        Tool = 1L << 16,
        Probe = 1L << 17,
        IsMachinePosition = 1L << 18,
        IsProbeSuccess = 1L << 19,
        Tlo = 1L << 20,             // TloReference + IsTloReferenceSet travel together
        AutoReporting = 1L << 21,   // AutoReportingEnabled + AutoReportInterval travel together
        FeedRate = 1L << 22,
        ProgrammedRPM = 1L << 23,
        ActualRPM = 1L << 24,
        PWM = 1L << 25,
        FeedOverride = 1L << 26,
        RapidsOverride = 1L << 27,
        RPMOverride = 1L << 28,
        THCVoltage = 1L << 29,
        LatheMode = 1L << 30,
        HomedState = 1L << 31,
        IsMPGActive = 1L << 32,
        IsMetric = 1L << 33,

        All = (1L << 34) - 1
    }

    /// <summary>
    /// The machine's reported state as wire data - the contract twin of the server's working
    /// MachineState. Inside a <see cref="MachineDelta"/>, only the fields its Changed flags name are
    /// meaningful.
    /// </summary>
    public class MachineSnapshot
    {
        /// <summary>Run state, substate, last alarm, error and MPG flag, as one unit.</summary>
        public GrblState GrblState { get; set; }

        // Positions are per-axis arrays, index order matching AxisLetters. A null element means that
        // axis has no reading (Core-side NaN). When flagged in a delta the whole array is present.
        public double?[] MachinePosition { get; set; }
        public double?[] WorkPosition { get; set; }
        public double?[] Position { get; set; }
        public double?[] WorkPositionOffset { get; set; }
        public double?[] ToolOffset { get; set; }
        public double?[] HomePosition { get; set; }
        public double?[] ProbePosition { get; set; }

        /// <summary>Axis naming for this machine, e.g. "XYZ" ("XZ" lathe). Defines position array order.</summary>
        public string AxisLetters { get; set; }

        public AxisFlags AxisHomed { get; set; }
        public Signals Signals { get; set; }
        public Signals OptionalSignals { get; set; }
        public AxisFlags AxisScaled { get; set; }
        public SpindleState SpindleState { get; set; }
        public THCSignals THCSignals { get; set; }

        /// <summary>Active work coordinate system, "G54".."G59.3".</summary>
        public string WorkCoordinateSystem { get; set; }

        /// <summary>Current tool as reported; "None" when none, empty until the first report.</summary>
        public string Tool { get; set; }

        /// <summary>Active probe id as reported (kept as the raw string, like the server side).</summary>
        public string Probe { get; set; }

        /// <summary>True when status reports carry machine coordinates, false for work coordinates.</summary>
        public bool IsMachinePosition { get; set; }

        /// <summary>Units mode from the controller's $13 report-inches setting: true = metric
        /// (millimeters). A client needs this to interpret every coordinate on this snapshot.</summary>
        public bool IsMetric { get; set; } = true;

        /// <summary>Whether the last probe cycle triggered; ProbePosition is meaningless when false.</summary>
        public bool IsProbeSuccess { get; set; }

        /// <summary>Tool length reference offset; null when unset (Core-side NaN). Flags as Tlo with the bool.</summary>
        public double? TloReference { get; set; }
        public bool IsTloReferenceSet { get; set; }

        /// <summary>Status auto-reporting (grblHAL). Flag as one unit (AutoReporting).</summary>
        public bool AutoReportingEnabled { get; set; }
        public int AutoReportInterval { get; set; }

        public double FeedRate { get; set; }
        public double ProgrammedRPM { get; set; }

        /// <summary>Encoder-measured spindle RPM; null when this build does not report it.</summary>
        public double? ActualRPM { get; set; }

        public int PWM { get; set; }

        // Override percentages, 100 = no override.
        public double FeedOverride { get; set; }
        public double RapidsOverride { get; set; }
        public double RPMOverride { get; set; }

        /// <summary>Torch height control arc voltage (plasma); null when not reported.</summary>
        public double? THCVoltage { get; set; }

        public LatheMode LatheMode { get; set; }
        public HomedState HomedState { get; set; }

        /// <summary>Whether an MPG pendant has control; null until a report has said either way.</summary>
        public bool? IsMPGActive { get; set; }
    }

    /// <summary>
    /// One state update. The first message on a connection is a full snapshot: Changed = All. After
    /// that, Changed names what moved. Seq increases by exactly 1 per message; a gap means the client
    /// missed one and must request a fresh snapshot rather than keep applying.
    /// </summary>
    public class MachineDelta
    {
        public long Seq { get; set; }
        public MachineField Changed { get; set; }
        public MachineSnapshot State { get; set; }
    }
}
