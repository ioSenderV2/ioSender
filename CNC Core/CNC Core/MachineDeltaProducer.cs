/*
 * MachineDeltaProducer.cs - part of CNC Core library
 *
 * The server side of the state stream (step 6 of the client/server split): turns MachineState into
 * CNC.Contracts.MachineDelta messages.
 *
 * SHADOW DIFF, not write-path hooks, and the choice matters: the producer keeps a private copy of the
 * last emitted state and diffs against it each poll tick. Any writer - the status parser, the $#/$I
 * handshakes, Clear() - just mutates MachineState as it always has, and the diff cannot miss it. The
 * alternative (every write site marking a dirty bitmask) breaks silently the first time a write site
 * forgets, and it re-creates notification-over-the-wire, which the contracts design rejected. A ~30
 * field compare at the 5-10Hz poll cadence is noise. Burst writes between ticks coalesce into one
 * delta; A->B->A inside one tick produces nothing, which is correct last-value mirror semantics.
 *
 * Threading: call Poll()/Snapshot() from the context that owns MachineState mutations (the same
 * contract the WPF bindings already rely on). The producer itself keeps no locks.
 *
 * Wire conversion: Core's NaN-means-never-reported becomes null on the wire (NaN is not legal JSON) -
 * per-axis position elements, TloReference, ActualRPM and THCVoltage. Delta messages populate ONLY the
 * fields their Changed flags name; everything else in the embedded snapshot is left at default and
 * receivers must ignore it. That is deliberately verifiable: apply a delta's flagged fields to a
 * mirror and the mirror must equal the source - a changed-but-unflagged field shows up as divergence
 * in the harness (tools/delta-probe) instead of in production.
 */

using System;
using CNC.Contracts;
using CNC.GCode;

namespace CNC.Core
{
    public class MachineDeltaProducer
    {
        private readonly MachineState state;
        private long seq = 0;

        // The shadow: plain values, one per wire field, holding what was last emitted.
        private GrblState lastGrblState;
        private readonly double[][] lastPositions = new double[7][];
        private string lastAxisLetters;
        private AxisFlags lastAxisHomed, lastAxisScaled;
        private Signals lastSignals, lastOptionalSignals;
        private SpindleState lastSpindleState;
        private THCSignals lastTHCSignals;
        private string lastWcs, lastTool, lastProbe;
        private bool lastIsMachinePosition, lastIsProbeSuccess, lastIsTloReferenceSet, lastAutoReportingEnabled;
        private double lastTloReference, lastFeedRate, lastProgrammedRPM, lastActualRPM;
        private double lastFeedOverride, lastRapidsOverride, lastRPMOverride, lastTHCVoltage;
        private int lastAutoReportInterval, lastPWM;
        private LatheMode lastLatheMode;
        private HomedState lastHomedState;
        private bool? lastIsMPGActive;

        public MachineDeltaProducer(MachineState state)
        {
            this.state = state;
            CaptureShadow();
        }

        /// <summary>Sequence number of the last emitted delta; a snapshot reports state as of this Seq.</summary>
        public long Seq { get { return seq; } }

        /// <summary>
        /// Full state with every field flagged - the first message on a connection, and the resync
        /// answer to a client that saw a Seq gap. Also refreshes the shadow, so the next Poll() diffs
        /// against exactly what this snapshot said.
        /// </summary>
        public MachineDelta Snapshot()
        {
            CaptureShadow();
            return new MachineDelta { Seq = seq, Changed = MachineField.All, State = BuildState(MachineField.All) };
        }

        /// <summary>
        /// Diff against the shadow: returns one delta naming and carrying everything that changed since
        /// the last emission, or null when nothing did. Seq increments only when a delta is emitted.
        /// </summary>
        public MachineDelta Poll()
        {
            MachineField changed = Diff();
            if (changed == MachineField.None)
                return null;

            CaptureShadow();
            return new MachineDelta { Seq = ++seq, Changed = changed, State = BuildState(changed) };
        }

        private Position[] StatePositions()
        {
            return new Position[]
            {
                state.MachinePosition, state.WorkPosition, state.Position, state.WorkPositionOffset,
                state.ToolOffset, state.HomePosition, state.ProbePosition
            };
        }

        private static readonly MachineField[] positionFields = new MachineField[]
        {
            MachineField.MachinePosition, MachineField.WorkPosition, MachineField.Position,
            MachineField.WorkPositionOffset, MachineField.ToolOffset, MachineField.HomePosition,
            MachineField.ProbePosition
        };

