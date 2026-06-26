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

        // Show only the fields relevant to the selected type.
        private void UpdateFieldVisibility(ProbeType type)
        {
            // A corner touch plate (lip) probes X/Y too, so it shows the XY fields - but uses the tool, not a tip.
            bool probesXY = type == ProbeType.ThreeDProbe || type == ProbeType.EdgeFinder || type == ProbeType.TouchPlate;

            Show(fldDiameter, type == ProbeType.ThreeDProbe || type == ProbeType.EdgeFinder);
            Show(fldXYClr, probesXY);
            Show(fldZClr, type != ProbeType.ToolSetter);     // Z drop to the edge for 3D / edge / plate
            Show(fldOffsetX, type == ProbeType.ThreeDProbe);
            Show(fldOffsetY, type == ProbeType.ThreeDProbe);
            Show(fldSpin, type == ProbeType.EdgeFinder);
            Show(fldPlate, type == ProbeType.TouchPlate);
            Show(fldLip, type == ProbeType.TouchPlate);
            Show(fldSetter, type == ProbeType.ToolSetter);
            Show(fldRapids, true);                            // positioning rapids apply to every type
            // Always shown: name, search feed, latch feed, probing distance, latch distance.
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
                    d.ProbeDiameter = 2d; d.ProbeFeedRate = 200d; d.LatchFeedRate = 50d; d.RapidsFeedRate = 0d;
                    d.ProbeDistance = 25d; d.LatchDistance = 1d; d.XYClearance = 5d; d.Depth = 10d;
                    d.ProbeOffsetX = 0d; d.ProbeOffsetY = 0d;
                    break;

                case ProbeType.TouchPlate:
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
            if (string.IsNullOrWhiteSpace((DataContext as ProbeDefinition)?.Name))
            {
                MessageBox.Show("Please enter a name for the probe.", "Probe definition", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
