/*
 * CommandMessages.cs - part of CNC Contracts
 *
 * The command channel: how a client tells the server to do something. This is the other direction
 * from StateMessages.cs/IMachineStateStream - state flows server->client, commands flow client->
 * server - and the two are deliberately asymmetric in shape, per the design worked out 2026-08-06:
 *
 *  - REALTIME commands (IMachineRealtimeChannel) are the single-byte, idempotent set grblHAL itself
 *    pulls out of the input stream ahead of line assembly (Feed Hold, reset, jog cancel, cycle start,
 *    overrides) - see grbl.cs's CMD_* constants, which this enum mirrors 1:1. Fire-and-forget: no
 *    result envelope, because the correct source of truth for "did the hold take" is the machine's
 *    own next status report, which already arrives over the state stream. Gating a Feed Hold button
 *    on a command round-trip would reintroduce the exact head-of-line-blocking bug this channel
 *    exists to avoid - see [[iosender-feedhold-input-starvation]]. On a real (non-in-process)
 *    transport this channel gets its OWN connection, so a stuck main channel can never take it down
 *    - see the half-open-socket lesson in MachineStateStream's header, same failure one layer up.
 *
 *  - QUEUED commands (IMachineCommands) are everything else - right now just Jog, the best-precedented
 *    piece (JogCommand already existed as pure data). These return a Task<CommandResult> even though
 *    the in-process implementation completes synchronously, because a real transport's round trip is
 *    genuinely asynchronous and the interface should not have to change shape when one arrives.
 *
 * Run control (RunProgram/StopJob/RewindJob) and MDI crossed the boundary 2026-08-08, one slice after
 * Jog. The shape follows Jog exactly: each command is a new DOOR into the real JobRunner - the engine
 * that already drives the run bar - never a second implementation. Semantics worth writing down:
 *
 *  - RunProgram means "the operator pressed Run": resume a hold, cycle-start a tool change, or start
 *    streaming the program from FromBlock - whatever the engine's state machine decides, exactly as
 *    the run-bar button does. Which program runs is the SERVER's business (its loaded job, or a host-
 *    registered active-program policy); the client does not get to pick.
 *  - A CommandResult's Success means ACCEPTED, not "it worked". The refusal gate is the engine's own
 *    enable-state (CanRun/CanStop/CanRewind - the exact booleans the run bar's buttons bind), so a
 *    client through this channel is refused precisely when the button would be greyed. What the
 *    machine then actually did arrives on the state stream, same truth-source rule as the realtime
 *    channel above.
 *  - Feed Hold is NOT here - it is realtime (above), by design.
 */

using System;
using System.Threading.Tasks;
using CNC.Core;   // JogCommand - physically in this assembly but kept in the CNC.Core namespace

namespace CNC.Contracts
{
    /// <summary>
    /// The realtime command set - mirrors grblHAL's own CMD_* single-byte codes (Grbl.cs) exactly,
    /// so mapping this enum to a wire byte on the server side is a lookup table, not a judgment call.
    /// Every member here is safe to send at any time, including mid-stream.
    /// </summary>
    public enum RealtimeCommand
    {
        FeedHold,
        CycleStart,
        SoftReset,
        JogCancel,
        SafetyDoor,
        OptionalStopToggle,
        SingleBlockToggle,
        MpgModeToggle,
        AutoReportingToggle,

        FeedOverrideReset,
        FeedOverrideCoarsePlus,
        FeedOverrideCoarseMinus,
        FeedOverrideFinePlus,
        FeedOverrideFineMinus,

        RapidOverrideFull,
        RapidOverrideMedium,
        RapidOverrideLow,

        SpindleOverrideReset,
        SpindleOverrideCoarsePlus,
        SpindleOverrideCoarseMinus,
        SpindleOverrideFinePlus,
        SpindleOverrideFineMinus,
        SpindleStop,

        CoolantFloodToggle,
        CoolantMistToggle
    }

    /// <summary>
    /// The realtime channel: fire-and-forget, never queued behind anything, never gated on a reply -
    /// see this file's header for why. An implementation must write straight to the controller's own
    /// realtime path (a single byte on the wire) and nothing else; it must never be routed through the
    /// same queue as streamed g-code or main-channel commands.
    /// </summary>
    public interface IMachineRealtimeChannel
    {
        void Send(RealtimeCommand command);
    }

    /// <summary>Outcome of a queued command. Success false + Error explains what refused it (e.g. "no
    /// axes moving", not a machine alarm - a real fault shows up on the state stream, not here).</summary>
    public class CommandResult
    {
        public long Id { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }

        public static CommandResult Ok(long id) { return new CommandResult { Id = id, Success = true }; }
        public static CommandResult Fail(long id, string error) { return new CommandResult { Id = id, Success = false, Error = error }; }
    }

    /// <summary>
    /// The queued command channel - everything that is NOT realtime. Grows one command at a time (see
    /// this file's header); Jog is the first.
    /// </summary>
    public interface IMachineCommands
    {
        Task<CommandResult> Jog(JogCommand jog);

        /// <summary>"The operator pressed Run": start the server's program from <paramref name="fromBlock"/>,
        /// or resume a hold/tool-change - the engine's state machine decides, exactly as the run bar does.
        /// Refused (Success false) when Run is not available in the current state.</summary>
        Task<CommandResult> RunProgram(int fromBlock);

        /// <summary>"The operator pressed Stop": abort the run in progress. Refused when there is nothing
        /// to stop. (Feed Hold is NOT this - a hold is realtime, see IMachineRealtimeChannel.)</summary>
        Task<CommandResult> StopJob();

        /// <summary>Rewind a part-streamed program to its start. Refused when there is nothing to rewind.</summary>
        Task<CommandResult> RewindJob();

        /// <summary>One manual g-code/system command line (MDI). Accepted means queued with the engine;
        /// the reply, like all machine truth, arrives via the state stream/console - not here.</summary>
        Task<CommandResult> Mdi(string command);
    }
}
