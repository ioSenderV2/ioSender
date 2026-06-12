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
            btnStartSimulator.Click += btnStartSimulator_Click;
            btnStopSimulator.Click += btnStopSimulator_Click;
            btnDownloadSimulator.Click += btnDownloadSimulator_Click;
            UpdateSimulatorButtons();
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
            // Reflect (and round-trip) the saved simulator launch settings, and the "My Machine" toggle.
            if (AppConfig.Settings.Base != null)
            {
                if (!string.IsNullOrWhiteSpace(AppConfig.Settings.Base.SimulatorExe))
                    prop.SimulatorExe = AppConfig.Settings.Base.SimulatorExe;
                prop.SimulatorArgs = AppConfig.Settings.Base.SimulatorArgs ?? string.Empty;
            }
            chkUseMyMachine.IsChecked = !string.IsNullOrEmpty(prop.SimulatorArgs)
                && prop.SimulatorArgs.IndexOf(SimulatorManager.MyMachineEepromName, StringComparison.OrdinalIgnoreCase) >= 0;
            UpdateSimulatorButtons();   // re-evaluate now the saved exe name is applied (offers Download if absent)

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

            ShowDialog();

            return port;
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

        // "My Machine" profile: toggle the -e MyMachine.DAT argument into the simulator launch args (which are
        // persisted), so the simulator boots from the EEPROM mirrored from the user's real controller.
        private void chkUseMyMachine_Checked(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(prop.SimulatorArgs) || prop.SimulatorArgs.IndexOf(SimulatorManager.MyMachineEepromName, StringComparison.OrdinalIgnoreCase) < 0)
                prop.SimulatorArgs = (string.IsNullOrWhiteSpace(prop.SimulatorArgs) ? string.Empty : prop.SimulatorArgs.Trim() + " ") + "-e " + SimulatorManager.MyMachineEepromName;
        }

        private void chkUseMyMachine_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(prop.SimulatorArgs))
                prop.SimulatorArgs = prop.SimulatorArgs.Replace("-e " + SimulatorManager.MyMachineEepromName, string.Empty).Replace("  ", " ").Trim();
        }

        private void btnStartSimulator_Click(object sender, RoutedEventArgs e)
        {
            if (TryStartSimulator())
            {
                prop.SimulatorStarted = true;
            }
            else
            {
                prop.SimulatorStarted = false;
            }

            UpdateSimulatorButtons();
        }

        private void btnStopSimulator_Click(object sender, RoutedEventArgs e)
        {
            if (SimulatorManager.StopSimulator())
                txtSimulatorStatus.Text = "Stopped";
            else
                txtSimulatorStatus.Text = "No simulator running";

            prop.SimulatorStarted = false;
            UpdateSimulatorButtons();
        }

        private bool TryStartSimulator()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[] {
                System.IO.Path.Combine(baseDir, "simulator", prop.SimulatorExe),
                System.IO.Path.Combine(baseDir, prop.SimulatorExe)
            };

            string found = null;
            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) { found = c; break; }
            }

            if (found == null)
            {
                txtSimulatorStatus.Text = "Executable not found.";
                MessageBox.Show($"Simulator executable not found. Tried:\n{candidates[0]}\n{candidates[1]}", "Simulator start failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                string args = $"-p {prop.NetPort}";
                if (!string.IsNullOrWhiteSpace(prop.SimulatorArgs))
                    args += " " + prop.SimulatorArgs;

                if (SimulatorManager.StartSimulator(found, args, prop.AutoKillSimulator))
                {
                    txtSimulatorStatus.Text = "Running";
                    MessageBox.Show($"Simulator started (minimized):\n{found}", "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
                    return true;
                }
                else
                {
                    txtSimulatorStatus.Text = "Failed to start";
                    MessageBox.Show($"Failed to start simulator using '{found}' with args '{args}'.", "Simulator start failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                txtSimulatorStatus.Text = "Start error";
                MessageBox.Show($"Error starting simulator:\n{ex.Message}", "Simulator start failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // Fetch the simulator from the grblHAL web builder when it isn't bundled. Runs off the UI thread (the
        // build + download can take a while), then re-evaluates so Start becomes available and Download hides.
        private async void btnDownloadSimulator_Click(object sender, RoutedEventArgs e)
        {
            if (SimulatorManager.IsSimulatorRunning)
                SimulatorManager.StopSimulator();   // release any lock on the exe we are about to overwrite

            btnDownloadSimulator.IsEnabled = false;
            txtSimulatorStatus.Text = "Downloading simulator from the grblHAL web builder...";
            string err = null;
            bool ok = await System.Threading.Tasks.Task.Run(() => SimulatorManager.DownloadSimulator(out err));
            btnDownloadSimulator.IsEnabled = true;

            if (ok)
                txtSimulatorStatus.Text = "Simulator downloaded.";
            else
                MessageBox.Show("Could not download the simulator:\n\n" + err +
                    "\n\nYou can also build it manually at the grblHAL Web Builder (Simulator driver, Windows board) " +
                    "and place grblHAL_sim.exe in the application's 'simulator' folder.",
                    "Download simulator", MessageBoxButton.OK, MessageBoxImage.Warning);

            UpdateSimulatorButtons();
        }

        private void UpdateSimulatorButtons()
        {
            bool running = prop.SimulatorStarted || SimulatorManager.IsSimulatorRunning;
            bool found = SimulatorManager.FindExecutable(
                string.IsNullOrWhiteSpace(prop.SimulatorExe) ? "grblHAL_sim.exe" : prop.SimulatorExe) != null;

            btnStartSimulator.IsEnabled = !running && found;
            btnStopSimulator.IsEnabled = running;
            btnDownloadSimulator.Visibility = found ? Visibility.Collapsed : Visibility.Visible;

            if (running)
                txtSimulatorStatus.Text = "Running";
            else
                txtSimulatorStatus.Text = found ? "Not running" : "Executable not found - click Download";
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
