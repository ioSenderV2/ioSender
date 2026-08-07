/*
 * delta-probe - exercises CNC.Core.MachineDeltaProducer against the state-stream protocol's promises.
 * See the csproj header. Exit code 0 = all checks passed.
 */

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using CNC.Contracts;
using CNC.Core;

static class Probe
{
    static int failures = 0;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) failures++;
    }

    static readonly JsonSerializerOptions json = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // The mirror: apply exactly the flagged fields, ignore the rest - the client-side contract.
    static void Apply(MachineSnapshot mirror, MachineDelta d)
    {
        var c = d.Changed;
        var s = d.State;
        if ((c & MachineField.GrblState) != 0) mirror.GrblState = s.GrblState;
        if ((c & MachineField.MachinePosition) != 0) mirror.MachinePosition = s.MachinePosition;
        if ((c & MachineField.WorkPosition) != 0) mirror.WorkPosition = s.WorkPosition;
        if ((c & MachineField.Position) != 0) mirror.Position = s.Position;
        if ((c & MachineField.WorkPositionOffset) != 0) mirror.WorkPositionOffset = s.WorkPositionOffset;
        if ((c & MachineField.ToolOffset) != 0) mirror.ToolOffset = s.ToolOffset;
        if ((c & MachineField.HomePosition) != 0) mirror.HomePosition = s.HomePosition;
        if ((c & MachineField.ProbePosition) != 0) mirror.ProbePosition = s.ProbePosition;
        if ((c & MachineField.AxisLetters) != 0) mirror.AxisLetters = s.AxisLetters;
        if ((c & MachineField.AxisHomed) != 0) mirror.AxisHomed = s.AxisHomed;
        if ((c & MachineField.Signals) != 0) mirror.Signals = s.Signals;
        if ((c & MachineField.OptionalSignals) != 0) mirror.OptionalSignals = s.OptionalSignals;
        if ((c & MachineField.AxisScaled) != 0) mirror.AxisScaled = s.AxisScaled;
        if ((c & MachineField.SpindleState) != 0) mirror.SpindleState = s.SpindleState;
        if ((c & MachineField.THCSignals) != 0) mirror.THCSignals = s.THCSignals;
        if ((c & MachineField.WorkCoordinateSystem) != 0) mirror.WorkCoordinateSystem = s.WorkCoordinateSystem;
        if ((c & MachineField.Tool) != 0) mirror.Tool = s.Tool;
        if ((c & MachineField.Probe) != 0) mirror.Probe = s.Probe;
        if ((c & MachineField.IsMachinePosition) != 0) mirror.IsMachinePosition = s.IsMachinePosition;
        if ((c & MachineField.IsProbeSuccess) != 0) mirror.IsProbeSuccess = s.IsProbeSuccess;
        if ((c & MachineField.Tlo) != 0) { mirror.TloReference = s.TloReference; mirror.IsTloReferenceSet = s.IsTloReferenceSet; }
        if ((c & MachineField.AutoReporting) != 0) { mirror.AutoReportingEnabled = s.AutoReportingEnabled; mirror.AutoReportInterval = s.AutoReportInterval; }
        if ((c & MachineField.FeedRate) != 0) mirror.FeedRate = s.FeedRate;
        if ((c & MachineField.ProgrammedRPM) != 0) mirror.ProgrammedRPM = s.ProgrammedRPM;
        if ((c & MachineField.ActualRPM) != 0) mirror.ActualRPM = s.ActualRPM;
        if ((c & MachineField.PWM) != 0) mirror.PWM = s.PWM;
        if ((c & MachineField.FeedOverride) != 0) mirror.FeedOverride = s.FeedOverride;
        if ((c & MachineField.RapidsOverride) != 0) mirror.RapidsOverride = s.RapidsOverride;
        if ((c & MachineField.RPMOverride) != 0) mirror.RPMOverride = s.RPMOverride;
        if ((c & MachineField.THCVoltage) != 0) mirror.THCVoltage = s.THCVoltage;
        if ((c & MachineField.LatheMode) != 0) mirror.LatheMode = s.LatheMode;
        if ((c & MachineField.HomedState) != 0) mirror.HomedState = s.HomedState;
        if ((c & MachineField.IsMPGActive) != 0) mirror.IsMPGActive = s.IsMPGActive;
    }

    // Deep-equal via JSON: property order is declaration order, NaN never occurs (nulls on the wire),
    // so byte-equal JSON is value-equal state.
    static bool SameState(MachineSnapshot a, MachineSnapshot b)
    {
        return JsonSerializer.Serialize(a, json) == JsonSerializer.Serialize(b, json);
    }

    static int Main()
    {
        Console.WriteLine("delta-probe: MachineDeltaProducer vs the state-stream protocol");

        var state = new MachineState();
        var producer = new MachineDeltaProducer(state);
        var mirror = new MachineSnapshot();

        // 1. Connect: full snapshot, everything flagged, Seq 0, never-reported values are null.
        var snap = producer.Snapshot();
        Check(snap.Changed == MachineField.All, "snapshot flags All");
        Check(snap.Seq == 0, "snapshot Seq 0 before any delta");
        Check(snap.State.MachinePosition != null && snap.State.MachinePosition.Length > 0
              && snap.State.MachinePosition[0] == null, "unset position axis crosses as null");
        Check(snap.State.TloReference == null, "unset TLO reference crosses as null");
        Check(snap.State.ActualRPM == null, "unreported actual RPM crosses as null");
        Check(snap.State.IsMPGActive == null, "unknown MPG crosses as null");
        Apply(mirror, snap);

        // 2. Quiet tick: no delta.
        Check(producer.Poll() == null, "no change -> no delta");

        // 3. A parser-shaped burst: run state, position, tool, TLO, an override, a signal.
        state.GrblState.State = GrblStates.Run;
        state.GrblState.Substate = 1;
        state.MachinePosition.Values[0] = 10.5;
        state.MachinePosition.Values[1] = -2.25;
        state.MachinePosition.Values[2] = 3.0;
        state.Tool = "2";
        state.TloReference = 6.971;
        state.IsTloReferenceSet = true;
        state.FeedOverride = 110d;
        state.Signals.Value |= Signals.Probe;

        var d1 = producer.Poll();
        var expect = MachineField.GrblState | MachineField.MachinePosition | MachineField.Tool
                   | MachineField.Tlo | MachineField.FeedOverride | MachineField.Signals;
        Check(d1 != null, "burst -> one delta");
        Check(d1.Changed == expect, "burst flags exactly the changed fields (got " + d1.Changed + ")");
        Check(d1.Seq == 1, "first delta Seq 1");
        Check(d1.State.MachinePosition[0] == 10.5 && d1.State.MachinePosition[1] == -2.25,
              "changed position carries the complete array");
        Check(d1.State.TloReference == 6.971 && d1.State.IsTloReferenceSet, "Tlo pair travels together");
        Apply(mirror, d1);
        Check(SameState(mirror, producer.Snapshot().State), "mirror == source after applying delta 1");

        // 4. Coalescing and last-value semantics: change-and-revert inside one tick is silence.
        double feed = state.FeedRate;
        state.FeedRate = 500d;
        state.FeedRate = feed;
        Check(producer.Poll() == null, "A->B->A inside one tick -> no delta");

        // 5. Second burst, including null->value string and value transitions on nullable telemetry.
        state.WorkCoordinateSystem = "G54";
        state.ActualRPM = 11987d;
        state.IsMPGActive = false;
        var d2 = producer.Poll();
        Check(d2 != null && d2.Changed == (MachineField.WorkCoordinateSystem | MachineField.ActualRPM | MachineField.IsMPGActive),
              "second burst flags exactly (got " + (d2 == null ? "null" : d2.Changed.ToString()) + ")");
        Check(d2.Seq == 2, "Seq increments by exactly 1 per delta");
        Check(d2.State.ActualRPM == 11987d, "actual RPM crosses once reported");
        Check(d2.State.IsMPGActive == false, "MPG known-false crosses as false, not null");
        Apply(mirror, d2);
        Check(SameState(mirror, producer.Snapshot().State), "mirror == source after applying delta 2");

        // 6. Snapshot resync refreshes the shadow: mutate, snapshot, then a quiet poll.
        state.PWM = 128;
        var resync = producer.Snapshot();
        Check(resync.Seq == 2 && resync.State.PWM == 128, "resync snapshot carries current state at current Seq");
        Check(producer.Poll() == null, "snapshot refreshes the shadow - nothing left to emit");
        Apply(mirror, resync);
        Check(SameState(mirror, producer.Snapshot().State), "mirror == source after resync");

        // 7. The unflagged parts of a delta's State are defaults (receiver must ignore them) - prove
        //    the producer populates only what it flags: Tool changed alone must not carry positions.
        state.Tool = "3";
        var d3 = producer.Poll();
        Check(d3 != null && d3.Changed == MachineField.Tool && d3.State.MachinePosition == null,
              "delta carries only flagged fields");
        Apply(mirror, d3);
        Check(SameState(mirror, producer.Snapshot().State), "mirror == source after delta 3");

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
