/*
 * OffsetFlyout.xaml.cs - part of CNC Controls library for Grbl
 *
 * Compact sidebar flyout for a single coordinate-system offset (G28, G30, G54...).
 * A "Go" button moves the machine to the offset (tooltip shows the coordinates);
 * predefined positions (G28/G30) also get a "Set" button to store the current position.
 * Designed to be tiny so several can be pinned open beside the jog panel.
 *
 */

using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class OffsetFlyout : UserControl, ISidebarControl, IPinnableFlyout
    {
        private readonly string code;

        public OffsetFlyout(string code)
        {
            InitializeComponent();
            this.code = code;
            PanelName = code;
            btnGo.Content = code;
            // "Get current position" applies to the predefined positions.
            btnSet.Visibility = (code == "G28" || code == "G30") ? Visibility.Visible : Visibility.Collapsed;
            IsVisibleChanged += OffsetFlyout_IsVisibleChanged;
        }

        public string PanelName { get; }
        public string MenuLabel { get { return code; } }
        public bool Pinned
        {
            get { return btnPin.IsChecked == true; }
            set { btnPin.IsChecked = value; }
        }
        public event Action<IPinnableFlyout> PinnedChanged;

        private CoordinateSystem Cs
        {
            get
            {
                return GrblWorkParameters.CoordinateSystems == null
                    ? null
                    : GrblWorkParameters.CoordinateSystems.FirstOrDefault(c => c.Code == code);
            }
        }

        private void OffsetFlyout_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                btnGo.ToolTip = CoordsTooltip();    // refresh - offset values may have changed
        }

        private string CoordsTooltip()
        {
            var cs = Cs;
            if (cs == null)
                return code + " (not available)";

            var sb = new StringBuilder("Go to " + code);
            for (int i = 0; i < cs.Values.Length; i++)
            {
                if (!double.IsNaN(cs.Values[i]))
                    sb.Append(string.Format("   {0}: {1}", GrblInfo.AxisIndexToLetter(i), cs.Values[i].ToInvariantString("F3")));
            }
            return sb.ToString();
        }

        private void btnGo_Click(object sender, RoutedEventArgs e)
        {
            var grbl = DataContext as GrblViewModel;
            if (grbl == null)
                return;

            if (code == "G28" || code == "G30")
                grbl.ExecuteCommand(code);
            else
            {
                var cs = Cs;
                if (cs == null)
                    return;

                // Move to the stored work origin in machine coordinates - XY only, no auto-Z.
                string axes = string.Empty;
                if (!double.IsNaN(cs.X)) axes += "X" + cs.X.ToInvariantString("F3");
                if (!double.IsNaN(cs.Y)) axes += "Y" + cs.Y.ToInvariantString("F3");
                if (axes != string.Empty)
                    grbl.ExecuteCommand("G53G0" + axes);
            }
        }

        private void btnSet_Click(object sender, RoutedEventArgs e)
        {
            // G28.1 / G30.1 store the current machine position.
            (DataContext as GrblViewModel)?.ExecuteCommand(code + ".1");
        }

        private void btn_Close(object sender, RoutedEventArgs e)
        {
            Visibility = Visibility.Hidden;
        }

        private void btnPin_Changed(object sender, RoutedEventArgs e)
        {
            PinnedChanged?.Invoke(this);
        }
    }
}
