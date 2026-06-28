/*
 * MainWindow.xaml.cs - part of ioSender
 *
 * v0.47 / 2026-04-29 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2026, Io Engineering (Terje Io)
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
using System.Windows;
using System.Windows.Controls;
using CNC.Core;
using CNC.Controls;
using CNC.Converters;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Windows.Input;
#if ADD_CAMERA
using CNC.Controls.Camera;
#endif

namespace GCode_Sender
{

    public partial class MainWindow : Window
    {
        private const string version = "2.0.47";
        public static MainWindow ui = null;
        public static CNC.Controls.Viewer.Viewer GCodeViewer = null;
        public static UIViewModel UIViewModel { get; } = new UIViewModel();

        private bool saveWinSize = false;

        public MainWindow()
        {
            CNC.Core.Resources.Path = AppDomain.CurrentDomain.BaseDirectory;

            InitializeComponent();

            ui = this;
//            GCodeViewer = viewer;
            Title = string.Format(Title, version);
            BaseWindowTitle = Title;

            // Load config synchronously now - before any control Loaded handler (e.g. JogControl)
            // reads AppConfig.Settings.Base. Only the connection is deferred (see CompleteStartup).
            int cfg = AppConfig.Settings.LoadConfig(Title);
            if (cfg != 0)
            {
                Environment.Exit(cfg);
                return;
            }

            // Restore the saved window size/position now (before the window is shown) so it opens at the
            // right size instead of painting at the default size and being resized later in CompleteStartup -
            // that post-paint resize was the visible "default layout then redraw" on launch.
            if (AppConfig.Settings.Base.KeepWindowSize)
            {
                if (AppConfig.Settings.Base.WindowWidth == -1)
                    WindowState = WindowState.Maximized;
                else
                {
                    Width = Math.Max(Math.Min(AppConfig.Settings.Base.WindowWidth, SystemParameters.PrimaryScreenWidth), MinWidth);
                    Height = Math.Max(Math.Min(AppConfig.Settings.Base.WindowHeight, SystemParameters.PrimaryScreenHeight), MinHeight);

                    double savedLeft = AppConfig.Settings.Base.WindowLeft, savedTop = AppConfig.Settings.Base.WindowTop;
                    if (!double.IsNaN(savedLeft) && !double.IsNaN(savedTop) && IsOnScreen(savedLeft, savedTop, Width, Height))
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = savedLeft;
                        Top = savedTop;
                    }
                    else
                    {
                        if (Left + Width > SystemParameters.PrimaryScreenWidth)
                            Left = 0d;
                        if (Top + Height > SystemParameters.PrimaryScreenHeight)
                            Top = 0d;
                    }
                }
            }
            saveWinSize = AppConfig.Settings.Base != null && AppConfig.Settings.Base.KeepWindowSize;

            if (DataContext is GrblViewModel viewModel)
                CNC.Core.Grbl.GrblViewModel = viewModel;

            // Let library-side generated-program runners (e.g. Surface Spoilboard) raise the floating run-control
            // panel, which lives in this assembly.
            CNC.Controls.MacroProcessor.RunControlPanel = m => MachineControlWindow.ShowFor(m, this);

            // When a generated program is large enough to stream through the real job streamer, bring the Grbl
            // (Job) tab forward (so the operator can watch progress / 3D and has the full run controls) and
            // start it via the public CycleStart path. When the job ends, return to the tab it was launched
            // from (e.g. Tools > Auto square) so iterative tools don't strand the operator on the Grbl tab.
            CNC.Controls.MacroProcessor.RunStreamedJob = m =>
            {
                TabItem origin = tabMode.SelectedItem as TabItem;
                TabItem tab = getTab(ViewType.GRBL);
                if (tab != null)
                    tabMode.SelectedItem = tab;
                var jv = getView(tab) as JobView;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new System.Action(() => jv?.StartLoadedJob()));
                if (origin != null && origin != tab)
                    RestoreTabOnJobEnd(m, origin);
            };

            new PipeServer(App.Current?.Dispatcher ?? Dispatcher);
            PipeServer.FileTransfer += Pipe_FileTransfer;
            AttachBasePropertyChangedHandler();
        }

        public string BaseWindowTitle { get; set; }

        public string WindowTitle
        {
            set
            {
                ui.Title = BaseWindowTitle + (string.IsNullOrEmpty(value) ? "" : " - " + value);
                ui.menuCloseFile.IsEnabled = ui.menuSaveFile.IsEnabled = !(string.IsNullOrEmpty(value) || value.StartsWith("SDCard:"));
                ui.menuTransform.IsEnabled = ui.menuCloseFile.IsEnabled && UIViewModel.TransformMenuItems.Count > 0;
            }
        }

        public bool JobRunning
        {
            get { return menuFile.IsEnabled != true; }
            set {
       //         menuFile.IsEnabled = xx.IsEnabled = !value;
                foreach (TabItem tabitem in UIUtils.FindLogicalChildren<TabItem>(ui.tabMode))
                {
                    var view = getView(tabitem);
                    if (view != null)
                        tabitem.IsEnabled = (!value && view.CanEnable) || tabitem == ui.tabMode.SelectedItem;
                }
            }
        }

        #region UIEvents

        private void Window_Load(object sender, EventArgs e)
        {
            MainPanelRegistry.LayoutEnabled = true; // ioSender XL: enable the "Main page layout" settings control

            // Defer connection setup and all settings-dependent view initialization to ApplicationIdle
            // so the main window paints before the (possibly blocking) connection dialog appears.
            // SetupAndOpen() runs Load(), which populates AppConfig.Settings.Base; everything in
            // CompleteStartup() depends on that (AppConfigView.Setup, FlyoutItems, Comms.com, ...),
            // so it must run only after SetupAndOpen() has completed.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new System.Action(CompleteStartup));
        }

        private void CompleteStartup()
        {
            // Build the main tabs from the registry (Phase 1: MainWindow is a container). Must run
            // before anything that resolves a tab via getTab()/getView() below.
            RegisterBuiltinTabs();
            BuildTabs();

            // Config already loaded in the constructor; here we only open the connection (deferred so
            // the main window paints first). res == 2: user cancelled / no connection - stay open
            // (disconnected) so the user can connect later via the Connect menu item.
            int res = AppConfig.Settings.OpenConnection(Title, (GrblViewModel)DataContext, App.Current.Dispatcher);
            bool connected = res == 0;

            UpdateSimulatorTint();   // pale-yellow background when the startup target is the simulator

            GrblInfo.LatheModeEnabled = AppConfig.Settings.Lathe.IsEnabled;

            // App settings now live inside the Settings tab (GrblConfigView forwards Setup to AppConfigView);
            // set the Settings view up first so the app-config controls are populated before the other views.
            var settingsView = getView(getTab(ViewType.GRBLConfig));

            settingsView?.Setup(UIViewModel, AppConfig.Settings);

            // Top-level tabs only - do NOT recurse into nested TabControls, or sub-tabs that host an
            // ICNCView (App Settings = AppConfigView) get wrongly disabled by the enable check below.
            foreach (TabItem tab in ui.tabMode.Items)
            {
                ICNCView view = getView(tab);
                if (view != null && view != settingsView)
                {
                    view.Setup(UIViewModel, AppConfig.Settings);
                    // Initial pre-connection enable state comes from each tab's descriptor (set in
                    // BuildTabs); the connection/JobRunning state drives enable from there on.
                }
            }
#if ADD_CAMERA
            enableCamera(this);
#else
            menuCamera.Visibility = Visibility.Hidden;
#endif
            if (!AppConfig.Settings.GCodeViewer.IsEnabled)
                ShowView(false, ViewType.GCodeViewer);

            // Publish the present tabs to the Edit Main Page > Tabs editor, then apply the saved order/visibility.
            PublishAndApplyTabs();

            UIViewModel.ConfigControls.Add(new CNC.Controls.Viewer.ConfigControl());

            xx.ItemsSource = UIViewModel.SidebarItems;

            // Build sidebar flyouts from the user's FlyoutItems list (Edit Main Page dialog).
            var seenFlyouts = new System.Collections.Generic.HashSet<string>();
            var pinnedFlyouts = new System.Collections.Generic.List<UserControl>();
            foreach (var name in AppConfig.Settings.Base.FlyoutItems)
            {
                if (!seenFlyouts.Add(name))     // guard against duplicate entries
                    continue;

                var item = MainPanelRegistry.ByName(name);
                if (item == null || !item.CanBeFlyout)   // skip leftover main-page-only panels (e.g. jog panels)
                    continue;

                UserControl flyout;
                bool alreadyInCanvas = false;

                switch (item.Kind)
                {
                    case PanelKind.Panel:
                        flyout = new PanelFlyout(item.Name, item.Label, item.CreateFlyout());
                        break;
                    case PanelKind.Offset:
                        flyout = new OffsetFlyout(item.Name);
                        break;
                    case PanelKind.Special: // reuse the controls declared in MainWindow.xaml
                        flyout = item.Name == "Macros" ? (UserControl)macroControl
                               : item.Name == "MachinePosition" ? mposFlyout : null;
                        alreadyInCanvas = true;
                        break;
                    default:
                        flyout = null;
                        break;
                }

                if (flyout == null || !(flyout is ISidebarControl))
                    continue;

                flyout.Visibility = Visibility.Hidden;

                if (flyout is IPinnableFlyout pin)
                {
                    pin.Pinned = AppConfig.Settings.Base.PinnedFlyouts.Contains(name);
                    pin.PinnedChanged += MainPanelFlyout_PinnedChanged;
                }

                if (!alreadyInCanvas)
                {
                    Canvas.SetRight(flyout, 22);
                    sidebarCanvas.Children.Add(flyout);
                }

                UIViewModel.SidebarItems.Add(new SidebarItem((ISidebarControl)flyout));

                if (flyout is IPinnableFlyout pinned && pinned.Pinned)   // reopen pinned flyouts on launch (deferred below)
                    pinnedFlyouts.Add(flyout);
            }

            // Reopen pinned flyouts on launch. Setting Visibility inline (or even at Loaded priority) did not
            // stick - the rest of CompleteStartup (tab select, connect, view Activate) runs right after and the
            // flyouts stayed closed. A one-shot timer asserts it once startup has settled, so it survives any
            // post-layout reset.
            if (pinnedFlyouts.Count > 0)
            {
                var showPinned = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = System.TimeSpan.FromMilliseconds(400)
                };
                showPinned.Tick += (s, e) =>
                {
                    showPinned.Stop();
                    foreach (var f in pinnedFlyouts)
                        f.Visibility = Visibility.Visible;
                };
                showPinned.Start();
            }

            UIViewModel.CurrentView = getView((TabItem)tabMode.Items[tabMode.SelectedIndex = 0]);
            if (connected)
            {
                System.Threading.Thread.Sleep(50);
                Comms.com.PurgeQueue();
                UIViewModel.CurrentView.Activate(true, ViewType.Startup);
            }

            // Restore preserved console preferences
            var gvm = DataContext as GrblViewModel;
            gvm.ResponseLogVerbose = AppConfig.Settings.Base.ConsoleVerbose;
            gvm.ResponseLogFilterRT = AppConfig.Settings.Base.ConsoleFilterRT;
            gvm.ResponseLogShowRTAll = AppConfig.Settings.Base.ConsoleShowRTAll;
            if (AppConfig.Settings.Base.ConsoleWindowOpen)
                openConsole();

            registerConsoleShortcut();

            if (!string.IsNullOrEmpty(AppConfig.Settings.FileName))
            {
                // Delay loading until app is ready
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() =>
                {
                    GCode.File.Load(AppConfig.Settings.FileName);
                }));
            }

            IGCodeConverter c = new Excellon2GCode();
            GCode.File.AddConverter(c.GetType(), c.FileType, c.FileExtensions);
            c = new HpglToGCode();
            GCode.File.AddConverter(c.GetType(), c.FileType, c.FileExtensions);

            GCode.File.AddTransformer(typeof(GCodeRotateViewModel), (string)FindResource("MenuRotate"), UIViewModel.TransformMenuItems);
            GCode.File.AddTransformer(typeof(ArcsToLines), (string)FindResource("MenuArcsToLines"), UIViewModel.TransformMenuItems);
            GCode.File.AddTransformer(typeof(GCodeCompress), (string)FindResource("MenuCompress"), UIViewModel.TransformMenuItems);
            GCode.File.AddTransformer(typeof(CNC.Controls.DragKnife.DragKnifeViewModel), (string)FindResource("MenuDragKnife"), UIViewModel.TransformMenuItems);

            // First-run gate: with no machine saved yet, jump to the Machine Setup Wizard once the controller is
            // ready, then return to the normal UI when the user presses Apply (see ForceMachineSetupIfNeeded).
            if (connected)
                ForceMachineSetupIfNeeded();
        }

        private bool _machineSetupForced = false;

        // On first run (no machine saved) wait for the controller to report version + settings, then bring the
        // Machine Setup Wizard to the foreground. Polls so it works regardless of connect/settings-read timing.
        private void ForceMachineSetupIfNeeded()
        {
            if (_machineSetupForced)
                return;

            int tries = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) =>
            {
                bool ready = !string.IsNullOrEmpty(GrblInfo.Version) && GrblSettings.IsLoaded;
                if (!ready && ++tries < 40)   // wait up to ~10s for the controller to report version + settings
                    return;
                timer.Stop();
                if (!ready)
                    return;
                // Skip the gate when connected to the simulator - it's not a real machine to set up.
                bool sim = Comms.com != null && Comms.com.IsOpen && AppConfig.Settings.Base.StartSimulator;
                int step = CNC.Controls.MachineSetupWizard.FirstIncompleteStep();
                if (step == 0 || sim)
                    return;

                // Confirm after a short settle: a connect/reset can momentarily yield a stale read - in
                // particular the macro check (step 6) does a synchronous filesystem listing that comes back
                // empty right after a reset, falsely flagging a step. Re-check once things have settled and
                // only gate if still incomplete (avoids the "prompt appears but every tab is green" race).
                var confirm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                confirm.Tick += (s2, e2) =>
                {
                    confirm.Stop();
                    if (!GrblSettings.IsLoaded)
                        return;
                    int step2 = CNC.Controls.MachineSetupWizard.FirstIncompleteStep();
                    if (step2 != 0)
                        ShowMachineSetup(step2);
                };
                confirm.Start();
            };
            timer.Start();
        }

        // After a tool-launched streamed job (which jumps to the Grbl tab) finishes, hop back to the tab it was
        // started from so iterative tools (e.g. Auto square: drill -> measure -> apply -> repeat) don't strand
        // the operator on the Grbl tab. Watches StreamingState: arm on the first running state, restore on the
        // next terminal one, then unsubscribe.
        private void RestoreTabOnJobEnd(GrblViewModel m, TabItem origin)
        {
            bool started = false;
            System.ComponentModel.PropertyChangedEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                var st = m.StreamingState;
                if (st == StreamingState.Send || st == StreamingState.SendMDI)
                    started = true;
                else if (started && (st == StreamingState.Idle || st == StreamingState.JobFinished ||
                                     st == StreamingState.Stop || st == StreamingState.NoFile))
                {
                    m.PropertyChanged -= handler;
                    Dispatcher.BeginInvoke(new System.Action(() => { if (origin != null) tabMode.SelectedItem = origin; }));
                }
            };
            m.PropertyChanged += handler;
        }

        private void ShowMachineSetup(int step)
        {
            _machineSetupForced = true;

            CNC.Controls.MachineSetupWizard.SetupApplied -= OnMachineSetupApplied;
            CNC.Controls.MachineSetupWizard.SetupApplied += OnMachineSetupApplied;

            TabItem tab = getTab(ViewType.MachineSetup);
            if (tab != null)
            {
                tab.IsEnabled = true;
                tabMode.SelectedItem = tab;
                (getView(tab) as CNC.Controls.MachineSetupView)?.GoToStep(step);
            }

            MessageBox.Show(this,
                "Let's finish setting up your machine.\n\nWork through the steps - the normal screen opens once all are complete.",
                "Machine setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Apply fired: re-check the setup steps. Still gaps -> lead to the next one and stay; all complete
        // -> return to the normal (Grbl) view.
        private void OnMachineSetupApplied()
        {
            int step = CNC.Controls.MachineSetupWizard.FirstIncompleteStep();
            if (step != 0)
            {
                Dispatcher.BeginInvoke(new System.Action(() =>
                    (getView(getTab(ViewType.MachineSetup)) as CNC.Controls.MachineSetupView)?.GoToStep(step)));
                return;
            }

            CNC.Controls.MachineSetupWizard.SetupApplied -= OnMachineSetupApplied;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                TabItem grbl = getTab(ViewType.GRBL);
                if (grbl == null)
                    return;
                tabMode.SelectedItem = grbl;
                // Move keyboard focus onto the Job view (off the wizard's text inputs) so keyboard jogging is
                // live again after setup - otherwise keys land in whatever box last had focus (e.g. the MDI/
                // console prompt) instead of jogging. Deferred to Input priority so it runs after the tab switch.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() =>
                {
                    if (getView(grbl) is UserControl jv)
                        jv.Focus();
                }));
            }));
        }

        // Persist a flyout's pin state so it reopens (pinned) on next launch.
        private void MainPanelFlyout_PinnedChanged(IPinnableFlyout flyout)
        {
            var pinned = AppConfig.Settings.Base.PinnedFlyouts;
            if (flyout.Pinned)
            {
                if (!pinned.Contains(flyout.PanelName))
                    pinned.Add(flyout.PanelName);
            }
            else
                pinned.Remove(flyout.PanelName);

            AppConfig.Settings.Save();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if(saveWinSize && !(AppConfig.Settings.Base.WindowWidth == e.NewSize.Width && AppConfig.Settings.Base.WindowHeight == e.NewSize.Height))
            {
                AppConfig.Settings.Base.WindowWidth = WindowState == WindowState.Maximized ? -1 : e.NewSize.Width;
                AppConfig.Settings.Base.WindowHeight = WindowState == WindowState.Maximized ? -1 : e.NewSize.Height;
                AppConfig.Settings.Save();
            }
        }

        // True if a window placed at (left, top) of the given size would have a grabbable strip of its title
        // bar on some connected monitor (the whole virtual desktop), so a position saved on a screen that is
        // no longer attached is rejected rather than opening the window off-screen.
        private static bool IsOnScreen(double left, double top, double width, double height)
        {
            double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
            double vr = vx + SystemParameters.VirtualScreenWidth, vb = vy + SystemParameters.VirtualScreenHeight;
            const double grab = 120;   // keep at least this much of the title bar reachable
            return top >= vy - 1 && top < vb - 20 && left + width > vx + grab && left < vr - grab;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (CNC.Core.Grbl.GrblViewModel.IsSDCardJob || !(e.Cancel = !menuFile.IsEnabled))
            {
                // Remember window placement for next launch (size is also tracked live in Window_SizeChanged;
                // position has no live handler, so capture it here). Use RestoreBounds when maximized so we
                // store the un-maximized rectangle, and re-maximize via WindowWidth == -1.
                if (saveWinSize)
                {
                    bool maximized = WindowState == WindowState.Maximized;
                    Rect b = maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
                    AppConfig.Settings.Base.WindowLeft = b.Left;
                    AppConfig.Settings.Base.WindowTop = b.Top;
                    if (!maximized)
                    {
                        AppConfig.Settings.Base.WindowWidth = b.Width;
                        AppConfig.Settings.Base.WindowHeight = b.Height;
                    }
                    AppConfig.Settings.Save();
                }

                UIViewModel.CurrentView.Activate(false, ViewType.Shutdown);

                if (UIViewModel.Console != null)
                {
                    // Don't let the shutdown close overwrite the preserved open state
                    UIViewModel.Console.IsVisibleChanged -= Console_IsVisibleChanged;
                    UIViewModel.Console.Close();
                }
#if ADD_CAMERA
                if (UIViewModel.Camera != null)
                {
                    UIViewModel.Camera.CloseCamera();
                    UIViewModel.Camera.Close();
                }
#endif
                Comms.com.DataReceived -= (DataContext as GrblViewModel).DataReceived;

                if (CNC.Core.Grbl.GrblViewModel.AutoReportInterval > 0)
                {
                    Comms.com.WriteByte(GrblConstants.CMD_AUTO_REPORTING_TOGGLE);
                    System.Threading.Thread.Sleep(50);
                }

                using (new UIUtils.WaitCursor())
                {
                    Comms.com.Close(); // disconnecting from websocket may take some time...
                    AppConfig.Settings.Shutdown();
                }
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            //Comms.com.Close(); // Makes fking process hang
        }

        private void exitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        void aboutWikiItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/terjeio/ioSender/wiki");
        }

        void tipsWikiItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/terjeio/ioSender/wiki/Usage-tips");
        }

        void briefTour_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://www.grbl.org/single-post/one-sender-to-rule-them-all");
        }

        void videoTutorials_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://youtube.com/playlist?list=PLnSV6o2cRxM5mQQe4ec5cS2J8jBsEciY3");
        }

        void errorAndAlarms_Click(object sender, EventArgs e)
        {
            new ErrorsAndAlarms(BaseWindowTitle) { Owner = Application.Current.MainWindow }.Show();
        }

        void aboutMenuItem_Click(object sender, EventArgs e)
        {
            About about = new About(BaseWindowTitle) { Owner = Application.Current.MainWindow };
            about.DataContext = DataContext;
            about.ShowDialog();
        }

        // Single item: "Connect..." when disconnected, "Reconnect..." when connected (which disconnects
        // the current target first, then shows the connection dialog so the user can pick another).
        private void menuFile_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            bool connected = Comms.com != null && Comms.com.IsOpen;
            menuConnect.Header = connected ? "Reco_nnect..." : "Co_nnect...";
        }

        private void connectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Reconnect: drop the current connection first so the dialog can switch targets/simulators.
            if (Comms.com != null && Comms.com.IsOpen)
                Disconnect();

            int res = AppConfig.Settings.Connect(Title, (GrblViewModel)DataContext, App.Current.Dispatcher);
            if (res == 0 && Comms.com != null && Comms.com.IsOpen)
            {
                Comms.com.PurgeQueue();
                // Activate the GRBL view to run the controller handshake (re-runs it after a disconnect
                // because PrepareForReconnect() cleared its init state).
                if (getView(getTab(ViewType.GRBL)) is ICNCView grbl)
                    grbl.Activate(true, ViewType.Startup);

                // A menu (re)connect can target a different controller than startup did (e.g. simulator ->
                // real machine), so re-run the first-run wizard gate + ATC macro check against the now-
                // connected controller. Startup only runs these from CompleteStartup; without this a target
                // switch skips provisioning (no Start Job seed, no upload prompt) even though $I now reports
                // a different (ATC) controller. ForceMachineSetupIfNeeded polls for the new $I/settings.
                ForceMachineSetupIfNeeded();
            }

            UpdateSimulatorTint();
        }

        private void Disconnect()
        {
            if (Comms.com == null || !Comms.com.IsOpen)
                return;

            Comms.com.Close(); // explicit close - cancels auto-reconnect (see StreamComms.Close)

            var model = (GrblViewModel)DataContext;
            model.ConnectionTarget = null; // status bar -> "Not connected"
            model.IsReady = false;

            // Clear the GRBL view's controller state so the next Connect re-runs the handshake.
            if (getView(getTab(ViewType.GRBL)) is JobView grbl)
                grbl.PrepareForReconnect();

            UpdateSimulatorTint();
        }

        // "Prefer network connection": after a serial/USB connection whose $I reported an IP, probe <ip>:23 and,
        // if it answers, switch the live connection from serial to the network. Called from JobView once the
        // controller info is loaded. No-op unless the option is set, the current link is serial, and an IP is
        // known. The probe runs off the UI thread; the actual switch is marshalled back and deferred so it runs
        // after the serial handshake has fully settled. Guarded against re-entry while a migration is in flight.
        private bool migratingToNetwork = false;
        public void TryMigrateToNetwork()
        {
            var cfg = AppConfig.Settings;
            if (migratingToNetwork || cfg.Base == null || !cfg.Base.PreferNetwork)
                return;
            if (Comms.com == null || !Comms.com.IsOpen || !cfg.Base.PortParams.ToLower().StartsWith("com"))
                return;   // only migrate away from a serial/USB link
            string ip = GrblInfo.IpAddress;
            if (string.IsNullOrWhiteSpace(ip))
                return;

            migratingToNetwork = true;
            string serialTarget = cfg.Base.PortParams;
            var model = (GrblViewModel)DataContext;

            new Thread(() =>
            {
                bool reachable = ProbeTcp(ip, 23, 1500);
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    try
                    {
                        // The link may have dropped or already moved off serial while we were probing.
                        if (reachable && Comms.com != null && Comms.com.IsOpen && cfg.Base.PortParams.ToLower().StartsWith("com"))
                        {
                            Disconnect();
                            bool migrated = cfg.ConnectTo(Title, model, App.Current.Dispatcher, ip + ":23") == 0
                                            && Comms.com != null && Comms.com.IsOpen;
                            if (!migrated)   // network connect failed despite the probe - fall back to the serial port
                                cfg.ConnectTo(Title, model, App.Current.Dispatcher, serialTarget);
                            if (Comms.com != null && Comms.com.IsOpen)
                            {
                                Comms.com.PurgeQueue();
                                if (getView(getTab(ViewType.GRBL)) is ICNCView grbl)
                                    grbl.Activate(true, ViewType.Startup);
                            }
                            model.Message = migrated
                                ? "Connection migrated to network (" + ip + ":23)"
                                : "Network migration failed; staying on " + serialTarget;
                            UpdateSimulatorTint();
                        }
                    }
                    finally
                    {
                        migratingToNetwork = false;
                    }
                }), DispatcherPriority.ApplicationIdle);
            }) { IsBackground = true }.Start();
        }

        // Quick TCP reachability check: can we open a connection to host:port within timeoutMs?
        private static bool ProbeTcp(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                        return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        // Tint the whole window pale yellow while connected to the simulator (Base.StartSimulator is set to
        // the chosen target's sim-ness on each connect) so a virtual machine is unmistakable at a glance -
        // the gray the app normally shows IS this window background, seen through the transparent content.
        // Restored to the default gray on a real target or when disconnected. Called from every connect path.
        private void UpdateSimulatorTint()
        {
            bool sim = Comms.com != null && Comms.com.IsOpen && AppConfig.Settings.Base.StartSimulator;
            Background = new System.Windows.Media.SolidColorBrush(sim
                ? System.Windows.Media.Color.FromRgb(0xF7, 0xEF, 0xA8)   // pale yellow = simulator
                : System.Windows.Media.Color.FromRgb(0xE5, 0xE5, 0xE5)); // default gray
        }

        // Right-click "Target" status item -> Validate. Only enabled while connected; exercises the
        // connected controller's G-code command set in check mode and reports which features it accepts.
        private void targetContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ContextMenu cm && cm.Items.Count > 0 && cm.Items[0] is MenuItem mi)
                mi.IsEnabled = Comms.com != null && Comms.com.IsOpen;
        }

        private void validateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ValidateProcessor.Run((GrblViewModel)DataContext);
        }

        // Copy the full status-bar message to the clipboard (the line itself is single-line / can be truncated).
        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            try { System.Windows.Clipboard.SetText((DataContext as GrblViewModel)?.Message ?? string.Empty); }
            catch { /* clipboard may be locked by another app - ignore */ }
        }

        private void AttachBasePropertyChangedHandler()
        {
            if (AppConfig.Settings.Base != null)
            {
                AppConfig.Settings.Base.PropertyChanged -= Base_PropertyChanged;
                AppConfig.Settings.Base.PropertyChanged += Base_PropertyChanged;
            }
        }

        private void Base_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if(e.PropertyName == nameof(Config.KeepWindowSize))
            {
                // Keep the live save flag in sync with the checkbox - it was only ever set at startup, so
                // enabling the option mid-session previously did nothing until a restart.
                saveWinSize = (sender as Config).KeepWindowSize;
                if(saveWinSize)
                {
                    // Capture (and persist) the current placement the moment it's enabled so THIS window's
                    // size and position are remembered, not just whatever it is at the next close.
                    bool maximized = WindowState == WindowState.Maximized;
                    Rect b = maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
                    AppConfig.Settings.Base.WindowWidth = maximized ? -1 : b.Width;
                    AppConfig.Settings.Base.WindowHeight = maximized ? -1 : b.Height;
                    AppConfig.Settings.Base.WindowLeft = b.Left;
                    AppConfig.Settings.Base.WindowTop = b.Top;
                    AppConfig.Settings.Save();
                }
            }
        }

        private void Pipe_FileTransfer(string filename)
        {
            if(!JobRunning)
                GCode.File.Load(filename);
        }

        private void fileSaveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Save();
        }

        private void fileOpenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Open();
        }

        private void fileOpenFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.OpenFolder();
        }

        private void fileCloseMenuItem_Click(object sender, RoutedEventArgs e)
        {
            GCode.File.Close();
        }

        private void TabMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(DataContext as GrblViewModel).IsReady)
                return;

            if (Equals(e.OriginalSource, sender) && UIViewModel.CurrentView != null && e.AddedItems.Count == 1)
            {
                ICNCView prevView = UIViewModel.CurrentView, nextView = getView((TabItem)e.AddedItems[0]);
                if (nextView != null && nextView != UIViewModel.CurrentView)
                {
                    UIViewModel.CurrentView = nextView;
                    prevView.Activate(false, nextView.ViewType);
                    nextView.Activate(true, prevView.ViewType);
                }
            }
        }

        private void SDCardView_FileSelected(string filename, bool rewind)
        {
            if((ui.DataContext as GrblViewModel).FileName != filename.Substring(filename.IndexOf(':') + 1))
                GCode.File.Close();
            (ui.DataContext as GrblViewModel).FileName = filename;
            (ui.DataContext as GrblViewModel).SDRewind = rewind;
            Dispatcher.BeginInvoke((System.Action)(() => ui.tabMode.SelectedItem = getTab(ViewType.GRBL)));
        }

        #endregion

        public static void CloseFile ()
        {
            ICNCView view, grbl = getView(getTab(ViewType.GRBL));

            grbl.CloseFile();

            foreach (TabItem tabitem in UIUtils.FindLogicalChildren<TabItem>(ui.tabMode))
            {
                if ((view = getView(tabitem)) != null && view != grbl)
                    view.CloseFile();
            }
        }

        private static TabItem getTab(ViewType mode)
        {
            TabItem tab = null;

            foreach (TabItem tabitem in UIUtils.FindLogicalChildren<TabItem>(ui.tabMode))
            {
                var view = getView(tabitem);
                if (view != null && view.ViewType == mode)
                {
                    tab = tabitem;
                    break;
                }
            }

            return tab;
        }

        public static bool EnableView(bool enable, ViewType view)
        {
            TabItem tab = getTab(view);
            if (tab != null)
                tab.IsEnabled = enable;

            return tab != null && enable;
        }

        public static void ShowView(bool show, ViewType view)
        {
            TabItem tab = getTab(view);
            if (tab != null && !show)
                ui.tabMode.Items.Remove(tab);
        }

        public static bool IsViewVisible(ViewType view)
        {
            TabItem tab = getTab(view);

            return tab != null;
        }

        // Register the built-in tabs (ioSender XL) as descriptors. This is the one place that knows the
        // concrete tab views; new tabs (incl. from plugins) register their own descriptor instead of
        // editing MainWindow.xaml + the scattered enable/visibility lists. Labels are not yet localized
        // (built-in tabs lost their LocBaml x:Uid headers in the container conversion - revisited when
        // registered-tab localization is designed).
        private void RegisterBuiltinTabs()
        {
            TabRegistry.Register(new TabDescriptor(ViewType.GRBL, "Grbl", () => new JobView(), 10, enabledWhenDisconnected: true));
            TabRegistry.Register(new TabDescriptor(ViewType.LoadStock, "Load stock", () => new LoadStockView(), 20, enabledWhenDisconnected: true));
            TabRegistry.Register(new TabDescriptor(ViewType.Offsets, "Offsets", () => new OffsetView(), 30));
            TabRegistry.Register(new TabDescriptor(ViewType.GRBLConfig, "Settings", () => new GrblConfigView(), 40, enabledWhenDisconnected: true, alwaysVisible: true));
            TabRegistry.Register(new TabDescriptor(ViewType.Probing, "Probing", () => new CNC.Controls.Probing.ProbingView(), 50));
            TabRegistry.Register(new TabDescriptor(ViewType.SDCard, "SD Card", () => new SDCardView(), 60,
                configure: ctl => ((SDCardView)ctl).FileSelected += SDCardView_FileSelected));
            TabRegistry.Register(new TabDescriptor(ViewType.LatheWizards, "Lathe Wizards", () => new CNC.Controls.Lathe.LatheWizardsView(), 70));
            TabRegistry.Register(new TabDescriptor(ViewType.Tools, "Tools", () => new ToolsView(), 80, alwaysVisible: true));
            TabRegistry.Register(new TabDescriptor(ViewType.MachineSetup, "Machine Setup", () => new MachineSetupView(), 90, enabledWhenDisconnected: true, alwaysVisible: true));
        }

        // Instantiate the registered tabs into the (XAML-empty) TabControl, in descriptor order.
        private void BuildTabs()
        {
            tabMode.Items.Clear();
            foreach (var d in TabRegistry.Descriptors)
            {
                var ctl = d.Create?.Invoke();
                if (ctl == null)
                    continue;
                d.Configure?.Invoke(ctl);
                tabMode.Items.Add(new TabItem
                {
                    Header = d.Label,
                    Content = ctl,
                    IsEnabled = d.EnabledWhenDisconnected
                });
            }
        }

        // Publish the tabs currently present (after capability filtering) so the "Edit Main Page" Tabs editor can
        // list them, then reorder/hide them per Config.Tabs (ordered ViewType names). Settings:App is always kept
        // visible (it hosts the editor), and an empty/invalid result falls back to the built-in order.
        private void PublishAndApplyTabs()
        {
            var present = new System.Collections.Generic.List<TabItem>();
            foreach (TabItem t in tabMode.Items)
                present.Add(t);

            var byName = new System.Collections.Generic.Dictionary<string, TabItem>();
            var infos = new System.Collections.Generic.List<CNC.Controls.TabInfo>();
            foreach (var t in present)
            {
                // Most tabs host an ICNCView (key = ViewType); some (e.g. Trinamic tuner) host an IGrblConfigTab
                // with no ViewType, so fall back to the TabItem's x:Name so they still appear in the editor.
                var v = getView(t);
                string name = v != null ? v.ViewType.ToString() : t.Name;
                if (string.IsNullOrEmpty(name) || byName.ContainsKey(name))
                    continue;
                byName[name] = t;
                infos.Add(new CNC.Controls.TabInfo(name, t.Header?.ToString() ?? name));
            }
            CNC.Controls.TabRegistry.Publish(infos);

            var order = AppConfig.Settings.Base.Tabs;
            if (order == null || order.Count == 0)
                return;     // default: keep built-in order and visibility

            var desired = new System.Collections.Generic.List<TabItem>();
            foreach (var name in order)
            {
                TabItem t;
                if (byName.TryGetValue(name, out t) && !desired.Contains(t))
                    desired.Add(t);
            }
            // Tabs flagged AlwaysVisible in the registry (Settings hosts App settings + the Edit Main
            // Page editor; Machine Setup is the setup gate; Tools is the hub) must remain reachable even
            // if a saved layout predates them or would hide them.
            TabItem prot;
            foreach (var d in CNC.Controls.TabRegistry.Descriptors)
            {
                if (d.AlwaysVisible && byName.TryGetValue(d.Name, out prot) && !desired.Contains(prot))
                    desired.Add(prot);
            }

            if (desired.Count == 0)
                return;     // safety: never hide everything

            tabMode.Items.Clear();
            foreach (var t in desired)
                tabMode.Items.Add(t);
            tabMode.SelectedIndex = 0;
        }

