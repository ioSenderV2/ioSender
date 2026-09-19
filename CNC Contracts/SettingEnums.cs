/*
 * SettingEnums.cs - part of CNC Contracts
 *
 * Moved verbatim from CNC Core\Grbl.cs (namespace kept: CNC.Core) - the $-setting number map
 * (legacy Grbl and grblHAL variants) plus the machine mode enums those settings encode. Pure
 * data: a client reading or editing controller settings over the wire addresses them by these
 * numbers, so they are contract material, not a CNC.Core dependency.
 *
 * v0.47 / 2026-04-29 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

namespace CNC.Core
{
    public enum GrblMode
    {
        Normal = 0,
        Laser,
        Lathe
    }

    public enum GrblEncoderMode
    {
        Unknown = 0,
        FeedRate = 1,
        RapidRate = 2,
        SpindleRPM = 3
    }

    public enum GrblSetting
    {
        PulseMicroseconds = 0,
        StepperIdleLockTime = 1,
        StepInvertMask = 2,
        DirInvertMask = 3,
        InvertStepperEnable = 4,
        LimitPinsInvertMask = 5,
        InvertProbePin = 6,
        StatusReportMask = 10,
        JunctionDeviation = 11,
        ArcTolerance = 12,
        ReportInches = 13,
        SoftLimitsEnable = 20,
        HardLimitsEnable = 21,
        HomingEnable = 22,
        HomingDirMask = 23,
        HomingFeedRate = 24,
        HomingSeekRate = 25,
        HomingDebounceDelay = 26,
        HomingPulloff = 27,
        G73Retract = 28,
        PulseDelayMicroseconds = 29,
        RpmMax = 30,
        RpmMin = 31,
        Mode = 32, // enum GrblMode
        PWMFreq = 33,
        PWMOffValue = 34,
        PWMMinValue = 35,
        PWMMaxValue = 36,
        TravelResolutionBase = 100,
        MaxFeedRateBase = 110,
        AccelerationBase = 120,
        MaxTravelBase = 130,
        MotorCurrentBase = 140,
    }

    public enum grblHALSetting
    {
        PulseMicroseconds = 0,
        StepperIdleLockTime = 1,
        StepInvertMask = 2,
        DirInvertMask = 3,
        InvertStepperEnable = 4,
        LimitPinsInvertMask = 5,
        InvertProbePin = 6,
        StatusReportMask = 10,
        JunctionDeviation = 11,
        ArcTolerance = 12,
        ReportInches = 13,
        ControlInvertMask = 14, // Note: Used for detecting GrblHAL firmware
        CoolantInvertMask = 15,
        SpindleInvertMask = 16,
        ControlPullUpDisableMask = 17,
        LimitPullUpDisableMask = 18,
        ProbePullUpDisable = 19,
        SoftLimitsEnable = 20,
        HardLimitsEnable = 21,
        HomingEnable = 22,
        HomingDirMask = 23,
        HomingFeedRate = 24,
        HomingSeekRate = 25,
        HomingDebounceDelay = 26,
        HomingPulloff = 27,
        G73Retract = 28,
        PulseDelayMicroseconds = 29,
        RpmMax = 30,
        RpmMin = 31,
        Mode = 32, // enum GrblMode
        PWMFreq = 33,
        PWMOffValue = 34,
        PWMMinValue = 35,
        PWMMaxValue = 36,
        StepperDeenergizeMask = 37,
        SpindlePPR = 38,
        EnableLegacyRTCommands = 39,
        SoftLimitJogging = 40,
        HomingLocateCycles = 43,
        HomingCycle_1 = 44,
        HomingCycle_2 = 45,
        HomingCycle_3 = 46,
        HomingCycle_4 = 47,
        HomingCycle_5 = 48,
        HomingCycle_6 = 49,
        JogStepSpeed = 50,
        JogSlowSpeed = 51,
        JogFastSpeed = 52,
        JogStepDistance = 53,
        JogSlowDistance = 54,
        JogFastDistance = 55,
        // Per axis settings
        TravelResolutionBase = 100,
        MaxFeedRateBase = 110,
        AccelerationBase = 120,
        MaxTravelBase = 130,
        MotorCurrentBase = 140,
        MicroStepsBase = 150,
        StallGuardBase = 200,
        // End per axis settings
        FtpPort0 = 308,
        FtpPort1 = 318,
        FtpPort2 = 328,
        ToolChangeMode = 341,
        UnlockAfterEStop = 484
    }
}
