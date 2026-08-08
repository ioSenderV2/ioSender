/*
 * MachineMirror.cs - part of CNC Client library
 *
 * The client-side twin of CNC.Core.MachineState: applies an IMachineStateStream's delta stream to
 * itself and raises INotifyPropertyChanged, so a UI can bind to it the way it already binds to
 * GrblViewModel. It is internally MAINTAINED (only this class ever calls a setter) and externally
 * READ-ONLY - every property outside this file is a get-only view. There is deliberately no public
 * way to push a value INTO the mirror from outside; the only way in is a delta from the stream.
 *
 * ---- Seam rule, client side of the step-5 rule ----
 *
 * Step 5 settled "MachineState holds storage, GrblViewModel holds notification" for the server side.
 * This is the client-side version of the same idea: MachineSnapshot (in CNC.Contracts) IS the
 * storage shape; this class adds notification on top of it, the same relationship GrblViewModel has
 * to MachineState, just one layer further out and across whatever transport is in between.
 *
 * ---- Selective notification ----
 *
 * A delta's Changed flags say exactly which fields are meaningful (see StateMessages.cs) - Apply
 * walks that flag set and raises PropertyChanged ONLY for the fields it names. This matters for a
 * UI's binding cost the same way it already mattered for the wire: a 5-10Hz stream where every
 * message re-notified all 30 properties would be exactly the kind of waste
 * [[iosender-onpropertychanged-marshal-perf]] found once already, just moved to a new layer.
 *
 * ---- Seq-gap self-healing ----
 *
 * A dropped or reordered delta (Seq skips) means the mirror's current state might already be wrong
 * in some field it doesn't know changed - continuing to apply deltas on top of an unknown-stale base
 * would make the divergence undetectable from here on. Instead: on a gap, ASK for a fresh snapshot
 * and discard the delta that revealed the gap (the snapshot supersedes it). A snapshot (Changed=All)
 * is always accepted unconditionally regardless of its Seq, by construction - the sender is telling
 * you "trust everything in this message," so there is nothing to gap-check.
 */

using System;
using System.ComponentModel;
using CNC.Contracts;
// GrblState/Signals/etc. and AxisFlags/SpindleState/LatheMode physically live in CNC.Contracts but
// kept their pre-move namespaces (CNC.Core / CNC.GCode) for zero call-site churn - see
// CNC Contracts/MachineEnums.cs and GCodeEnums.cs headers.
using CNC.Core;
using CNC.GCode;

namespace CNC.Client
{
    public class MachineMirror : INotifyPropertyChanged, IDisposable
    {
        private readonly IMachineStateStream stream;
        private readonly IDisposable subscription;
        private long lastSeq = -1;
        private bool hasSnapshot = false;

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>True once at least one full snapshot has been applied - false means every property
        /// below is still at its type default, not a real "unknown" state from the machine.</summary>
        public bool HasData { get { return hasSnapshot; } }

        public MachineMirror(IMachineStateStream stream)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            // Subscribe's own contract (IMachineStateStream) delivers a full snapshot synchronously
            // before this call returns, so HasData is already true by the time the constructor exits.
            subscription = stream.Subscribe(OnDelta);
        }

        public void Dispose()
        {
            subscription.Dispose();
        }

        private void OnDelta(MachineDelta delta)
        {
            if (delta.Changed == MachineField.All)
            {
                // A snapshot is authoritative regardless of Seq continuity - accept it unconditionally
                // and resynchronize the sequence counter to it.
                ApplyFields(MachineField.All, delta.State);
                lastSeq = delta.Seq;
                hasSnapshot = true;
                return;
            }

            if (hasSnapshot && delta.Seq != lastSeq + 1)
            {
                // Missed at least one delta - this message's base state is unknown-stale, so it is
                // dropped rather than applied on top of a base that might already be wrong. Ask for a
                // fresh snapshot instead; in-process delivery is synchronous, so by the time
                // RequestSnapshot() returns this handler has already been re-entered with Changed=All
                // and the branch above has brought the mirror back into sync.
                stream.RequestSnapshot();
                return;
            }

            ApplyFields(delta.Changed, delta.State);
            lastSeq = delta.Seq;
        }