#if ADD_CAMERA
        private static bool enableCamera(MainWindow owner)
        {
            if (UIViewModel.Camera == null)
            {
                UIViewModel.Camera = new Camera();
                UIViewModel.Camera.Setup(UIViewModel);
                //        Camera.Owner = owner;
                owner.menuCamera.IsEnabled = UIViewModel.Camera.HasCamera;
            }

            return UIViewModel.Camera != null;
        }

        private void CameraOpen_Click(object sender, RoutedEventArgs e)
        {
            UIViewModel.Camera.Open();
        }

        private void openConsoleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            openConsole();
        }
#else
        private void CameraOpen_Click(object sender, RoutedEventArgs e)
        {
        }
#endif

        private void openConsole()
        {
            if (UIViewModel.Console == null)
            {
                UIViewModel.Console = new ConsoleWindow();
                UIViewModel.Console.DataContext = DataContext;
                UIViewModel.Console.IsVisibleChanged += Console_IsVisibleChanged;
                // Same shortcut handler on the console window so the toggle also fires when the
                // console (not the main window) has focus - lets one keypress hide it again.
                UIViewModel.Console.PreviewKeyDown += MainWindow_PreviewKeyDown;
                UIViewModel.Console.Show();
            }
            else
            {
                if (UIViewModel.Console.IsVisible)
                    UIViewModel.Console.Visibility = Visibility.Hidden;
                else
                    UIViewModel.Console.Show();
            }
        }

        private void Console_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Preserve the floating console window open state for the next session
            if (AppConfig.Settings.Base != null)
            {
                AppConfig.Settings.Base.ConsoleWindowOpen = (bool)e.NewValue;
                AppConfig.Settings.Save();
            }
        }

        private Key consoleKey = Key.None;
        private ModifierKeys consoleModifiers = ModifierKeys.None;

        private bool consoleShortcutHooked = false;

        private void registerConsoleShortcut()
        {
            // Parse the configurable console shortcut into key + modifiers. Parsed manually (not via
            // KeyGesture) so a modifier-less key such as Esc is allowed. Default is Esc.
            ShortcutKey.TryParse(AppConfig.Settings.Base.ConsoleShortcut, out consoleKey, out consoleModifiers);

            if (!consoleShortcutHooked)
            {
                // Tunneling preview so the key is seen before child controls (jog/keypress handlers) consume it.
                PreviewKeyDown += MainWindow_PreviewKeyDown;
                PreviewKeyUp += MainWindow_PreviewKeyUp;   // so a jog started while the Job view is unfocused still stops
                // Re-register live when the shortcut is changed in the Key Mappings editor.
                AppConfig.ConsoleShortcutChanged += registerConsoleShortcut;
                consoleShortcutHooked = true;
            }

            menuOpenConsole.InputGestureText = consoleKey == Key.None ? string.Empty : ShortcutKey.ToDisplayString(consoleKey, consoleModifiers);
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (consoleKey != Key.None && e.Key == consoleKey && Keyboard.Modifiers == consoleModifiers)
            {
                openConsole();   // openConsole() toggles: shows when hidden/new, hides when visible
                e.Handled = true;
                return;
            }

            // Keep keyboard jogging alive on the Job page even when focus has drifted out of the Job view
            // (a flyout, side panel or the menu). The Job view only sees keys through its own OnPreviewKeyDown,
            // which requires focus inside its tree; this window-level preview always fires, so forward jog keys
            // when the Job view is the current view but is not focused. Skip if focus is in any text input
            // (typing) - the Job view's own handler covers the focused case, including its MDI/DRO gates.
            if (UIViewModel?.CurrentView is JobView jobView && !jobView.IsKeyboardFocusWithin
                 && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase))
                e.Handled = jobView.ProcessKeyPreview(e);
        }

        private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            // Mirror the key-down forwarding so a continuous jog started while the Job view was unfocused still
            // receives its key-up and stops. No text-input guard here: an in-progress jog must always cancel.
            if (UIViewModel?.CurrentView is JobView jobView && !jobView.IsKeyboardFocusWithin)
                e.Handled = jobView.ProcessKeyPreview(e);
        }

        private static ICNCView getView(TabItem tab)
        {
            ICNCView view = null;

            foreach (UserControl uc in UIUtils.FindLogicalChildren<UserControl>(tab))
            {
                if (uc is ICNCView) {
                    view = (ICNCView)uc;
                    break;
                }
            }

            return view;
        }
    }
}
