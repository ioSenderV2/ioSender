using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    // Settings > Simulator: lets the user pick grblHAL compile options and build a simulator via the same
    // CI workflow the auto-matched flow uses (SimulatorManager's EnsureMatchedSimulator/DispatchBuild), but
    // installed to a fixed %AppData%\Simulator\grblHAL_sim.exe - the Connect dialog's Simulator tab is only
    // enabled once that file exists (see PortDialog.xaml.cs).
    public partial class SimulatorConfigView : UserControl, IGrblConfigTab
    {
        private bool restoredOptions;

        public SimulatorConfigView()
        {
            InitializeComponent();
            Loaded += (s, e) => { RestoreOptions(); RefreshStatus(); };
        }

        public GrblConfigType GrblConfigType { get { return GrblConfigType.Simulator; } }

        public void Activate(bool activate)
        {
            if (activate)
                RefreshStatus();
        }

        private int SelectedAxes { get { return cbxAxes.SelectedIndex + 3; } }

        // Restore the axes/probe/rotation picks that produced the currently-installed exe (read back from
        // sim-options.json, see SimulatorManager.AppDataActiveOptions) instead of always starting from the
        // XAML defaults - once only, the first time the tab is shown, so it doesn't stomp a mid-session edit
        // the user hasn't built yet.
        private void RestoreOptions()
        {
            if (restoredOptions)
                return;
            restoredOptions = true;

            var opts = SimulatorManager.AppDataActiveOptions();
            if (opts == null)
                return;

            int index = opts.Axes - 3;
            if (index >= 0 && index < cbxAxes.Items.Count)
                cbxAxes.SelectedIndex = index;
            chkProbe.IsChecked = opts.Probe;
            chkRotation.IsChecked = opts.Rotation;
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
            SimulatorManager.BuildManualOptionSymbols(SelectedAxes, chkProbe.IsChecked == true, chkRotation.IsChecked == true, out sig);
            txtStatus.Text = sig == SimulatorManager.AppDataActiveSignature()
                ? "Up to date with the options below."
                : "Installed, but built for different options - click Build to update.";
        }

        private void btnBuild_Click(object sender, RoutedEventArgs e)
        {
            btnBuild.IsEnabled = false;
            int axes = SelectedAxes;
            bool probe = chkProbe.IsChecked == true, rotation = chkRotation.IsChecked == true;
            txtStatus.Text = "Checking for a matching build...";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string sig, detail;
                    var r = SimulatorManager.EnsureAppDataSimulator(axes, probe, rotation, out sig, out detail);
                    switch (r)
                    {
                        case SimulatorManager.MatchResult.AlreadyCurrent:
                            Post("Already up to date (build " + sig + ").", true);
                            break;
                        case SimulatorManager.MatchResult.InstalledFromRelease:
                            Post("Installed (build " + sig + ").", true);
                            break;
                        case SimulatorManager.MatchResult.BuildTriggered:
                            Post("Building (build " + sig + ") - this can take a few minutes...", false);
                            if (SimulatorManager.PollAndInstallAppData(axes, probe, rotation, sig))
                                Post("Build ready and installed (build " + sig + ").", true);
                            else
                                Post("Still building (build " + sig + ") - try Build again shortly.", true);
                            break;
                        case SimulatorManager.MatchResult.Failed:
                            Post(detail ?? "Build failed.", true);
                            break;
                    }
                }
                catch (Exception ex) { Post(ex.Message, true); }
            });
        }

        // Marshal a status update (and optionally re-enable Build + refresh the up-to-date check) to the UI thread.
        private void Post(string text, bool reenable)
        {
            try
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    txtStatus.Text = text;
                    if (reenable)
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
