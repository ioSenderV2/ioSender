/*
 * JobWorkspace.xaml.cs - part of ioSender
 *
 * The center workspace (program list / 3D view / console), extracted from JobView in Phase 2a of
 * the registration architecture refactor (see docs/Architecture-Registration-Refactor.md). Its tabs
 * are built in code from the layout tree's Grbl/"center" slot (Phase 2b step 4) via ComponentRegistry,
 * so the center panels are placeable/orderable like the top-level tabs and the Tools sub-tabs. It owns
 * the 3D-render and 3D-tab-visibility wiring that used to live in JobView's code-behind.
 *
 * Split screen (Settings > User Interface > General, 2026-08-10) is a second presentation of the same
 * components: program view and 3D view side by side with a splitter, no tab strip. It is built INSTEAD
 * of the tabs, never alongside them - building both would mean two live 3D views.
 */

using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Controls;
using CNC.Controls.Viewer;

namespace GCode_Sender
{
    public partial class JobWorkspace : UserControl
    {
        // The 3D-view instance (built from the tree) - kept for show/clear. Held as the INTERFACE, not as
        // RenderControl: this was "ctl as RenderControl", and CarveView - the component actually
        // registered - is a sibling UserControl, not a RenderControl, so the cast returned null and both
        // ShowToolpath() and ClearToolpath() were silent no-ops. Loading still drew, because CarveView
        // watches FileName itself; only the CLEAR was lost, which is why a finished job's carving stayed
        // on the stock after the program evaporated.
        private IToolpathView gcodeRenderer;
        private TabItem tab3D;                  // the 3D-view tab - kept so it can be hidden when 3D is disabled
        private bool view3DEnabled = true;      // mirrors the GCodeViewer setting - see Set3DViewEnabled
        private bool splitBuilt;                // which presentation is currently live

        public JobWorkspace()
        {
            InitializeComponent();
            RegisterCenter();
            Build();

            // The checkbox takes effect immediately, like the jog-pad toggles next to it. Rebuilding is
            // the honest way to switch: the two presentations own different control instances, and the
            // 3D view in particular cannot be in both at once.
            //
            // Null-guarded because this runs from a constructor: Base is populated when the config loads,
            // and a control built before that has already crashed the app once on this branch (the
            // "config read before it is loaded" startup fix). Without Base there is nothing to subscribe
            // to and SplitRequested has already answered false, so the tabs are the correct fallback.
            if (AppConfig.Settings.Base != null)
                AppConfig.Settings.Base.PropertyChanged += Base_PropertyChanged;
            // The splitter's position is only meaningful once it has been dragged, and dragging is the
            // only way it moves - so save on completion rather than polling the column widths.
            jobSplitter.DragCompleted += (s, e) => SaveSplitRatio();
        }

        // Fails closed to the tab strip if the config is not loaded yet - see the constructor's note.
        private static bool SplitRequested
        {
            get { return AppConfig.Settings.Base != null && AppConfig.Settings.Base.ShowJobSplitView; }
        }

