/*
 * ToolTipTextTemplateSelector.cs - part of CNC Controls library
 *
 * Applies a wrapping text template to tooltips whose content is a plain string, and leaves every other
 * tooltip alone.
 *
 * The app-wide implicit ToolTip style wants to wrap long tooltip text at a maximum width. Setting
 * ContentTemplate unconditionally would also hit the tooltips whose content is a UIElement rather than
 * a string (FeedsAndSpeedsView builds one from a TextBlock) - those would render as their type name.
 * Selecting by content type keeps the wrap for the ~470 string tooltips without touching the others,
 * and stays correct for any element-content tooltip added later.
 */

using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public class ToolTipTextTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            return item is string ? TextTemplate : null;
        }
    }
}
