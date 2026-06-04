/*
 * PanelFlyout.xaml.cs - part of CNC Controls library for Grbl
 *
 * Generic flyout host (border + close + pin) used to show a main-page pool
 * panel that the user has not placed in a main-page slot. The hosted panel
 * keeps its own GroupBox header, which serves as the flyout title.
 *
 */

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    public partial class PanelFlyout : UserControl, ISidebarControl, IPinnableFlyout
    {
        private readonly string menuLabel;

        public PanelFlyout(string panelName, string menuLabel, UserControl content)
        {
            InitializeComponent();
            PanelName = panelName;
            this.menuLabel = menuLabel;
            host.Content = content;
            Loaded += PanelFlyout_Loaded;
        }

        public string PanelName { get; }

        // ISidebarControl - the sidebar tab text.
        public string MenuLabel { get { return menuLabel; } }

        // IPinnableFlyout
        public bool Pinned
        {
            get {  return btnPin.IsChecked == true; }
            set { btnPin.IsChecked = value; }
        }

        // Raised when the user toggles the pin, so the host window can persist the set.
        public event Action<IPinnableFlyout> PinnedChanged;

        private void PanelFlyout_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is GrblViewModel gvm)
                gvm.PropertyChanged += OnModelPropertyChanged;
        }

        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Auto-hide while a job runs (unless pinned), matching the other flyouts.
            // GrblViewModel raises PropertyChanged from background threads (e.g. settings load), so test
            // the (thread-safe) property name first, then marshal any DependencyProperty access to the UI thread.
            if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new System.Action(() => OnModelPropertyChanged(sender, e)));
                return;
            }

            if (!Pinned && Visibility == Visibility.Visible && (sender as GrblViewModel).IsJobRunning)
                Visibility = Visibility.Hidden;
        }

        private void btn_Close(object sender, RoutedEventArgs e)
        {
            Visibility = Visibility.Hidden;
        }

        private void btnPin_Changed(object sender, RoutedEventArgs e)
        {
            PinnedChanged?.Invoke(this);
        }
    }
}
