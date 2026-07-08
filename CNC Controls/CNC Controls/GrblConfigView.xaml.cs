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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Serialization;
using CNC.Core;

namespace CNC.Controls
{
    /// <summary>
    /// The Settings view: a single flat tab strip -
    /// Grbl (controller $ settings + search) | App | Jogging | G Code | Keyboard &amp; Controller | Macros | Main Page.
    /// The App/Jogging/G Code tabs bucket the app-config panels by type; the last three host editors that were
    /// converted from modal dialogs to inline tabs (save-on-leave). This view also owns the shared Save/Restart
    /// footer and the app-config auto-save-on-leave behaviour (formerly AppConfigView).
    /// </summary>
    public partial class GrblConfigView : UserControl, ICNCView
    {
        private UIViewModel model;
        private GrblViewModel grblmodel;
        private string settingsSnapshot;    // serialized Config captured when the view is entered (for autosave/diff)
        private readonly HashSet<object> restartHooked = new HashSet<object>();

        // Inline editor tabs (built lazily on first show).
        private KeyMapEditor keyMapTab;
        private MacroManagerDialog macrosTab;
        private MainPageEditor mainPageTab;

        // Camera + Probing share one vertical column on the App tab (both are short panels).
        private StackPanel _camProbeColumn;

        // True only while the Settings view is the active top-level view. The inner TabControl raises an initial
        // SelectionChanged for its default tab during eager startup layout (this view is built before the user ever
        // opens Settings, and before the controller handshake completes). Reacting to it there fired a premature
        // basicConfig.Activate(true) mid-connect - $$ answered but $I not yet, so HasEnums was false - which loaded
        // the grbl settings group-less and left the tree empty (the #61/#64 regression). The top-level Activate()
        // already EnterTab()s the current tab when the view is genuinely shown, so tab switches only need handling
        // once we're active; ignore inner selection churn until then.
        private bool _viewActive;

        public GrblConfigView()
        {
            InitializeComponent();
        }

        #region Methods and properties required by CNCView interface

        public ViewType ViewType { get { return ViewType.GRBLConfig; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (grblmodel != null)
                grblmodel.Message = string.Empty;

            var cur = tabConfig.SelectedItem as TabItem ?? tabConfig.Items[0] as TabItem;

            _viewActive = activate;

            if (activate)
            {
                settingsSnapshot = SerializeConfig(AppConfig.Settings.Base);
                ApplyPanelVisibility();
                EnterTab(cur);
            }
            else
            {
                LeaveTab(cur);
                AutoSaveOnLeave();

                // A restart-only change was made and not yet applied - offer to restart now on the way out of the
                // Settings area (the flashing Restart button otherwise just persists until the next visit).
                if (RestartPending &&
                    MessageBox.Show("Some changes you made only take effect after a restart. Restart ioSender now?",
                                    "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    DoRestart();
            }
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
        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
            if (this.model != null)
                return;

            this.model = model;
            grblmodel = DataContext as GrblViewModel;

            _camProbeColumn = new StackPanel { Orientation = Orientation.Vertical };

            // App-config panels bind to the Config object.
            pnlApp.DataContext = pnlJogging.DataContext = pnlGCode.DataContext = profile.Base;

            // Build the built-in panels, then drain any feature-contributed panels registered via the registry.
            // Feature panels (Camera/Probing/Viewer/Lathe) also self-add to model.ConfigControls from their own
            // views - usually after this Setup - so bucket present controls now and react to later additions.
            model.ConfigControls.Add(new BasicConfigControl());
            model.ConfigControls.Add(new JogUiConfigControl());
            model.ConfigControls.Add(new JogConfigControl());
            model.ConfigControls.Add(new StripGCodeConfigControl());

            foreach (var d in SettingsPanelRegistry.Collect())
            {
                var ctl = d.Create?.Invoke();
                if (ctl != null)
                    model.ConfigControls.Add(ctl);
            }

            foreach (var c in model.ConfigControls)
            {
                Bucket(c);
                HookRestart(c);
            }
            model.ConfigControls.CollectionChanged += (s, e) => {
                if (e.NewItems != null)
                    foreach (var c in e.NewItems.OfType<UserControl>())
                    {
                        Bucket(c);
                        HookRestart(c);
                    }
            };

            // The Main Page editor is only meaningful when the main-page/tab layout is user-editable.
            if (!MainPanelRegistry.LayoutEnabled)
                tabConfig.Items.Remove(tabMainPage);

            UpdateFooterForTab(tabConfig.SelectedItem as TabItem);
            AppConfig.Settings.Base.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(Config.AutoSaveSettings) || e.PropertyName == nameof(Config.AutoSaveGrblSettings))
                    UpdateFooterForTab(tabConfig.SelectedItem as TabItem);
            };
        }

        #endregion

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
        }

