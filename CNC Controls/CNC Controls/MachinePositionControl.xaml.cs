/*
 * MachinePositionControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Main-page panel showing the machine position (MPos) readout. Each axis is a
 * MultiBinding of the live work Position + WorkPositionOffset (machine = work + WCO),
 * which tracks the same notifications the DRO uses. Registered as a placeable panel.
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