        private MachineField Diff()
        {
            MachineField changed = MachineField.None;

            GrblState gs = state.GrblState;
            if (gs.State != lastGrblState.State || gs.Substate != lastGrblState.Substate ||
                gs.LastAlarm != lastGrblState.LastAlarm || gs.Error != lastGrblState.Error || gs.MPG != lastGrblState.MPG)
                changed |= MachineField.GrblState;

            Position[] positions = StatePositions();
            for (int p = 0; p < positions.Length; p++)
            {
                // .Equals, not ==: NaN.Equals(NaN) is true, and NaN means "never reported" here.
                for (int i = 0; i < lastPositions[p].Length; i++)
                    if (!positions[p].Values[i].Equals(lastPositions[p][i]))
                    {
                        changed |= positionFields[p];
                        break;
                    }
            }

            if (GrblInfo.AxisLetters != lastAxisLetters)
                changed |= MachineField.AxisLetters;
            if (state.AxisHomed.Value != lastAxisHomed)
                changed |= MachineField.AxisHomed;
            if (state.Signals.Value != lastSignals)
                changed |= MachineField.Signals;
            if (state.OptionalSignals.Value != lastOptionalSignals)
                changed |= MachineField.OptionalSignals;
            if (state.AxisScaled.Value != lastAxisScaled)
                changed |= MachineField.AxisScaled;
            if (state.SpindleState.Value != lastSpindleState)
                changed |= MachineField.SpindleState;
            if (state.THCSignals.Value != lastTHCSignals)
                changed |= MachineField.THCSignals;
            if (state.WorkCoordinateSystem != lastWcs)
                changed |= MachineField.WorkCoordinateSystem;
            if (state.Tool != lastTool)
                changed |= MachineField.Tool;
            if (state.Probe != lastProbe)
                changed |= MachineField.Probe;
            if (state.IsMachinePosition != lastIsMachinePosition)
                changed |= MachineField.IsMachinePosition;
            if (state.IsProbeSuccess != lastIsProbeSuccess)
                changed |= MachineField.IsProbeSuccess;
            if (!state.TloReference.Equals(lastTloReference) || state.IsTloReferenceSet != lastIsTloReferenceSet)
                changed |= MachineField.Tlo;
            if (state.AutoReportingEnabled != lastAutoReportingEnabled || state.AutoReportInterval != lastAutoReportInterval)
                changed |= MachineField.AutoReporting;
            if (!state.FeedRate.Equals(lastFeedRate))
                changed |= MachineField.FeedRate;
            if (!state.ProgrammedRPM.Equals(lastProgrammedRPM))
                changed |= MachineField.ProgrammedRPM;
            if (!state.ActualRPM.Equals(lastActualRPM))
                changed |= MachineField.ActualRPM;
            if (state.PWM != lastPWM)
                changed |= MachineField.PWM;
            if (!state.FeedOverride.Equals(lastFeedOverride))
                changed |= MachineField.FeedOverride;
            if (!state.RapidsOverride.Equals(lastRapidsOverride))
                changed |= MachineField.RapidsOverride;
            if (!state.RPMOverride.Equals(lastRPMOverride))
                changed |= MachineField.RPMOverride;
            if (!state.THCVoltage.Equals(lastTHCVoltage))
                changed |= MachineField.THCVoltage;
            if (state.LatheMode != lastLatheMode)
                changed |= MachineField.LatheMode;
            if (state.HomedState != lastHomedState)
                changed |= MachineField.HomedState;
            if (state.IsMPGActive != lastIsMPGActive)
                changed |= MachineField.IsMPGActive;

            return changed;
        }

        private void CaptureShadow()
        {
            lastGrblState = state.GrblState;

            Position[] positions = StatePositions();
            for (int p = 0; p < positions.Length; p++)
            {
                if (lastPositions[p] == null)
                    lastPositions[p] = new double[9];
                for (int i = 0; i < lastPositions[p].Length; i++)
                    lastPositions[p][i] = positions[p].Values[i];
            }

            lastAxisLetters = GrblInfo.AxisLetters;
            lastAxisHomed = state.AxisHomed.Value;
            lastSignals = state.Signals.Value;
            lastOptionalSignals = state.OptionalSignals.Value;
            lastAxisScaled = state.AxisScaled.Value;
            lastSpindleState = state.SpindleState.Value;
            lastTHCSignals = state.THCSignals.Value;
            lastWcs = state.WorkCoordinateSystem;
            lastTool = state.Tool;
            lastProbe = state.Probe;
            lastIsMachinePosition = state.IsMachinePosition;
            lastIsProbeSuccess = state.IsProbeSuccess;
            lastTloReference = state.TloReference;
            lastIsTloReferenceSet = state.IsTloReferenceSet;
            lastAutoReportingEnabled = state.AutoReportingEnabled;
            lastAutoReportInterval = state.AutoReportInterval;
            lastFeedRate = state.FeedRate;
            lastProgrammedRPM = state.ProgrammedRPM;
            lastActualRPM = state.ActualRPM;
            lastPWM = state.PWM;
            lastFeedOverride = state.FeedOverride;
            lastRapidsOverride = state.RapidsOverride;
            lastRPMOverride = state.RPMOverride;
            lastTHCVoltage = state.THCVoltage;
            lastLatheMode = state.LatheMode;
            lastHomedState = state.HomedState;
            lastIsMPGActive = state.IsMPGActive;
        }

