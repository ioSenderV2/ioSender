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

            Loaded += (s, e) => RefreshBindUi();
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
    }
}