        #region Tab bucketing and per-tab lifecycle

        // Place a config panel into the tab that owns its category. Built-ins are matched by concrete type;
        // feature panels live in other assemblies (CNC Controls can't reference them) so they're matched by
        // full type name. Unknown panels default to the App tab.
        private void Bucket(UserControl c)
        {
            var panel = TargetPanel(c);
            if (panel == null)
                return;

            // Place the shared Camera/Probing column into the App tab the first time it's needed.
            if (ReferenceEquals(panel, _camProbeColumn) && !pnlApp.Children.Contains(_camProbeColumn))
                pnlApp.Children.Add(_camProbeColumn);

            if (c.Parent is Panel prev && !ReferenceEquals(prev, panel))
                prev.Children.Remove(c);

            if (!panel.Children.Contains(c))
                panel.Children.Add(c);
        }

        private Panel TargetPanel(UserControl c)
        {
            if (c is BasicConfigControl)
                return pnlApp;
            if (c is JogUiConfigControl || c is JogConfigControl)
                return pnlJogging;
            if (c is StripGCodeConfigControl)
                return pnlGCode;

            switch (c.GetType().FullName)
            {
                case "CNC.Controls.Viewer.ConfigControl":
                    return pnlGCode;
                case "CNC.Controls.Camera.ConfigControl":
                case "CNC.Controls.Probing.ConfigControl":
                    return _camProbeColumn;   // Camera + Probing share one column
                case "CNC.Controls.Lathe.ConfigControl":
                    return pnlApp;
            }

            return pnlApp;
        }

        // Central runtime visibility (mirrors the old AppConfigView.Activate): hide keyboard-jog config when the
        // controller itself owns jog settings ($50-$55), and hide the camera panel when no camera is present.
        private void ApplyPanelVisibility()
        {
            if (model == null)
                return;

            foreach (var control in model.ConfigControls)
            {
                if (control is JogConfigControl jc)
                {
                    if (GrblSettings.GetString(grblHALSetting.JogStepSpeed) != null)
                        control.Visibility = Visibility.Collapsed;
                    else
                    {
                        control.Visibility = Visibility.Visible;
                        jc.IsGrbl = !GrblInfo.IsGrblHAL;
                    }
                }
                else if (control is ICameraConfig && model.Camera != null && !model.Camera.HasCamera)
                    control.Visibility = Visibility.Collapsed;
            }
        }

        private void tab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // KeyMap and Main Page host their own inner TabControls - ignore their bubbled selection changes.
            if (!Equals(e.OriginalSource, sender))
                return;

            e.Handled = true;

            // Ignore selection churn while the Settings view isn't the active top-level view - most importantly the
            // initial SelectionChanged raised during eager startup layout, which used to fire a premature
            // Activate mid-handshake. The top-level Activate(true) EnterTab()s the current tab when we're genuinely
            // shown; this handler only needs to service real user tab switches thereafter. See _viewActive.
            if (!_viewActive)
                return;

            if (e.AddedItems.Count != 1)
                return;

            var removed = e.RemovedItems.Count == 1 ? e.RemovedItems[0] as TabItem : null;
            var added = e.AddedItems[0] as TabItem;

