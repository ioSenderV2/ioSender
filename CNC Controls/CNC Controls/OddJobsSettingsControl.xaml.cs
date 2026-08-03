/*
 * OddJobsSettingsControl.xaml.cs - part of CNC Controls library
 *
 * Settings:App panel for Odd Jobs - the Work Order autosave-on-exit pair (mirrors the existing "Auto-save
 * settings on exit" / "Prompt before auto-saving" idiom, see AppConfig.Config), plus a scrollable list of
 * every tool Odd Jobs knows about (CustomTool.cs) - the factory defaults seeded from Default-App.config and
 * anything the operator has added, with no distinction between the two: every row can be clicked to preview/
 * tune its feeds and speeds, and right-clicked to Edit/Delete. Left-click opens the same Feeds and Speeds
 * dialog the Work Order builder itself uses (OddJobsFeedsSpeedsDialog), in its material-picker mode
 * (EnableMaterialPicker) - there's no work order/Setup tab material to read here, so the operator picks one
 * to preview and dial in that tool's numbers ahead of time. Settling on values there is remembered the same
 * way as from a real operation (OddJobsToolMemory). The "+" button (btnAddTool_Click) opens
 * CustomToolEditDialog to add a new one.
 */

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class OddJobsSettingsControl : UserControl, ISettingsResettable, ISettingsPanelCategory
    {
        // Where this panel sits in the settings navigation tree (ISettingsPanelCategory).
        public string SettingsCategory { get { return SettingsCategories.Application; } }
        public int SettingsOrder { get { return 20; } }

        public OddJobsSettingsControl()
        {
            InitializeComponent();

            BuildToolList();

            // Read/written directly against AppConfig.Settings.Base rather than a XAML binding, same as
            // ResetToDefaults below - this is a machine-level fact (see SpindleDirectionCapability's own
            // comment), not something tied to a particular work order or DataContext.
            var cfg = AppConfig.Settings.Base;
            cbxSpindleDirection.SelectedIndex = cfg == null ? 0 : (int)cfg.SpindleDirectionCapability;
        }

        private void cbxSpindleDirection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg != null && cbxSpindleDirection.SelectedIndex >= 0)
                cfg.SpindleDirectionCapability = (SpindleDirectionCapability)cbxSpindleDirection.SelectedIndex;
        }

        // The docLabel/showDoc pair WorkOrderView's own Feeds and Speeds button passes per operation kind -
        // mirrored here per TOOL instead, since each of these tools only ever means one operation kind in
        // practice (a countersink bit is always a Countersink op, a V-bit/chamfer bit always a Chamfer, a
        // drill always a Drill) - see WorkOrderView.btnFeedsSpeeds_Click for the operation-kind version this
        // parallels.
        private static string DocLabelFor(CustomTool tool)
        {
            switch (tool.Kind)
            {
                case CustomToolKind.Countersink: return "Countersink diameter:";
                case CustomToolKind.VBitOrChamfer: return "Chamfer depth:";
                case CustomToolKind.Drill: return "Peck depth:";
                default: return "Depth of cut:";
            }
        }

        private void BuildToolList()
        {
            lstOddJobsTools.Items.Clear();

            var tools = CustomTools.SectionConfig?.Entries;
            if (tools == null)
                return;

            foreach (var ct in tools)
            {
                var item = new ListBoxItem { Content = ct.Name, Tag = ct };
                AttachToolContextMenu(item, ct);
                lstOddJobsTools.Items.Add(item);
            }
        }

        private void lstOddJobsTools_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstOddJobsTools.SelectedItem is ListBoxItem item && item.Tag is CustomTool ct)
            {
                var dlg = new OddJobsFeedsSpeedsDialog(ct.Id, docLabel: DocLabelFor(ct))
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.EnableMaterialPicker();

                if (dlg.ShowDialog() == true)
                    OddJobsToolMemory.Remember(dlg.SelectedToolValue, dlg.BitDiameter, dlg.Material, dlg.SpindleRPM, dlg.Feed, dlg.PlungeFeed, dlg.DepthOfCut);
            }

            // Cleared rather than left selected, so clicking the SAME row again still raises SelectionChanged -
            // this is a launcher, not a persistent selection.
            lstOddJobsTools.SelectedIndex = -1;
        }

        private void btnAddTool_Click(object sender, RoutedEventArgs e)
        {
            var tool = new CustomTool { Id = CustomTools.NextId() };
            var dlg = new CustomToolEditDialog(tool) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true)
                return;

            if (CustomTools.SectionConfig == null)
                CustomTools.SectionConfig = new CustomToolList();
            CustomTools.SectionConfig.Entries.Add(tool);
            CustomTools.Save();
            BuildToolList();
        }

        // Right-click only - Edit clones-then-copies-back like ProbeDefinitionEditDialog; Delete confirms
        // first, same as ProbeDelete_Click/FixtureDelete_Click (MachineSetupWizard.xaml.cs). Applies to every
        // row, factory-default or operator-added - there's no protected/read-only tier any more. Neither
        // locks on the tool being referenced by an existing Work Order operation - WorkOrderView.
        // ParameterWarnings flags that at Generate time instead (same as a Fixture/Probe still in use
        // elsewhere in the app today).
        private void AttachToolContextMenu(ListBoxItem item, CustomTool ct)
        {
            var editItem = new MenuItem { Header = "Edit…" };
            var deleteItem = new MenuItem { Header = "Delete" };
            editItem.Click += (s, ev) => EditTool(ct);
            deleteItem.Click += (s, ev) => DeleteTool(ct);

            var menu = new ContextMenu();
            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
            item.ContextMenu = menu;

            // WPF's own ContextMenuService opens on right-button UP by default, which something else in
            // this row's input handling swallows before it gets there - same fix WorkOrderView's own row
            // context menus already use (AttachRowContextMenu): open it ourselves on right-button DOWN and
            // swallow both the down and the following up.
            item.PreviewMouseRightButtonDown += (s, ev) =>
            {
                ev.Handled = true;
                item.IsSelected = false;   // don't also trigger the left-click launch behavior
                item.Focus();
                menu.PlacementTarget = item;
                menu.IsOpen = true;
            };
            item.PreviewMouseRightButtonUp += (s, ev) => ev.Handled = true;
        }

        private void EditTool(CustomTool ct)
        {
            var dlg = new CustomToolEditDialog(ct) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                CustomTools.Save();
                BuildToolList();
            }
        }

        private void DeleteTool(CustomTool ct)
        {
            if (AppDialogs.Show(Window.GetWindow(this), string.Format("Delete tool \"{0}\"?", ct.Name), "Tool",
                                 MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            CustomTools.SectionConfig?.Entries.Remove(ct);
            CustomTools.Save();
            BuildToolList();
        }

        public void ResetToDefaults()
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg == null)
                return;

            cfg.AutoSaveWorkOrderOnExit = false;
            cfg.PromptBeforeAutoSaveWorkOrder = false;
            cfg.SpindleDirectionCapability = SpindleDirectionCapability.Bidirectional;
            cbxSpindleDirection.SelectedIndex = 0;
        }
    }
}
