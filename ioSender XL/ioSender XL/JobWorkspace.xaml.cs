/*
 * JobWorkspace.xaml.cs - part of ioSender
 *
 * The center workspace (program list / 3D view / console), extracted from JobView in Phase 2a of
 * the registration architecture refactor (see docs/Architecture-Registration-Refactor.md). It owns
 * the 3D-render and 3D-tab-visibility wiring that used to live in JobView's code-behind; JobView now
 * coordinates through the small surface below.
 */

using System.Windows.Controls;
using CNC.Controls;

namespace GCode_Sender
{
    public partial class JobWorkspace : UserControl
    {
        public JobWorkspace()
        {
            InitializeComponent();
        }

        // Render the currently-loaded program's toolpath in the 3D view. JobView calls this on a
        // FileName change (gating the job poller around it, since GCodeSender lives in JobView).
        public void ShowToolpath()
        {
            gcodeRenderer.Open(GCode.File.Tokens);
        }

        // Clear the 3D view (file closed).
        public void ClearToolpath()
        {
            gcodeRenderer.Close();
        }

        // Show/hide the 3D View tab to match the GCodeViewer-enabled setting (called once at init).
        public void Set3DViewEnabled(bool enabled)
        {
            if (!enabled && tabGCode.Items.Contains(tab3D))
                tabGCode.Items.Remove(tab3D);
        }
    }
}
