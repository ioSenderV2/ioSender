/*
 * MachineControlPanel.xaml.cs - part of ioSender XL
 *
 * A reusable block of the Grbl tab's lower-left machine controls (status/signals, realtime run controls,
 * feed + rapids override, MDI), bound to the shared GrblViewModel. Floated on Run by the .nc generate tools
 * (see MachineControlWindow) so the operator never has to switch back to the Grbl tab to drive a run.
 *
 * The run controls send REALTIME commands (so they work for a macro-path run, not just a job stream).
 */

using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace GCode_Sender
{
    public partial class MachineControlPanel : UserControl
    {
        public MachineControlPanel()
        {
            InitializeComponent();
        }

        private void Start_Click(object sender, RoutedEventArgs e)   // resume from feed hold
        {
            Comms.com?.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_CYCLE_START));
        }

        private void Hold_Click(object sender, RoutedEventArgs e)    // pause motion
        {
            Comms.com?.WriteByte(GrblLegacy.ConvertRTCommand(GrblConstants.CMD_FEED_HOLD));
        }

        private void Stop_Click(object sender, RoutedEventArgs e)    // soft reset / abort
        {
            Comms.com?.WriteByte((byte)GrblConstants.CMD_RESET);
        }
    }
}
