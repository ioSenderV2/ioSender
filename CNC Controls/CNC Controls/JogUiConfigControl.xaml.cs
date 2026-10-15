/*
 * JogUIConfigControl.xaml.cs - part of CNC Controls library
 *
 * v0.34 / 2021-07-26 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2021, Io Engineering (Terje Io)
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

using System.Windows.Controls;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for JogUiConfigControl.xaml
    /// </summary>
    public partial class JogUiConfigControl : UserControl, ISettingsResettable, ISettingsPanelCategory
    {
        // Where this panel sits in the settings navigation tree (ISettingsPanelCategory).
        public string SettingsCategory { get { return SettingsCategories.Jogging; } }
        public int SettingsOrder { get { return 0; } }

        public JogUiConfigControl()
        {
            InitializeComponent();
        }

        // The Continuous checkbox is the one control here that reflects LIVE jog state rather than a stored
        // setting, so it takes the shared JogViewModel as its DataContext - the same instance the on-screen
        // jog panels use, so toggling it here and selecting a distance there stay in agreement.
        //
        // Assigned on Loaded, not in the constructor: JogBaseControl.JogData is created by the first
        // JogBaseControl and there is no ordering guarantee against a settings panel, which is why
        // UIJogGridControl reads it the same way. If it is genuinely absent the checkbox is disabled rather
        // than left bound to nothing - a control that silently does nothing is worse than one that says so.
        private void JogUiConfigControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            var jog = JogBaseControl.JogData;
            chkContinuous.DataContext = jog;
            chkContinuous.IsEnabled = jog != null;
        }

        // Reset the on-screen jog presets (Config.JogUiMetric) this panel owns to their factory defaults.
        public void ResetToDefaults()
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg?.JogUiMetric != null)
                ConfigReset.CopyScalars(AppConfig.GetFactoryDefaults().JogUiMetric, cfg.JogUiMetric);
        }
    }
}
