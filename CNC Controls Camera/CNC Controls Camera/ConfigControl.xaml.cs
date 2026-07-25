/*
 * ConfigControl.xaml.cs - part of CNC Controls Camera library
 *
 * v0.10 / 2019-03-05 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2020, Io Engineering (Terje Io)
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
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls.Camera
{
    /// <summary>
    /// Interaction logic for ConfigControl.xaml
    /// </summary>
    public partial class ConfigControl : UserControl, ICameraConfig
    {
        public ConfigControl()
        {
            InitializeComponent();

            Loaded += (s, e) => { RefreshBindUi(); RefreshObsPassword(); };
        }

        // PasswordBox.Password can't be data-bound directly (WPF deliberately keeps it out of the normal
        // binding/undo/dependency-property machinery so a plaintext password is never left sitting in a
        // binding trace) - so it's synced by hand both ways instead of via the usual {Binding ...} the
        // rest of this panel uses.
        private void RefreshObsPassword()
        {
            var cfg = Cfg;
            if (cfg != null)
                pwdObsPassword.Password = cfg.ObsPassword ?? string.Empty;
        }

        private void pwdObsPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var cfg = Cfg;
            if (cfg != null)
                cfg.ObsPassword = pwdObsPassword.Password;
        }

        private CameraConfig Cfg { get { return (DataContext as Config)?.Camera; } }

        // Reflect the current bind state: select the bound device (or the first one), toggle the button between
        // Connect/Disconnect, and lock the picker while connected. Menu visibility follows via the SelectedCamera
        // PropertyChanged the app subscribes to (menu overhaul).
        private void RefreshBindUi()
        {
            var cfg = Cfg;
            if (cfg == null)
                return;

            if (cfg.IsCameraBound)
                cbxDevice.SelectedValue = cfg.SelectedCamera;
            else if (cbxDevice.SelectedItem == null && cbxDevice.Items.Count > 0)
                cbxDevice.SelectedIndex = 0;

            btnCameraConnect.Content = cfg.IsCameraBound ? "Disconnect" : "Connect";
            cbxDevice.IsEnabled = !cfg.IsCameraBound;
        }

        // Re-enumerate on drop-down open so a just-plugged-in camera appears without reopening Settings.
        private void cbxDevice_DropDownOpened(object sender, EventArgs e)
        {
            cbxDevice.GetBindingExpression(ComboBox.ItemsSourceProperty)?.UpdateTarget();
        }

        private void btnCameraConnect_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Cfg;
            if (cfg == null)
                return;

            if (cfg.IsCameraBound)
                cfg.SelectedCamera = string.Empty;                                 // Disconnect
            else
                cfg.SelectedCamera = (cbxDevice.SelectedValue as string) ?? string.Empty;   // Connect

            RefreshBindUi();
        }

        private void getPosition_Click(object sender, RoutedEventArgs e)
        {
            var model = (GrblViewModel)Application.Current.MainWindow.DataContext;

            ((Config)DataContext).Camera.XOffset = -model.Position.X;
            ((Config)DataContext).Camera.YOffset = -model.Position.Y;
        }

        // Opens the real demo-recording setup doc (Source Record plugin, per-source filter names, Record
        // Mode) in whatever the user's default .md handler is, rather than duplicating those instructions
        // here where they'd silently drift out of sync with the actual recipe. Dev-checkout only (walks up
        // from the exe looking for docs/demo-videos/README.md) - this whole panel only matters for a
        // -demomarker shoot anyway, which is a dev/demo-recording workflow, not an end-user one.
        private void btnObsInstructions_Click(object sender, RoutedEventArgs e)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string found = null;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = System.IO.Path.Combine(dir, "docs", "demo-videos", "README.md");
                if (System.IO.File.Exists(candidate))
                {
                    found = candidate;
                    break;
                }
                dir = System.IO.Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }

            if (found == null)
            {
                AppDialogs.Show(Window.GetWindow(this),
                    "Couldn't find docs\\demo-videos\\README.md - this only works from a repo checkout, not an installed build.",
                    "OBS setup instructions", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(found) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                AppDialogs.Show(Window.GetWindow(this), "Could not open the instructions: " + ex.Message,
                    "OBS setup instructions", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Connects to OBS right now with whatever's currently typed (not necessarily saved yet) and checks
        // each configured source name is actually present, so a typo in a source name is caught here rather
        // than discovered mid-shoot when recording silently never starts.
        private void btnObsValidate_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Cfg;
            if (cfg == null)
                return;

            string error;
            System.Collections.Generic.List<string> sources;
            using (new UIUtils.WaitCursor())
            {
                ObsBridge.ValidateConnection(cfg.ObsHost, cfg.ObsPort, cfg.ObsPassword, out error, out sources);
            }

            if (error != null)
            {
                AppDialogs.Show(Window.GetWindow(this), "Could not validate the OBS connection:\n\n" + error,
                    "Validate OBS connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool anyMissing = false;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format("Connected to OBS - found {0} source(s).", sources.Count));
            sb.AppendLine();
            AppendSourceCheck(sb, sources, "Front Left", cfg.ObsCamASource, ref anyMissing);
            AppendSourceCheck(sb, sources, "Front Right", cfg.ObsCamBSource, ref anyMissing);
            AppendSourceCheck(sb, sources, "App (screen)", cfg.ObsAppSource, ref anyMissing);

            AppDialogs.Show(Window.GetWindow(this), sb.ToString().TrimEnd(),
                "Validate OBS connection", MessageBoxButton.OK, anyMissing ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private static void AppendSourceCheck(System.Text.StringBuilder sb, System.Collections.Generic.List<string> sources, string label, string name, ref bool anyMissing)
        {
            if (string.IsNullOrEmpty(name))
            {
                sb.AppendLine(label + ": (not set)");
                return;
            }

            // Case/whitespace-insensitive match - the field is free text the user typed by hand to match
            // whatever they named the source in OBS, so a harmless casing difference shouldn't read as a
            // real "this source doesn't exist" failure.
            string trimmed = name.Trim();
            bool found = sources.Any(s => string.Equals(s.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (found)
                sb.AppendLine(label + ": \"" + name + "\" - found");
            else
            {
                sb.AppendLine(label + ": \"" + name + "\" - NOT FOUND in OBS");
                anyMissing = true;
            }
        }
    }
}
