/*
 * UiRemoteConfigControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Settings > User Interface > Remote. The shutter-remote enable, what a press actually does, and the
 * binding status.
 *
 * The setting itself is not new - it was born on the Height map tab (2026-09-18), because that is where
 * the need was: place the plate, press Continue, walk back, sixteen times, with the laptop across the
 * shop. It stayed there while that was the only place a press meant anything.
 *
 * It means something in several places now - any prompt, any running job, any hold (see RemoteActions) -
 * so it is an input setting rather than a height-map setting, and it belongs next to Keyboard and
 * Controller. The Height map checkbox binds the same Config.ShutterRemoteEnabled property and the two
 * stay in step automatically; it is left in place deliberately, since that is still where an operator
 * first discovers they want it.
 *
 * The table is here because the policy lived only in RemoteActions' header comment, which is the one
 * place the operator standing at the machine cannot read.
 *
 * ---- Why the binding status line exists ----
 *
 * Binding is armed by ticking the box and completed by pressing a button on the remote. That is a good
 * interaction - the remote is in your hand, pressing it is the most direct way to say "this one" - but
 * built without this line it is ENTIRELY INVISIBLE. Shipped that way once, 2026-09-21: the mechanism
 * worked perfectly, the log said "binding ARMED" and then "BOUND to ...", and the operator's report was
 * simply "there was no binding UI when I tick the enable box". They were right. A chime is not feedback
 * if nothing ever told you to listen for it.
 *
 * So this says which of the three states it is in, in words, on the page where the tick happens.
 */

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class UiRemoteConfigControl : UserControl, ISettingsPanelCategory
    {
        // Where this panel sits in the settings navigation tree (ISettingsPanelCategory).
        public string SettingsCategory { get { return SettingsCategories.UserInterface; } }

        // Between Keyboard (10) and Controller (20) - it is another way of talking to the app, and it
        // reads as one of the input pages rather than as an editor. AddPanelNode places by this order
        // against the nodes declared in GrblConfigView.BuildNav.
        public int SettingsOrder { get { return 15; } }

        public UiRemoteConfigControl()
        {
            InitializeComponent();

            Loaded += (s, e) => { Hook(); RefreshStatus(); };
            Unloaded += (s, e) => Unhook();
        }

        private Config hooked;

        private void Hook()
        {
            var config = AppConfig.Settings?.Base;
            if (ReferenceEquals(config, hooked))
                return;

            Unhook();
            hooked = config;
            if (hooked != null)
                hooked.PropertyChanged += Config_PropertyChanged;

            // Raised from the raw-input path the instant a press claims the device. That path runs on the
            // UI thread (HwndSource hook), so this can touch the controls directly.
            RemoteDevices.BoundTo += OnBoundTo;
        }

        private void Unhook()
        {
            if (hooked != null)
                hooked.PropertyChanged -= Config_PropertyChanged;
            hooked = null;
            RemoteDevices.BoundTo -= OnBoundTo;
        }

        private void Config_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Config.ShutterRemoteEnabled) ||
                 e.PropertyName == nameof(Config.ShutterRemoteDevice) ||
                 e.PropertyName == nameof(Config.ShutterRemoteName))
                RefreshStatus();
        }

        private void OnBoundTo(string device)
        {
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var config = AppConfig.Settings?.Base;
            bool on = config != null && config.ShutterRemoteEnabled;
            string device = config == null ? string.Empty : config.ShutterRemoteDevice;

            btnRebind.IsEnabled = on;
            txtBindStatus.ToolTip = string.IsNullOrEmpty(device) ? null : device;

            if (!on)
            {
                txtBindStatus.Text = "Switched off - tick the box above to use a remote.";
                return;
            }

            if (string.IsNullOrEmpty(device))
            {
                // The armed state. Said plainly, because the operator has to DO something for this to
                // resolve and nothing else on screen is going to tell them what.
                txtBindStatus.Text = "Waiting - press a button on your remote to bind it.";
                return;
            }

            // The name Windows itself shows, when we have it. Falling back to the path is not a failure
            // worth announcing - it just means the remote was not answering when it was bound.
            string friendly = config.ShutterRemoteName;
            txtBindStatus.Text = "Bound to " + (string.IsNullOrEmpty(friendly) ? Short(device) : friendly);
        }

        /// <summary>
        /// The raw-input device path is a wall of braces and hex that means nothing at a glance. Show the
        /// part that distinguishes one remote from another, and keep the whole path in the tooltip for
        /// when someone genuinely needs it.
        /// </summary>
        private static string Short(string device)
        {
            if (string.IsNullOrEmpty(device))
                return "?";

            int vid = device.IndexOf("VID", System.StringComparison.OrdinalIgnoreCase);
            if (vid >= 0)
            {
                int end = device.IndexOf('#', vid);
                string chunk = end > vid ? device.Substring(vid, end - vid) : device.Substring(vid);
                if (chunk.Length > 0)
                    return chunk;
            }

            return device.Length > 40 ? "..." + device.Substring(device.Length - 37) : device;
        }

        private void btnRebind_Click(object sender, RoutedEventArgs e)
        {
            // Forget, then listen again. Same two steps the tick does, without making the operator toggle
            // the enable off and on - which also stops and restarts the hook for no reason.
            RemoteDevices.ClearBinding();
            RemoteDevices.ArmBinding();
            RefreshStatus();
        }
    }
}
