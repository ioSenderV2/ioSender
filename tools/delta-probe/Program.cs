/*
 * delta-probe - exercises CNC.Core.MachineDeltaProducer against the state-stream protocol's promises.
 * See the csproj header. Exit code 0 = all checks passed.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CNC.Client;
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
        IncludeFields = true,   // GrblState is a fields-only struct; without this it serializes as {}
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

        // ---- MachineStateStream: the in-process channel, driven through the REAL parser ----
        Console.WriteLine("MachineStateStream via GrblViewModel.DataReceived:");

        var model = new GrblViewModel();
        using (var stream = new MachineStateStream(model))
        {
            var seenA = new System.Collections.Generic.List<MachineDelta>();
            var mirrorA = new MachineSnapshot();
            var subA = stream.Subscribe(d => { seenA.Add(d); Apply(mirrorA, d); });

            Check(seenA.Count == 1 && seenA[0].Changed == MachineField.All, "subscribe is snapshot-first");

            // A real status report drives parse -> state mutation -> pump -> delta.
            model.DataReceived("<Idle|MPos:10.000,20.000,5.000|FS:0,0>");
            Check(seenA.Count == 2, "status report -> one delta");
            var sd = seenA[1];
            Check((sd.Changed & MachineField.GrblState) != 0 && sd.State.GrblState.State == GrblStates.Idle,
                  "parsed run state crosses (Idle)");
            Check((sd.Changed & MachineField.MachinePosition) != 0 && sd.State.MachinePosition[0] == 10.0
                  && sd.State.MachinePosition[1] == 20.0 && sd.State.MachinePosition[2] == 5.0,
                  "parsed machine position crosses");

            // The wire FORM, not just object equality: the run state's NAME must appear in the JSON.
            // Mirror-vs-source compares both sides through one serializer and cannot see a field the
            // serializer drops - exactly how GrblState shipped as {} (fields-only struct + S.T.J
            // defaults) and was only caught by reading the first live wire log.
            string wire = JsonSerializer.Serialize(sd, json);
            Check(wire.Contains("\"State\":\"Idle\""), "wire JSON literally carries the run state name");

            // An identical report changes nothing and must emit nothing.
            model.DataReceived("<Idle|MPos:10.000,20.000,5.000|FS:0,0>");
            Check(seenA.Count == 2, "identical report -> no delta");

            // Late joiner with a change PENDING (mutated outside any status report): the newcomer's
            // snapshot must carry it AND the existing subscriber must still receive it - the
            // flush-before-snapshot rule.
            model.State.Tool = "5";
            var seenB = new System.Collections.Generic.List<MachineDelta>();
            var mirrorB = new MachineSnapshot();
            var subB = stream.Subscribe(d => { seenB.Add(d); Apply(mirrorB, d); });
            Check(seenB.Count == 1 && seenB[0].State.Tool == "5", "late joiner's snapshot has the pending change");
            Check(seenA.Count == 3 && seenA[2].Changed == MachineField.Tool && seenA[2].State.Tool == "5",
                  "existing subscriber got the pending change flushed, not swallowed");

            // Both mirrors track through further traffic, and Seq stays gap-free per the contract.
            model.DataReceived("<Run|MPos:11.000,20.000,5.000|FS:800,12000>");
            Check(seenA.Count == 4 && seenB.Count == 2, "delta fans out to all subscribers");
            Check(SameState(mirrorA, mirrorB), "both mirrors agree");
            bool seqOk = true;
            for (int i = 2; i < seenA.Count; i++)
                seqOk &= seenA[i].Seq == seenA[i - 1].Seq + 1;
            Check(seqOk, "Seq gap-free across the stream");

            // RequestSnapshot resyncs everyone.
            stream.RequestSnapshot();
            Check(seenA[seenA.Count - 1].Changed == MachineField.All && seenB[seenB.Count - 1].Changed == MachineField.All,
                  "RequestSnapshot delivers All to every subscriber");
            Check(SameState(mirrorA, mirrorB), "mirrors agree after resync");

            // Unsubscribe is honored.
            subA.Dispose();
            int countA = seenA.Count;
            model.DataReceived("<Run|MPos:12.000,20.000,5.000|FS:800,12000>");
            Check(seenA.Count == countA && seenB[seenB.Count - 1].State.MachinePosition[0] == 12.0,
                  "disposed subscription stops receiving; live one continues");
            subB.Dispose();
        }

        // ---- CNC.Client.MachineMirror: precise gap/selective-apply checks against a fake stream ----
        Console.WriteLine("MachineMirror vs a fake IMachineStateStream (precise control over Seq):");
        {
            var fake = new FakeStateStream();
            var notified = new List<string>();
            var clientMirror = new MachineMirror(fake);
            Check(!clientMirror.HasData, "clientMirror starts with no data before any message");

            fake.Push(Snapshot(seq: 0, tool: "1", state: GrblStates.Idle));
            Check(clientMirror.HasData && clientMirror.Tool == "1" && clientMirror.GrblState.State == GrblStates.Idle,
                  "first snapshot populates the clientMirror");

            clientMirror.PropertyChanged += (s, e) => notified.Add(e.PropertyName);
            fake.Push(Delta(seq: 1, changed: MachineField.Tool, tool: "2"));
            Check(clientMirror.Tool == "2", "delta updates the flagged field");
            Check(notified.Count == 1 && notified[0] == nameof(MachineMirror.Tool),
                  "notification fires ONLY for the flagged field (got [" + string.Join(",", notified) + "])");

            // Seq gap: deltas 2 and 3 both claim to change WorkCoordinateSystem, but only 3 is delivered -
            // the clientMirror must detect the missing 2, refuse to apply 3 blindly, and self-heal via
            // RequestSnapshot instead. FakeStateStream's RequestSnapshot delivers NextSnapshot().
            notified.Clear();
            fake.NextSnapshot = () => Snapshot(seq: 5, tool: "9", state: GrblStates.Run);
            fake.Push(Delta(seq: 3, changed: MachineField.WorkCoordinateSystem, wcs: "G55"));
            // The load-bearing check: WorkCoordinateSystem lands at null (the resync snapshot's own
            // value), NOT "G55" from the dropped delta - proving the gap delta's payload never took
            // effect, only the resync's did.
            Check(clientMirror.Tool == "9" && clientMirror.WorkCoordinateSystem == null,
                  "a Seq gap is NOT applied on top of stale state - the resync snapshot wins instead");
            Check(fake.SnapshotRequests == 1, "exactly one resync requested for the gap");
            // A full resync (Changed=All) correctly notifies EVERY field, not just the ones that
            // differ from before - it's the conservative-correct reading of "trust everything here",
            // and resyncs are rare enough that over-notifying costs nothing.
            Check(notified.Contains(nameof(MachineMirror.Tool)) && notified.Contains(nameof(MachineMirror.WorkCoordinateSystem)),
                  "a full resync notifies every field, including ones already at their resync value");

            // Back in sync: seq 6 following the resync's seq 5 applies normally, no further resync.
            fake.Push(Delta(seq: 6, changed: MachineField.FeedRate, feedRate: 42d));
            Check(clientMirror.FeedRate == 42d && fake.SnapshotRequests == 1, "normal delta after resync applies with no extra resync");

            clientMirror.Dispose();
        }

        // ---- MachineMirror end-to-end: the real MachineStateStream, driven by the real parser ----
        Console.WriteLine("MachineMirror via the REAL MachineStateStream + parser:");
        {
            var model2 = new GrblViewModel();
            using var stream2 = new MachineStateStream(model2);
            using var mirror2 = new MachineMirror(stream2);

            Check(mirror2.HasData, "mirror has data immediately after construction (synchronous snapshot)");

            model2.DataReceived("<Idle|MPos:1.000,2.000,3.000|FS:0,0>");
            Check(mirror2.GrblState.State == GrblStates.Idle && mirror2.MachinePosition[0] == 1.0,
                  "mirror reflects a real parsed status report end to end");

            model2.DataReceived("<Run|MPos:4.000,2.000,3.000|FS:800,12000>");
            Check(mirror2.GrblState.State == GrblStates.Run && mirror2.MachinePosition[0] == 4.0
                  && mirror2.FeedRate == 800d,
                  "mirror tracks a second real report (state + position + feed)");
        }

        // ---- Command channel: MachineRealtimeChannel + MachineCommandChannel ----
        Console.WriteLine("Command channel (realtime byte mapping + queued Jog via the real JogController):");
        {
            var fakeComms = new FakeStreamComms();
            Comms.com = fakeComms;

            var rt = new MachineRealtimeChannel();
            rt.Send(RealtimeCommand.FeedHold);
            Check(fakeComms.LastByte == GrblConstants.CMD_FEED_HOLD, "FeedHold writes CMD_FEED_HOLD");
            rt.Send(RealtimeCommand.CycleStart);
            Check(fakeComms.LastByte == GrblConstants.CMD_CYCLE_START, "CycleStart writes CMD_CYCLE_START");
            rt.Send(RealtimeCommand.JogCancel);
            Check(fakeComms.LastByte == GrblConstants.CMD_JOG_CANCEL, "JogCancel writes CMD_JOG_CANCEL");
            rt.Send(RealtimeCommand.SpindleOverrideFineMinus);
            Check(fakeComms.LastByte == GrblConstants.CMD_SPINDLE_OVR_FINE_MINUS, "an override maps correctly too (spot check, not just the common 3)");
            Check(fakeComms.WriteCount == 4, "realtime writes are single bytes, one per Send - no batching, no extra traffic");

            // Queued: Jog goes through the REAL JogController (model.Keyboard), the same engine the
            // jog pad/keyboard/gamepad already use - this channel is a new door, not a new lock.
            var model3 = new GrblViewModel();
            // JogController starts with every axis template empty until Configure() runs - in the real
            // app this happens via LatheModeEnabled's setter / MainWindow startup; a bare model needs
            // it done explicitly, same as GrblInfo.NumAxes/AxisLetters would be set from a real $I reply.
            model3.Keyboard.Configure(3, "XYZ", false);
            var commands = new MachineCommandChannel(model3);

            var empty = new JogCommand(3); // all-zero directions: nothing to do
            var r1 = commands.Jog(empty).Result;
            Check(!r1.Success && r1.Id == 1, "an empty jog command is refused, not silently accepted");

            var move = new JogCommand(3) { Distance = 5d, Feedrate = 200d, Mode = JogMode.Step };
            move.Directions[0] = 1d;
            var r2 = commands.Jog(move).Result;
            Check(r2.Success && r2.Id == 2, "a real jog command succeeds");
            Check(fakeComms.LastWrite != null && fakeComms.LastWrite.StartsWith("$J="),
                  "the jog actually rendered and sent a $J= command (got \"" + fakeComms.LastWrite + "\")");

            Comms.com = null;
        }

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // Minimal StreamComms double: records the last realtime byte and the last multi-byte write (what
    // JogController.Send ultimately calls) without opening any real transport. Every other member is
    // a harmless default - this harness never exercises them.
    sealed class FakeStreamComms : StreamComms
    {
        public byte LastByte;
        public int WriteCount;
        public string LastWrite;

        public bool IsOpen => true;
        public int OutCount => 0;
        public string Reply => string.Empty;
        public Comms.StreamType StreamType => Comms.StreamType.Serial;
        public Comms.State CommandState { get; set; }
        public bool EventMode { get; set; }
        public System.Action<int> ByteReceived { get; set; }
        public System.Action<string> AckSink { get; set; }
        public bool BlockingWrites { get; set; }
        public bool IsReconnecting => false;

        public void NotifyLinkLost() { }
        public void Close() { }
        public int ReadByte() { return -1; }
        public void WriteByte(byte data) { LastByte = data; WriteCount++; }
        public void WriteBytes(byte[] bytes, int len) { LastWrite = System.Text.Encoding.ASCII.GetString(bytes, 0, len); }
        public void WriteString(string data) { LastWrite = data; }
        public void WriteCommand(string command) { LastWrite = command; }
        public string GetReply(string command) { return string.Empty; }
        public void AwaitAck() { }
        public void AwaitAck(string command) { }
        public void AwaitResponse(string command) { }
        public void AwaitResponse() { }
        public void PurgeQueue() { }

        public event DataReceivedHandler DataReceived { add { } remove { } }
        public event System.Action ConnectionLost { add { } remove { } }
        public event System.Action Reconnected { add { } remove { } }
    }

    // ---- Test helpers for the MachineMirror section ----

    static MachineDelta Snapshot(long seq, string tool = null, GrblStates state = GrblStates.Unknown)
    {
        return new MachineDelta
        {
            Seq = seq,
            Changed = MachineField.All,
            State = new MachineSnapshot { Tool = tool, GrblState = new GrblState { State = state } }
        };
    }

    static MachineDelta Delta(long seq, MachineField changed, string tool = null, string wcs = null, double feedRate = 0d)
    {
        return new MachineDelta
        {
            Seq = seq,
            Changed = changed,
            State = new MachineSnapshot { Tool = tool, WorkCoordinateSystem = wcs, FeedRate = feedRate }
        };
    }

    // Minimal IMachineStateStream double: fans a pushed delta out to subscribers, and answers
    // RequestSnapshot with whatever NextSnapshot() currently returns. Deliberately does NOT
    // auto-deliver a snapshot on Subscribe (unlike the real contract) - test code pushes the first
    // message itself, which gives precise control over the Seq sequence under test.
    sealed class FakeStateStream : IMachineStateStream
    {
        // CNC.Core.Action (an enum, GCode.cs) shadows System.Action inside a `using CNC.Core;` scope -
        // qualify explicitly rather than fight the using list (same gotcha b9456f9 hit in Core itself).
        private readonly List<System.Action<MachineDelta>> subscribers = new List<System.Action<MachineDelta>>();
        public Func<MachineDelta> NextSnapshot;
        public int SnapshotRequests;

        public IDisposable Subscribe(System.Action<MachineDelta> handler)
        {
            subscribers.Add(handler);
            return new Unsub(() => subscribers.Remove(handler));
        }

        public void Push(MachineDelta delta)
        {
            foreach (var h in subscribers.ToArray())
                h(delta);
        }

        public void RequestSnapshot()
        {
            SnapshotRequests++;
            if (NextSnapshot != null)
                Push(NextSnapshot());
        }

        private sealed class Unsub : IDisposable
        {
            private System.Action dispose;
            public Unsub(System.Action dispose) { this.dispose = dispose; }
            public void Dispose() { dispose?.Invoke(); dispose = null; }
        }
    }
}
