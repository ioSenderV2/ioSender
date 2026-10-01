/*
 * CustomToolEditDialog.xaml.cs - part of CNC Controls library
 *
 * Add/Edit a user-defined Work Order tool (see CustomTool.cs). Edits a clone and copies back on OK so
 * Cancel reverts, same idiom as ProbeDefinitionEditDialog/FixtureEditDialog.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class CustomToolEditDialog : Window
    {
        private readonly CustomTool target;
        private readonly CustomTool edit;

        public CustomToolEditDialog(CustomTool tool)
        {
            InitializeComponent();
            target = tool;
            edit = new CustomTool { Id = tool.Id, Name = tool.Name, Kind = tool.Kind, DiameterMm = tool.DiameterMm, Flutes = tool.Flutes, IncludedAngleDeg = tool.IncludedAngleDeg };

            txtName.Text = edit.Name;
            fldDiameter.Value = edit.DiameterMm;
            fldFlutes.Value = edit.Flutes;
            fldAngle.Value = edit.IncludedAngleDeg;

            // Select by Tag (the enum member's name as text), not SelectedIndex - robust against the XAML
            // item order ever drifting out of lockstep with the enum's own declaration order.
            foreach (ComboBoxItem item in cbxKind.Items)
                if ((string)item.Tag == edit.Kind.ToString()) { cbxKind.SelectedItem = item; break; }
        }

        private CustomToolKind SelectedKind()
        {
            var item = cbxKind.SelectedItem as ComboBoxItem;
            return item != null ? (CustomToolKind)Enum.Parse(typeof(CustomToolKind), (string)item.Tag) : CustomToolKind.EndMill;
        }

        private void cbxKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Flute count doesn't feed the Drill/Countersink feed formulas (see CustomTool.Flutes' own
            // comment) - hide it there so an inert field doesn't invite setting a number that's ignored.
            var kind = SelectedKind();
            fldFlutes.Visibility = (kind == CustomToolKind.Drill || kind == CustomToolKind.Countersink) ? Visibility.Collapsed : Visibility.Visible;

            // The included angle only means anything on a V-shaped tool - same reasoning as Flutes above:
            // an inert field invites setting a number that is then ignored.
            fldAngle.Visibility = (kind == CustomToolKind.VBitOrChamfer || kind == CustomToolKind.Countersink) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            string name = txtName.Text.Trim();
            if (name.Length == 0)
            {
                AppDialogs.Show(Window.GetWindow(this), "Give this tool a name.", "Custom tool", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (fldDiameter.Value <= 0d)
            {
                AppDialogs.Show(Window.GetWindow(this), "Diameter must be greater than 0.", "Custom tool", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var kindNow = SelectedKind();
            if ((kindNow == CustomToolKind.VBitOrChamfer || kindNow == CustomToolKind.Countersink) &&
                 (fldAngle.Value < 1d || fldAngle.Value > 179d))
            {
                AppDialogs.Show(Window.GetWindow(this), "Included angle must be between 1 and 179 degrees.", "Custom tool", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            target.Name = name;
            target.Kind = SelectedKind();
            target.DiameterMm = fldDiameter.Value;
            target.Flutes = (int)Math.Round(fldFlutes.Value);
            target.IncludedAngleDeg = fldAngle.Value;

            DialogResult = true;
        }
    }
}
