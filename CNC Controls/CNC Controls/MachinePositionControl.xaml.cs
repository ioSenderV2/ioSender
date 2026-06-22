/*
 * MachinePositionControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Main-page panel showing the machine position (MPos) readout, bound to the
 * GrblViewModel DataContext. Registered as a placeable panel so it can sit
 * left of / right of the 3D view or be hosted in a sidebar flyout.
 *
 */

using System.Windows.Controls;

namespace CNC.Controls
{
    public partial class MachinePositionControl : UserControl
    {
        public MachinePositionControl()
        {
            InitializeComponent();
        }
    }
}