        // One flag check + one assignment + one notify per field, in MachineField declaration order.
        // Deliberately not a loop over the enum (Position arrays would need special-casing, the Tlo/
        // AutoReporting pairs are two fields under one flag) - a flat list is easier to audit against
        // StateMessages.cs than a generic walker would be.
        private void ApplyFields(MachineField changed, MachineSnapshot s)
        {
            if ((changed & MachineField.GrblState) != 0) { _grblState = s.GrblState; Notify(nameof(GrblState)); }
            if ((changed & MachineField.MachinePosition) != 0) { _machinePosition = s.MachinePosition; Notify(nameof(MachinePosition)); }
            if ((changed & MachineField.WorkPosition) != 0) { _workPosition = s.WorkPosition; Notify(nameof(WorkPosition)); }
            if ((changed & MachineField.Position) != 0) { _position = s.Position; Notify(nameof(Position)); }
            if ((changed & MachineField.WorkPositionOffset) != 0) { _workPositionOffset = s.WorkPositionOffset; Notify(nameof(WorkPositionOffset)); }
            if ((changed & MachineField.ToolOffset) != 0) { _toolOffset = s.ToolOffset; Notify(nameof(ToolOffset)); }
            if ((changed & MachineField.HomePosition) != 0) { _homePosition = s.HomePosition; Notify(nameof(HomePosition)); }
            if ((changed & MachineField.ProbePosition) != 0) { _probePosition = s.ProbePosition; Notify(nameof(ProbePosition)); }
            if ((changed & MachineField.AxisLetters) != 0) { _axisLetters = s.AxisLetters; Notify(nameof(AxisLetters)); }
            if ((changed & MachineField.AxisHomed) != 0) { _axisHomed = s.AxisHomed; Notify(nameof(AxisHomed)); }
            if ((changed & MachineField.Signals) != 0) { _signals = s.Signals; Notify(nameof(Signals)); }
            if ((changed & MachineField.OptionalSignals) != 0) { _optionalSignals = s.OptionalSignals; Notify(nameof(OptionalSignals)); }
            if ((changed & MachineField.AxisScaled) != 0) { _axisScaled = s.AxisScaled; Notify(nameof(AxisScaled)); }
            if ((changed & MachineField.SpindleState) != 0) { _spindleState = s.SpindleState; Notify(nameof(SpindleState)); }
            if ((changed & MachineField.THCSignals) != 0) { _thcSignals = s.THCSignals; Notify(nameof(THCSignals)); }
            if ((changed & MachineField.WorkCoordinateSystem) != 0) { _workCoordinateSystem = s.WorkCoordinateSystem; Notify(nameof(WorkCoordinateSystem)); }
            if ((changed & MachineField.Tool) != 0) { _tool = s.Tool; Notify(nameof(Tool)); }
            if ((changed & MachineField.Probe) != 0) { _probe = s.Probe; Notify(nameof(Probe)); }
            if ((changed & MachineField.IsMachinePosition) != 0) { _isMachinePosition = s.IsMachinePosition; Notify(nameof(IsMachinePosition)); }
            if ((changed & MachineField.IsProbeSuccess) != 0) { _isProbeSuccess = s.IsProbeSuccess; Notify(nameof(IsProbeSuccess)); }
            if ((changed & MachineField.Tlo) != 0)
            {
                _tloReference = s.TloReference; _isTloReferenceSet = s.IsTloReferenceSet;
                Notify(nameof(TloReference)); Notify(nameof(IsTloReferenceSet));
            }
            if ((changed & MachineField.AutoReporting) != 0)
            {
                _autoReportingEnabled = s.AutoReportingEnabled; _autoReportInterval = s.AutoReportInterval;
                Notify(nameof(AutoReportingEnabled)); Notify(nameof(AutoReportInterval));
            }
            if ((changed & MachineField.FeedRate) != 0) { _feedRate = s.FeedRate; Notify(nameof(FeedRate)); }
            if ((changed & MachineField.ProgrammedRPM) != 0) { _programmedRPM = s.ProgrammedRPM; Notify(nameof(ProgrammedRPM)); }
            if ((changed & MachineField.ActualRPM) != 0) { _actualRPM = s.ActualRPM; Notify(nameof(ActualRPM)); }
            if ((changed & MachineField.PWM) != 0) { _pwm = s.PWM; Notify(nameof(PWM)); }
            if ((changed & MachineField.FeedOverride) != 0) { _feedOverride = s.FeedOverride; Notify(nameof(FeedOverride)); }
            if ((changed & MachineField.RapidsOverride) != 0) { _rapidsOverride = s.RapidsOverride; Notify(nameof(RapidsOverride)); }
            if ((changed & MachineField.RPMOverride) != 0) { _rpmOverride = s.RPMOverride; Notify(nameof(RPMOverride)); }
            if ((changed & MachineField.THCVoltage) != 0) { _thcVoltage = s.THCVoltage; Notify(nameof(THCVoltage)); }
            if ((changed & MachineField.LatheMode) != 0) { _latheMode = s.LatheMode; Notify(nameof(LatheMode)); }
            if ((changed & MachineField.HomedState) != 0) { _homedState = s.HomedState; Notify(nameof(HomedState)); }
            if ((changed & MachineField.IsMPGActive) != 0) { _isMPGActive = s.IsMPGActive; Notify(nameof(IsMPGActive)); }
        }

