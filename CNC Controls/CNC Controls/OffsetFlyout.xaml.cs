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
        private CoordinateSystem subscribedCs;

        public OffsetFlyout(string code)
        {
            InitializeComponent();
            this.code = code;
            PanelName = code;
            btnGo.Content = code;
            // What "Set" means depends on what is being set, so say so on the button itself:
            //   G54-G59    a WORK ORIGIN -> G10 L20, "make here read zero" (see btnSet_Click)
            //   G59.1-.3   a MACHINE location (toolsetter etc.) -> G10 L2, raw machine position
            //   G28/G30    a MACHINE location by definition -> G28.1/G30.1
            btnSet.Visibility = (code == "G28" || code == "G30" || code.StartsWith("G5")) ? Visibility.Visible : Visibility.Collapsed;
            if (btnSet.Visibility == Visibility.Visible)
                btnSet.ToolTip = string.Format(LibStrings.FindResource(
                                                    code == "G28" || code == "G30" ? "SetTipPredefined"
                                                     : IsWorkOrigin(code) ? "SetTipOrigin"
                                                     : "SetTipMachinePos"), code);

            // G28 only: a read-only picker over the Fixture library (Machine Setup owns Set/edit - this
            // flyout only navigates). Only offers VALIDATED fixtures, same guard Start Job's own fixture
            // dropdown uses (an unproven Coords is exactly what caused a real Alarm:5 probe fail before).
            if (code == "G28")
            {
                cbxFixture.Visibility = Visibility.Visible;
                // A dedicated ListCollectionView, NOT "ItemsSource = Fixtures.Items; Items.Filter = ...":
                // WPF caches ONE default CollectionView per source collection instance (CollectionViewSource.
                // GetDefaultView) and setting ItemsSource directly to a shared IEnumerable makes ItemsControl.
                // Items an alias for that SAME default view - so a filter set here used to apply to EVERY
                // other control bound directly to Fixtures.Items, anywhere in the app. Confirmed on real
                // hardware: this filter (validated-only) leaked into MachineSetupWizard's grdFixtures, which
                // has no filter of its own and expects to show every fixture - only "Small Vise" (the one
                // validated fixture) ever showed there, looking exactly like the other 3 fixtures had been
                // deleted. IsLiveFiltering + LiveFilteringProperties so this dropdown still updates itself the
                // moment Test position validates a fixture, same as the old shared-filter behavior did.
                var view = new System.Windows.Data.ListCollectionView(Fixtures.Items)
                {
                    Filter = o => (o as Fixture)?.PositionValidated == true,
                    IsLiveFiltering = true
                };
                view.LiveFilteringProperties.Add(nameof(Fixture.PositionValidated));
                cbxFixture.ItemsSource = view;
            }

            // Values for this code can arrive (or get updated by a Set) at any time, independent of
            // Visibility - subscribe to the live source instead of only refreshing on show/hide, otherwise
            // a flyout that's already visible when data lands (e.g. pinned open before connect) keeps
            // showing the stale "(not available)" tooltip until it's hidden and reshown.
            GrblWorkParameters.CoordinateSystems.CollectionChanged += CoordinateSystems_CollectionChanged;
            TrySubscribeCs();

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
            {
                TrySubscribeCs();
                btnGo.ToolTip = CoordsTooltip();    // refresh - offset values may have changed
            }
        }

        private void CoordinateSystems_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            TrySubscribeCs();
            btnGo.ToolTip = CoordsTooltip();
        }

        private void Cs_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            btnGo.ToolTip = CoordsTooltip();
        }

        // CoordinateSystems entries are created once (AddOrUpdateCS) and updated in place thereafter, so
        // the instance for this code, once found, never changes - only one subscription is ever needed.
        private void TrySubscribeCs()
        {
            var cs = Cs;
            if (cs != null && cs != subscribedCs)
            {
                subscribedCs = cs;
                cs.PropertyChanged += Cs_PropertyChanged;
            }
        }

        private string CoordsTooltip()
        {
            var cs = Cs;
            if (cs == null)
                return code + " (not available)";

            bool isSet = false;
            var sb = new StringBuilder("Go to " + code);
            for (int i = 0; i < cs.Values.Length; i++)
            {
                if (!double.IsNaN(cs.Values[i]))
                {
                    isSet |= cs.Values[i] != 0d;
                    sb.Append(string.Format("   {0}: {1}", GrblInfo.AxisIndexToLetter(i), cs.Values[i].ToInvariantString("F3")));
                }
            }
            return isSet ? sb.ToString() : (code + " not set");
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

            var cs = Cs;
            if (cs == null)
                return;

            StringBuilder sb;

            if (IsWorkOrigin(code))
            {
                // A WORK ORIGIN. "Set G54" means "make here the origin", which is G10 L20 - the same thing
                // the DRO's Zero all sends. L20 stores MPos - G92 - TLO (grblHAL gcode.c, the L20 case:
                // "WPos = MPos - WCS - G92 - TLO -> WCS = MPos - G92 - TLO - WPos"), so the DRO reads zero
                // afterwards whatever offsets are live.
                //
                // This used to send G10 L2 with the raw machine position, which stores MPos with nothing
                // subtracted - identical while G92 and TLO are both zero, and silently wrong otherwise:
                // with a tool length offset loaded it left the DRO reading -(G92 + TLO) instead of 0.
                sb = new StringBuilder("G10L20P" + cs.Id);
                for (int i = 0; i < GrblInfo.NumAxes; i++)
                    sb.Append(GrblInfo.AxisIndexToLetter(i) + "0");
            }
            else
            {
                // G59.1-.3 are MACHINE locations, not work origins - G59.3 is conventionally the toolsetter
                // (jog to it and Set). A tool length offset has no business entering those, so they keep the
                // raw machine position. G10 L2 P<n> takes machine coordinates.
                sb = new StringBuilder("G10L2P" + cs.Id);
                for (int i = 0; i < GrblInfo.NumAxes; i++)
                    sb.Append(GrblInfo.AxisIndexToLetter(i) + grbl.MachinePosition.Values[i].ToInvariantString("F3"));
            }

            Comms.com.WriteCommand(sb.ToString());
        }

        // G54-G59 are the ordinary work origins; G59.1/.2/.3 (the only G5x codes carrying a suffix) are
        // fixed machine references. The distinction decides whether Set means L20 or L2.
        private static bool IsWorkOrigin(string code)
        {
            return code != null && code.StartsWith("G5") && !code.Contains(".");
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
