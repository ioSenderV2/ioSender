/*
 * ToolsView.xaml.cs - part of CNC Controls library
 *
 * Top-level "Tools" tab: the tool table plus the commissioning / tuning tools (stepper calibration,
 * spoilboard surfacing, Trinamic and PID tuning). The tool table is an ICNCView; the tools are
 * IGrblConfigTab. The selected child is activated either way (mirrors GrblConfigView).
 */

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class ToolsView : UserControl, ICNCView, ITabBindingHost
    {
        public ToolsView()
        {
            InitializeComponent();
            RegisterTools();
            BuildTools();
        }

        // Register the built-in tools as placeable components. Adding a new tool is a Register call from
        // its own code + a node in the default layout's Tools slot - no edit to this view's markup.
        private static void RegisterTools()
        {
            ComponentRegistry.Register(LayoutKeys.ToolTable, L("TabToolTable", "Tool table"), () => new ToolView());
            ComponentRegistry.Register(LayoutKeys.StepperCal, L("TabStepperCal", "Stepper calibration"), () => new StepperCalibrationWizard());
            ComponentRegistry.Register(LayoutKeys.StepperScratch, L("TabStepperScratch", "Stepper calibration (scratch)"), () => new StepperCalibrationScratchWizard());
            ComponentRegistry.Register(LayoutKeys.SurfaceSpoilboard, L("TabSurfaceSpoilboard", "Surface spoilboard"), () => new SurfaceSpoilboardWizard());
            ComponentRegistry.Register(LayoutKeys.Squareness, L("TabSquareness", "Squareness"), () => new AutoSquareWizard());
            ComponentRegistry.Register(LayoutKeys.Trinamic, L("TabTrinamic", "Trinamic tuner"), () => new TrinamicView());
            ComponentRegistry.Register(LayoutKeys.PID, L("TabPID", "PID Tuner"), () => new PIDLogView());
        }

        // Localized component label via LibStrings, falling back to the English literal if absent.
        private static string L(string key, string fallback)
        {
            string s = LibStrings.FindResource(key);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        // Build the sub-tabs from the layout tree's Tools/"tools" slot (order = tree order).
        private void BuildTools()
        {
            var toolsNode = LayoutTree.Flatten(AppConfig.Settings.Layout).FirstOrDefault(n => n.Component == LayoutKeys.Tools);
            var slot = toolsNode?.Slot(LayoutKeys.SlotTools);
            if (slot == null)
                return;

            tabTools.Items.Clear();
            foreach (var node in slot.Items)
            {
                var d = ComponentRegistry.Get(node.Component);
                var ctl = d?.Create?.Invoke();
                if (ctl != null)
                {
                    // Bindable sub-tab: id = "Tab.Tools.<componentKey>" (the Tag stays the layout key for reorder).
                    string tabId = "Tab.Tools." + node.Component;
                    // x:Uid is a markup-only directive, and these tabs are built in code, so they have no
                    // authored Uid. Set it explicitly from the registry key (unique + stable) so the UI test
                    // server can address the Tools sub-tabs by Uid and select one via its SelectionItem peer.
                    var tab = new TabItem { Content = ctl, Tag = node.Component, Uid = "tab_" + node.Component };
                    tab.Header = new TabHeaderControl(d.Label, tabId);
                    TabKeyBinder.AttachBindMenu(tab, tabId);
                    tabTools.Items.Add(tab);
                }
            }

            // Persist drag-reorder into the layout tree's Tools slot (the order authority BuildTools reads).
            tabTools.TabsReordered += (s, e) => AppConfig.Settings.ReorderSlot(LayoutKeys.Tools, LayoutKeys.SlotTools,
                tabTools.Items.Cast<TabItem>().Select(t => t.Tag as string).Where(k => !string.IsNullOrEmpty(k)));
        }

        // Drill into a tool sub-tab from a "Tab.Tools.*" keyboard shortcut (ITabBindingHost). The suffix is the
        // layout component key held in each tab's Tag; a tab removed at runtime (no tool table / Trinamic / PID)
        // simply isn't found.
        public bool SelectSubTab(string id)
        {
            const string prefix = "Tab.Tools.";
            if (id == null || !id.StartsWith(prefix))
                return false;

            string key = id.Substring(prefix.Length);
            var target = tabTools.Items.Cast<TabItem>().FirstOrDefault(t => (t.Tag as string) == key);
            if (target == null)
                return false;

            tabTools.SelectedItem = target;
            return true;
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.Tools; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            ActivateTab(tabTools.SelectedItem as TabItem ?? tabTools.Items[0] as TabItem, activate);
        }

        public void CloseFile() { }

        public void Setup(UIViewModel model, AppConfig profile)
        {
            // Forward Setup to the nested tool table (ICNCView) so it initialises like a top-level view would.
            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(this))
                if (uc is ToolView tv)
                {
                    ((ICNCView)tv).Setup(model, profile);
                    break;
                }
        }

        #endregion

        // Activate/deactivate the selected sub-tab, whether it hosts an IGrblConfigTab (the tools) or an
        // ICNCView (the tool table).
        private void ActivateTab(TabItem tab, bool activate)
        {
            if (tab == null)
                return;

            var cfg = getView(tab);
            if (cfg != null)
            {
                cfg.Activate(activate);
                return;
            }

            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(tab))
                if (uc is ICNCView view)
                {
                    view.Activate(activate, ViewType.Tools);
                    return;
                }
        }

        private static IGrblConfigTab getView(TabItem tab)
        {
            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(tab))
                if (uc is IGrblConfigTab)
                    return (IGrblConfigTab)uc;
            return null;
        }

        private void tab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!Equals(e.OriginalSource, sender))
                return;

            e.Handled = true;
            if (e.AddedItems.Count != 1)
                return;

            var removed = e.RemovedItems.Count == 1 ? e.RemovedItems[0] as TabItem : null;
            var added = e.AddedItems[0] as TabItem;

            // Defer: a child's Activate may pump the dispatcher (ToolView -> GrblWorkParameters.Get -> DoEvents),
            // which throws if it runs during the layout pass that generated this nested TabControl's items.
            Dispatcher.BeginInvoke((System.Action)(() =>
            {
                if (removed != null)
                    ActivateTab(removed, false);
                ActivateTab(added, true);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Drop the Tools sub-tabs the controller can't support (empty tool table / no Trinamic drivers / no
            // PID log) and record why for Edit Main Page > Unavailable. Each tool owns its own prerequisite +
            // reason (IAvailabilityGated) - the one source the removal and the listing share. Auto Square is kept
            // even without the squaring-offset setting (it still serves as a squareness GAUGE - drill the L,
            // measure the gap - with only the Apply-offset step disabled); PruneUnavailable notes that limitation
            // without removing the tab.
            ComponentAvailability.Note(tabTools.PruneUnavailable());
        }
    }
}
