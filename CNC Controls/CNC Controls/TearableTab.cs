/*
 * TearableTab.cs - part of CNC Controls library
 *
 * A generic "tear-off" tab: double-clicking its header detaches the tab's content into a standalone,
 * custom-chrome window; double-clicking that window's own title bar re-docks the content back into the
 * tab strip. There is no close button on the floating window and no close/hide affordance on the tab
 * itself - re-docking is the only way out of the floating state, matching these tabs' existing
 * behaviour (JobWorkspace's Program/3D View/Console/Simulator tabs).
 */

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CNC.Controls
{
    public static class TearableTab
    {
        // Build a TabItem whose header, on double-click, detaches <content> into its own window and
        // removes the tab from <owner> - double-clicking the floating window's title bar reverses that.
        public static TabItem Attach(TabControl owner, string label, UIElement content)
        {
            var tab = new TabItem { Content = content };

            var header = new TextBlock { Text = label, ToolTip = "Double-click to detach into its own window." };
            tab.Header = header;

            TearOffWindow win = null;
            header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 2)
                    return;

                var detached = tab.Content as UIElement;
                // Capture the resolved (possibly inherited) DataContext before detaching - a bare Window
                // has no ancestor to inherit from, so without this the content's bindings (e.g.
                // ConsoleControl's inherited GrblViewModel) silently go null: blank display, and any
                // action routed through the model (like sending console input) becomes a no-op.
                var dataContext = (detached as FrameworkElement)?.DataContext;
                tab.Content = null;
                owner.Items.Remove(tab);

                win = new TearOffWindow(label, detached, () =>
                {
                    tab.Content = win.TakeContent();
                    owner.Items.Add(tab);
                    owner.SelectedItem = tab;
                    win.Close();
                })
                {
                    Owner = Window.GetWindow(owner),
                    DataContext = dataContext
                };
                win.Show();
            };

            return tab;
        }
    }

    // Borderless custom-chrome window used only by TearableTab.Attach. AllowsTransparency stays false
    // (WindowStyle=None + opaque background still supports normal OS edge-resize) - true would break
    // hosted native child windows (e.g. an HwndHost-embedded process window), a well-known WPF "airspace"
    // issue, and this window needs to be safe for exactly that case (the Simulator tab).
    internal sealed class TearOffWindow : Window
    {
        private readonly ContentControl contentHost;

        public TearOffWindow(string title, UIElement content, Action onRedock)
        {
            Title = title;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;
            Width = 900;
            Height = 650;
            Background = Brushes.White;

            var root = new DockPanel();

            var titleBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                Height = 28,
                ToolTip = "Double-click to dock back into the tab strip."
            };
            titleBar.Child = new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                    onRedock();
                else
                    try { DragMove(); } catch { /* button already released mid-gesture - ignore */ }
            };
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            contentHost = new ContentControl { Content = content };
            root.Children.Add(contentHost);

            Content = root;
        }

        // Detach the content so the caller can move it back into a TabItem; leaves this window empty
        // right before it's closed.
        public UIElement TakeContent()
        {
            var c = contentHost.Content as UIElement;
            contentHost.Content = null;
            return c;
        }
    }
}
