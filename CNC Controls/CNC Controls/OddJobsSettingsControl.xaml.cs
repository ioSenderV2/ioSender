/*
 * OddJobsSettingsControl.xaml.cs - part of CNC Controls library
 *
 * Settings:App panel for Odd Jobs - the Work Order autosave-on-exit pair (mirrors the existing "Auto-save
 * settings on exit" / "Prompt before auto-saving" idiom, see AppConfig.Config), plus a scrollable list of
 * every tool Odd Jobs knows about. Clicking a tool opens the same Feeds and Speeds dialog the Work Order
 * builder itself uses (OddJobsFeedsSpeedsDialog), but in its material-picker mode (EnableMaterialPicker) -
 * there's no work order/Setup tab material to read here, so the operator picks one to preview and dial in
 * that tool's numbers ahead of time. Settling on values there is remembered the same way as from a real
 * operation (OddJobsToolMemory), so it's ready to prefill the next time that tool/material pair is used.
 */

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class OddJobsSettingsControl : UserControl, ISettingsResettable
    {
        public OddJobsSettingsControl()
        {
            InitializeComponent();

            foreach (OddJobsTool tool in Enum.GetValues(typeof(OddJobsTool)))
                lstOddJobsTools.Items.Add(new ListBoxItem { Content = OddJobsFeedsSpeedsDialog.DisplayName(tool), Tag = tool });

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
        // practice (a countersink bit is always a Countersink op, the V-bit always a Chamfer, the drill bit
        // always a Drill) - see WorkOrderView.btnFeedsSpeeds_Click for the operation-kind version this parallels.
        private static string DocLabelFor(OddJobsTool tool)
        {
            if (OddJobsFeedsSpeedsDialog.IsCountersinkBit(tool))
                return "Countersink diameter:";
            if (tool == OddJobsTool.VBit45)
                return "Chamfer depth:";
            if (tool == OddJobsTool.DrillBit)
                return "Peck depth:";
            return "Depth of cut:";
        }

        private void lstOddJobsTools_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstOddJobsTools.SelectedItem is ListBoxItem item && item.Tag is OddJobsTool tool)
            {
                var dlg = new OddJobsFeedsSpeedsDialog(tool, docLabel: DocLabelFor(tool))
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.EnableMaterialPicker();

                if (dlg.ShowDialog() == true)
                    OddJobsToolMemory.Remember(dlg.SelectedTool, dlg.BitDiameter, dlg.Material, dlg.SpindleRPM, dlg.Feed, dlg.PlungeFeed, dlg.DepthOfCut);
            }

            // Cleared rather than left selected, so clicking the SAME row again still raises SelectionChanged -
            // this is a launcher, not a persistent selection.
            lstOddJobsTools.SelectedIndex = -1;
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
