/*
 * GCodeEnums.cs - part of CNC Contracts
 *
 * The g-code-level enums the wire messages carry, moved verbatim from CNC Core's GCode.cs. Namespace
 * stays CNC.GCode for the same zero-churn reason MachineEnums.cs keeps CNC.Core - see that header.
 */

using System;

namespace CNC.GCode
{
    [Flags]
    public enum AxisFlags : int
    {
        None = 0,
        X = 1 << 0,
        Y = 1 << 1,
        Z = 1 << 2,
        A = 1 << 3,
        B = 1 << 4,
        C = 1 << 5,
        U = 1 << 6,
        V = 1 << 7,
        W = 1 << 8,
        XY = 0x03,
        XZ = 0x05,
        XYZ = 0x07,
        ABC = 0x38,
        XYZABC = 0x3F,
        UVW = 0x1C,
        All = 0x1FF
    }

    [Flags]
    public enum SpindleState : int
    {
        Off = 1 << 0,
        CW = 1 << 1,
        CCW = 1 << 2
    }

    [Flags]
    public enum LatheMode : int
    {
        Disabled = 0,
        Diameter = 1, // Do not change
        Radius = 2    // Do not change
    }
}