        private GrblState _grblState;
        private double?[] _machinePosition, _workPosition, _position, _workPositionOffset, _toolOffset, _homePosition, _probePosition;
        private string _axisLetters, _workCoordinateSystem, _tool, _probe;
        private AxisFlags _axisHomed, _axisScaled;
        private Signals _signals, _optionalSignals;
        private SpindleState _spindleState;
        private THCSignals _thcSignals;
        private bool _isMachinePosition, _isProbeSuccess, _isTloReferenceSet, _autoReportingEnabled;
        private double? _tloReference, _actualRPM, _thcVoltage;
        private int _autoReportInterval, _pwm;
        private double _feedRate, _programmedRPM, _feedOverride, _rapidsOverride, _rpmOverride;
        private LatheMode _latheMode;
        private HomedState _homedState;
        private bool? _isMPGActive;

        public GrblState GrblState { get { return _grblState; } }
        public double?[] MachinePosition { get { return _machinePosition; } }
        public double?[] WorkPosition { get { return _workPosition; } }
        public double?[] Position { get { return _position; } }
        public double?[] WorkPositionOffset { get { return _workPositionOffset; } }
        public double?[] ToolOffset { get { return _toolOffset; } }
        public double?[] HomePosition { get { return _homePosition; } }
        public double?[] ProbePosition { get { return _probePosition; } }
        public string AxisLetters { get { return _axisLetters; } }
        public AxisFlags AxisHomed { get { return _axisHomed; } }
        public Signals Signals { get { return _signals; } }
        public Signals OptionalSignals { get { return _optionalSignals; } }
        public AxisFlags AxisScaled { get { return _axisScaled; } }
        public SpindleState SpindleState { get { return _spindleState; } }
        public THCSignals THCSignals { get { return _thcSignals; } }
        public string WorkCoordinateSystem { get { return _workCoordinateSystem; } }
        public string Tool { get { return _tool; } }
        public string Probe { get { return _probe; } }
        public bool IsMachinePosition { get { return _isMachinePosition; } }
        public bool IsProbeSuccess { get { return _isProbeSuccess; } }
        public double? TloReference { get { return _tloReference; } }
        public bool IsTloReferenceSet { get { return _isTloReferenceSet; } }
        public bool AutoReportingEnabled { get { return _autoReportingEnabled; } }
        public int AutoReportInterval { get { return _autoReportInterval; } }
        public double FeedRate { get { return _feedRate; } }
        public double ProgrammedRPM { get { return _programmedRPM; } }
        public double? ActualRPM { get { return _actualRPM; } }
        public int PWM { get { return _pwm; } }
        public double FeedOverride { get { return _feedOverride; } }
        public double RapidsOverride { get { return _rapidsOverride; } }
        public double RPMOverride { get { return _rpmOverride; } }
        public double? THCVoltage { get { return _thcVoltage; } }
        public LatheMode LatheMode { get { return _latheMode; } }
        public HomedState HomedState { get { return _homedState; } }
        public bool? IsMPGActive { get { return _isMPGActive; } }
    }
}