            // Defer: a child's activation may pump the dispatcher (DoEvents while waiting on the controller),
            // which throws if it runs during the layout pass that generated this TabControl's items.
            Dispatcher.BeginInvoke((System.Action)(() =>
            {
                LeaveTab(removed);
                EnterTab(added);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void EnterTab(TabItem tab)
        {
            if (tab == null)
                return;

            UpdateFooterForTab(tab);
            EnsureEditorTab(tab);

            if (tab == tabGrbl)
                basicConfig.Activate(true);
        }

        private void LeaveTab(TabItem tab)
        {
            if (tab == null)
                return;

            if (tab == tabGrbl)
                basicConfig.Activate(false);
            else if (tab.Content is ISettingsEditorTab editor)
                editor.Commit();   // Keyboard & Controller / Macros / Main Page: save-on-leave
        }

        // Build an editor tab's content the first time it is shown.
        private void EnsureEditorTab(TabItem tab)
        {
            if (tab == tabKeys && keyMapTab == null && tab.Content == null)
            {
                if (Grbl.GrblViewModel?.Keyboard != null)
                {
                    keyMapTab = new KeyMapEditor(Grbl.GrblViewModel);
                    tab.Content = keyMapTab;
                }
                else
                    tab.Content = new TextBlock { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap,
                        Text = "Key mappings are not available until a controller is connected." };
            }
            else if (tab == tabMacros && macrosTab == null && tab.Content == null)
            {
                if (AppConfig.Settings.Macros != null)
                {
                    macrosTab = new MacroManagerDialog(AppConfig.Settings.Macros);
                    tab.Content = macrosTab;
                }
                else
                    tab.Content = new TextBlock { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap,
                        Text = "Macros are not available." };
            }
            else if (tab == tabMainPage && mainPageTab == null && tab.Content == null)
            {
                mainPageTab = new MainPageEditor();
                mainPageTab.RestartRequired += (s, ev) => EnableRestart(ev.Message);
                tab.Content = mainPageTab;
            }
        }

        #endregion

        #region Footer (Save settings / Restart) + restart hooking

        // One shared footer, its buttons shown per the active tab (see the applicability table): the Grbl tools
        // sub-row only on Grbl; Save hidden when that tab's autosave is on; Reset to Default only where a panel
        // opts in via ISettingsResettable.
        private void UpdateFooterForTab(TabItem tab)
        {
            if (tab == null)
                return;

            grblTools.Visibility = tab == tabGrbl ? Visibility.Visible : Visibility.Collapsed;
            btnSave.Visibility = SaveApplies(tab) ? Visibility.Visible : Visibility.Collapsed;
            btnReset.Visibility = ResettablesFor(tab).Any() ? Visibility.Visible : Visibility.Collapsed;
        }

        // Save is offered unless the tab's autosave (which persists on leave) is on: grbl-settings autosave for the
        // Grbl tab, app-settings autosave for everything else.
        private bool SaveApplies(TabItem tab)
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg == null)
                return true;
            return tab == tabGrbl ? !cfg.AutoSaveGrblSettings : !cfg.AutoSaveSettings;
        }

        // The map from a config panel to the tab it lives on (parallels TargetPanel).
        private TabItem TabFor(UserControl c)
        {
            if (c is BasicConfigControl)
                return tabApp;
            if (c is JogUiConfigControl || c is JogConfigControl)
                return tabJogging;
            if (c is StripGCodeConfigControl)
                return tabGCode;

            switch (c.GetType().FullName)
            {
                case "CNC.Controls.Viewer.ConfigControl":
                    return tabGCode;
                case "CNC.Controls.Camera.ConfigControl":
                case "CNC.Controls.Probing.ConfigControl":
                case "CNC.Controls.Lathe.ConfigControl":
                    return tabApp;
            }
            return tabApp;
        }

        // The resettable panels/editors on a tab: the Grbl control itself ($RST=$), the visible app-config panels
        // that opt in, or an editor tab that opts in (key bindings). Empty => the tab has no Reset button.
        private IEnumerable<ISettingsResettable> ResettablesFor(TabItem tab)
        {
            if (tab == tabGrbl)
                return new ISettingsResettable[] { basicConfig };

            if (tab == tabApp || tab == tabJogging || tab == tabGCode)
                return model == null ? Enumerable.Empty<ISettingsResettable>()
                    : model.ConfigControls.Where(c => TabFor(c) == tab && c.Visibility == Visibility.Visible)
                                          .OfType<ISettingsResettable>().ToList();

            return tab.Content is ISettingsResettable r ? new[] { r } : Enumerable.Empty<ISettingsResettable>();
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            var tab = tabConfig.SelectedItem as TabItem;
            if (tab == tabGrbl)
                basicConfig.SaveSettings();
            else if (tab?.Content is ISettingsEditorTab editor)
                editor.Commit();
            else if (AppConfig.Settings.Save())
                Grbl.GrblViewModel.Message = LibStrings.FindResource("SettingsSaved");
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            var tab = tabConfig.SelectedItem as TabItem;
            foreach (var r in ResettablesFor(tab).ToList())
                r.ResetToDefaults();
        }

        // Grbl sub-footer tools -> the Grbl control's public methods.
        private void btnReload_Click(object sender, RoutedEventArgs e) { basicConfig.ReloadSettings(); }
        private void btnBackup_Click(object sender, RoutedEventArgs e) { basicConfig.BackupSettings(); }
        private void btnRestore_Click(object sender, RoutedEventArgs e) { basicConfig.RestoreSettings(); }
        private void btnCopyToSim_Click(object sender, RoutedEventArgs e) { basicConfig.CopyToSimulator(); }

        // Surface the Restart button (relaunch to apply) for a setting that only takes effect at startup.
        private void EnableRestart(string message)
        {
            footer.Visibility = Visibility.Visible;
            btnRestart.Visibility = Visibility.Visible;
            btnRestart.IsEnabled = true;
            Grbl.GrblViewModel.Message = message;
        }

        private void HookRestart(UserControl c)
        {
            if (c is IRestartRequired rr && restartHooked.Add(c))
                rr.RestartRequired += (s, e) => EnableRestart(e.Message);
        }

        private void btnRestart_Click(object sender, RoutedEventArgs e)
        {
            DoRestart();
        }

        // True while a restart-only change is pending (the Restart button is shown + enabled and pulsing).
        private bool RestartPending { get { return btnRestart.IsEnabled && btnRestart.Visibility == Visibility.Visible; } }

        private void DoRestart()
        {
            AppConfig.Settings.Save();
            try
            {
                System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                Application.Current.Shutdown();
            }
            catch { }   // relaunch failed - leave the app open; changes are saved and apply on next manual restart
        }

        #endregion

        #region App-config autosave on view-leave (opt-in)

        private void AutoSaveOnLeave()
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg == null || !cfg.AutoSaveSettings || settingsSnapshot == null)
                return;

