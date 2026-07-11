/*
 * FixtureEditDialog.xaml.cs - part of CNC Controls library
 *
 * Edits a single Fixture. The Kind dropdown drives which offset fields are shown/relevant and applies
 * reasonable defaults - same idiom as ProbeDefinitionEditDialog. The caller passes a clone and copies it back
 * on OK so Cancel reverts. "Set position" here does exactly what the fixture list's own Set position button
 * does - captures the CURRENT machine position into this fixture's Coords. It is NOT a firmware G28 write;
 * the position lives only in this fixture's own definition (see Fixtures.CurrentCoordsCsv).
 *
 */

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;
using Microsoft.Win32;

namespace CNC.Controls
{
    public partial class FixtureEditDialog : Window
    {
        private readonly GrblViewModel model;
        private bool loading;

        // isNew: seed probe-aware defaults for a brand-new Fixture (Add). False for Edit - an existing
        // fixture's saved values must NOT be clobbered just because the dialog opened.
        public FixtureEditDialog(Fixture fixture, GrblViewModel model, bool isNew = false)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            DataContext = fixture;
            this.model = model;

            loading = true;
            SelectKind(fixture.Kind);             // sets the combo without applying defaults
            if (isNew)
                ApplyDefaults(fixture.Kind, fixture);
            UpdateFieldVisibility(fixture.Kind);
            UpdatePositionDisplay();
            loading = false;
        }

        private void UpdatePositionDisplay()
        {
            var fx = DataContext as Fixture;
            txtPosition.Text = fx != null && fx.HasPosition ? fx.Coords : "Not set";
        }

        private void btnSetPosition_Click(object sender, RoutedEventArgs e)
        {
            var fx = DataContext as Fixture;
            if (fx == null)
                return;

            string coords = Fixtures.CurrentCoordsCsv(model);
            if (coords == null)
            {
                if (model != null)
                    model.Message = "Machine position unknown - home first to save a fixture position.";
                return;
            }

            fx.Coords = coords;
            UpdatePositionDisplay();
        }

        private void SelectKind(FixtureKind kind)
        {
            foreach (ComboBoxItem item in cbxKind.Items)
                if ((string)item.Tag == kind.ToString())
                {
                    cbxKind.SelectedItem = item;
                    break;
                }
        }

        private FixtureKind SelectedKind
        {
            get { return (FixtureKind)Enum.Parse(typeof(FixtureKind), (string)((ComboBoxItem)cbxKind.SelectedItem).Tag); }
        }

        private void cbxKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbxKind.SelectedItem == null)
                return;

            var kind = SelectedKind;
            var fx = DataContext as Fixture;
            if (fx != null)
                fx.Kind = kind;

            if (!loading)                                  // user changed the kind - reset to that kind's defaults
                ApplyDefaults(kind, fx);

            UpdateFieldVisibility(kind);
        }

        // Show only the fields relevant to the selected kind (the ProbesSpoilboard/ProbesEdges facts are
        // fixed per-kind, not user-editable - see FixtureKinds), and switch to the matching schematic so it's
        // obvious what each offset is measured from/to (same reasoning as ProbeDefinitionEditDialog).
        private void UpdateFieldVisibility(FixtureKind kind)
        {
            bool spoil = FixtureKinds.ProbesSpoilboard(kind);
            Show(fldSpoilX, spoil);
            Show(fldSpoilY, spoil);

            bool edges = FixtureKinds.ProbesEdges(kind);
            Show(fldEdgeX, edges);
            Show(fldEdgeY, edges);

            // Top-probe (Z) fields always apply - every fixture kind probes stock-top somehow.

            // The three edge-probing kinds (Corner fence / Dog-hole / Vacuum) share one schematic - only the
            // known-position kind (Vise) differs in shape.
            Show(drwCornerStyle, edges);
            Show(drwKnownPosition, !edges);

            txtNotImplemented.Visibility = FixtureKinds.Implemented(kind) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void Show(UIElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Reasonable starting values when a kind is chosen. Top-probe insets keep the 3D probe's BODY (not
        // just the tip) clear of the fence/corner while it seeks - so they scale off the probe's MinStandoff
        // (body radius) plus a small margin, rather than being a flat guess. With no 3D probe defined yet, or
        // the default 42mm-body probe, this reproduces pcorner.macro's original hardcoded 30mm exactly
        // (21mm standoff + 9mm margin = 30) - a bigger/smaller probe body scales accordingly.
        // Edge-probe offset is a fixed physical constraint, not a clearance choice: the probe point must be
        // D/2 away from the X/Y faces (else the body doesn't clear the corner) and no more than D away (else
        // it seeks past the face and never touches). D (the probe body diameter) is the exact, always-valid
        // default.
        private static void ApplyDefaults(FixtureKind kind, Fixture fx)
        {
            if (fx == null)
                return;

            switch (kind)
            {
                case FixtureKind.CornerFence:
                case FixtureKind.DogHoleGridCorner:
                case FixtureKind.VacuumTableZeroCorner:
                {
                    var probe = ProbeDefinitions.Items.FirstOrDefault(p => p.ProbeType == ProbeType.ThreeDProbe);
                    double clearance = probe != null ? probe.MinStandoff + 9d : 30d;
                    double edge = probe != null ? probe.BodyDiameter : 30d;
                    fx.SpoilProbeOffsetX = 0d; fx.SpoilProbeOffsetY = 0d;
                    fx.TopProbeOffsetX = clearance; fx.TopProbeOffsetY = clearance;
                    fx.EdgeProbeOffsetX = edge; fx.EdgeProbeOffsetY = edge;
                    break;
                }

                case FixtureKind.MachinistVise:
                    fx.SpoilProbeOffsetX = 0d; fx.SpoilProbeOffsetY = 0d;
                    fx.TopProbeOffsetX = 0d; fx.TopProbeOffsetY = 0d;
                    fx.EdgeProbeOffsetX = 0d; fx.EdgeProbeOffsetY = 0d;
                    break;
            }
        }

        private void btnBrowsePicture_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true)
                txtPictureFile.Text = dlg.FileName;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
