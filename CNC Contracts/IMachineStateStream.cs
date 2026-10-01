/*
 * IMachineStateStream.cs - part of CNC Contracts
 *
 * The state channel's shape: how a client receives MachineDelta messages. This is the one kind of
 * non-data type contracts holds - an interface whose entire signature is contract types is the
 * protocol description in C# form (a browser client implements the same semantics over its socket;
 * the "could it be JSON" rule governs what the channel CARRIES, and this declares no more than that).
 * No implementation lives here: in-process today (CNC.Core.MachineStateStream), a socket later,
 * same contract.
 *
 * Semantics an implementation must honor (and tools/delta-probe checks):
 *  - Subscribe is snapshot-first: the handler's first message is a full snapshot (Changed = All at
 *    the current Seq), then every subsequent delta in order. No subscriber ever misses a change
 *    between its snapshot and its first delta - implementations must flush pending changes to
 *    existing subscribers before capturing a newcomer's snapshot.
 *  - Seq on consecutive deltas increases by exactly 1; a subscriber that observes a gap resyncs via
 *    RequestSnapshot rather than continuing to apply.
 *  - RequestSnapshot delivers a fresh Changed=All message to ALL subscribers.
 *  - Delivery thread is the implementation's; handlers must be fast and must not mutate state.
 */

using System;

namespace CNC.Contracts
{
    public interface IMachineStateStream
    {
        /// <summary>
        /// Register for state messages. The handler immediately receives a full snapshot, then every
        /// delta in order. Dispose the returned token to unsubscribe.
        /// </summary>
        IDisposable Subscribe(Action<MachineDelta> handler);

        /// <summary>Deliver a fresh full snapshot (Changed = All) to every subscriber - the resync path.</summary>
        void RequestSnapshot();
    }
}
