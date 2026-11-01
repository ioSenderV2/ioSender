/*
 * ProbeMotionParamsDialog.xaml.cs - part of CNC Controls library
 *
 * The "Edit motion params" sub-dialog of the probe editor: probing speeds/distances most users leave at
 * the defaults. Edits the same ProbeDefinition live (two-way binding); Close just dismisses.
 *
 */

using System.Windows;

namespace CNC.Controls
{
    public partial class ProbeMotionParamsDialog : Window
    {
        public ProbeMotionParamsDialog(ProbeDefinition definition)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            DataContext = definition;

            var type = definition.ProbeType;

            // Both of these describe approaching an edge FROM THE SIDE: stand off this far out, drop this
            // far down, then probe sideways. A flat Z-only plate never does that - it is set on top of the
            // work and touched straight down - so the two fields are not "leave them at the default", they
            // describe a move it cannot make. A corner plate still shows them, because it does.
            bool probesSideways = type == ProbeType.ThreeDProbe || type == ProbeType.EdgeFinder ||
                                  (type == ProbeType.TouchPlate && definition.CanProbeCorner);

            Show(fldXYClr, probesSideways);
            Show(fldZClr, probesSideways);
            Show(fldOffsetX, type == ProbeType.ThreeDProbe);
            Show(fldOffsetY, type == ProbeType.ThreeDProbe);
        }

        private static void Show(UIElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
