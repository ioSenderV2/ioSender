using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    // Settings > Simulator: lets the user pick grblHAL compile options and build a simulator via the same
    // CI workflow the auto-matched flow uses (SimulatorManager's EnsureMatchedSimulator/DispatchBuild), but
    // installed to a fixed %AppData%\Simulator\grblHAL_sim.exe - the Connect dialog's Simulator tab is only
    // enabled once that file exists (see PortDialog.xaml.cs). When a real controller is connected, defaults
    // the picks to match it (from $I's OPT/NEWOPT, already parsed into GrblInfo) and Build also copies its
    // live settings into EEPROM.DAT so the simulator boots up configured like the real machine.
    public partial class SimulatorConfigView : UserControl, IGrblConfigTab
    {
        private bool seededDefaults;

        public SimulatorConfigView()
        {
            InitializeComponent();
            Loaded += (s, e) => { SeedDefaults(); RefreshStatus(); };
        }

        public GrblConfigType GrblConfigType { get { return GrblConfigType.Simulator; } }

        public void Activate(bool activate)
        {
            if (activate)
                RefreshStatus();
        }

        private int SelectedAxes { get { return cbxAxes.SelectedIndex + 3; } }

        // Everything currently picked in the UI, packaged for SimulatorManager.
        private SimulatorManager.ManualSimOptions CurrentOptions()
        {
            return new SimulatorManager.ManualSimOptions
            {
                Axes = SelectedAxes,
                Probe = chkProbe.IsChecked == true,
                Rotation = chkRotation.IsChecked == true,
                LatheUvw = chkLatheUvw.IsChecked == true,
                SafetyDoor = chkSafetyDoor.IsChecked == true,
                EStop = chkEStop.IsChecked == true,
                YGanged = chkYGanged.IsChecked == true,
                YAutoSquare = chkYAutoSquare.IsChecked == true
            };
        }

        // Seed every option once, the first time the tab is shown, so it doesn't stomp a mid-session edit the
        // user hasn't built yet. Prefers the CONNECTED controller's actual options (from $I's OPT/NEWOPT,
        // already parsed into GrblInfo) over whatever was last built, since matching real hardware is the
        // point; falls back to the picks that produced the currently-installed exe (sim-options.json) when
        // nothing's connected. Ganged/auto-square have no $I equivalent to detect - left at their prior/
        // default value either way, since they're not something a controller reports.
        private void SeedDefaults()
        {
            if (seededDefaults)
                return;
            seededDefaults = true;

            if (SimulatorManager.IsRealControllerConnected())
            {
                int index = CNC.Core.GrblInfo.NumAxes - 3;
                if (index >= 0 && index < cbxAxes.Items.Count)
                    cbxAxes.SelectedIndex = index;
                chkProbe.IsChecked = CNC.Core.GrblInfo.HasProbe;
                chkRotation.IsChecked = CNC.Core.GrblInfo.RotationSupported;
                chkLatheUvw.IsChecked = CNC.Core.GrblInfo.LatheUVWModeEnabled;
                chkSafetyDoor.IsChecked = (CNC.Core.GrblInfo.OptionalSignals & CNC.Core.Signals.SafetyDoor) != 0;
                chkEStop.IsChecked = (CNC.Core.GrblInfo.OptionalSignals & CNC.Core.Signals.EStop) != 0;
                return;
            }

            var opts = SimulatorManager.AppDataActiveOptions();
            if (opts == null)
                return;

            int savedIndex = opts.Axes - 3;
            if (savedIndex >= 0 && savedIndex < cbxAxes.Items.Count)
                cbxAxes.SelectedIndex = savedIndex;
            chkProbe.IsChecked = opts.Probe;
            chkRotation.IsChecked = opts.Rotation;
            chkLatheUvw.IsChecked = opts.LatheUvw;
            chkSafetyDoor.IsChecked = opts.SafetyDoor;
            chkEStop.IsChecked = opts.EStop;
            chkYGanged.IsChecked = opts.YGanged;
            chkYAutoSquare.IsChecked = opts.YAutoSquare;
        }

        // Reflects the currently-installed exe (if any) against the currently-picked options, so the user can
        // tell at a glance whether Build would actually do anything.
        private void RefreshStatus()
        {
            bool present = SimulatorManager.AppDataSimulatorPresent();

            if (!present)
            {
                txtStatus.Text = "No simulator built yet.";
                txtPath.Text = string.Empty;
                return;
            }

            txtPath.Text = SimulatorManager.AppDataSimulatorExePath();

            string sig;
            SimulatorManager.BuildManualOptionSymbols(CurrentOptions(), out sig);
            txtStatus.Text = sig == SimulatorManager.AppDataActiveSignature()
                ? "Up to date with the options below."
                : "Installed, but built for different options - click Build to update.";
        }

        private void btnBuild_Click(object sender, RoutedEventArgs e)
        {
            btnBuild.IsEnabled = false;
            var opts = CurrentOptions();
            bool copyMachineSettings = SimulatorManager.IsRealControllerConnected();
            txtStatus.Text = "Checking for a matching build...";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string sig, detail;
                    var r = SimulatorManager.EnsureAppDataSimulator(opts, out sig, out detail);
                    bool installed;
                    string exeStatus;
                    switch (r)
                    {
                        case SimulatorManager.MatchResult.AlreadyCurrent:
                            installed = true; exeStatus = "Already up to date (build " + sig + ")."; break;
                        case SimulatorManager.MatchResult.InstalledFromRelease:
                            installed = true; exeStatus = "Installed (build " + sig + ")."; break;
                        case SimulatorManager.MatchResult.BuildTriggered:
                            SetStatus("Building (build " + sig + ") - this can take a few minutes...");
                            installed = SimulatorManager.PollAndInstallAppData(opts, sig);
                            exeStatus = installed
                                ? "Build ready and installed (build " + sig + ")."
                                : "Still building (build " + sig + ") - try Build again shortly.";
                            break;
                        default:
                            Finish(detail ?? "Build failed.");
                            return;
                    }

                    // Also copy the connected controller's live settings into EEPROM.DAT, so the simulator
                    // boots up configured like the real machine - same NVRAM-replay mechanism the Grbl tab's
                    // "Copy to simulator" button uses (BuildMyMachineEeprom), aimed at this exe/folder instead.
                    if (!installed || !copyMachineSettings)
                    {
                        Finish(exeStatus);
                        return;
                    }

                    SetStatus(exeStatus + " Copying connected machine's settings...");
                    var cmds = CNC.Core.GrblSettings.Settings.Select(s => "$" + s.Id + "=" + s.Value).ToList();
                    string eepromErr = "no settings to copy.";
                    bool eepromOk = cmds.Count > 0 && SimulatorManager.BuildAppDataEeprom(cmds, out eepromErr);
                    Finish(exeStatus + (eepromOk
                        ? " Machine settings copied to EEPROM.DAT."
                        : " Settings copy failed" + (string.IsNullOrEmpty(eepromErr) ? "." : (": " + eepromErr))));
                }
                catch (Exception ex) { Finish(ex.Message); }
            });
        }

        // Marshal an in-progress status update to the UI thread (Build stays disabled).
        private void SetStatus(string text)
        {
            try { Dispatcher.BeginInvoke((Action)(() => txtStatus.Text = text)); }
            catch { }
        }

        // Marshal the final status to the UI thread, refresh the path readout, and re-enable Build.
        private void Finish(string text)
        {
            try
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    txtStatus.Text = text;
                    txtPath.Text = SimulatorManager.AppDataSimulatorPresent() ? SimulatorManager.AppDataSimulatorExePath() : string.Empty;
                    btnBuild.IsEnabled = true;
                }));
            }
            catch { }
        }

        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = SimulatorManager.AppDataSimulatorDir();
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }
    }
}
