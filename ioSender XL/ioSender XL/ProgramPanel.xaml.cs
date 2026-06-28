/*
 * ProgramPanel.xaml.cs - part of ioSender XL
 *
 * The loaded-program list + source title bar, registered as the "Program" center component so the Grbl
 * (Job) tab's center can be built from the layout tree (Phase 2b step 4).
 */

using System.Windows.Controls;

namespace GCode_Sender
{
    public partial class ProgramPanel : UserControl
    {
        public ProgramPanel()
        {
            InitializeComponent();
        }
    }
}
