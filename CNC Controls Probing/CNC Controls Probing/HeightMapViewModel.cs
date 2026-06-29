/*
 * HeightViewModel.xaml.cs - part of CNC Probing library
 *
 * v0.27 / 2020-09-20 / Io Engineering (Terje Io)
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

using System.Windows.Media.Media3D;
using CNC.Core;

namespace CNC.Controls.Probing
{
    public class HeightMapViewModel : ViewModelBase
    {
        private bool _hasHeightMap = false, _canApply = false, _setToolOffset = false, _addPause = false, _lockGridSizeXY = true;
        private double _minX = 0d, _minY = 0d, _maxX = 50, _maxY = 50d, _gridSizeX = 5d, _gridSizeY = 5d;
        private double _safeZ = 5d, _probeDepth = 5d, _probeFeed = 0d;
        private HeightMap _heightMap = null;
        private Point3DCollection _mapPoints;
        private Point3DCollection _bp;
        private MeshGeometry3D _meshGeometry;

        public double MinX { get { return _minX; } set { if (value != _minX) { _minX = value; OnPropertyChanged(); OnPropertyChanged(nameof(Width)); } } }
        public double MaxX { get { return _maxX; } set { if (value != _maxX) { _maxX = value; OnPropertyChanged(); OnPropertyChanged(nameof(Width)); } } }
        public double MinY { get { return _minY; } set { if (value != _minY) { _minY = value; OnPropertyChanged(); OnPropertyChanged(nameof(Height)); } } }
        public double MaxY { get { return _maxY; } set { if (value != _maxY) { _maxY = value; OnPropertyChanged(); OnPropertyChanged(nameof(Height)); } } }
        public double Width { get { return _maxX - _minX; } set { if (value != Width) { _maxX = value + _minX; OnPropertyChanged(nameof(MaxX)); } } }
        public double Height { get { return _maxY - _minY; } set { if (value != Height) { _maxY = value + _minY; OnPropertyChanged(nameof(MaxY)); } } }

        public HeightMap Map { get { return _heightMap; } set { if (value != _heightMap) { _heightMap = value; OnPropertyChanged(); } } }
        public bool HasHeightMap { get { return _hasHeightMap && _heightMap != null; } set { if (value != _hasHeightMap) _hasHeightMap = value; OnPropertyChanged(); } }
        public bool CanApply { get { return _canApply && HasHeightMap; } set { _canApply = value; OnPropertyChanged(); } }
        public bool SetToolOffset { get { return _setToolOffset; } set { _setToolOffset = value; OnPropertyChanged(); } }
        public bool AddPause { get { return _addPause; } set { _addPause = value; OnPropertyChanged(); } }

        // Retract / clearance height above Z0 the probe rapids to between points (mm).
        public double SafeZ { get { return _safeZ; } set { if (value != _safeZ) { _safeZ = value; OnPropertyChanged(); } } }
        // How far below the start plane each probe may travel before faulting (mm).
        public double ProbeDepth { get { return _probeDepth; } set { if (value != _probeDepth) { _probeDepth = value; OnPropertyChanged(); } } }
        // Feed used for every height-map probe (mm/min). The surface is already roughly known (Z0 is set), so all
        // points probe at this slow approach feed - no fast search pass - which avoids overshoot/false triggers on
        // a fragile probe. 0 = default to the probe definition's latch (slow) feed.
        public double ProbeFeed { get { return _probeFeed; } set { if (value != _probeFeed) { _probeFeed = value; OnPropertyChanged(); } } }

        public bool GridSizeLockXY
        {
            get { return _lockGridSizeXY; }
            set
            {
                _lockGridSizeXY = value;
                OnPropertyChanged();
                if (_lockGridSizeXY && _gridSizeY != _gridSizeX)
                {
                    _gridSizeY = _gridSizeX;
                    OnPropertyChanged(nameof(GridSizeY));
                }
            }
        }
        public double GridSizeX
        {
            get { return _gridSizeX; }
            set
            {
                _gridSizeX = value;
                OnPropertyChanged();
                if (_lockGridSizeXY)
                {
                    _gridSizeY = value;
                    OnPropertyChanged(nameof(GridSizeY));
                }
            }
        }
        public double GridSizeY
        {
            get { return _gridSizeY; }
            set
            {
                _gridSizeY = value;
                OnPropertyChanged();
                if (_lockGridSizeXY)
                {
                    _gridSizeX = value;
                    OnPropertyChanged(nameof(GridSizeX));
                }
            }
        }

        public MeshGeometry3D MeshGeometry { get { return _meshGeometry; } set { _meshGeometry = value; OnPropertyChanged(); } }
        public Point3DCollection MapPoints { get { return _mapPoints; } set { _mapPoints = value; OnPropertyChanged(); } }
        public Point3DCollection BoundaryPoints { get { return _bp; } set { _bp = value; OnPropertyChanged(); } }
    }
}
