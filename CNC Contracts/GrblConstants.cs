/*
 * GrblConstants.cs - part of CNC Contracts
 *
 * Moved verbatim from CNC Core\Grbl.cs (namespace kept: CNC.Core) - these constants ARE the
 * protocol: the realtime single-byte command set, the $-command strings, axis indices and the
 * display formats a client renders coordinates with. Pure const data, so it satisfies the
 * assembly rule (nothing here couldn't be JSON) and every client-side use of a command byte or
 * format string stops being a CNC.Core dependency.
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
    public class GrblConstants
    {
        public const byte
            CMD_EXIT = 0x03, // ctrl-C
            CMD_RESET = 0x18, // ctrl-X
            CMD_STOP = 0x19, // ctrl-Y
            CMD_STATUS_REPORT = 0x80,
            CMD_CYCLE_START = 0x81,
            CMD_FEED_HOLD = 0x82,
            CMD_GCODE_REPORT = 0x83,
            CMD_SAFETY_DOOR = 0x84,
            CMD_JOG_CANCEL = 0x85,
            CMD_STATUS_REPORT_ALL = 0x87,
            CMD_OPTIONAL_STOP_TOGGLE = 0x88,
            CMD_SINGLE_BLOCK_TOGGLE = 0x89,
            CMD_OVERRIDE_FAN0_TOGGLE = 0x8A,
            CMD_MPG_MODE_TOGGLE = 0x8B,
            CMD_AUTO_REPORTING_TOGGLE = 0x8C,
            CMD_FEED_OVR_RESET = 0x90,
            CMD_FEED_OVR_COARSE_PLUS = 0x91,
            CMD_FEED_OVR_COARSE_MINUS = 0x92,
            CMD_FEED_OVR_FINE_PLUS = 0x93,
            CMD_FEED_OVR_FINE_MINUS = 0x94,
            CMD_RAPID_OVR_RESET = 0x95,
            CMD_RAPID_OVR_MEDIUM = 0x96,
            CMD_RAPID_OVR_LOW = 0x97,
            CMD_SPINDLE_OVR_RESET = 0x99,
            CMD_SPINDLE_OVR_COARSE_PLUS = 0x9A,
            CMD_SPINDLE_OVR_COARSE_MINUS = 0x9B,
            CMD_SPINDLE_OVR_FINE_PLUS = 0x9C,
            CMD_SPINDLE_OVR_FINE_MINUS = 0x9D,
            CMD_SPINDLE_OVR_STOP = 0x9E,
            CMD_COOLANT_FLOOD_OVR_TOGGLE = 0xA0,
            CMD_COOLANT_MIST_OVR_TOGGLE = 0xA1,
            CMD_PID_REPORT = 0xA2,
            CMD_TOOL_ACK = 0xA3,
            CMD_PROBE_CONNECTED_TOGGLE = 0xA4;

        public const string
            CMD_STATUS_REPORT_LEGACY = "?",
            CMD_CYCLE_START_LEGACY = "~",
            CMD_FEED_HOLD_LEGACY = "!",
            CMD_UNLOCK = "$X",
            CMD_HOMING = "$H",
            CMD_CHECK = "$C",
            CMD_GETSETTINGS = "$$",
            CMD_GETSETTINGS_ALL = "$+",
            CMD_GETPARSERSTATE = "$G",
            CMD_GETINFO = "$I",
            CMD_GETINFO_EXTENDED = "$I+",
            CMD_GETNGCPARAMETERS = "$#",
            CMD_GETSTARTUPLINES = "$N",
            CMD_GETSETTINGSDETAILS = "$ES",
            CMD_GETSETTINGSGROUPS = "$EG",
            CMD_GETALARMCODES = "$EA",
            CMD_GETERRORCODES = "$EE",
            CMD_GETSPINDLES = "$SPINDLESH",
            CMD_PROGRAM_DEMARCATION = "%",
            CMD_SDCARD_MOUNT = "$FM",
            CMD_SDCARD_DIR = "$F",
            CMD_SDCARD_DIR_ALL = "$F+",
            CMD_SDCARD_REWIND = "$FR",
            CMD_SDCARD_RUN = "$F=",
            CMD_SDCARD_UNLINK = "$FD=",
            CMD_SDCARD_DUMP = "$F<=",
            CMD_FS_PWD = "$PWD",
            AXISLETTERS = "XYZABCUVW",
            FORMAT_METRIC = "###0.000",
            FORMAT_IMPERIAL = "##0.0000",
            NO_TOOL = "None",
            THCSIGNALS = "AERTOVHDUBF"; // Keep in sync with THCSignals enum (MachineEnums.cs)!!

        public const int
            X_AXIS = 0,
            Y_AXIS = 1,
            Z_AXIS = 2,
            A_AXIS = 3,
            B_AXIS = 4,
            C_AXIS = 5,
            U_AXIS = 6,
            V_AXIS = 7,
            W_AXIS = 8;
    }
}
