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
using System.Diagnostics;
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
