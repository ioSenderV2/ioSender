/*
 * UnitToggleMenu.cs - part of CNC Controls library
 *
 * Right-click a GroupBox's header to show every NumericField inside it in the other length unit
 * (mm<->in), for panels that have no unit toggle of their own (DRO, Machine Position, Machine/Program
 * limits). Reuses NumericField.IsImperial - the same inherited attached property Start Job and Stepper
 * Calibration already set from an explicit radio-button pair - so a panel-level right-click is just
 * another writer of the same mechanism, not a new one. Session-only: nothing here persists, the panel
 * reverts to the app's normal unit on next launch.
 *
 * The menu reflects the panel's ACTUAL current state (enumerated live via FindLogicalChildren, not
 * assumed from the box-level flag): all-mm offers "Show in inches", all-in offers "Show in millimeters",
 * and a mixed panel offers both - either one sets every field in the panel to that unit.
 *
 * Deliberately does NOT set GroupBox.ContextMenu (which would resolve/bubble via WPF's normal
 * ContextMenuOpening machinery and risk swallowing an inner NumericField's own per-field menu - see the
 * tab-header ContextMenu scoping fix for the same class of bug). Instead hooks PreviewMouseRightButtonDown
 * directly and only acts when the click landed outside the GroupBox's own Content subtree, then opens a
 * plain Popup-style ContextMenu itself - completely independent of any content's own context menus.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    public static class UnitToggleMenu
    {
        public static void Attach(GroupBox box)
        {
            if (box == null)
                return;

            box.PreviewMouseRightButtonDown += (s, e) => OnPreviewRightButtonDown(box, e);
        }

        private static void OnPreviewRightButtonDown(GroupBox box, MouseButtonEventArgs e)
        {
            var content = box.Content as DependencyObject;
            var source = e.OriginalSource as DependencyObject;

            if (content != null && IsDescendant(source, content))
                return;   // click landed in the panel body, not the header - leave it alone

            // Read each contained field's OWN EffectiveUnit rather than trusting the box-level IsImperial
            // flag - a field can locally override its container's setting, and non-length fields (deg, rpm,
            // blank, ...) don't participate at all. The menu reflects what's actually on screen right now.
            bool anyMm = false, anyIn = false;
            foreach (var field in UIUtils.FindLogicalChildren<NumericField>(box))
            {
                if (!NumericProperties.IsLengthUnit(field.EffectiveUnit))
                    continue;
                if (field.EffectiveUnit == "in")
                    anyIn = true;
                else
                    anyMm = true;
            }

            if (!anyMm && !anyIn)
                return;   // nothing convertible in this panel - no menu to show

            e.Handled = true;

            var menu = new ContextMenu { PlacementTarget = box };

            // All one unit -> a single item offering the other. Mixed -> both, either one sets every
            // field in the panel to that unit (not just the ones that were already showing it).
            if (anyMm && !anyIn)
            {
                var toIn = new MenuItem { Header = "Show in inches" };
                toIn.Click += (s2, e2) => NumericField.SetIsImperial(box, true);
                menu.Items.Add(toIn);
            }
            else if (anyIn && !anyMm)
            {
                var toMm = new MenuItem { Header = "Show in millimeters" };
                toMm.Click += (s2, e2) => NumericField.SetIsImperial(box, false);
                menu.Items.Add(toMm);
            }
            else
            {
                var toMm = new MenuItem { Header = "Show all in millimeters" };
                var toIn = new MenuItem { Header = "Show all in inches" };
                toMm.Click += (s2, e2) => NumericField.SetIsImperial(box, false);
                toIn.Click += (s2, e2) => NumericField.SetIsImperial(box, true);
                menu.Items.Add(toMm);
                menu.Items.Add(toIn);
            }

            menu.IsOpen = true;
        }

        private static bool IsDescendant(DependencyObject node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (node == ancestor)
                    return true;
                node = (VisualTreeHelper.GetParent(node)) ?? LogicalTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}
