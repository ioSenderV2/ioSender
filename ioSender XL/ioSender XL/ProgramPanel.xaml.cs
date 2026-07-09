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

        // The title bar doubles as the file toolbar (menu overhaul): these mirror the old File-menu
        // Load / Load Folder / Close items, which all route through the shared static GCode.File.
        private void LoadFile_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Open();
        }

        private void LoadFolder_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.OpenFolder();
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Close();
        }
    }
}
