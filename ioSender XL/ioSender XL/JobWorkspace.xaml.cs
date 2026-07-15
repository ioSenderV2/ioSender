/*
 * JobWorkspace.xaml.cs - part of ioSender
 *
 * The center workspace (program list / 3D view / console), extracted from JobView in Phase 2a of
 * the registration architecture refactor (see docs/Architecture-Registration-Refactor.md). Its tabs
 * are built in code from the layout tree's Grbl/"center" slot (Phase 2b step 4) via ComponentRegistry,
 * so the center panels are placeable/orderable like the top-level tabs and the Tools sub-tabs. It owns
 * the 3D-render and 3D-tab-visibility wiring that used to live in JobView's code-behind.
 */

using System.Linq;
using System.Windows.Controls;
using CNC.Controls;
using CNC.Controls.Viewer;

namespace GCode_Sender
{
    public partial class JobWorkspace : UserControl
    {
        private RenderControl gcodeRenderer;   // the 3D-view instance (built from the tree) - kept for show/clear
        private TabItem tab3D;                  // the 3D-view tab - kept so it can be hidden when 3D is disabled

        public JobWorkspace()
        {
            InitializeComponent();
            RegisterCenter();
            BuildCenter();
        }

        // Register the center components (program list / 3D view / console) as placeable components. The
        // layout tree decides their presence/order; the slot presents them as the bottom-strip tabs.
        private static void RegisterCenter()
        {
            ComponentRegistry.Register(LayoutKeys.Program, "Program", () => new ProgramPanel());
            ComponentRegistry.Register(LayoutKeys.Toolpath3D, "3D View", () => new CarveView());   // live machine/carve view (replaces RenderControl)
            ComponentRegistry.Register(LayoutKeys.Console, "Console", () => new ConsoleControl());
        }

        // Build the center tabs from the layout tree's Grbl/"center" slot (order = tree order).
        private void BuildCenter()
        {
            var grblNode = LayoutTree.Flatten(AppConfig.Settings.Layout).FirstOrDefault(n => n.Component == LayoutKeys.Grbl);
            var slot = grblNode?.Slot(LayoutKeys.SlotCenter);
            if (slot == null)
                return;

            tabGCode.Items.Clear();
            foreach (var node in slot.Items)
            {
                var d = ComponentRegistry.Get(node.Component);
                var ctl = d?.Create?.Invoke();
                if (ctl == null)
                    continue;

                // Every center tab is tearable: double-click its header to pop it into its own window,
                // double-click that window's title bar to dock it back (CNC.Controls.TearableTab).
                var tab = TearableTab.Attach(tabGCode, d.Label, ctl);
                if (node.Component == LayoutKeys.Toolpath3D)
                {
                    tab3D = tab;
                    gcodeRenderer = ctl as RenderControl;
                }
                tabGCode.Items.Add(tab);
            }
        }

        // Render the currently-loaded program's toolpath in the 3D view. JobView calls this on a
        // FileName change (gating the job poller around it, since GCodeSender lives in JobView).
        public void ShowToolpath()
        {
            gcodeRenderer?.Open(GCode.File.Tokens);
        }

        // Clear the 3D view (file closed).
        public void ClearToolpath()
        {
            gcodeRenderer?.Close();
        }

        // Show/hide the 3D View tab to match the GCodeViewer-enabled setting (called once at init).
        public void Set3DViewEnabled(bool enabled)
        {
            if (!enabled && tab3D != null && tabGCode.Items.Contains(tab3D))
                tabGCode.Items.Remove(tab3D);
        }
    }
}
