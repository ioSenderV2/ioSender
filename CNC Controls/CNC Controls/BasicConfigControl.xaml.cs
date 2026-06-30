/*
 * BasicConfigControl.xaml.cs - part of CNC Controls library
 *
 * v0.09 / 2020-02-28 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020, Io Engineering (Terje Io)
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
using System.Windows.Controls;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for BasicConfigControl.xaml
    /// </summary>
    public partial class BasicConfigControl : UserControl, IRestartRequired
    {
        public event EventHandler<RestartRequiredEventArgs> RestartRequired;

        public BasicConfigControl()
        {
            InitializeComponent();

            // Max buffer size (streamer flow-control window) is latched once at job-view init, and reset delay is
            // captured into the serial stream at connect - both only take effect at startup, so a change needs a
            // restart. The other basic settings here are applied live / on next use.
            if (AppConfig.Settings.Base != null)
                AppConfig.Settings.Base.PropertyChanged += (s, e) => {
                    if (e.PropertyName == nameof(Config.MaxBufferSize) || e.PropertyName == nameof(Config.ResetDelay))
                        RestartRequired?.Invoke(this, new RestartRequiredEventArgs("Restart required to apply the buffer/reset-delay change."));
                };
        }
    }
}