        private void Base_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppConfig.Settings.Base.ShowJobSplitView))
                Build();
        }

        // Register the center components (program list / 3D view / console) as placeable components. The
        // layout tree decides their presence/order; the slot presents them as the bottom-strip tabs.
        private static void RegisterCenter()
        {
            ComponentRegistry.Register(LayoutKeys.Program, "Program", () => new ProgramPanel());
            ComponentRegistry.Register(LayoutKeys.Toolpath3D, "3D View", () => new CarveView());   // live machine/carve view (replaces RenderControl)
            ComponentRegistry.Register(LayoutKeys.Console, "Console", () => new ConsoleControl());
        }

        /// <summary>
        /// Build whichever presentation the settings ask for, discarding the other one's controls.
        /// </summary>
        private void Build()
        {
            // Split screen needs a 3D view to put in the other half. With the viewer disabled there is
            // nothing to split, so fall back to the tabs rather than present half an empty screen.
            bool split = SplitRequested && view3DEnabled;

            tabGCode.Items.Clear();
            splitProgram.Content = null;
            split3D.Content = null;
            gcodeRenderer = null;
            tab3D = null;

            if (split)
                BuildSplit();
            else
                BuildCenter();

            splitBuilt = split;
            splitRoot.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            tabGCode.Visibility = split ? Visibility.Collapsed : Visibility.Visible;

            // A rebuild replaces the renderer, so whatever is loaded has to be drawn into the new one -
            // otherwise toggling the setting mid-job leaves an empty 3D view until the next file load.
            if (GCode.File.IsLoaded)
                ShowToolpath();
        }

        // Build the center tabs from the layout tree's Grbl/"center" slot (order = tree order).
        private void BuildCenter()
        {
            var grblNode = LayoutTree.Flatten(AppConfig.Settings.Layout).FirstOrDefault(n => n.Component == LayoutKeys.Grbl);
            var slot = grblNode?.Slot(LayoutKeys.SlotCenter);
            if (slot == null)
                return;

            foreach (var node in slot.Items)
            {
                var d = ComponentRegistry.Get(node.Component);
                var ctl = d?.Create?.Invoke();
                if (ctl == null)
                    continue;

                // Every center tab is tearable: double-click its header to pop it into its own window,
                // double-click that window's title bar to dock it back (CNC.Controls.TearableTab).
                var tab = TearableTab.Attach(tabGCode, d.Label, ctl);
                // x:Uid is a markup-only directive, and these tabs are built in code, so they have no
                // authored Uid. Set it explicitly from the registry key (unique + stable) so the UI test
                // server can address the center tabs by Uid and select one via its SelectionItem peer.
                tab.Uid = "tab_" + node.Component;
                if (node.Component == LayoutKeys.Toolpath3D)
                {
                    tab3D = tab;
                    gcodeRenderer = ctl as IToolpathView;
                }
                tabGCode.Items.Add(tab);
            }

            if (!view3DEnabled && tab3D != null && tabGCode.Items.Contains(tab3D))
                tabGCode.Items.Remove(tab3D);
        }

        /// <summary>
        /// Split screen: program view and 3D view side by side. Deliberately NOT built from the layout
        /// tree's order - the split is two named halves, not an orderable list, and the tree's third
        /// member (the console) has no place in it.
        /// </summary>
        private void BuildSplit()
        {
            splitProgram.Content = ComponentRegistry.Get(LayoutKeys.Program)?.Create?.Invoke();

            var view = ComponentRegistry.Get(LayoutKeys.Toolpath3D)?.Create?.Invoke();
            split3D.Content = view;
            gcodeRenderer = view as IToolpathView;

            // Star widths, so the two halves keep their PROPORTIONS when the window resizes rather than
            // one half absorbing everything - the same reason the columns are "*" in the markup.
            double r = AppConfig.Settings.Base == null ? 0.5d : AppConfig.Settings.Base.JobSplitRatio;
            splitLeftCol.Width = new GridLength(r, GridUnitType.Star);
            splitRightCol.Width = new GridLength(1d - r, GridUnitType.Star);
        }

        private void SaveSplitRatio()
        {
            double left = splitLeftCol.Width.Value, right = splitRightCol.Width.Value;
            double total = left + right;
            if (total > 0d && AppConfig.Settings.Base != null)
                AppConfig.Settings.Base.JobSplitRatio = left / total;
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

        // Show/hide the 3D View to match the GCodeViewer-enabled setting (called once at init). With the
        // viewer off there is nothing to split, so this can also force the presentation back to tabs.
        public void Set3DViewEnabled(bool enabled)
        {
            view3DEnabled = enabled;

            if (!enabled && splitBuilt)
            {
                Build();
                return;
            }
            if (!enabled && tab3D != null && tabGCode.Items.Contains(tab3D))
                tabGCode.Items.Remove(tab3D);
        }
    }
}
