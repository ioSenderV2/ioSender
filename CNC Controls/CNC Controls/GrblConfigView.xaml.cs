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
    public partial class GrblConfigView : UserControl, ICNCView, ITabBindingHost
    {
        private UIViewModel model;
        private GrblViewModel grblmodel;
        private string settingsSnapshot;    // serialized Config captured when the view is entered (for autosave/diff)
        private readonly HashSet<object> restartHooked = new HashSet<object>();

        // Inline editor pages (built lazily on first show).
        private KeyMapEditor keyMapTab;
        private MacroManagerDialog macrosTab;
        private MainPageEditor mainPageTab;

        // The two panels that used to be declared in XAML as tab content. They are ordinary pages now,
        // but the rest of this file still talks to them by name.
        private readonly GrblConfigControl basicConfig = new GrblConfigControl();
        private readonly SimulatorConfigView simConfig = new SimulatorConfigView();

        // Category keys live in SettingsCategories so panels in any assembly can name them.
        private const string CatController = SettingsCategories.Controller;
        private const string CatApplication = SettingsCategories.Application;
        private const string CatJogging = SettingsCategories.Jogging;
        private const string CatGCode = SettingsCategories.GCode;
        private const string CatInterface = SettingsCategories.UserInterface;

        private SettingsNavNode nodeGrbl, nodeSimulator, nodeKeyboard, nodeController, nodeMacros, nodeJobLayout, nodeTopTabs;
        private readonly Dictionary<UserControl, List<SettingsNavNode>> panelNodes = new Dictionary<UserControl, List<SettingsNavNode>>();

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
            nav.ContentRequested += (s, e) => EnsureEditorContent(e.To);
            nav.SelectedNodeChanged += nav_SelectedNodeChanged;
        }

        #region Methods and properties required by CNCView interface

        public ViewType ViewType { get { return ViewType.GRBLConfig; } }
        public bool CanEnable { get { return DataContext is GrblViewModel ? (DataContext as GrblViewModel).SystemCommandsAllowed : true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if (grblmodel != null)
                grblmodel.Message = string.Empty;

            _viewActive = activate;

            if (activate)
            {
                settingsSnapshot = SerializeConfig(AppConfig.Settings.Base);
                ApplyPanelVisibility();
                UpdateSimulatorTabVisibility();

                // Settle the selection with navigation events suppressed. On first entry the selection
                // moves null -> first page, and letting that raise SelectedNodeChanged would enter the
                // page twice - once from the event, once from the explicit EnterNode below (a doubled
                // basicConfig.Activate(true), i.e. a second settings read from the controller).
                _viewActive = false;
                nav.RefreshVisibility();
                nav.EnsureSelection();
                _viewActive = true;

                EnterNode(nav.SelectedNode);
            }
            else
            {
                LeaveNode(nav.SelectedNode);

                // Once a restart is under way the replacement instance is already launching and its splash is
                // topmost, so ANY MessageBox this dying instance shows would be hidden behind that splash and only
                // surface after the new instance clears - the classic "restart prompt appears after the app already
                // relaunched" glitch. Skip all on-leave prompting (Application.Shutdown re-enters this Activate(false)
                // during teardown, which is where the stray second prompt came from).
                if (_restarting)
                    return;

                AutoSaveOnLeave();

                // A restart-only change was made and not yet applied - offer to restart now on the way out of the
                // Settings area (the flashing Restart button otherwise just persists until the next visit).
                if (RestartPending &&
                    AppDialogs.Show("Some changes you made only take effect after a restart. Restart ioSender now?",
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

            BuildNavTree();

            // App-config panels bind to the Config object. Each panel is its own page now, so the
            // DataContext goes on the categories that hold them rather than on column StackPanels.
            foreach (var key in new[] { CatApplication, CatJogging, CatGCode })
            {
                var cat = nav.FindByKey(key);
                if (cat != null)
                    foreach (var n in cat.Children)
                        if (n.Content != null)
                            n.Content.DataContext = profile.Base;
            }

            // Build the built-in panels, then drain any feature-contributed panels registered via the registry.
            // Feature panels (Camera/Probing/Viewer/Lathe) also self-add to model.ConfigControls from their own
            // views - usually after this Setup - so place present controls now and react to later additions.
            model.ConfigControls.Add(new BasicConfigControl());
            model.ConfigControls.Add(new UiGeneralConfigControl());
            model.ConfigControls.Add(new OddJobsSettingsControl());
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
                AddPanelNode(c, profile);
                HookRestart(c);
            }
            model.ConfigControls.CollectionChanged += (s, e) => {
                if (e.NewItems != null)
                    foreach (var c in e.NewItems.OfType<UserControl>())
                    {
                        AddPanelNode(c, profile);
                        HookRestart(c);
                    }
            };

            // The Main Page editor is only meaningful when the main-page/tab layout is user-editable.
            if (!MainPanelRegistry.LayoutEnabled)
            {
                nodeJobLayout.IsVisible = false;
                nodeTopTabs.IsVisible = false;
            }

            nav.RefreshVisibility();
            nav.EnsureSelection();
            UpdateFooterForNode(nav.SelectedNode);
            AppConfig.Settings.Base.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(Config.AutoSaveSettings) || e.PropertyName == nameof(Config.AutoSaveGrblSettings))
                    UpdateFooterForNode(nav.SelectedNode);
            };
        }

        #endregion

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
        }

        #region Nav tree and per-page lifecycle

        // The fixed skeleton. Config panels attach themselves to a category as they arrive (AddPanelNode).
        // LibStrings.FindResource returns string.Empty (NOT null) for a key that isn't in the locale
        // dictionary, so ?? would happily hand back a blank label. The category strings have no CSV rows
        // yet - they land with the localization pass - so fall back on the English text meanwhile.
        private static string Localized(string key, string fallback)
        {
            var s = LibStrings.FindResource(key);
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }

        private void BuildNavTree()
        {
            var controller = new SettingsNavNode(CatController, Localized("SettingsCatController", "Controller"));
            nodeGrbl = controller.Add(new SettingsNavNode("Tab.Settings.Grbl", "Grbl", basicConfig));
            nodeSimulator = controller.Add(new SettingsNavNode("Tab.Settings.Simulator", "Simulator", simConfig));

            var application = new SettingsNavNode(CatApplication, Localized("SettingsCatApplication", "Application"));
            var jogging = new SettingsNavNode(CatJogging, Localized("SettingsCatJogging", "Jogging"));
            var gcode = new SettingsNavNode(CatGCode, Localized("SettingsCatGCode", "G Code"));

            // The two inline editors each carried their own tab strip; those tabs are nodes here instead.
            // Both pages of an editor share the one editor instance as content and record it as Owner, so
            // save-on-leave and reset-to-defaults still reach the editor rather than the bare page body.
            var iface = new SettingsNavNode(CatInterface, Localized("SettingsCatInterface", "User Interface"));
            // Explicit Orders so the panel-contributed pages sort against these rather than just
            // appending: AddPanelNode places by Order, and these defaulted to 0, which put the General
            // page (Order 0, contributed by UiGeneralConfigControl) last instead of first.
            nodeKeyboard = iface.Add(new SettingsNavNode("Tab.Settings.Keyboard", Localized("SettingsPageKeyboard", "Keyboard")) { Order = 10 });
            nodeController = iface.Add(new SettingsNavNode("Tab.Settings.Controller", Localized("SettingsPageController", "Controller")) { Order = 20 });
            nodeMacros = iface.Add(new SettingsNavNode("Tab.Settings.Macros", "Macros") { Order = 30 });
            nodeJobLayout = iface.Add(new SettingsNavNode("Tab.Settings.MainPage", Localized("SettingsPageJobLayout", "Job tab layout")) { Order = 40 });
            nodeTopTabs = iface.Add(new SettingsNavNode("Tab.Settings.Tabs", Localized("SettingsPageTopTabs", "Top-level tabs")) { Order = 50 });

            nav.Nodes.Add(controller);
            nav.Nodes.Add(application);
            nav.Nodes.Add(jogging);
            nav.Nodes.Add(gcode);
            nav.Nodes.Add(iface);
        }

        // Give a config panel its own node under the category that owns it. This replaces the old
        // TargetPanel()/TabFor() switch pair - the label comes from the panel's own localized GroupBox
        // header, so a new panel needs no entry here and no new locale rows.
        private void AddPanelNode(UserControl c, AppConfig profile)
        {
            if (c == null || panelNodes.ContainsKey(c))
                return;

            var category = nav.FindByKey(CategoryFor(c));
            if (category == null)
                return;

            // The panel was previously parented by a column StackPanel; it is a page's whole content now.
            if (c.Parent is Panel prev)
                prev.Children.Remove(c);

            if (c.DataContext == null && profile != null)
                c.DataContext = profile.Base;

            // A panel that hosts unrelated sections contributes one node per section (Camera + Demo
            // recording), keyed and labelled by the panel itself; everything else is a single node
            // labelled from its own GroupBox header.
            var made = new List<SettingsNavNode>();
            var provider = c as ISettingsPageProvider;
            if (provider != null)
            {
                int sub = 0;
                foreach (var page in provider.GetPages())
                    made.Add(new SettingsNavNode(page.Key, page.Label, page.Content ?? c)
                    {
                        Owner = c,
                        Order = OrderFor(c) + sub++,
                        Harvest = SettingsSearchIndex.Harvest(page.IndexRoot ?? page.Content ?? c)
                    });
            }
            else
                made.Add(new SettingsNavNode(c.GetType().FullName, SettingsNavNode.LabelFrom(c, c.GetType().Name), c)
                {
                    Order = OrderFor(c),
                    Harvest = SettingsSearchIndex.Harvest(c)
                });

            panelNodes[c] = made;

            // Feature panels register whenever their own view is built, which is not a stable order, so
            // place by declared order instead of appending - otherwise the tree's contents depend on
            // which features happened to load first.
            foreach (var node in made)
            {
                node.IsVisible = c.Visibility == Visibility.Visible;
                int at = category.Children.Count;
                for (int i = 0; i < category.Children.Count; i++)
                    if (category.Children[i].Order > node.Order) { at = i; break; }
                category.Insert(at, node);
            }

            nav.RefreshVisibility();
        }

        // The panel says where it belongs (ISettingsPanelCategory). The host no longer knows any panel
        // type - the switch this replaced had to match panels in other assemblies by full type name,
        // because CNC Controls cannot reference them. A panel that declares nothing lands in Application.
        private static string CategoryFor(UserControl c)
        {
            var declared = (c as ISettingsPanelCategory)?.SettingsCategory;
            return string.IsNullOrEmpty(declared) ? SettingsCategories.Application : declared;
        }

        private static int OrderFor(UserControl c)
        {
            return (c as ISettingsPanelCategory)?.SettingsOrder ?? 1000;
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

            // A hidden panel must take its nav node(s) with it, or the tree offers a page that renders blank.
            foreach (var kv in panelNodes)
                foreach (var node in kv.Value)
                    node.IsVisible = kv.Key.Visibility == Visibility.Visible;
        }

        private void nav_SelectedNodeChanged(object sender, SettingsNavEventArgs e)
        {
            // Ignore selection churn while the Settings view isn't the active top-level view - most importantly
            // the initial selection made during eager startup layout, which used to fire a premature Activate
            // mid-handshake. The top-level Activate(true) enters the current page when we're genuinely shown;
            // this handler only needs to service real user navigation thereafter. See _viewActive.
            if (!_viewActive)
                return;

            var from = e.From;
            var to = e.To;

            // Defer: a child's activation may pump the dispatcher (DoEvents while waiting on the controller),
            // which throws if it runs during the layout pass that generated the tree's containers.
            Dispatcher.BeginInvoke((System.Action)(() =>
            {
                LeaveNode(from, to);
                EnterNode(to);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Settings > Simulator makes no sense while the active connection IS the simulator (there's no
        // "connected machine" left to build a matching one from) - hidden rather than merely disabled, and
        // bumped off if it happened to be the selected tab when the sim connection was made.
        private void UpdateSimulatorTabVisibility()
        {
            bool hide = SimulatorManager.IsSimulatorConnection();
            nodeSimulator.IsVisible = !hide;
            if (hide && ReferenceEquals(nav.SelectedNode, nodeSimulator))
                nav.Select(nodeGrbl);
        }

        private void EnterNode(SettingsNavNode node)
        {
            if (node == null)
                return;

            EnsureEditorContent(node);
            UpdateFooterForNode(node);

            // An editor backing several pages shows the matching one.
            node.Behaviour<ISettingsPageProvider>()?.ShowPage(node.Key);

            if (node == nodeGrbl)
                basicConfig.Activate(true);
            else if (node == nodeSimulator)
                simConfig.Activate(true);
        }

        private void LeaveNode(SettingsNavNode node, SettingsNavNode next = null)
        {
            if (node == null)
                return;

            if (node == nodeGrbl)
                basicConfig.Activate(false);
            else if (node == nodeSimulator)
                simConfig.Activate(false);
            else
            {
                // Save-on-leave. Moving BETWEEN two pages of the same editor (Keyboard -> Controller) is
                // not leaving it, so don't persist and re-register hotkeys on an internal page switch.
                var owner = node.Owner;
                if (owner != null && next != null && ReferenceEquals(owner, next.Owner))
                    return;
                node.Behaviour<ISettingsEditorTab>()?.Commit();
            }
        }

        // Build an editor page's content the first time it is shown.
        private void EnsureEditorContent(SettingsNavNode node)
        {
            if (node.Content != null)
                return;

            if (node == nodeKeyboard || node == nodeController)
            {
                if (Grbl.GrblViewModel?.Keyboard == null)
                {
                    Unavailable(node, "Key mappings are not available until a controller is connected.");
                    return;
                }
                if (keyMapTab == null)
                {
                    keyMapTab = new KeyMapEditor(Grbl.GrblViewModel);
                    AttachPages(keyMapTab);
                }
            }
            else if (node == nodeMacros)
            {
                if (AppConfig.Settings.Macros != null)
                {
                    macrosTab = new MacroManagerDialog(AppConfig.Settings.Macros);
                    macrosTab.RestartRequired += (s, ev) => EnableRestart(ev.Message);
                    node.Content = macrosTab;
                }
                else
                    Unavailable(node, "Macros are not available.");
            }
            else if (node == nodeJobLayout || node == nodeTopTabs)
            {
                if (mainPageTab == null)
                {
                    mainPageTab = new MainPageEditor();
                    mainPageTab.RestartRequired += (s, ev) => EnableRestart(ev.Message);
                    AttachPages(mainPageTab);
                }
            }
        }

        // Give every page an editor contributes the editor itself as content, keyed by the editor's own
        // page keys. One editor instance backs several nodes - it is never taken apart, because its
        // behaviour is hooked on the control (KeyMapEditor's PreviewKeyDown capture and its Loaded/Unloaded
        // controller-dispatch pause would both stop firing if it left the visual tree).
        private void AttachPages(ISettingsPageProvider provider)
        {
            foreach (var page in provider.GetPages())
            {
                var node = nav.FindByKey(page.Key);
                if (node == null)
                    continue;
                node.Content = page.Content;
                node.Owner = provider;
                if (!string.IsNullOrWhiteSpace(page.Label))
                    node.Label = page.Label;

                // These editors are built on first show, so this is the first chance to index them.
                // Until then they match on label only.
                node.Harvest = SettingsSearchIndex.Harvest(page.IndexRoot ?? page.Content);
            }
        }

        private static void Unavailable(SettingsNavNode node, string why)
        {
            node.Content = new TextBlock { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, Text = why };
        }

        #endregion

        #region Footer (Save settings / Restart) + restart hooking

        // One shared footer, its buttons shown per the active tab (see the applicability table): the Grbl tools
        // sub-row only on Grbl; Save hidden when that tab's autosave is on; Reset to Default only where a panel
        // opts in via ISettingsResettable.
        private void UpdateFooterForNode(SettingsNavNode node)
        {
            if (node == null)
                return;

            grblTools.Visibility = node == nodeGrbl ? Visibility.Visible : Visibility.Collapsed;
            btnSave.Visibility = SaveApplies(node) ? Visibility.Visible : Visibility.Collapsed;
            btnReset.Visibility = ResettablesFor(node).Any() ? Visibility.Visible : Visibility.Collapsed;
        }

        // Save is offered unless the page's autosave (which persists on leave) is on: grbl-settings autosave for
        // the Grbl page, app-settings autosave for everything else.
        private bool SaveApplies(SettingsNavNode node)
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg == null)
                return true;
            return node == nodeGrbl ? !cfg.AutoSaveGrblSettings : !cfg.AutoSaveSettings;
        }

        // Select a Settings sub-tab by its bindable tab-switch id ("Tab.Settings.Grbl" / "Tab.Settings.App").
        // Called by the main-window tab-switch shortcuts after the Settings tab is shown; unknown/absent tabs
        // are ignored (the Main Page sub-tab, for one, may be removed - see tabConfig.Items.Remove(tabMainPage)).
        public bool SelectSubTab(string id)
        {
            // The old ids addressed tabs; "App", "Jogging" and "GCode" are categories now, so they
            // resolve to that category's first visible page rather than failing.
            switch (id)
            {
                case "Tab.Settings.App": return SelectFirstIn(CatApplication);
                case "Tab.Settings.Jogging": return SelectFirstIn(CatJogging);
                case "Tab.Settings.GCode": return SelectFirstIn(CatGCode);
            }
            return nav.SelectByKey(id);
        }

        private bool SelectFirstIn(string categoryKey)
        {
            var cat = nav.FindByKey(categoryKey);
            var page = cat?.Children.FirstOrDefault(n => !n.IsCategory && n.IsShown);
            if (page == null)
                return false;
            nav.Select(page);
            return true;
        }

        // One panel per page, so this is now just "does this page opt in?" - the old version had to
        // gather every visible panel sharing a tab.
        private IEnumerable<ISettingsResettable> ResettablesFor(SettingsNavNode node)
        {
            if (node == nodeGrbl)
                return new ISettingsResettable[] { basicConfig };

            var r = node?.Behaviour<ISettingsResettable>();
            return r != null ? new[] { r } : Enumerable.Empty<ISettingsResettable>();
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            var node = nav.SelectedNode;
            if (node == nodeGrbl)
                basicConfig.SaveSettings();
            else if (node?.Behaviour<ISettingsEditorTab>() is ISettingsEditorTab editor)
                editor.Commit();
            else if (AppConfig.Settings.Save())
                Grbl.GrblViewModel.Message = LibStrings.FindResource("SettingsSaved");
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in ResettablesFor(nav.SelectedNode).ToList())
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

        // Set once a relaunch has been initiated, so the shutting-down instance suppresses any further on-leave
        // prompts (see Activate) - they would otherwise pop up modal behind the new instance's topmost splash.
        private static bool _restarting;

        // public static: the settings footer's Restart button is no longer the only caller - applying or
        // undoing a config overlay (Help > Support) relaunches through this same path, so the relaunch
        // mechanics and the _restarting guard stay in one place.
        public static void DoRestart()
        {
            if (_restarting)
                return;
            _restarting = true;

            AppConfig.Settings.Save();

            // Relaunch ourselves in-process. The new process is passed -self-relaunch (CLI arg) so its own
            // single-instance probe (App.xaml.cs OnStartup) skips checking for a running instance: this
            // instance is still mid-teardown at that point and may still be listening on the singleton
            // pipe for a moment (NamedPipeServerStream.WaitForConnection() isn't reliably cancelable via
            // Dispose on .NET Framework, so there's no reliable way to force-close our own listener first) -
            // without the skip, the new process would detect us, fold into us, and exit, so "Restart" would
            // just close the app instead of relaunching it.
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                    // ProcessStartInfo.ArgumentList isn't available on net462 (added in later .NET
                    // Framework versions) - plain Arguments string instead. "-self-relaunch" needs no
                    // quoting/escaping so this is safe as a literal.
                    Arguments = "-self-relaunch",
                    UseShellExecute = false
                };
                // Logged because a failed relaunch is otherwise indistinguishable from the user quitting:
                // the app exits, the log just stops, and nothing says a restart was even attempted
                // (confirmed 2026-08-03 - a relaunch failed to come back and left no evidence at all).
                CNC.Core.DebugLog.Write("app", string.Format("Restart: relaunching \"{0}\" {1}", psi.FileName, psi.Arguments));
                var relaunched = System.Diagnostics.Process.Start(psi);
                CNC.Core.DebugLog.Write("app", string.Format("Restart: started pid {0} - shutting this instance down", relaunched == null ? -1 : relaunched.Id));
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                // Relaunch failed - leave the app open; changes are saved and apply on next manual restart.
                CNC.Core.DebugLog.Write("app", "Restart: relaunch FAILED, app staying open - " + ex.Message);
                _restarting = false;
            }
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
                    if (AppDialogs.Show(msg, "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
