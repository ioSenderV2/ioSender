/*
 * ProbeDefinitionEditDialog.xaml.cs - part of CNC Controls library
 *
 * Edits a single ProbeDefinition. The Type dropdown drives which fields are shown; picking a type applies
 * reasonable defaults. The caller passes a clone and copies it back on OK so Cancel reverts.
 *
 */

using System;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public partial class ProbeDefinitionEditDialog : Window
    {
        private bool loading;

        public ProbeDefinitionEditDialog(ProbeDefinition definition)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            DataContext = definition;

            loading = true;
            SelectType(definition.ProbeType);            // sets the combo without applying defaults
            UpdateFieldVisibility(definition.ProbeType);
            loading = false;
        }

        private void SelectType(ProbeType type)
        {
            foreach (ComboBoxItem item in cbxType.Items)
                if ((string)item.Tag == type.ToString())
                {
                    cbxType.SelectedItem = item;
                    break;
                }
        }

        private ProbeType SelectedType
        {
            get { return (ProbeType)Enum.Parse(typeof(ProbeType), (string)((ComboBoxItem)cbxType.SelectedItem).Tag); }
        }

        private void cbxType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbxType.SelectedItem == null)
                return;

            var type = SelectedType;
            var def = DataContext as ProbeDefinition;
            if (def != null)
                def.ProbeType = type;

            if (!loading)                                // user changed the type - reset to that type's defaults
                ApplyDefaults(type, def);

            UpdateFieldVisibility(type);
        }

        // Show only the essentials relevant to the selected type (speeds/distances live behind 'Edit motion params').
        private void UpdateFieldVisibility(ProbeType type)
        {
            // The touch plate has no stylus - whatever bit is in the collet varies job to job, so it's not a
            // fixed property of the PLATE. The loaded program's own (TOOL T=n D=d ...) comment for the current
            // tool is preferred at runtime (ProbingViewModel.SelectedProbe); this field is only the fallback
            // used when that's unavailable (no program loaded, or its comments don't mention this tool).
            bool hasDiameter = type == ProbeType.ThreeDProbe || type == ProbeType.EdgeFinder || type == ProbeType.TouchPlate;
            Show(fldDiameter, hasDiameter);
            if (type == ProbeType.TouchPlate)
            {
                fldDiameter.Label = "Fallback diameter:";
                fldDiameter.ToolTip = "Used only when the loaded program's own (TOOL T=n D=...) comment doesn't cover the current tool - edge radius compensation prefers that live value when available.";
            }
            else
            {
                fldDiameter.Label = "Tip diameter:";
                fldDiameter.ToolTip = "Stylus tip / bit diameter that contacts the work (used for edge radius compensation).";
            }

            // The 3D probe also has a large body; its radius is the minimum standoff for rapid/G28 moves
            // and drives the Load Stock approach clearance. Probe length (stylus below the body, excluding
            // the body itself) is informational.
            Show(fldBody, type == ProbeType.ThreeDProbe);
            Show(fldLength, type == ProbeType.ThreeDProbe);

            Show(fldPlate, type == ProbeType.TouchPlate);
            Show(fldLip, type == ProbeType.TouchPlate);
            Show(fldSetter, type == ProbeType.ToolSetter);
            Show(fldSpin, type == ProbeType.EdgeFinder);

            // The schematic follows the type - shape + the key measurement labels for that probe.
            Show(drwThreeD, type == ProbeType.ThreeDProbe);
            Show(drwPlate, type == ProbeType.TouchPlate);
            Show(drwSetter, type == ProbeType.ToolSetter);
            Show(drwEdge, type == ProbeType.EdgeFinder);
        }

        private void btnMotion_Click(object sender, RoutedEventArgs e)
        {
            new ProbeMotionParamsDialog(DataContext as ProbeDefinition) { Owner = this }.ShowDialog();
        }

        private static void Show(UIElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Reasonable starting values when a type is chosen.
        private static void ApplyDefaults(ProbeType type, ProbeDefinition d)
        {
            if (d == null)
                return;

            switch (type)
            {
                case ProbeType.ThreeDProbe:
                    d.ProbeDiameter = 2d; d.BodyDiameter = 42d; d.ProbeLength = 50d; d.ProbeFeedRate = 200d; d.LatchFeedRate = 50d; d.RapidsFeedRate = 0d;
                    d.ProbeDistance = 25d; d.LatchDistance = 1d; d.XYClearance = 5d; d.Depth = 10d;
                    d.ProbeOffsetX = 0d; d.ProbeOffsetY = 0d;
                    break;

                case ProbeType.TouchPlate:
                    d.ProbeDiameter = 6d;   // bit in the collet
                    d.PlateThickness = 12d; d.LipWidth = 10d; d.XYClearance = 5d; d.Depth = 5d;
                    d.ProbeFeedRate = 100d; d.LatchFeedRate = 25d; d.RapidsFeedRate = 0d;
                    d.ProbeDistance = 25d; d.LatchDistance = 1d;
                    break;

                case ProbeType.ToolSetter:
                    d.SetterHeight = 0d; d.ProbeFeedRate = 200d; d.LatchFeedRate = 25d; d.RapidsFeedRate = 0d;
                    d.ProbeDistance = 50d; d.LatchDistance = 2d;
                    break;

                case ProbeType.EdgeFinder:
                    d.ProbeDiameter = 10d; d.ProbeFeedRate = 150d; d.LatchFeedRate = 50d; d.RapidsFeedRate = 0d;
                    d.ProbeDistance = 25d; d.LatchDistance = 1d; d.XYClearance = 5d; d.Depth = 10d; d.SpinRPM = 0d;
                    break;
            }
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            // Names are derived from type + count (ProbeDefinitions.Renumber), so no name entry is needed.
            DialogResult = true;
            Close();
        }
    }
}
