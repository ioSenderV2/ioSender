/*
 * OffsetFlyout.xaml.cs - part of CNC Controls library for Grbl
 *
 * Compact sidebar flyout for a single coordinate-system offset (G28, G30, G54...).
 * A "Go" button moves the machine to the offset (tooltip shows the coordinates);
 * predefined positions (G28/G30) also get a "Set" button to store the current position.
 * Designed to be tiny so several can be pinned open beside the jog panel.
 *
 */

using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;
using CNC.GCode;

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
            // "Set" stores the current machine position: G28/G30 via their .1 form, G54-G59.3 via G10 L2.
            btnSet.Visibility = (code == "G28" || code == "G30" || code.StartsWith("G5")) ? Visibility.Visible : Visibility.Collapsed;

            // G28 only: a read-only picker over the Fixture library (Machine Setup owns Set/edit - this
            // flyout only navigates). Only offers VALIDATED fixtures, same guard Start Job's own fixture
            // dropdown uses (an unproven Coords is exactly what caused a real Alarm:5 probe fail before).
            if (code == "G28")
            {
                cbxFixture.Visibility = Visibility.Visible;
                cbxFixture.ItemsSource = Fixtures.Items;
                cbxFixture.Items.Filter = o => (o as Fixture)?.PositionValidated == true;
            }

            IsVisibleChanged += OffsetFlyout_IsVisibleChanged;
        }

        public string PanelName { get; }
        public string MenuLabel { get { return code; } }
        public bool Pinned
        {
            get { return btnPin.IsChecked == true; }
            set { btnPin.IsChecked = value; }
        }
        public event System.Action<IPinnableFlyout> PinnedChanged;

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

            // A selected fixture overrides the firmware G28 slot entirely - go straight to its own saved
            // machine-coord origin. No selection (the common case, and every other offset) falls through
            // to the normal firmware-slot behavior unchanged.
            var fixture = cbxFixture.Visibility == Visibility.Visible ? cbxFixture.SelectedItem as Fixture : null;
            if (fixture != null)
                GotoBaseControl.SafeGotoMachine(grbl, new Position(fixture.Coords));
            else
                GotoBaseControl.SafeGoto(grbl, code);   // the one shared Go-To routine - applies Safe Z uniformly
        }

        private void btnSet_Click(object sender, RoutedEventArgs e)
        {
            var grbl = DataContext as GrblViewModel;
            if (grbl == null || Comms.com == null)
                return;

            // Write directly to the controller, like the Offsets tab (OffsetView) does. Going through
            // ExecuteCommand -> MDI -> JobControl.SendCommand silently drops the command unless the streaming
            // state machine happens to be in an idle-ish state (and it also runs it through ParseBlock) - which
            // is why the flyout Set "quietly did nothing" while the Offsets tab worked.
            if (code == "G28" || code == "G30")
            {
                Comms.com.WriteCommand(code + ".1");   // store the current machine position
                return;
            }

            // G54-G59.3: set this coordinate system's origin to the current machine position, all axes
            // (e.g. jog to the toolsetter and Set G59.3). G10 L2 P<n> takes machine coordinates.
            var cs = Cs;
            if (cs == null)
                return;

            var sb = new StringBuilder("G10L2P" + cs.Id);
            for (int i = 0; i < GrblInfo.NumAxes; i++)
                sb.Append(GrblInfo.AxisIndexToLetter(i) + grbl.MachinePosition.Values[i].ToInvariantString("F3"));

            Comms.com.WriteCommand(sb.ToString());
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
