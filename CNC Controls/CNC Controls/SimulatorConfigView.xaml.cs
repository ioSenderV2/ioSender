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
        private bool restoredFromDisk;

        public SimulatorConfigView()
        {
            InitializeComponent();
            Loaded += (s, e) => { SeedDefaults(); RefreshStatus(); };

            // Keep the "up to date" / "click Build to update" status live as the user picks options -
            // RefreshStatus() only used to run on tab-show and after a build, so toggling a checkbox mid-
            // visit left the status text stale (reflecting whatever was picked when the tab was last shown)
            // until the user navigated away and back. Wired centrally here rather than per-control in XAML.
            RoutedEventHandler onOptionChanged = (s, e) => RefreshStatus();
            cbxAxes.SelectionChanged += (s, e) => RefreshStatus();
            foreach (var chk in new[] { chkProbe, chkToolsetter, chkRotation, chkLatheUvw, chkSafetyDoor, chkEStop, chkYGanged, chkYAutoSquare })
            {
                chk.Checked += onOptionChanged;
                chk.Unchecked += onOptionChanged;
            }
        }

        public GrblConfigType GrblConfigType { get { return GrblConfigType.Simulator; } }

        public void Activate(bool activate)
        {
            if (activate)
            {
                // Unlike the disk-restore fallback below, hardware sync re-runs every time this tab
                // becomes visible while connected (not just once) - visiting Settings > Simulator is
                // supposed to show what Build would actually do against the machine you're on RIGHT NOW,
                // not whatever happened to be true the first time this control ever loaded (e.g. before
                // you'd connected yet).
                if (SimulatorManager.IsRealControllerConnected())
                    SyncFromHardware();
                RefreshStatus();
            }
        }

        private void SyncFromHardware()
        {
            int index = CNC.Core.GrblInfo.NumAxes - 3;
            if (index >= 0 && index < cbxAxes.Items.Count)
                cbxAxes.SelectedIndex = index;
            chkProbe.IsChecked = CNC.Core.GrblInfo.HasProbe;
            chkToolsetter.IsChecked = CNC.Core.GrblInfo.HasToolSetter;
            chkRotation.IsChecked = CNC.Core.GrblInfo.RotationSupported;
            chkLatheUvw.IsChecked = CNC.Core.GrblInfo.LatheUVWModeEnabled;
            chkSafetyDoor.IsChecked = (CNC.Core.GrblInfo.OptionalSignals & CNC.Core.Signals.SafetyDoor) != 0;
            chkEStop.IsChecked = (CNC.Core.GrblInfo.OptionalSignals & CNC.Core.Signals.EStop) != 0;
        }

        private int SelectedAxes { get { return cbxAxes.SelectedIndex + 3; } }

        // Everything currently picked in the UI, packaged for SimulatorManager.
        private SimulatorManager.ManualSimOptions CurrentOptions()
        {
            return new SimulatorManager.ManualSimOptions
            {
                Axes = SelectedAxes,
                Probe = chkProbe.IsChecked == true,
                Toolsetter = chkToolsetter.IsChecked == true,
                Rotation = chkRotation.IsChecked == true,
                LatheUvw = chkLatheUvw.IsChecked == true,
                SafetyDoor = chkSafetyDoor.IsChecked == true,
                EStop = chkEStop.IsChecked == true,
                YGanged = chkYGanged.IsChecked == true,
                YAutoSquare = chkYAutoSquare.IsChecked == true
            };
        }

        // First show: prefer the CONNECTED controller's actual options (see SyncFromHardware, also re-run on
        // every later Activate while connected - see there for why). Falls back to the picks that produced
        // the currently-installed exe (sim-options.json), once only, when nothing's connected on first show -
        // there's no live source to keep re-syncing that from, so once is right (a later Build overwrites it
        // anyway). Ganged/auto-square have no $I equivalent to detect - left at their prior/default value
        // either way, since they're not something a controller reports.
        private void SeedDefaults()
        {
            if (SimulatorManager.IsRealControllerConnected())
            {
                SyncFromHardware();
                return;
            }

            if (restoredFromDisk)
                return;
            restoredFromDisk = true;

            var opts = SimulatorManager.AppDataActiveOptions();
            if (opts == null)
                return;

            int savedIndex = opts.Axes - 3;
            if (savedIndex >= 0 && savedIndex < cbxAxes.Items.Count)
                cbxAxes.SelectedIndex = savedIndex;
            chkProbe.IsChecked = opts.Probe;
            chkToolsetter.IsChecked = opts.Toolsetter;
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
