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
using System.Collections.Generic;
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

        // G28 gets the named-fixture library (an app-side dropdown of saved machine positions); other codes
        // keep the plain single-slot Set/Go. See NamedPositionConfig / GotoBaseControl.SafeGotoMachine.
        private readonly bool named;

        public OffsetFlyout(string code)
        {
            InitializeComponent();
            this.code = code;
            PanelName = code;
            btnGo.Content = code;
            // "Set" stores the current machine position: G28/G30 via their .1 form, G54-G59.3 via G10 L2.
            btnSet.Visibility = (code == "G28" || code == "G30" || code.StartsWith("G5")) ? Visibility.Visible : Visibility.Collapsed;

            named = code == "G28";
            if (named)
            {
                cboFixtures.Visibility = Visibility.Visible;
                DedupeFixtures();       // heal any same-position duplicates left by earlier sessions
                RefreshFixtures();
            }

            IsVisibleChanged += OffsetFlyout_IsVisibleChanged;
        }

        private static NamedPositionConfig Presets { get { return ConfigStore.Get<NamedPositionConfig>(); } }

        // (Re)load the fixture names into the dropdown, preserving the current edit-box text.
        private void RefreshFixtures()
        {
            var cfg = Presets;
            if (cfg == null)
                return;

            string current = cboFixtures.Text;
            cboFixtures.ItemsSource = cfg.For(code).Select(p => p.Name)
                                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            cboFixtures.Text = current;
        }

        // The enabled axes' current machine position as an invariant CSV (the stored-coords format), or null
        // when the position is unknown (disconnected / not homed) - in which case callers leave the box alone.
        private string CurrentCoordsCsv(GrblViewModel grbl)
        {
            if (grbl == null)
                return null;
            var idx = GrblInfo.AxisFlags.ToIndices().ToList();
            if (idx.Any(i => double.IsNaN(grbl.MachinePosition.Values[i])))
                return null;
            return string.Join(",", idx.Select(i => grbl.MachinePosition.Values[i].ToInvariantString("F3")));
        }

        private static bool IsDefaultName(string name)
        {
            return name != null && name.StartsWith("Fixture-", StringComparison.OrdinalIgnoreCase);
        }

        // Enforce one-name-per-position across the whole store (heals duplicates from earlier sessions).
        // When two fixtures share a position, keep the user's custom name over an auto "Fixture-N" default.
        private void DedupeFixtures()
        {
            var cfg = Presets;
            if (cfg == null)
                return;

            var kept = new List<NamedPosition>();
            bool changed = false;

            foreach (var p in cfg.For(code).ToList())
            {
                var dup = kept.FirstOrDefault(k => CoordsMatch(k.Coords, p.Coords));
                if (dup == null)
                {
                    kept.Add(p);
                    continue;
                }

                if (IsDefaultName(dup.Name) && !IsDefaultName(p.Name))   // prefer the custom name
                {
                    cfg.Remove(code, dup.Name);
                    kept.Remove(dup);
                    kept.Add(p);
                }
                else
                    cfg.Remove(code, p.Name);

                changed = true;
            }

            if (changed)
                AppConfig.Settings.Save();
        }

        private static bool CoordsMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            var pa = new Position(a);
            var pb = new Position(b);
            foreach (int i in GrblInfo.AxisFlags.ToIndices())
                if (Math.Abs(pa.Values[i] - pb.Values[i]) > 0.001)
                    return false;
            return true;
        }

        // Called when the name box gets control: reconcile the name to where the machine ACTUALLY is, so Set
        // saves the right thing without the user hunting. If the current name already describes this position,
        // leave it; else if a saved fixture matches this position show its name (you're parked at a known one);
        // else offer the next Fixture-N (you're at a new spot -> Set creates it). Editing/picking still overrides.
        private void ResolveNameForCurrentPosition()
        {
            var cfg = Presets;
            var grbl = DataContext as GrblViewModel;
            if (cfg == null || grbl == null)
                return;

            string here = CurrentCoordsCsv(grbl);
            if (here == null)                                   // position unknown - don't disturb the box
                return;

            var current = cfg.Find(code, cboFixtures.Text);
            if (current != null && CoordsMatch(current.Coords, here))
                return;                                         // current name already fits this position

            var match = cfg.For(code).FirstOrDefault(p => CoordsMatch(p.Coords, here));
            cboFixtures.Text = match != null ? match.Name : cfg.NextDefaultName(code);
        }

        // GotKeyboardFocus and DropDownOpened carry different delegate signatures - both funnel here.
        private void OnFixtureBoxEnter()
        {
            RefreshFixtures();
            ResolveNameForCurrentPosition();
            btnGo.ToolTip = CoordsTooltip();
        }

        private void cboFixtures_GotFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            OnFixtureBoxEnter();
        }

        private void cboFixtures_DropDownOpened(object sender, EventArgs e)
        {
            OnFixtureBoxEnter();
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
            {
                if (named)
                    OnFixtureBoxEnter();            // reconcile the fixture name to the current machine position
                else
                    btnGo.ToolTip = CoordsTooltip();    // refresh - offset values may have changed
            }
        }

        private void cboFixtures_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            btnGo.ToolTip = CoordsTooltip();
        }

        // The preset (if any) the edit box currently names.
        private NamedPosition SelectedFixture
        {
            get { return named ? Presets?.Find(code, cboFixtures.Text) : null; }
        }

        private string CoordsTooltip()
        {
            // Named G28: show the selected fixture's stored machine coordinates (or fall back to the firmware
            // slot when the edit box names no saved preset).
            var fixture = SelectedFixture;
            if (fixture != null)
            {
                var pos = new Position(fixture.Coords);
                var fsb = new StringBuilder("Go to " + fixture.Name);
                foreach (int i in GrblInfo.AxisFlags.ToIndices())
                    if (!double.IsNaN(pos.Values[i]))
                        fsb.Append(string.Format("   {0}: {1}", GrblInfo.AxisIndexToLetter(i), pos.Values[i].ToInvariantString("F3")));
                return fsb.ToString();
            }

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

            // Named G28: rapid to the SELECTED fixture's stored machine coordinates (app-side, decoupled from
            // the firmware G28 slot). With no saved preset named, fall through to the plain firmware go-to.
            var fixture = SelectedFixture;
            if (fixture != null)
            {
                GotoBaseControl.SafeGotoMachine(grbl, new Position(fixture.Coords));
                return;
            }

            GotoBaseControl.SafeGoto(grbl, code);   // the one shared Go-To routine - applies Safe Z uniformly
        }

        private void btnSet_Click(object sender, RoutedEventArgs e)
        {
            var grbl = DataContext as GrblViewModel;
            if (grbl == null || Comms.com == null)
                return;

            // Named G28: associate the name in the edit box with the CURRENT machine position and store it
            // app-side (no firmware write). Blank name -> the next Fixture-N default.
            if (named)
            {
                var cfg = Presets;
                if (cfg == null)
                    return;

                // Capture the enabled axes' machine coordinates (round-trips via Position). Refuse when the
                // position is unknown (disconnected / not homed) - nothing valid to store.
                string coords = CurrentCoordsCsv(grbl);
                if (coords == null)
                {
                    grbl.Message = "Machine position unknown - home first to save a fixture position.";
                    return;
                }

                string name = cboFixtures.Text?.Trim();
                if (string.IsNullOrEmpty(name))
                    name = cfg.NextDefaultName(code);

                // One name per position: drop any OTHER fixture already saved at this exact position, so
                // editing the name of the fixture you're parked at is a rename (not a duplicate), and stray
                // duplicates collapse to the single name you just Set.
                foreach (var dup in cfg.For(code).Where(p => p.Name != name && CoordsMatch(p.Coords, coords)).ToList())
                    cfg.Remove(code, dup.Name);

                cfg.Set(code, name, coords);
                AppConfig.Settings.Save();

                RefreshFixtures();
                cboFixtures.Text = name;            // select the just-saved fixture
                btnGo.ToolTip = CoordsTooltip();
                return;
            }

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
