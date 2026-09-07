/*
 * ViewHostWindow.xaml.cs - part of CNC Controls library for Grbl
 *
 * Hosts a registered ICNCView in its own top-level window, for the views that moved off the main tab
 * bar into the File/Tools menus (2026-08-03). One window per ViewType: opening a view that is already
 * up re-focuses the existing window instead of creating a second one.
 *
 * Lifecycle mirrors what a TabItem gave the view, because the views themselves were written against
 * tab semantics and assume it:
 *   open   -> Setup(model, profile) once, then Activate(true, ViewType)
 *   close  -> Activate(false, ViewType), then CloseFile()
 * Setup runs ONCE per view instance (like a tab, which is built once and re-activated); the instance
 * is cached so state survives close/reopen exactly as a tab's did.
 *
 * Closes on ESC or the window X. Deliberately non-modal (Show, not ShowDialog): the machine keeps
 * running behind these windows and the operator must still be able to reach the DRO and jog controls.
 */

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CNC.Controls
{
    public partial class ViewHostWindow : Window
    {
        // Live windows and the view instances they host, both keyed by ViewType. The view cache
        // outlives the window so reopening a view returns to it in the state it was left in.
        private static readonly Dictionary<ViewType, ViewHostWindow> _open = new Dictionary<ViewType, ViewHostWindow>();
        private static readonly Dictionary<ViewType, UserControl> _views = new Dictionary<ViewType, UserControl>();

        private ViewType _viewType;
        private ICNCView _view;

        public ViewHostWindow()
        {
            InitializeComponent();
            // Hand focus back to the owner on close. This class is where that behaviour was first
            // worked out (Owner alone does NOT do it - see WindowFocusReturn's header); it now lives
            // in one place and applies to every window. Attached explicitly as well as by App's class
            // handler so a host that never called Install still gets it - Attach is idempotent.
            WindowFocusReturn.Attach(this);
        }

        /// <summary>
        /// Open (or re-focus) the window hosting <paramref name="descriptor"/>'s view.
        /// </summary>
        public static ViewHostWindow Open(TabDescriptor descriptor, UIViewModel model, AppConfig profile, Window owner)
        {
            if (descriptor == null)
                return null;

            ViewHostWindow existing;
            if (_open.TryGetValue(descriptor.ViewType, out existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return existing;
            }

            UserControl ctl;
            bool fresh = !_views.TryGetValue(descriptor.ViewType, out ctl) || ctl == null;
            if (fresh)
            {
                ctl = descriptor.Create?.Invoke();
                if (ctl == null)
                    return null;
                descriptor.Configure?.Invoke(ctl);
                _views[descriptor.ViewType] = ctl;
            }

            var win = new ViewHostWindow
            {
                Title = descriptor.Label,
                Owner = owner,
                // Addressable by the UI test server, and unique per view.
                Uid = "win_" + descriptor.Name,
                // MUST be set before the content is attached. As tabs these views inherited the main
                // window's DataContext (the GrblViewModel) down the visual tree, and several read their
                // model from it rather than from Setup() - MachineSetupWizard.Activate NREs on
                // model.Axes without it. DataContext inheritance does NOT cross a Window boundary, and
                // Owner does not propagate it, so hand it over explicitly. Same idiom MainWindow
                // already uses for the About and Console windows.
                DataContext = owner?.DataContext
            };
            win._viewType = descriptor.ViewType;
            win._view = ctl as ICNCView;
            win.host.Content = ctl;

            // Setup is the tab-build-time hook: run it once per view instance, not per open.
            if (fresh && win._view != null)
                win._view.Setup(model, profile);

            _open[descriptor.ViewType] = win;
            win.SizeToWorkArea();
            win.Show();
            win._view?.Activate(true, descriptor.ViewType);
            return win;
        }

        /// <summary>True if this view currently has a window open.</summary>
        public static bool IsOpen(ViewType view)
        {
            return _open.ContainsKey(view);
        }

        // Open at a usable fraction of the screen rather than a hardcoded size.
        //
        // It was a fixed 1000x720, which cut off any view that wanted more (Machine Setup's Apply row ended up
        // outside the window). SizeToContent was tried instead and was WORSE: the settings shell builds its
        // pages lazily on first show, so at Show() time there was nothing to measure and the window opened
        // tiny. Neither the author of the view nor the window can know the right size here, so take a
        // generous share of the work area and let the user resize from there - the content stretches to fit
        // it, now that the page scroller no longer measures at infinite width.
        private void SizeToWorkArea()
        {
            var wa = SystemParameters.WorkArea;
            Width = System.Math.Max(MinWidth, System.Math.Min(1200d, wa.Width * 0.85d));
            Height = System.Math.Max(MinHeight, System.Math.Min(900d, wa.Height * 0.85d));
        }

        // Windows hosting a plain layout component rather than a registered ICNCView - the tools the
        // dissolved Tools tab used to carry (tool table, Trinamic tuner, PID tuner). They have no
        // ViewType and no Setup/Activate contract, so they only need show/re-focus and ESC-to-close.
        private static readonly Dictionary<string, ViewHostWindow> _openComponents = new Dictionary<string, ViewHostWindow>();
        private static readonly Dictionary<string, UserControl> _componentViews = new Dictionary<string, UserControl>();
        private string _componentKey;

        /// <summary>Open (or re-focus) a window hosting a registered layout component.</summary>
        public static ViewHostWindow OpenComponent(string key, string label, UIViewModel model, AppConfig profile, Window owner)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            ViewHostWindow existing;
            if (_openComponents.TryGetValue(key, out existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return existing;
            }

            UserControl ctl;
            if (!_componentViews.TryGetValue(key, out ctl) || ctl == null)
            {
                var d = ComponentRegistry.Get(key);
                ctl = d?.Create?.Invoke();
                if (ctl == null)
                    return null;
                _componentViews[key] = ctl;
                // Some components still expect the tab-time Setup hook.
                (ctl as ICNCView)?.Setup(model, profile);
            }

            // DataContext before content, for the same reason as Open() above.
            var win = new ViewHostWindow { Title = label, Owner = owner, Uid = "win_" + key,
                                           DataContext = owner?.DataContext };
            win._componentKey = key;
            win.host.Content = ctl;
            _openComponents[key] = win;
            win.SizeToWorkArea();
            win.Show();
            return win;
        }

        /// <summary>The live view instance for a menu-hosted view, or null if never opened.</summary>
        public static UserControl ViewInstance(ViewType view)
        {
            UserControl ctl;
            return _views.TryGetValue(view, out ctl) ? ctl : null;
        }

        /// <summary>Close every open host window - used when the app shuts down or disconnects.</summary>
        public static void CloseAll()
        {
            foreach (var w in new List<ViewHostWindow>(_open.Values))
                w.Close();
            foreach (var w in new List<ViewHostWindow>(_openComponents.Values))
                w.Close();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // ESC closes, unless the focused control wants it first (an open combo drop-down, an
            // in-place edit being cancelled) - those mark it handled before this bubbles up.
            if (e.Key == Key.Escape && !e.Handled)
            {
                Close();
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_view != null)
            {
                _view.Activate(false, _viewType);
                _view.CloseFile();
            }
            // Detach so the cached view isn't still parented to a dead window on reopen.
            host.Content = null;
            if (_componentKey != null)
                _openComponents.Remove(_componentKey);
            else
                _open.Remove(_viewType);
            base.OnClosed(e);
            // Focus return to the owner is WindowFocusReturn's job now (attached in the constructor).
        }
    }
}
