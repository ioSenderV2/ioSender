/*
 * ToolsView.xaml.cs - part of CNC Controls library
 *
 * Top-level "Tools" tab: the tool table plus the commissioning / tuning tools (stepper calibration,
 * spoilboard surfacing, Trinamic and PID tuning). The tool table is an ICNCView; the tools are
 * IGrblConfigTab. The selected child is activated either way (mirrors GrblConfigView).
 */

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
            if (Equals(e.OriginalSource, sender))
            {
                if (e.AddedItems.Count == 1)
                {
                    if (e.RemovedItems.Count == 1)
                        ActivateTab(e.RemovedItems[0] as TabItem, false);

                    ActivateTab(e.AddedItems[0] as TabItem, true);
                }
                e.Handled = true;
            }
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
            if (string.IsNullOrEmpty(GrblInfo.TrinamicDrivers))
                RemoveTab(GrblConfigType.Trinamic);

            if (!GrblInfo.HasPIDLog)
                RemoveTab(GrblConfigType.PidTuning);
        }
    }
}
