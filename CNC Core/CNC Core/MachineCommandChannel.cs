/*
 * MachineCommandChannel.cs - part of CNC Core library
 *
 * In-process implementations of CNC.Contracts' command channel (step 6a, the client->server
 * direction, symmetric with MachineStateStream for server->client). See CommandMessages.cs for the
 * design; this file is the mechanism.
 */

using System;
using System.Threading.Tasks;
using CNC.Contracts;

namespace CNC.Core
{
    /// <summary>
    /// Realtime commands write STRAIGHT to the controller's single-byte realtime path
    /// (Comms.com.WriteByte) - never through JobRunner's queue, never through anything that could
    /// block behind a streamed program. This is the one-line lookup table from the portable enum to
    /// grblHAL's own wire byte (GrblConstants.CMD_*); the enum and the table are meant to be trivial
    /// to keep in sync by inspection, which is why the mapping lives in one switch rather than
    /// anything cleverer.
    /// </summary>
    public class MachineRealtimeChannel : IMachineRealtimeChannel
    {
        public void Send(RealtimeCommand command)
        {
            Comms.com.WriteByte(ToByte(command));
        }

        private static byte ToByte(RealtimeCommand command)
        {
            switch (command)
            {
                case RealtimeCommand.FeedHold: return GrblConstants.CMD_FEED_HOLD;
                case RealtimeCommand.CycleStart: return GrblConstants.CMD_CYCLE_START;
                case RealtimeCommand.SoftReset: return GrblConstants.CMD_RESET;
                case RealtimeCommand.JogCancel: return GrblConstants.CMD_JOG_CANCEL;
                case RealtimeCommand.SafetyDoor: return GrblConstants.CMD_SAFETY_DOOR;
                case RealtimeCommand.OptionalStopToggle: return GrblConstants.CMD_OPTIONAL_STOP_TOGGLE;
                case RealtimeCommand.SingleBlockToggle: return GrblConstants.CMD_SINGLE_BLOCK_TOGGLE;
                case RealtimeCommand.MpgModeToggle: return GrblConstants.CMD_MPG_MODE_TOGGLE;
                case RealtimeCommand.AutoReportingToggle: return GrblConstants.CMD_AUTO_REPORTING_TOGGLE;

                case RealtimeCommand.FeedOverrideReset: return GrblConstants.CMD_FEED_OVR_RESET;
                case RealtimeCommand.FeedOverrideCoarsePlus: return GrblConstants.CMD_FEED_OVR_COARSE_PLUS;
                case RealtimeCommand.FeedOverrideCoarseMinus: return GrblConstants.CMD_FEED_OVR_COARSE_MINUS;
                case RealtimeCommand.FeedOverrideFinePlus: return GrblConstants.CMD_FEED_OVR_FINE_PLUS;
                case RealtimeCommand.FeedOverrideFineMinus: return GrblConstants.CMD_FEED_OVR_FINE_MINUS;

                case RealtimeCommand.RapidOverrideFull: return GrblConstants.CMD_RAPID_OVR_RESET;
                case RealtimeCommand.RapidOverrideMedium: return GrblConstants.CMD_RAPID_OVR_MEDIUM;
                case RealtimeCommand.RapidOverrideLow: return GrblConstants.CMD_RAPID_OVR_LOW;

                case RealtimeCommand.SpindleOverrideReset: return GrblConstants.CMD_SPINDLE_OVR_RESET;
                case RealtimeCommand.SpindleOverrideCoarsePlus: return GrblConstants.CMD_SPINDLE_OVR_COARSE_PLUS;
                case RealtimeCommand.SpindleOverrideCoarseMinus: return GrblConstants.CMD_SPINDLE_OVR_COARSE_MINUS;
                case RealtimeCommand.SpindleOverrideFinePlus: return GrblConstants.CMD_SPINDLE_OVR_FINE_PLUS;
                case RealtimeCommand.SpindleOverrideFineMinus: return GrblConstants.CMD_SPINDLE_OVR_FINE_MINUS;
                case RealtimeCommand.SpindleStop: return GrblConstants.CMD_SPINDLE_OVR_STOP;

                case RealtimeCommand.CoolantFloodToggle: return GrblConstants.CMD_COOLANT_FLOOD_OVR_TOGGLE;
                case RealtimeCommand.CoolantMistToggle: return GrblConstants.CMD_COOLANT_MIST_OVR_TOGGLE;

                default: throw new ArgumentOutOfRangeException(nameof(command), command, "no wire byte mapped for this realtime command");
            }
        }
    }