        private static double?[] ToWire(Position p, int numAxes)
        {
            double?[] values = new double?[numAxes];
            for (int i = 0; i < numAxes; i++)
                values[i] = double.IsNaN(p.Values[i]) ? (double?)null : p.Values[i];
            return values;
        }

        private static double? ToWire(double value)
        {
            return double.IsNaN(value) ? (double?)null : value;
        }

        // Populate ONLY the flagged fields - receivers must ignore the rest, and the harness exploits
        // that: a changed-but-unflagged field diverges the mirror there instead of in production.
        private MachineSnapshot BuildState(MachineField changed)
        {
            var s = new MachineSnapshot();
            int numAxes = GrblInfo.NumAxes;

            if ((changed & MachineField.GrblState) != 0)
                s.GrblState = state.GrblState;

            Position[] positions = StatePositions();
            if ((changed & MachineField.MachinePosition) != 0) s.MachinePosition = ToWire(positions[0], numAxes);
            if ((changed & MachineField.WorkPosition) != 0) s.WorkPosition = ToWire(positions[1], numAxes);
            if ((changed & MachineField.Position) != 0) s.Position = ToWire(positions[2], numAxes);
            if ((changed & MachineField.WorkPositionOffset) != 0) s.WorkPositionOffset = ToWire(positions[3], numAxes);
            if ((changed & MachineField.ToolOffset) != 0) s.ToolOffset = ToWire(positions[4], numAxes);
            if ((changed & MachineField.HomePosition) != 0) s.HomePosition = ToWire(positions[5], numAxes);
            if ((changed & MachineField.ProbePosition) != 0) s.ProbePosition = ToWire(positions[6], numAxes);

            if ((changed & MachineField.AxisLetters) != 0) s.AxisLetters = GrblInfo.AxisLetters;
            if ((changed & MachineField.AxisHomed) != 0) s.AxisHomed = state.AxisHomed.Value;
            if ((changed & MachineField.Signals) != 0) s.Signals = state.Signals.Value;
            if ((changed & MachineField.OptionalSignals) != 0) s.OptionalSignals = state.OptionalSignals.Value;
            if ((changed & MachineField.AxisScaled) != 0) s.AxisScaled = state.AxisScaled.Value;
            if ((changed & MachineField.SpindleState) != 0) s.SpindleState = state.SpindleState.Value;
            if ((changed & MachineField.THCSignals) != 0) s.THCSignals = state.THCSignals.Value;
            if ((changed & MachineField.WorkCoordinateSystem) != 0) s.WorkCoordinateSystem = state.WorkCoordinateSystem;
            if ((changed & MachineField.Tool) != 0) s.Tool = state.Tool;
            if ((changed & MachineField.Probe) != 0) s.Probe = state.Probe;
            if ((changed & MachineField.IsMachinePosition) != 0) s.IsMachinePosition = state.IsMachinePosition;
            if ((changed & MachineField.IsProbeSuccess) != 0) s.IsProbeSuccess = state.IsProbeSuccess;
            if ((changed & MachineField.Tlo) != 0)
            {
                s.TloReference = ToWire(state.TloReference);
                s.IsTloReferenceSet = state.IsTloReferenceSet;
            }
            if ((changed & MachineField.AutoReporting) != 0)
            {
                s.AutoReportingEnabled = state.AutoReportingEnabled;
                s.AutoReportInterval = state.AutoReportInterval;
            }
            if ((changed & MachineField.FeedRate) != 0) s.FeedRate = state.FeedRate;
            if ((changed & MachineField.ProgrammedRPM) != 0) s.ProgrammedRPM = state.ProgrammedRPM;
            if ((changed & MachineField.ActualRPM) != 0) s.ActualRPM = ToWire(state.ActualRPM);
            if ((changed & MachineField.PWM) != 0) s.PWM = state.PWM;
            if ((changed & MachineField.FeedOverride) != 0) s.FeedOverride = state.FeedOverride;
            if ((changed & MachineField.RapidsOverride) != 0) s.RapidsOverride = state.RapidsOverride;
            if ((changed & MachineField.RPMOverride) != 0) s.RPMOverride = state.RPMOverride;
            if ((changed & MachineField.THCVoltage) != 0) s.THCVoltage = ToWire(state.THCVoltage);
            if ((changed & MachineField.LatheMode) != 0) s.LatheMode = state.LatheMode;
            if ((changed & MachineField.HomedState) != 0) s.HomedState = state.HomedState;
            if ((changed & MachineField.IsMPGActive) != 0) s.IsMPGActive = state.IsMPGActive;

            return s;
        }
    }
}
