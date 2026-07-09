/*
 * GrblStatusProvider.cs - part of Grbl Code Sender
 *
 * ioSender's domain side of the UI test server seam. The server (CNC.UiTestServer) is app-agnostic; this
 * maps ioSender's live state - connection, controller state, streaming/job, loaded file, DRO - onto the
 * name/value pairs the server exposes at GET /status and that GET /waitfor?status=<name>&equals=<value>
 * polls. This is the ONE place that knows both ioSender's view-model and the test server.
 */

using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using CNC.Core;
using WpfUiTestServer;

namespace GCode_Sender
{
    internal sealed class GrblStatusProvider : IUiTestStatusProvider
    {
        private readonly Window _main;

        public GrblStatusProvider(Window main) { _main = main; }

        public IEnumerable<KeyValuePair<string, string>> GetStatus()
        {
            var list = new List<KeyValuePair<string, string>>();
            var m = _main?.DataContext as GrblViewModel;
            bool connected = Comms.com != null && Comms.com.IsOpen;

            Add(list, "connected", connected ? "true" : "false");
            Add(list, "state", m == null ? "" : m.GrblState.State.ToString());
            Add(list, "error", m == null ? "0" : m.GrblState.Error.ToString(CultureInfo.InvariantCulture));
            Add(list, "streaming", m == null ? "" : m.StreamingState.ToString());
            Add(list, "jobRunning", m != null && m.IsJobRunning ? "true" : "false");
            Add(list, "fileLoaded", m != null && m.IsFileLoaded ? "true" : "false");
            Add(list, "fileName", m == null ? "" : m.FileName);
            Add(list, "tool", m == null ? "" : m.Tool);
            Add(list, "message", m == null ? "" : m.Message);
            Add(list, "mposX", m == null ? "" : m.MachinePosition.X.ToString(CultureInfo.InvariantCulture));
            Add(list, "mposY", m == null ? "" : m.MachinePosition.Y.ToString(CultureInfo.InvariantCulture));
            Add(list, "mposZ", m == null ? "" : m.MachinePosition.Z.ToString(CultureInfo.InvariantCulture));
            Add(list, "wposX", m == null ? "" : m.WorkPosition.X.ToString(CultureInfo.InvariantCulture));
            Add(list, "wposY", m == null ? "" : m.WorkPosition.Y.ToString(CultureInfo.InvariantCulture));
            Add(list, "wposZ", m == null ? "" : m.WorkPosition.Z.ToString(CultureInfo.InvariantCulture));
            return list;
        }

        private static void Add(List<KeyValuePair<string, string>> list, string k, string v)
        {
            list.Add(new KeyValuePair<string, string>(k, v));
        }
    }
}