            string current = SerializeConfig(cfg);
            if (current == null || current == settingsSnapshot)
                return;     // nothing changed

            if (cfg.PromptOnSave)
            {
                var changes = new List<string>();
                DiffObject(string.Empty, DeserializeConfig(settingsSnapshot), cfg, changes);

                if (changes.Count > 0)
                {
                    var msg = "Save these setting changes?\n\n" + string.Join("\n", changes);
                    if (MessageBox.Show(msg, "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        AppConfig.Settings.Save();
                        settingsSnapshot = SerializeConfig(cfg);
                    }
                    else
                        CopyScalars(DeserializeConfig(settingsSnapshot), cfg);   // discard edits
                    return;
                }
            }

            AppConfig.Settings.Save();
            settingsSnapshot = current;
        }

        private static string SerializeConfig(Config c)
        {
            try
            {
                var xs = new XmlSerializer(typeof(Config));
                using (var sw = new StringWriter())
                {
                    xs.Serialize(sw, c);
                    return sw.ToString();
                }
            }
            catch { return null; }
        }

        private static Config DeserializeConfig(string xml)
        {
            var xs = new XmlSerializer(typeof(Config));
            using (var sr = new StringReader(xml))
                return (Config)xs.Deserialize(sr);
        }

        private static bool IsScalar(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(double) || t == typeof(decimal);
        }

        // List scalar property differences (incl. nested Jog / JogUi config) as "Name: old -> new".
        private static void DiffObject(string prefix, object oldO, object newO, List<string> changes)
        {
            if (oldO == null || newO == null)
                return;

            foreach (var p in oldO.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || Attribute.IsDefined(p, typeof(XmlIgnoreAttribute)))
                    continue;

                object ov, nv;
                try { ov = p.GetValue(oldO); nv = p.GetValue(newO); } catch { continue; }

                if (IsScalar(p.PropertyType))
                {
                    if (!Equals(ov, nv))
                        changes.Add(string.Format("  {0}{1}: {2} → {3}", prefix, p.Name, ov, nv));
                }
                else if (p.PropertyType == typeof(JogConfig) || p.PropertyType == typeof(JogUIConfig))
                    DiffObject(p.Name + ".", ov, nv, changes);
                else if (p.PropertyType == typeof(string[]))
                {
                    var oa = ov as string[];
                    var na = nv as string[];
                    if (oa != null && na != null && !oa.SequenceEqual(na))
                        changes.Add(string.Format("  {0}{1}: {2} → {3}", prefix, p.Name, string.Join(",", oa), string.Join(",", na)));
                }
            }
        }

        // Copy scalar property values from src into dst (used to discard unsaved edits on the live Config).
        private static void CopyScalars(object src, object dst)
        {
            if (src == null || dst == null)
                return;

            foreach (var p in src.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || Attribute.IsDefined(p, typeof(XmlIgnoreAttribute)))
                    continue;

                if (IsScalar(p.PropertyType))
                {
                    if (p.CanWrite)
                    {
                        try { var v = p.GetValue(src); if (!Equals(v, p.GetValue(dst))) p.SetValue(dst, v); } catch { }
                    }
                }
                else if (p.PropertyType == typeof(JogConfig) || p.PropertyType == typeof(JogUIConfig))
                    CopyScalars(p.GetValue(src), p.GetValue(dst));
            }
        }

        #endregion
    }
}
