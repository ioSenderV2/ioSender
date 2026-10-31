/*
 * CarveViewOptionsControl.xaml.cs - part of CNC GCodeViewer
 *
 * The 3D view's options, defined once and hosted twice - see the XAML's header for why.
 *
 * Deliberately knows nothing about either host: no Save, no Close, no view reference. Saving belongs to
 * whoever is showing it (the dialog saves on close; the settings page is saved by the settings host),
 * and "Reset view" stayed on the dialog because it acts on a view this control has no business holding.
 */

using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls.Viewer
{
    public partial class CarveViewOptionsControl : UserControl
    {
        public CarveViewOptionsControl()
        {
            InitializeComponent();
        }

        private void btnDefaults_Click(object sender, RoutedEventArgs e)
        {
            // The config, not the view: ResetColors puts the three swatches back to the shipped palette,
            // which a colour picker can otherwise make unrecoverable - a cut path the same colour as the
            // stock looks like a toolpath that vanished, with nothing on screen saying why.
            (DataContext as CarveViewConfig)?.ResetColors();
        }
    }
}
