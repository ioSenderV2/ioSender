/*
 * OddJobsView.xaml.cs - part of CNC Controls library
 *
 * Top-level "Odd Jobs" tab: a Setup sub-tab (a constrained Start Job instance targeting G59, registered
 * from MainWindow.xaml.cs since only it can see StartJobView) plus simple one-off job wizards (Surface
 * Stock, Drill/Bore Hole, Pocket, Contour/Slot). Same sub-tab-from-layout-tree hosting as ToolsView.
 */

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class OddJobsView : UserControl, ICNCView, ITabBindingHost
    {
        public OddJobsView()
        {
            InitializeComponent();
            RegisterJobs();
            BuildJobs();
        }

        // Both sub-tabs are always available. The Work Order tab used to be hidden until Setup had provably
        // run; that gate is gone (see StartJobConfig.cs for what replaced it and why) - a job can be composed
        // and inspected whenever, and Generate is where the cached-origin question gets asked.

        // Register the work-order composer as a placeable component (Setup itself is registered by
        // MainWindow.xaml.cs, which is the only assembly that can see both ComponentRegistry and StartJobView).
        // This one tab replaced the five fixed job wizards - the operator composes whatever operations a job
        // needs instead of picking the tab whose feature set happens to match.
        private static void RegisterJobs()
        {
            ComponentRegistry.Register(LayoutKeys.OddJobsWorkOrder, L("TabOddJobsWorkOrder", "Work Order"), () => new WorkOrderView());
        }

        private static string L(string key, string fallback)
        {
            string s = LibStrings.FindResource(key);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        // Build the sub-tabs from the layout tree's OddJobs/"oddjobs" slot (order = tree order).
        private void BuildJobs()
        {
            var oddJobsNode = LayoutTree.Flatten(AppConfig.Settings.Layout).FirstOrDefault(n => n.Component == LayoutKeys.OddJobs);
            var slot = oddJobsNode?.Slot(LayoutKeys.SlotOddJobs);
            if (slot == null)
                return;

            tabOddJobs.Items.Clear();
            foreach (var node in slot.Items)
            {
                var d = ComponentRegistry.Get(node.Component);
                var ctl = d?.Create?.Invoke();
                if (ctl != null)
                {
                    string tabId = "Tab.OddJobs." + node.Component;
                    var tab = new TabItem { Content = ctl, Tag = node.Component, Uid = "tab_" + node.Component };
                    tab.Header = new TabHeaderControl(d.Label, tabId);
                    TabKeyBinder.AttachBindMenu(tab, tabId);
                    tabOddJobs.Items.Add(tab);
                }
            }

            tabOddJobs.TabsReordered += (s, e) => AppConfig.Settings.ReorderSlot(LayoutKeys.OddJobs, LayoutKeys.SlotOddJobs,
                tabOddJobs.Items.Cast<TabItem>().Select(t => t.Tag as string).Where(k => !string.IsNullOrEmpty(k)));
        }

        public bool SelectSubTab(string id)
        {
            const string prefix = "Tab.OddJobs.";
            if (id == null || !id.StartsWith(prefix))
                return false;

            string key = id.Substring(prefix.Length);
            var target = tabOddJobs.Items.Cast<TabItem>().FirstOrDefault(t => (t.Tag as string) == key);
            if (target == null)
                return false;

            tabOddJobs.SelectedItem = target;
            return true;
        }

        #region ICNCView

        public ViewType ViewType { get { return ViewType.OddJobs; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            // Opening Odd Jobs used to force focus back to Setup every time, because Setup was where the gate
            // got armed and checked. With no gate, whatever sub-tab was last in use is simply reopened.
            ActivateTab(tabOddJobs.SelectedItem as TabItem ?? tabOddJobs.Items.Cast<TabItem>().FirstOrDefault(), activate);
        }

        public void CloseFile() { }

        public void Setup(UIViewModel model, AppConfig profile) { }

        #endregion

        // Activate/deactivate the selected sub-tab, whether it hosts an IGrblConfigTab (the job wizards) or
        // an ICNCView (Setup, a StartJobView).
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
                    view.Activate(activate, ViewType.OddJobs);
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

            Dispatcher.BeginInvoke((System.Action)(() =>
            {
                if (removed != null)
                    ActivateTab(removed, false);
                ActivateTab(added, true);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
