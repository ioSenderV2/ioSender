/*
 * MachineEnums.cs - part of CNC Contracts
 *
 * The controller-state enums and the GrblState struct, moved verbatim from CNC Core's Grbl.cs so the
 * wire messages (StateMessages.cs) can carry them without the contracts assembly referencing CNC.Core.
 *
 * Namespace stays CNC.Core deliberately: every consumer already has `using CNC.Core`, so the move cost
 * zero call-site churn. New contract-only types go in the CNC.Contracts namespace instead.
 */

using System;

namespace CNC.Core
{
    public enum GrblStates
    {
        Unknown = 0,
        Idle,
        Run,
        Tool,
        Hold,
        Home,
        Check,
        Jog,
        Alarm,
        Door,
        Sleep
    }

    public enum HomedState
    {
        Unknown = 0,
        NotHomed,
        Homed
    }

    [Flags]
    public enum Signals : int // Keep in sync with the GrblInfo.SignalLetters constant in CNC Core's Grbl.cs
    {
        Off = 0,
        LimitX = 1 << 0,
        LimitY = 1 << 1,
        LimitZ = 1 << 2,
        LimitA = 1 << 3,
        LimitB = 1 << 4,
        LimitC = 1 << 5,
        LimitU = 1 << 6,
        LimitV = 1 << 7,
        LimitW = 1 << 8,
        EStop = 1 << 9,
        Probe  = 1 << 10,
        Reset = 1 << 11,
        SafetyDoor = 1 << 12,
        Hold = 1 << 13,
        CycleStart = 1 << 14,
        BlockDelete = 1 << 15,
        OptionalStop = 1 << 16,
        ProbeDisconnected = 1 << 17,
        MotorWarning = 1 << 18,
        MotorFault = 1 << 19
    }

    [Flags]
    public enum THCSignals : int
    {
        Off = 0,
        ArcOk = 1 << 0,
        THCEnabled = 1 << 1,
        THCActive = 1 << 2,
        TorchOn = 1 << 3,
        OhmicProbe = 1 << 4,
        VelocityLock = 1 << 5,
        VoidLock = 1 << 6,
        Down = 1 << 7,
        Up = 1 << 8,
        Breakaway = 1 << 9,
        FloatSwitch = 1 << 10
    }

    // FIELDS, not properties, deliberately: the status parser mutates members individually, and C#
    // forbids mutating members through a struct-typed property (see MachineState.GrblState's remarks).
    // ⚠ Serializer consequence: System.Text.Json ignores fields by default and emits {} - every wire
    // serializer must set IncludeFields = true (found live in the first -debuglog=delta wire log).
    public struct GrblState
    {
        public GrblStates State;
        public int Substate;
        public int LastAlarm;
        public int Error;
        public bool MPG;
    }
}
