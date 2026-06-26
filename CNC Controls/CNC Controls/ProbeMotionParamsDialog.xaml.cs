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
            DataContext = definition;

            var type = definition.ProbeType;
            bool xy = type == ProbeType.ThreeDProbe || type == ProbeType.EdgeFinder || type == ProbeType.TouchPlate;

            Show(fldXYClr, xy);
            Show(fldZClr, type != ProbeType.ToolSetter);
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
