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
    public partial class ToolsView : UserControl, ICNCView
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
                    tabTools.Items.Add(new TabItem { Header = d.Label, Content = ctl });
            }
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

        private TabItem getTab(GrblConfigType tabtype)
        {
            foreach (TabItem tabitem in UIUtils.FindLogicalChildren<TabItem>(tabTools))
            {
                var view = getView(tabitem);
                if (view != null && view.GrblConfigType == tabtype)
                    return tabitem;
            }
            return null;
        }

        private void RemoveTab(GrblConfigType type)
        {
            var ptab = getTab(type);
            if (ptab != null)
                tabTools.Items.Remove(ptab);
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // No controller tool table -> drop the (empty) Tool table sub-tab; the rest of the hub still applies.
            if (GrblInfo.NumTools == 0)
                foreach (TabItem t in UIUtils.FindLogicalChildren<TabItem>(tabTools))
                    if (t.Content is ToolView) { tabTools.Items.Remove(t); break; }

            if (string.IsNullOrEmpty(GrblInfo.TrinamicDrivers))
                RemoveTab(GrblConfigType.Trinamic);

            if (!GrblInfo.HasPIDLog)
                RemoveTab(GrblConfigType.PidTuning);

            // Auto square is kept on every build: when the squaring-offset setting is present it tunes that
            // offset; when it isn't (e.g. ganged but not auto-squared, or not ganged at all) it still serves as
            // a squareness GAUGE - drill the L, measure the gap with a framing square to see how far out of
            // square the X/Y axes are - just with the Apply-offset step disabled (correct mechanically instead).
        }
    }
}
