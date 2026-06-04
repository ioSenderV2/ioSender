/*
 * UIJoggingControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Assignable main-page / flyout panel: the UI jog distance and rate selectors
 * rendered as two sliders with a read-only value readout under each. Backed by
 * the shared JogViewModel (JogBaseControl.JogData); values are edited on Settings:App.
 *
 */

using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public partial class UIJoggingControl : UserControl
    {
        public UIJoggingControl()
        {
            InitializeComponent();
        }

        private void UIJoggingControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Bind the sliders to the shared jog view-model (the active selection used by the arrows).
            // content inherits the GrblViewModel DataContext, so set it explicitly (not "== null").
            content.DataContext = JogBaseControl.JogData;
        }
    }
}
