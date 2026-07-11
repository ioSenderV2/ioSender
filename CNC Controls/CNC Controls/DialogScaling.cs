/*
 * DialogScaling.cs - part of CNC Controls library
 *
 * MainWindow applies the app's UiScale zoom (Ctrl+Alt+Plus/Minus) via a LayoutTransform/ScaleTransform on its
 * own root panel (MainWindow.xaml) - but every other top-level Window (Probe/Fixture/Macro/... edit dialogs)
 * is a separate visual tree, so none of them picked up that zoom. This is the shared opt-in: call
 * DialogScaling.Apply(this) once, right after InitializeComponent(), and the dialog's content scales with
 * the same live-bound UiScale value MainWindow uses.
 *
 */

using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CNC.Controls
{
    public static class DialogScaling
    {
        public static void Apply(Window window)
        {
            if (window == null || !(window.Content is FrameworkElement root))
                return;

            var scale = new ScaleTransform();
            var binding = new Binding("Base.UiScale") { Source = AppConfig.Settings };
            BindingOperations.SetBinding(scale, ScaleTransform.ScaleXProperty, binding);
            BindingOperations.SetBinding(scale, ScaleTransform.ScaleYProperty, binding);
            root.LayoutTransform = scale;
        }
    }
}