    /// <summary>
    /// Queued commands. In-process today, so every call completes synchronously before the returned
    /// Task is handed back - but the interface is Task-shaped because a real transport's round trip
    /// genuinely is asynchronous, and the call site should not have to change when one arrives.
    /// </summary>
    public class MachineCommandChannel : IMachineCommands
    {
        private readonly GrblViewModel model;
        private readonly JobRunner runner;
        private long nextId = 0;

        // runner is optional so a host that only jogs (or a harness that only tests jogging) need not
        // build a streaming engine; run-control commands then refuse rather than throw. No static
        // JobRunner.Instance exists to fall back on, deliberately - same no-static-in-Core rule as
        // GCodeProgram (a server has no "the one" job runner; the host wires the instance it means).
        public MachineCommandChannel(GrblViewModel model, JobRunner runner = null)
        {
            this.model = model ?? throw new ArgumentNullException(nameof(model));
            this.runner = runner;
        }

        public Task<CommandResult> Jog(JogCommand jog)
        {
            long id = System.Threading.Interlocked.Increment(ref nextId);

            if (jog == null)
                return Task.FromResult(CommandResult.Fail(id, "no jog command supplied"));

            // Reuses the existing engine verbatim - JogController owns clamping, $J= rendering and the
            // actual write; this channel is only a new DOOR into it, not a second implementation. See
            // JogController.Execute's own doc comment for what "false" means (nothing to do).
            bool sent = model.Keyboard.Execute(jog);

            return Task.FromResult(sent ? CommandResult.Ok(id) : CommandResult.Fail(id, "nothing to jog (no axes moving, or jog mode not set)"));
        }

        // Run control: the gates are the engine's OWN enable-state - CanRun/CanStop/CanRewind, the exact
        // booleans the run bar's buttons bind - so a client through this channel is refused precisely when
        // the button would be greyed, and there is no second copy of the gating logic to drift. The host
        // keeps that state fresh the same way it does for its buttons (the engine's handlers write most of
        // it; ioSender's JobControl adds the idle/file-load refresh, and a headless host owns that role).

        public Task<CommandResult> RunProgram(int fromBlock)
        {
            long id = System.Threading.Interlocked.Increment(ref nextId);

            if (runner == null)
                return Task.FromResult(CommandResult.Fail(id, "no job runner attached to this channel"));
            if (!runner.CanRun)
                return Task.FromResult(CommandResult.Fail(id, "run is not available in the current state"));

            runner.Run(fromBlock);

            return Task.FromResult(CommandResult.Ok(id));
        }

        public Task<CommandResult> StopJob()
        {
            long id = System.Threading.Interlocked.Increment(ref nextId);

            if (runner == null)
                return Task.FromResult(CommandResult.Fail(id, "no job runner attached to this channel"));
            if (!runner.CanStop)
                return Task.FromResult(CommandResult.Fail(id, "there is no run to stop"));

            // Abort(), NOT Stop() - the naming is inverted (found on real hardware 2026-08-08, see
            // JobRunner.Run's onOperatorCancel comment): Stop() marks the job operator-stopped FIRST,
            // which suppresses the CMD_STOP byte in StreamingIdle's Stop case. Abort() is what the
            // run bar's Stop button calls, and it lets the stop byte go out.
            runner.Abort();

            return Task.FromResult(CommandResult.Ok(id));
        }

        public Task<CommandResult> RewindJob()
        {
            long id = System.Threading.Interlocked.Increment(ref nextId);

            if (runner == null)
                return Task.FromResult(CommandResult.Fail(id, "no job runner attached to this channel"));
            if (!runner.CanRewind)
                return Task.FromResult(CommandResult.Fail(id, "there is nothing to rewind"));

            runner.Rewind();

            return Task.FromResult(CommandResult.Ok(id));
        }

        public Task<CommandResult> Mdi(string command)
        {
            long id = System.Threading.Interlocked.Increment(ref nextId);

            if (runner == null)
                return Task.FromResult(CommandResult.Fail(id, "no job runner attached to this channel"));
            if (string.IsNullOrWhiteSpace(command))
                return Task.FromResult(CommandResult.Fail(id, "no command supplied"));

            // Accepted means HANDED to the engine, not delivered: SendCommand itself still drops a
            // command whose streaming state disallows it (with a -debuglog=jobrunner trace) - the same
            // behaviour the UI's MDI field gets. The console/state stream carries the actual outcome.
            runner.SendCommand(command);

            return Task.FromResult(CommandResult.Ok(id));
        }
    }
}
