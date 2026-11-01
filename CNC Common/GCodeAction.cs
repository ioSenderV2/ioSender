/*
 * GCodeAction.cs - part of CNC Common
 *
 * The g-code document-building action (AddBlock's first-line/append/finalize discriminator),
 * moved verbatim from CNC Core\GCodeJob.cs (namespace kept: CNC.Core). First piece of the g-code
 * DOCUMENT MODEL to land in Common: converters and transformers (client-side document tooling)
 * build programs with it, and the server-side program model consumes it - shared library surface,
 * no machine I/O.
 *
 * ⚠ This enum is the CNC.Core.Action that SHADOWS System.Action inside any file with
 * `using CNC.Core;` - the trap is documented in Grbl.cs and has bitten three times. Files in this
 * assembly must write System.Action explicitly.
 */

namespace CNC.Core
{
    public enum Action
    {
        New,
        Add,
        End
    }
}
