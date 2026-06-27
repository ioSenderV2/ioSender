/*
 * GrblConfigView.xaml.cs - part of CNC Probing library
 *
 * v0.46 / 2025-06-05 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2025, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System.Windows;
using System.Windows.Controls;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for ConfigView.xaml
    /// </summary>
    public partial class GrblConfigView : UserControl, ICNCView
    {
        public GrblConfigView()
        {
            InitializeComponent();
        }

        #region Methods and properties required by CNCView interface

        public ViewType ViewType { get { return ViewType.GRBLConfig; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            ActivateTab(tabConfig.SelectedItem as TabItem ?? tabConfig.Items[0] as TabItem, activate);
        }

        // We've talked to a controller (version known) and the machine has not been set up via the wizard yet -
        // either no machine has been saved (first run) or travel ($130-$132) is still all zero.
        public static bool MachineIsUnconfigured()
        {
            if (string.IsNullOrEmpty(GrblInfo.Version))
                return false;

            // Don't push machine setup when connected to the simulator - there's no real machine to configure.
            if (Comms.com != null && Comms.com.IsOpen && AppConfig.Settings.Base != null && AppConfig.Settings.Base.StartSimulator)
                return false;

            if (AppConfig.Settings.Base != null && string.IsNullOrEmpty(AppConfig.Settings.Base.LastMachine))
                return true;   // no machine picked/applied via the wizard yet

            return GrblSettings.GetDouble(GrblSetting.MaxTravelBase) <= 0d
                && GrblSettings.GetDouble(GrblSetting.MaxTravelBase + 1) <= 0d
                && GrblSettings.GetDouble(GrblSetting.MaxTravelBase + 2) <= 0d;
        }

        public void CloseFile()
        {

            //if (!string.IsNullOrEmpty(GrblInfo.TrinamicDrivers))
            //    MainWindow.EnableView(true, ViewType.TrinamicTuner);
            //else
            //    MainWindow.ShowView(false, ViewType.TrinamicTuner);


            //if (GrblInfo.HasPIDLog)
            //    MainWindow.EnableView(true, ViewType.PIDTuner);
            //else
            //    MainWindow.ShowView(false, ViewType.PIDTuner);

        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
            // The App Settings sub-tab (AppConfigView, an ICNCView) owns the app-config controls; forward
            // Setup to it so they are initialised even though it is no longer a top-level tab.
            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(this))
                if (uc is AppConfigView app)
                {
                    ((ICNCView)app).Setup(model, profile);
                    break;
                }
        }

        #endregion

        private  TabItem getTab(GrblConfigType tabtype)
        {
            TabItem tab = null;

            foreach (TabItem tabitem in UIUtils.FindLogicalChildren<TabItem>(tabConfig))
            {
                var view = getView(tabitem);
                if (view != null && view.GrblConfigType == tabtype)
                {
                    tab = tabitem;
                    break;
                }
            }

            return tab;
        }

        private static IGrblConfigTab getView(TabItem tab)
        {
            IGrblConfigTab view = null;

            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(tab))
            {
                if (uc is IGrblConfigTab)
                {
                    view = (IGrblConfigTab)uc;
                    break;
                }
            }

            return view;
        }

        private void tab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!Equals(e.OriginalSource, sender))
                return;

            e.Handled = true;
            if (e.AddedItems.Count != 1)
                return;

            var removed = e.RemovedItems.Count == 1 ? e.RemovedItems[0] as TabItem : null;
            var added = e.AddedItems[0] as TabItem;

            // Defer: a child's Activate may pump the dispatcher (DoEvents while waiting on the controller),
            // which throws if it runs during the layout pass that generated this nested TabControl's items.
            Dispatcher.BeginInvoke((System.Action)(() =>
            {
                if (removed != null)
                    ActivateTab(removed, false);
                ActivateTab(added, true);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Activate/deactivate a sub-tab's content. Most are IGrblConfigTab (Activate(bool)); the App Settings
        // tab hosts AppConfigView (ICNCView) - it MUST get Activate(false) on leave so its auto-save-on-leave
        // runs, otherwise edited app settings are silently lost.
        private void ActivateTab(TabItem tab, bool activate)
        {
            if (tab == null)
                return;

            var cfg = getView(tab);
            if (cfg != null)
            {
                cfg.Activate(activate);
                return;
            }

            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(tab))
                if (uc is ICNCView view)
                {
                    view.Activate(activate, ViewType.AppConfig);
                    return;
                }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
        }
    }
}
