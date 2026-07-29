/*
 * ProgramPanel.xaml.cs - part of ioSender XL
 *
 * The loaded-program list + source title bar, registered as the "Program" center component so the Grbl
 * (Job) tab's center can be built from the layout tree (Phase 2b step 4).
 */

using System.Windows;
using System.Windows.Controls;
using CNC.Controls;

namespace GCode_Sender
{
    public partial class ProgramPanel : UserControl
    {
        public ProgramPanel()
        {
            InitializeComponent();
        }

        // The title bar's close button mirrors the old File-menu Close item, routed through the shared
        // static GCode.File. Load moved to the main menu's Load File item (MainWindow.xaml.cs).
        private void CloseFile_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Close();
        }
    }
}
