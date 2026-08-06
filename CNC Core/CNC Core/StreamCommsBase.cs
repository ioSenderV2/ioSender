/*
 * StreamCommsBase.cs - part of CNC Core library
 *
 * The shared write path for every transport (serial, telnet, websocket, Eltima).
 *
 * StreamComms is an INTERFACE, so until now the four transports had no shared implementation and
 * anything cross-cutting had to be copied into each one - or, in practice, into whichever one someone
 * happened to be debugging. That is exactly what the codebase looked like: TelnetStream carried a
 * hand-added "TEMPORARY DIAGNOSTIC (2026-07-12)" tracing its writes, and Serial and Websocket carried
 * nothing at all. On 2026-08-06 that asymmetry decided what could be diagnosed - the operator was on
 * telnet, so the trace existed; on serial the same investigation would have had no TX data whatsoever.
 *
 * So the tap belongs where the writes converge, not sprinkled across the implementations. Every write
 * ultimately becomes either one byte or a byte range, so those are the two abstract leaves each transport
 * supplies; the public entry points here trace and delegate. WriteString/WriteCommand need no changes and
 * are not hoisted - they route through these leaves and are therefore traced for free.
 *
 * Deliberately NOT a dedup exercise. The transports' WriteString/WriteCommand are near-identical and
 * could be collapsed, but they differ in ways that are load-bearing rather than accidental - SerialStream
 * encodes commands UTF8 while its WriteString uses Encoding.Default, and single-byte writes stay
 * synchronous everywhere so a realtime Reset/Feed-Hold is never queued behind a streamer's async write.
 * Collapsing those silently changes what reaches a machine. The goal here is one tap, not fewer lines.
 */

namespace CNC.Core
{
    public abstract class StreamCommsBase
    {
        /// <summary>
        /// Write exactly one byte. Realtime commands come through here ('?', Reset, Feed Hold, Cycle Start,
        /// jog cancel), so implementations must keep this synchronous and must never let it queue behind a
        /// bulk write - see StreamComms.BlockingWrites.
        /// </summary>
        protected abstract void WriteByteRaw(byte data);

        /// <summary>Write <paramref name="len"/> bytes of <paramref name="bytes"/>.</summary>
        protected abstract void WriteBytesRaw(byte[] bytes, int len);

        public void WriteByte(byte data)
        {
            WireLog.TxByte(data);
            WriteByteRaw(data);
        }

        public void WriteBytes(byte[] bytes, int len)
        {
            WireLog.TxBytes(bytes, len);
            WriteBytesRaw(bytes, len);
        }

        /// <summary>
        /// Trace a write that legitimately does not route through the leaves above. Exactly one caller:
        /// SerialStream.WriteCommand, which does its own SYNCHRONOUS write - sending it through WriteBytes
        /// would quietly make it asynchronous (WriteBytes uses WriteAsync unless BlockingWrites is set) and
        /// change the ordering guarantees of the code that talks to the machine. Tracing it explicitly is
        /// honest; "simplifying" it is not.
        /// </summary>
        protected static void TraceRawWrite(byte[] bytes, int len)
        {
            WireLog.TxBytes(bytes, len);
        }
    }
}
