/*
 * LimitsControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.01 / 2019-10-21 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019, Io Engineering (Terje Io)
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

using System.ComponentModel;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for LimitsControl.xaml
    /// </summary>
    public partial class LimitsControl : UserControl
    {
        private ProgramLimits _hooked;

        public LimitsControl()
        {
            InitializeComponent();
            Loaded += (s, e) => Refresh();
            DataContextChanged += (s, e) => { HookLimits(); Refresh(); };
        }

        // Re-evaluate whenever the program bounding box changes, so the title flips to "Program limits"
        // the moment the loaded program has motion - and back to "Machine limits" when it is cleared.
        private void HookLimits()
        {
            var model = DataContext as GrblViewModel;
            if (model == null || ReferenceEquals(model.ProgramLimits, _hooked))
                return;
            if (_hooked != null)
                _hooked.PropertyChanged -= Limits_PropertyChanged;
            _hooked = model.ProgramLimits;
            _hooked.PropertyChanged += Limits_PropertyChanged;
        }

        private void Limits_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Refresh();
        }

        // A loaded program "has moves" only when its bounding box has a real extent on some axis. A program
        // with no motion finalizes to an all-zero box (every axis Min==Max==0) and an unloaded one is all-NaN.
        private static bool ProgramHasMoves(GrblViewModel model)
        {
            if (!GCode.File.IsLoaded)
                return false;
            var pl = model.ProgramLimits;
            for (int i = 0; i < GrblInfo.NumAxes; i++)
            {
                double min = pl.MinValues[i], max = pl.MaxValues[i];
                if (!double.IsNaN(min) && !double.IsNaN(max) && max != min)
                    return true;
            }
            return false;
        }

        // Shows the loaded program's bounding box ("Program limits") once it has moves; otherwise the machine
        // soft-limit envelope from $13x/$23 ("Machine limits") - i.e. no program loaded, or one with no motion.
        public void Refresh()
        {
            var model = DataContext as GrblViewModel;
            if (model == null)
                return;

            var ctrls = new[] { axisX, axisY, axisZ, axisA, axisB, axisC };

            if (ProgramHasMoves(model))
            {
                grpLimits.Header = "Program limits";
                var pl = model.ProgramLimits;
                for (int i = 0; i < ctrls.Length && i < GrblInfo.NumAxes; i++)
                {
                    ctrls[i].MinValue = pl.MinValues[i];
                    ctrls[i].MaxValue = pl.MaxValues[i];
                }
            }
            else
            {
                // An axis that homes at max ($23 bit clear) runs 0..-travel; one that homes at min runs 0..+travel.
                grpLimits.Header = "Machine limits";
                int homeMask = GrblSettings.GetInteger(GrblSetting.HomingDirMask);
                if (homeMask < 0) homeMask = 0;
                for (int i = 0; i < ctrls.Length && i < GrblInfo.NumAxes; i++)
                {
                    double travel = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + i);
                    if (travel < 0d) travel = 0d;
                    bool homeAtMin = (homeMask & (1 << i)) != 0;
                    ctrls[i].MinValue = homeAtMin ? 0d : -travel;
                    ctrls[i].MaxValue = homeAtMin ? travel : 0d;
                }
            }
        }
    }
}
