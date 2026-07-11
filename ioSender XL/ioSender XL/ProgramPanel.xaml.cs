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
        // Load / Close items, which route through the shared static GCode.File. Load Folder retired -
        // the Fusion ioSenderBatchPost add-in now posts one already-combined file (with the same section
        // markers/outline) instead of a folder of per-op files, so plain Load File covers it.
        private void LoadFile_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Open();
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Close();
        }
    }
}
