/*
 * JogPadControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * A small X/Y/Z jog pad for the run-control bar. Each button issues one discrete jog step at the
 * currently selected UI-jog distance/feed (the same JogViewModel the UI Jogging panel drives), so it
 * stays consistent with the rest of the app's jogging and follows the Settings:App presets.
 *
 */

using System;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class JogPadControl : UserControl
    {
        public JogPadControl()
        {
            InitializeComponent();
        }

        private void Jog_Click(object sender, RoutedEventArgs e)
        {
            var model = DataContext as GrblViewModel;
            var jd = JogBaseControl.JogData;
            if (model == null || jd == null)
                return;

            // Only jog when the controller can (idle/jog/tool-change) and no job is streaming.
            var state = model.GrblState.State;
            if (model.IsJobRunning || !(state == GrblStates.Idle || state == GrblStates.Jog || state == GrblStates.Tool))
                return;

            string cmd = (string)((Button)sender).Tag;      // "X+", "X-", "Y+", ...
            int axis = GrblInfo.AxisLetterToIndex(cmd[0]);
            if (axis < 0)
                return;

            double dist = jd.SelectedDistance;              // finite UI-jog preset step (mm/in per $13)
            double feed = jd.SelectedFeedrate;
            if (dist <= 0d || feed <= 0d)
                return;

            double signed = dist * (cmd[1] == '-' ? -1d : 1d);
            string mode = jd.IsMetric ? "G21" : "G20";
            string j = string.Format("$J=G91{0}{1}{2}F{3}", mode, cmd[0], signed.ToInvariantString(), Math.Ceiling(feed).ToInvariantString());
            model.ExecuteCommand(j);
        }
    }
}
