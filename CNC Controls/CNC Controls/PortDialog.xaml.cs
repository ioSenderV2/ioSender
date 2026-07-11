/*
 * PortDialog.xaml.cs - part of CNC Controls library
 *
 * v0.47 / 2025-09-25 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2025, Io Engineering (Terje Io)
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CNC.Core;

namespace CNC.Controls
{
    public partial class PortDialog : Window
    {
        private string port = null, handshake = string.Empty;
        private PortProperties prop;
        public PortDialog()
        {
            InitializeComponent();
            DialogScaling.Apply(this);

            DataContext = prop = new PortProperties();
        }

        private void CbxPorts_DropDownOpened(object sender, System.EventArgs e)
        {
            prop.Com.Refresh();
        }

        private bool PortAvailable(string port)
        {
            bool found = false;

            foreach (var p in prop.Com.Ports)
                found = found || p.Name == port;

            return found;
        }

        private void parsenet(string uri)
        {
            int port = 0;
            string[] values = uri.Split(':');

            prop.IpAddress = values[0];
            if (values.Length == 2 && int.TryParse(values[1], out port))
                prop.NetPort = port;
            else
                prop.NetPort = prop.IsWebSocket ? 80 : 23;

            tab.SelectedIndex = 1;
        }

        public string ShowDialog(string orgport)
        {
            // Own the dialog to the top-most VISIBLE window so it can never be hidden behind it, and keep it off
            // the taskbar. At startup the main window is still invisible (shown at Opacity 0) while a Topmost
            // splash is up, so owning to MainWindow would drop this dialog behind the splash - pick the visible
            // Topmost window (the splash) instead and match its Topmost so the dialog wins. Owner must be set
            // before ShowDialog for WindowStartupLocation=CenterOwner.
            var wins = Application.Current?.Windows?.OfType<Window>();
            Window owner = null;
            if (wins != null)
                owner = wins.FirstOrDefault(w => w != this && w.IsVisible && w.Topmost)     // startup splash
                     ?? wins.FirstOrDefault(w => w != this && w.IsVisible && w.IsActive)    // normal active window
                     ?? Application.Current.MainWindow;
            if (owner != null && owner != this && owner.IsLoaded)
            {
                Owner = owner;
                Topmost = owner.Topmost;   // beat a Topmost splash; no-op for the normal main window
            }
            else
                Topmost = true;            // no suitable visible owner - float on top rather than get lost

            // At startup the dialog owns to the Topmost splash (above). The splash now docks itself to the top
            // of the work area, so just center on the SCREEN - it lands in the middle of the display, clear of
            // the splash, instead of centering over the (top-docked) splash. The normal (menu) case keeps
            // CenterOwner over the main window.
            if (Topmost)
                WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Network is the default transport: open on the Network tab unless an existing serial/simulator
            // target below selects another. Most grblHAL controllers are networked, and the Scan button lives
            // here - so a first-time / no-target connect lands where discovery is.
            tab.SelectedItem = tabNetwork;

            // Default the network tab's host to the last successfully-connected IP (falls back to the mDNS
            // name set in PortProperties). If the saved target itself is a network one, parsenet below
            // overrides this with that exact host.
            if (!string.IsNullOrWhiteSpace(AppConfig.Settings.Base?.NetworkHost))
                prop.IpAddress = AppConfig.Settings.Base.NetworkHost;

            if (!string.IsNullOrEmpty(orgport)) {

                if ((prop.IsWebSocket = orgport.ToLower().StartsWith("ws://")))
                    parsenet(orgport.Substring(5));
                else if (orgport.IndexOf(':') > 0 && !orgport.ToLower().StartsWith("com")) // host:port (IP or hostname)
                    parsenet(orgport);
                else
                {
                    string portname = orgport.Substring(0, orgport.IndexOf(':'));
                    if (PortAvailable(portname))
                    {
                        prop.Com.SelectedPort = portname;
                        string[] values = orgport.Split(':')[1].Split(',');

                        if (!prop.Com.Baud.Contains(values[0]))
                            prop.Com.Baud.Add(values[0]);

                        prop.Com.SelectedBaud = values[0];

                        handshake = values.Length > 4 && (values[4] == "X" || values[4] == "P") ? "," + values[4] : string.Empty;

                        if (values.Length > 5)
                        {
                            Comms.ResetMode mode = Comms.ResetMode.None;
                            Enum.TryParse(values[5], true, out mode);
                            if (mode != Comms.ResetMode.None)
                            {
                                foreach (ConnectMode m in prop.Com.ConnectModes)
                                    if (m.Mode == mode)
                                        prop.Com.SelectedMode = m;
                            }
                        }
                    }
                }
            }

            HighlightActiveConnection(orgport);

            // Seed the host combo with the current default (grblHAL.local or the last-connected IP) so the
            // dropdown isn't empty before a scan; Scan network replaces this with the discovered controllers.
            if (!string.IsNullOrWhiteSpace(prop.IpAddress))
                prop.DiscoveredHosts.Add(new DiscoveredController { Host = prop.IpAddress, Port = prop.NetPort });

            ShowDialog();

            return port;
        }

        // Open on - and green-tint - the tab for the active/current connection, so the transport in use is
        // obvious and a reconnect starts on the right tab. "Active" means a real target has been configured
        // (Base.PortParams is not the unset "COMn..." placeholder); with none, the default tab is left as-is.
        // The simulator and a plain localhost network target both look like host:port, so Base.StartSimulator
        // disambiguates them.
        private void HighlightActiveConnection(string orgport)
        {
            if (string.IsNullOrWhiteSpace(orgport) || orgport.ToLower().StartsWith("comn"))
                return;   // no configured target yet - keep the default tab, no highlight

            var active = (AppConfig.Settings.Base != null && AppConfig.Settings.Base.StartSimulator) ? tabSimulator
                       : orgport.ToLower().StartsWith("com") ? tabSerial
                       : tabNetwork;   // ws:// or host:port

            tab.SelectedItem = active;
            active.Foreground = System.Windows.Media.Brushes.ForestGreen;
            active.FontWeight = FontWeights.Bold;
        }

        // Exposed so the caller can persist whether this connection is the bundled simulator (and how to
        // launch it) - used to auto-start the simulator on a later startup auto-reconnect to the same target.
        public bool IsSimulatorConnection { get; private set; }
        public string SelectedSimulatorExe { get { return prop.SimulatorExe; } }
        public string SelectedSimulatorArgs { get { return prop.SimulatorArgs; } }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            IsSimulatorConnection = tab.SelectedIndex == 2;

            if (tab.SelectedIndex == 1)
            {
                port = string.Format("{0}{1}:{2}", prop.IsWebSocket ? "ws://" : string.Empty, prop.IpAddress, prop.NetPort.ToString());
            }
            else if (tab.SelectedIndex == 2)
            {
                port = string.Format("127.0.0.1:{0}", prop.NetPort.ToString());
            }
            else if(prop.Com.Ports.Count > 0)
            {
                port = prop.Com.SelectedPort + ":" + prop.Com.SelectedBaud + ",N,8,1" + handshake;
                if (prop.Com.SelectedMode.Mode != Comms.ResetMode.None)
                    port += (handshake == string.Empty ? ",," : ",") + prop.Com.SelectedMode.Mode.ToString();
            }

            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Telnet port the scan probes with the raw grbl '?'/$I handshake. WebSocket-only controllers (no
        // open port 23) aren't discovered yet - a follow-up could add a WS-upgrade probe on 80/81.
        private const int ScanPort = 23;

        private CancellationTokenSource scanCts;

        private async void btnScan_Click(object sender, RoutedEventArgs e)
        {
            // Second click while a scan is running = cancel.
            if (scanCts != null)
            {
                scanCts.Cancel();
                return;
            }

            scanCts = new CancellationTokenSource();
            btnScan.Content = "Cancel";

            string typedHost = prop.IpAddress;   // preserve what the user had typed, in case the scan finds nothing
            var progress = new Progress<string>(s => scanStatus.Text = s);
            List<DiscoveredController> results = null;

            try
            {
                results = await NetworkScanner.DiscoverAsync(ScanPort, progress, scanCts.Token);
            }
            catch (OperationCanceledException)
            {
                scanStatus.Text = "Scan cancelled";
            }
            catch (Exception ex)
            {
                scanStatus.Text = "Scan failed: " + ex.Message;
            }
            finally
            {
                scanCts.Dispose();
                scanCts = null;
                btnScan.Content = "Scan network";
            }

            if (results == null)
                return;

            if (results.Count == 0)
            {
                scanStatus.Text = "No controllers found";
                return;
            }

            // Repopulate the host combo with the discovered controllers and select the first, which fills the
            // IP/port. Keep the user's typed host too (unless a scan hit already covers it) so it's not lost.
            scanStatus.Text = string.Format("{0} controller{1} found", results.Count, results.Count == 1 ? "" : "s");
            prop.DiscoveredHosts.Clear();
            foreach (var c in results)
                prop.DiscoveredHosts.Add(c);
            if (!string.IsNullOrWhiteSpace(typedHost) && !results.Any(c => c.Host == typedHost))
                prop.DiscoveredHosts.Add(new DiscoveredController { Host = typedHost, Port = prop.NetPort });

            cbxHost.SelectedItem = results[0];   // sets host + port (via cbxHost_SelectionChanged)
        }

        private void cbxHost_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var c = cbxHost.SelectedItem as DiscoveredController;
            if (c != null)
            {
                prop.IsWebSocket = false;
                prop.IpAddress = c.Host;
                prop.NetPort = c.Port;
            }
        }
    }

    class PortProperties : ViewModelBase
    {
        bool isWebSocket = false;
        string ipAddress = "grblHAL.local";
        int netport = 23;
        bool simulatorStarted = false;
        string simulatorExe = "grblHAL_sim.exe";
        string simulatorArgs = string.Empty;
        bool autoKillSimulator = true;

        // Hosts shown in the Network tab's editable IP combo: seeded with the default/last host, then
        // repopulated with the controllers found by Scan network.
        public System.Collections.ObjectModel.ObservableCollection<DiscoveredController> DiscoveredHosts { get; }
            = new System.Collections.ObjectModel.ObservableCollection<DiscoveredController>();

        public SerialPorts Com { get; private set; } = new SerialPorts();
        public bool IsWebSocket {
            get { return isWebSocket; }
            set {
                if (isWebSocket != value)
                    NetPort = value ? 80 : 23;
                isWebSocket = value;
                OnPropertyChanged();
            }
        }
        public bool SimulatorStarted { get { return simulatorStarted; } set { simulatorStarted = value; OnPropertyChanged(); } }
        public string SimulatorExe { get { return simulatorExe; } set { simulatorExe = value; OnPropertyChanged(); } }
        public string SimulatorArgs { get { return simulatorArgs; } set { simulatorArgs = value; OnPropertyChanged(); } }
        public bool AutoKillSimulator { get { return autoKillSimulator; } set { autoKillSimulator = value; OnPropertyChanged(); } }
        public string IpAddress { get { return ipAddress; } set { ipAddress = value; OnPropertyChanged(); } }
        public int NetPort { get { return netport; } set { netport = value; OnPropertyChanged(); } }
    }
}
