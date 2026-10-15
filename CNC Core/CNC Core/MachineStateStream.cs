/*
 * MachineStateStream.cs - part of CNC Core library
 *
 * The in-process implementation of IMachineStateStream (step 6a of the client/server split): pumps a
 * MachineDeltaProducer every time GrblViewModel finishes parsing a status report. That hook is the
 * right cadence twice over - status reports ARE the poll tick, and OnRealtimeStatusProcessed fires on
 * the same thread that just mutated MachineState, which is exactly the producer's threading contract.
 * Changes made outside a status report (handshakes, Clear) ride out with the next report's delta;
 * with no connection there are no reports and no deltas, which is correct - there is no machine to
 * mirror.
 *
 * Multi-subscriber over a single-shadow producer has one trap, and Subscribe steps around it: the
 * producer's Snapshot() refreshes the shadow, so taking a newcomer's snapshot with changes pending
 * would silently swallow those changes for every EXISTING subscriber (their next Poll diffs against
 * the refreshed shadow and sees nothing). Subscribe therefore flushes a Poll() to existing
 * subscribers first, then captures the snapshot - the flush makes the refresh a no-op.
 *
 * Observability: with -debuglog=delta (or a bare -debuglog), every emitted message is written to the
 * debug log as its wire JSON - the first place the actual byte-for-byte protocol becomes visible in
 * the running app.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using CNC.Contracts;

namespace CNC.Core
{
    public class MachineStateStream : IMachineStateStream, IDisposable
    {
        private readonly GrblViewModel model;
        private readonly MachineDeltaProducer producer;
        private readonly object sync = new object();
        private readonly List<System.Action<MachineDelta>> subscribers = new List<System.Action<MachineDelta>>();

        private static readonly JsonSerializerOptions wireJson = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // NOT optional: GrblState is a struct with public FIELDS (the parser mutates its members
            // individually - see MachineState's remarks), and System.Text.Json silently serializes a
            // fields-only type as {} unless fields are enabled. Found live: the first real wire log
            // had every run state as an empty object. Any future wire serializer needs the same.
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public MachineStateStream(GrblViewModel model)
        {
            this.model = model;
            producer = new MachineDeltaProducer(model.State);
            model.OnRealtimeStatusProcessed += Pump;
        }

        public void Dispose()
        {
            model.OnRealtimeStatusProcessed -= Pump;
        }

        public IDisposable Subscribe(System.Action<MachineDelta> handler)
        {
            lock (sync)
            {
                // Flush pending changes to the current subscribers BEFORE Snapshot() refreshes the
                // shadow, or those changes would vanish for them - see the header.
                Flush();
                subscribers.Add(handler);
                MachineDelta snapshot = producer.Snapshot();
                Log(snapshot);
                handler(snapshot);
            }
            return new Subscription(this, handler);
        }

        public void RequestSnapshot()
        {
            lock (sync)
            {
                MachineDelta snapshot = producer.Snapshot();
                Log(snapshot);
                foreach (var handler in subscribers)
                    handler(snapshot);
            }
        }

        private void Pump(string report)
        {
            lock (sync)
                Flush();
        }

        // Must hold sync. Emits one delta covering everything changed since the last emission.
        private void Flush()
        {
            MachineDelta delta = producer.Poll();
            if (delta == null)
                return;
            Log(delta);
            foreach (var handler in subscribers)
                handler(delta);
        }

        // Once per MESSAGE, not per delivery - the log is the wire trace, and one delta fanned out to
        // three subscribers is still one message on the (future) wire.
        private static void Log(MachineDelta message)
        {
            if (DebugLog.Enabled)
                DebugLog.Write("delta", JsonSerializer.Serialize(message, wireJson));
        }

        private sealed class Subscription : IDisposable
        {
            private readonly MachineStateStream owner;
            private System.Action<MachineDelta> handler;

            public Subscription(MachineStateStream owner, System.Action<MachineDelta> handler)
            {
                this.owner = owner;
                this.handler = handler;
            }

            public void Dispose()
            {
                if (handler == null)
                    return;
                lock (owner.sync)
                    owner.subscribers.Remove(handler);
                handler = null;
            }
        }
    }
}
