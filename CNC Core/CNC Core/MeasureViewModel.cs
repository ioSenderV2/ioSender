/*
 * MeasureViewModel.cs - part of CNC Controls library
 *
 * v0.42 / 2023-03-21 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020-2023, Io Engineering (Terje Io)
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

namespace CNC.Core
{
    public class MeasureViewModel : ViewModelBase
    {
        bool _isMetric = true;

        public const double MM_PER_INCH = 25.4d;

        // Storage seam: GrblViewModel overrides this to back onto MachineState.IsMetric so the units
        // mode reaches the wire (client/server split step 5 rule - state holds the storage, the view
        // model holds the notification). Every derived getter below must read the hook, not _isMetric,
        // or an overriding class silently splits into two sources of truth.
        protected virtual bool IsMetricStore
        {
            get { return _isMetric; }
            set { _isMetric = value; }
        }

        public bool IsMetric
        {
            get { return IsMetricStore; }
            set
            {
                if (value != IsMetricStore)
                {
                    IsMetricStore = value;
                    OnPropertyChanged("Unit");
                    OnPropertyChanged("FeedrateUnit");
                    OnPropertyChanged("UnitFactor");
                    OnPropertyChanged("Format");
                    OnPropertyChanged("FormatSigned");
                    OnPropertyChanged();
                }
            }
        }

        public string Unit { get { return IsMetricStore ? "mm" : "in"; } }
        public string FeedrateUnit { get { return IsMetricStore ? "mm/min" : "in/min"; } }
        public double UnitFactor { get { return IsMetricStore ? 1.0d : 25.4d; } }
        public string Format { get { return IsMetricStore ? GrblConstants.FORMAT_METRIC : GrblConstants.FORMAT_IMPERIAL; } }
        public string FormatSigned { get { return "-" + Format; } }
        public int Precision { get { return IsMetricStore ? 3 : 4; } }

        public double ConvertMM2Current (double value)
        {
            if(!IsMetricStore)
                value /= 25.4d;

            return value;
        }
    }
}
