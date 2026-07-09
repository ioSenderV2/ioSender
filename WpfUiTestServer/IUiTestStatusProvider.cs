/*
 * IUiTestStatusProvider.cs - part of WpfUiTestServer
 *
 * The one seam between the app-agnostic UI test server and its host application. The server knows how to
 * address and drive any WPF UI by x:Uid; it knows nothing about a particular app's DOMAIN state (for ioSender:
 * connection, controller state, streaming/job, DRO). The host supplies that through this interface, and the
 * server surfaces it at GET /status and lets GET /waitfor?status=<name>&equals=<value> poll it.
 */

using System.Collections.Generic;

namespace WpfUiTestServer
{
    public interface IUiTestStatusProvider
    {
        // Ordered (name, value) pairs of host/domain state. Names become the /status JSON keys and the
        // /waitfor?status= query keys (matched case-insensitively); values are plain strings. Invoked on the
        // WPF UI thread, so an implementation may read view-models directly without marshalling.
        IEnumerable<KeyValuePair<string, string>> GetStatus();
    }
}
