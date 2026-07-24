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
using System.Windows.Media;
using System.Windows.Media.Animation;
using CNC.Core;
using CNC.Controls;
using CNC.Converters;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
#if ADD_CAMERA
using CNC.Controls.Camera;
#endif

namespace GCode_Sender
{

    public partial class MainWindow : Window
    {
        // Legacy fallback for local/dev builds (BuildInfo.Version == "dev", not embedded by CI).
        private const string legacyVersion = "2.27";
        public static string Version { get { return BuildInfo.Version == "dev" ? legacyVersion : BuildInfo.Version; } }
        public static MainWindow ui = null;
        public static CNC.Controls.Viewer.Viewer GCodeViewer = null;
        public static UIViewModel UIViewModel { get; } = new UIViewModel();

        private bool saveWinSize = false;

        public MainWindow()
        {
            CNC.Core.Resources.Path = AppDomain.CurrentDomain.BaseDirectory;

            // A -testserver launch must never take OS foreground focus, at any point - not just skip our
            // own Activate() call. Testing showed the Opacity 0->1 transition in RevealMainWindow (needed
            // for whole-window RenderTargetBitmap screenshots - see there) is ITSELF enough to make Windows
            // grant the window initial foreground activation, independent of ShowInTaskbar/Activate()/window
            // position. The only reliable fix is WS_EX_NOACTIVATE on the raw window style, applied as soon
            // as the HWND exists (SourceInitialized) - a documented, first-party Win32 flag (not an
            // undocumented API) that tells Windows this window is never activatable, full stop.
            if (App.TestServerPort >= 0)
                SourceInitialized += (s, e) => SetNoActivate();

            InitializeComponent();

            ui = this;

            // Job tab Run dropdown "Simulate" mode (JobControl.xaml.cs) - CNC Controls can't reference
            // MainWindow directly (see SimulatorManager's own comment on these hooks), so wire them here,
            // same pattern as CameraConfig.DeviceEnumerator below.
            SimulatorManager.SwitchToSimulatorForRun = SwitchToSimulatorForRun;
            SimulatorManager.RestoreConnectionAfterSimulate = RestoreConnectionAfterSimulate;
//            GCodeViewer = viewer;
            Title = string.Format(Title, Version);
            BaseWindowTitle = Title;

            // Register the Lathe wizard profile sections before the document is read (AppConfig itself
            // can't reference CNC.Controls.Lathe types - see LatheProfileSections' own comment).
            CNC.Controls.Lathe.LatheProfileSections.RegisterSections();

            // Load config synchronously now - before any control Loaded handler (e.g. JogControl)
            // reads AppConfig.Settings.Base. Only the connection is deferred (see CompleteStartup).
            int cfg = AppConfig.Settings.LoadConfig(Title);
            if (cfg != 0)
            {
                Environment.Exit(cfg);
                return;
            }

#if ADD_CAMERA
            // Camera-device bind (menu overhaul): let the App-settings Camera picker enumerate local webcams
            // (CNC.Controls owns the picker but can't reference this assembly's AForge), and drive the Camera
            // menu's visibility off the bound device - re-checked whenever the binding changes in Settings.
            CameraConfig.DeviceEnumerator = Camera.EnumerateDevices;
            if (AppConfig.Settings.Camera != null)
                AppConfig.Settings.Camera.PropertyChanged += (s, e) => {
                    if (e.PropertyName == nameof(CameraConfig.SelectedCamera))
                        UpdateCameraMenu();
                };
#endif

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

            // DPI diagnostics (-debuglog): dump the scale WPF actually resolves at startup + on any live change,
            // to see whether "tiny UI" is a DPI/scale problem vs a display-resolution problem.
            Loaded += LogDpiDiagnostics;

            // Silent startup update check - appends "(update available)" to the title bar (via
            // BaseWindowTitle, the canonical source other code derives Title from on file load/close)
            // rather than popping a dialog. Fire-and-forget: never blocks startup, and GetNewerVersionSilently
            // swallows every failure mode itself, so there is nothing to await/observe here.
            Loaded += async (s, e) =>
            {
                string newer = await GetNewerVersionSilently();
                if (newer != null)
                {
                    BaseWindowTitle = BaseWindowTitle + " (update available)";
                    Title = BaseWindowTitle;
                }
            };
            DpiChanged += (s, e) => CNC.Core.DebugLog.Write("dpi",
                string.Format("DpiChanged: old PixelsPerDip={0} -> new={1}", e.OldDpi.PixelsPerDip, e.NewDpi.PixelsPerDip));

            // Keep sidebarCanvas's top offset in sync with the main-nav tab strip's ACTUAL rendered height,
            // measured from the real TabPanel part inside tabMode's template, instead of a hardcoded pixel
            // guess in XAML. History: that guess (originally 34) was tuned against the tab strip's old
            // MinHeight=32; the Apple-HIG pill redesign raised MinHeight to 34 and two successive manual
            // re-guesses (36, then 38) both turned out still wrong when checked live - a magic number here
            // is fundamentally fragile since it has to be re-derived by hand every time the tab template's
            // rendered size changes for ANY reason (font, DPI, theme). Measuring the real part instead
            // means it's correct by construction and self-corrects if the template changes again.
            Loaded += (s, e) =>
            {
                var headerPanel = FindVisualChild<System.Windows.Controls.Primitives.TabPanel>(tabMode);
                if (headerPanel == null)
                    return;
                void SyncSidebarTop() => sidebarCanvas.Margin = new Thickness(0, headerPanel.ActualHeight, 0, 0);
                headerPanel.SizeChanged += (s2, e2) => SyncSidebarTop();
                SyncSidebarTop();
            };

            if (DataContext is GrblViewModel viewModel)
                CNC.Core.Grbl.GrblViewModel = viewModel;

            // The run control is now fixed at the main-window bottom (always visible on every tab), so the
            // floating run-control panel is retired - leave MacroProcessor.RunControlPanel unset (its callers
            // use ?.Invoke, so they no-op). Feed Hold / Stop are always reachable from the fixed bar.

            // Every producer (Load/Load Folder and each wizard) owns its ProgramView and connects it; host the
            // connected view in the overlay with its own title bar. Wizards (AutoShow) pop it open as Generate
            // feedback; the loaded job connects quietly. On disconnect, revert to the view beneath (job, or none).
            CNC.Controls.ProgramView.ActiveChanged += OnOverlayActiveChanged;

            // When the active view collapses to its 3-line run view, size the overlay popup to content (top-
            // aligned) instead of stretching full height, so the popup itself shrinks - not just the grid.
            CNC.Controls.ProgramView.CompactChanged += ApplyOverlayCompact;

            // Every streamed macro/wizard run goes here: stream the generated program through the flow-controlled
            // streamer, in its own ProgramView, without leaving the current tab or touching the loaded job.
            CNC.Controls.MacroProcessor.RunStreamedJobInPlace = (m, name, code, isFinalBurst, onDone) => RunStreamedJobInPlace(m, name, code, isFinalBurst, onDone);

            // Matches App.xaml.cs's skip of the single-instance CHECK for a -testserver launch: this
            // instance must not become a pipe listener either, or a later normal launch would silently
            // forward its file-open/activate request into this hidden test instance instead of starting
            // its own window. A -testserver instance is deliberately NOT part of single-instance semantics.
            if (App.TestServerPort < 0)
            {
                new PipeServer(App.Current?.Dispatcher ?? Dispatcher);
                PipeServer.FileTransfer += Pipe_FileTransfer;
                PipeServer.ActivateRequested += BringToForeground;
            }
            AttachBasePropertyChangedHandler();
            WireBarOverlays();
        }

        // ---- startup splash: the window is created invisible and revealed once startup has settled ----
        private SplashWindow _splash = null;
        private bool _revealed = false;

        public void AttachSplash(SplashWindow splash)
        {
            _splash = splash;
        }

        private void SetSplashStatus(string status)
        {
            _splash?.SetStatus(status);
        }

        // Reveal the (until-now invisible) main window and dismiss the splash. Idempotent: wired to every
        // startup exit (connected+ready, incomplete-setup, disconnected, timeout) so it fires exactly once.
        private void RevealMainWindow()
        {
            if (_revealed)
                return;
            _revealed = true;

            // Opacity ALWAYS goes to 1, even for -testserver: WpfUiTestServer's /screenshot renders the
            // window's own Visual via RenderTargetBitmap (an in-memory render, not a screen grab), and that
            // honours the Window's own Opacity - leaving it at 0 (the startup value) would make every
            // whole-window screenshot come back blank, breaking automated visual verification.
            Opacity = 1d;

            if (App.TestServerPort < 0)
            {
                ShowInTaskbar = true;
                Activate();
            }
            else
            {
                // Parked far outside any real display so the operator never sees it, on top of the
                // WS_EX_NOACTIVATE style already applied in the constructor (the actual focus-steal fix -
                // see there). RenderTargetBitmap doesn't care where a window physically sits, so this
                // doesn't affect screenshots at all.
                Left = -32000;
                Top = -32000;
                ShowInTaskbar = false;
            }

            if (_splash != null)
            {
                _splash.Close();
                _splash = null;
            }

            // -message=text (see App.StartupMessage) - deferred one dispatcher tick so the window has actually
            // painted before the popup steals focus. Skipped for -testserver: no one is watching that window.
            if (App.TestServerPort < 0 && !string.IsNullOrEmpty(App.StartupMessage))
            {
                string msg = App.StartupMessage;
                Dispatcher.BeginInvoke(new System.Action(() =>
                    CNC.Core.AppDialogs.Show(this, msg, "Startup message", MessageBoxButton.OK, MessageBoxImage.None)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        // ---- bottom-bar overlays: program view (toggle) and/or console log (on command-box focus) ----
        private bool _programOverlay = false, _consoleOverlay = false;

        private void WireBarOverlays()
        {
            // Console log overlay follows the command box: appears when it gets focus, dismisses once focus
            // leaves BOTH the box and the log (so you can scroll/select/copy from the log), or on Esc.
            mdiControl.GotKeyboardFocus += (s, e) => { _consoleOverlay = true; UpdateOverlay(); };
            mdiControl.LostKeyboardFocus += (s, e) => ScheduleConsoleOverlayCheck();
            overlayConsole.LostKeyboardFocus += (s, e) => ScheduleConsoleOverlayCheck();
            mdiControl.PreviewKeyDown += ConsoleOverlay_Key;
            overlayConsole.PreviewKeyDown += ConsoleOverlay_Key;

            // A real file/folder load creates the job's own program view and connects it (ProgramView refactor):
            // the overlay hosts it like any tool's view, and Cycle Start streams the freshly loaded file.
            if (DataContext is GrblViewModel gvm)
                gvm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(GrblViewModel.FileName))
                        OnJobFileChanged((DataContext as GrblViewModel)?.FileName);
                    else if (e.PropertyName == nameof(GrblViewModel.GrblState))
                        UpdateConnectionGatedTabs();   // enable operational tabs on connect, disable on disconnect
                    else if (e.PropertyName == nameof(GrblViewModel.IsJobRunning))
                        OnJobRunningChanged((s as GrblViewModel)?.IsJobRunning == true);
                    else if (e.PropertyName == nameof(GrblViewModel.Message))
                        FlashMessage((s as GrblViewModel)?.IsMessageError == true);
                    else if (e.PropertyName == nameof(GrblViewModel.ConnectionTarget))
                        UpdateConnectMenuHeader();   // keep the top-level Connect/Reconnect label current
                };

            // Status message permanently shown at double size (was a 10s enlarge-then-shrink animation that
            // shifted the whole window layout up/down on every message - distracting; see FlashMessage for
            // the replacement notice mechanism). Doubled once here rather than hardcoding a size, so it
            // always tracks whatever the ambient/theme default actually is.
            lblMessage.FontSize *= 2.0;

            // Demo-video timelapse toggle is a capture-only affordance: show it only when the demo
            // marker facility is armed (-demomarker), hidden in normal use.
            btnTimeLapse.Visibility = CNC.Core.DemoMarker.Enabled ? Visibility.Visible : Visibility.Collapsed;

            // Run-bar "All" recording toggle - same gating as Timelapse (see above). Resyncs from
            // ObsBridge just like RtspCamerasControl's own "All" row, so either control reflects the
            // other's state regardless of which one (or a keyboard shortcut) triggered a change.
            btnAllRecord.Visibility = btnTimeLapse.Visibility;
            CNC.Core.ObsBridge.CamerasChanged += AllRecord_Resync;
            AllRecord_Resync();
        }

        private bool _suppressAllRecordToggled;

        private void AllRecord_Resync()
        {
            _suppressAllRecordToggled = true;
            bool allRecording = true;
            for (int i = 0; i < CNC.Core.ObsBridge.Cameras.Length; i++)
                allRecording &= CNC.Core.ObsBridge.IsCameraRecording(i);
            btnAllRecord.IsChecked = allRecording;
            _suppressAllRecordToggled = false;
        }

        private void AllRecord_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressAllRecordToggled)
                return;
            CNC.Controls.RtspCamerasControl.ToggleAll(this, btnAllRecord.IsChecked == true);
        }

        // ---- status-bar message flash ----
        // The status line at the bottom is short and easy to miss. Used to briefly enlarge its font (10s) to
        // draw the eye, but that shifted the whole window layout up/down on every message - distracting. The
        // font is now permanently large (see the constructor) and a new message instead flashes
        // msgFlashBorder's background light green (normal) or light red (GrblViewModel.IsMessageError) for
        // 5s, fading back to transparent - same "notice me" job, no layout shift.
        private static readonly Color FlashColorNormal = Color.FromRgb(0xC8, 0xE6, 0xC9);   // light green
        private static readonly Color FlashColorError = Color.FromRgb(0xFF, 0xCD, 0xD2);    // light red

        private void FlashMessage(bool isError)
        {
            if (msgFlashBorder == null || string.IsNullOrWhiteSpace((DataContext as GrblViewModel)?.Message))
                return;

            var color = isError ? FlashColorError : FlashColorNormal;
            var brush = new SolidColorBrush(color);
            msgFlashBorder.Background = brush;

            var anim = new ColorAnimation
            {
                From = color,
                To = Colors.Transparent,
                Duration = TimeSpan.FromSeconds(5),
                FillBehavior = FillBehavior.Stop   // Completed below sets the real final value
            };
            anim.Completed += (s, e) => msgFlashBorder.Background = Brushes.Transparent;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        // ---- -testserver: make the window permanently non-activatable (see the constructor comment) ----
        // GetWindowLongPtr/SetWindowLongPtr are C preprocessor macros, not real DLL exports. On 64-bit
        // Windows the real export is GetWindowLongPtrW; this app runs 32-bit (AnyCPU without an explicit
        // 64-bit preference still lands WOW64 here - confirmed via IsWow64Process), where GetWindowLongPtrW
        // doesn't exist at all - only the older GetWindowLongW (32-bit LONG, not a pointer-sized value)
        // does. Standard cross-bitness pattern: pick the real export by IntPtr.Size at call time.
        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }
        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private void SetNoActivate()
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(exStyle.ToInt64() | WS_EX_NOACTIVATE));
        }

        // ---- DPI diagnostics ----
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
        private static string ProcessDpiAwareness()
        {
            try
            {
                int a = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
                string[] n = { "UNAWARE", "SYSTEM", "PER-MONITOR" };
                return (a >= 0 && a < n.Length) ? n[a] : ("?(" + a + ")");
            }
            catch { return "?"; }
        }

        private void LogDpiDiagnostics(object sender, RoutedEventArgs e)
        {
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                var src = PresentationSource.FromVisual(this);
                double m11 = src != null ? src.CompositionTarget.TransformToDevice.M11 : 0;
                double m22 = src != null ? src.CompositionTarget.TransformToDevice.M22 : 0;
                CNC.Core.DebugLog.Write("dpi", string.Format(
                    "MainWindow Loaded: awareness={0} DpiScaleX={1} DpiScaleY={2} PixelsPerDip={3} TransformToDevice.M11={4} M22={5} | "
                    + "PrimaryScreen(logical)={6}x{7} WorkArea={8}x{9} | Window Actual={10}x{11} Set={12}x{13} State={14} | savedWin={15}x{16}",
                    ProcessDpiAwareness(), dpi.DpiScaleX, dpi.DpiScaleY, dpi.PixelsPerDip, m11, m22,
                    SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight,
                    SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height,
                    ActualWidth, ActualHeight, Width, Height, WindowState,
                    AppConfig.Settings.Base.WindowWidth, AppConfig.Settings.Base.WindowHeight));
            }
            catch (Exception ex) { CNC.Core.DebugLog.Write("dpi", "diag error: " + ex.Message); }
        }

        // Depth-first search for the first visual-tree descendant of the given type (e.g. the TabPanel
        // part inside a TabControl's default template). Returns null if none is found.
        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                var found = FindVisualChild<T>(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        // ---- Demo-video timelapse window marking (see docs/demo-videos/README.md) ----
        // The operator flips btnTimeLapse at the moment they switch the camera to/from timelapse; each flip
        // writes TIMELAPSE_ON/OFF to the demo marker file so the compositor knows the exact segment to speed
        // up. RUN_START/RUN_END are only fallbacks: auto-ON a few minutes in if the operator never flips it,
        // and a forced auto-OFF at job end. No feed-based runtime estimate exists in the app, so the auto-ON
        // delay is a fixed guess matching the "hands-on for the first few minutes" workflow.
        private const double TimeLapseAutoOnMinutes = 5.0;
        private DispatcherTimer _timelapseAutoTimer;
        private bool _timelapseSuppressMark;

        private void SetTimeLapseChecked(bool value, bool suppressMark)
        {
            _timelapseSuppressMark = suppressMark;
            btnTimeLapse.IsChecked = value;   // fires TimeLapse_Toggled unless the value is unchanged
            _timelapseSuppressMark = false;
        }

        private void TimeLapse_Toggled(object sender, RoutedEventArgs e)
        {
            if (_timelapseSuppressMark)
                return;
            bool on = btnTimeLapse.IsChecked == true;
            CNC.Core.DemoMarker.Mark(on ? "TIMELAPSE_ON" : "TIMELAPSE_OFF");
            if (on)
                StopTimeLapseAutoTimer();   // engaged (by hand or fallback) - no need to auto-fire
        }

        private void OnJobRunningChanged(bool running)
        {
            if (!CNC.Core.DemoMarker.Enabled)
                return;

            if (running)   // RUN_START: fresh job - clear the toggle without marking, arm the fallback
            {
                SetTimeLapseChecked(false, suppressMark: true);
                StartTimeLapseAutoTimer();
            }
            else           // RUN_END: stop the fallback and force timelapse off (marks) if still on
            {
                StopTimeLapseAutoTimer();
                if (btnTimeLapse.IsChecked == true)
                    SetTimeLapseChecked(false, suppressMark: false);
            }
        }

        private void StartTimeLapseAutoTimer()
        {
            StopTimeLapseAutoTimer();
            _timelapseAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(TimeLapseAutoOnMinutes) };
            _timelapseAutoTimer.Tick += TimeLapseAutoTimer_Tick;
            _timelapseAutoTimer.Start();
        }

        private void StopTimeLapseAutoTimer()
        {
            if (_timelapseAutoTimer != null)
            {
                _timelapseAutoTimer.Stop();
                _timelapseAutoTimer.Tick -= TimeLapseAutoTimer_Tick;
                _timelapseAutoTimer = null;
            }
        }

        private void TimeLapseAutoTimer_Tick(object sender, EventArgs e)
        {
            StopTimeLapseAutoTimer();   // one-shot
            if ((DataContext as GrblViewModel)?.IsJobRunning == true && btnTimeLapse.IsChecked != true)
                SetTimeLapseChecked(true, suppressMark: false);   // fallback engage - marks TIMELAPSE_ON
        }

        private void ConsoleOverlay_Key(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && _consoleOverlay)
            {
                _consoleOverlay = false;
                UpdateOverlay();
                e.Handled = true;
            }
        }

        private void ScheduleConsoleOverlayCheck()
        {
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (!mdiControl.IsKeyboardFocusWithin && !overlayConsole.IsKeyboardFocusWithin)
                {
                    _consoleOverlay = false;
                    UpdateOverlay();
                }
            }), DispatcherPriority.Input);
        }

        private void ProgramView_Toggled(object sender, RoutedEventArgs e)
        {
            _programOverlay = btnProgramView.IsChecked == true;
            UpdateOverlay();
        }

        // Program view and console log share one overlay over the work area, side by side. Each column is
        // shown (and given equal width) only when its trigger is active; the host collapses when neither is.
        private void UpdateOverlay()
        {
            // Program view (right) and console log (above the command box) are independent overlays.
            overlayProgram.Visibility = _programOverlay ? Visibility.Visible : Visibility.Collapsed;
            overlayConsole.Visibility = _consoleOverlay ? Visibility.Visible : Visibility.Collapsed;
        }


        // The loaded job's own program view (ProgramView refactor): Load File / Load Folder create+connect it so
        // the overlay hosts the job uniformly, alongside the wizards - no more "the fallback == the job" special
        // case. It renders via SetProgram(null) (the null == loaded-job convention) so it keeps the live streamed
        // collection, the mint source highlight and folder outline grouping; AutoShow is off so a load doesn't
        // pop the overlay open.
        private CNC.Controls.ProgramView jobProgramView;

        // A plain streamed macro (not a tool that owns its own view) runs in this dedicated view, so it shows in
        // its own overlay with live markers and never overwrites the loaded job. Reused across macro runs.
        private CNC.Controls.ProgramView _macroRunView;
        private System.Windows.Threading.DispatcherTimer _macroRunViewTimer;
        private void EnsureMacroRunView()
        {
            if (_macroRunView == null)
                _macroRunView = new CNC.Controls.ProgramView();
            if (_macroRunViewTimer == null)
            {
                // The run view has no tab to close it and holds nothing useful once the run is done, so auto-
                // dismiss it 20 s after it stops streaming (a new run/burst re-uses it and cancels the timer).
                // On fire, disconnect it - the overlay reverts to the view beneath (the loaded job, or none).
                _macroRunViewTimer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(20) };
                _macroRunViewTimer.Tick += (s, e) => { _macroRunViewTimer.Stop(); _macroRunView.Disconnect(); };
            }
        }

        private void OnJobFileChanged(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                CNC.Core.ObsBridge.StopRecording();   // file closed: stop the demo recording (safety net)
                jobProgramView?.Disconnect();   // file closed: drop the job view (overlay reverts to the empty state)
                return;
            }
            CNC.Core.ObsBridge.StartRecording();   // program loaded: begin the demo recording (no-op unless armed)
            if (jobProgramView == null)
            {
                jobProgramView = new CNC.Controls.ProgramView { AutoShow = false, IsLoadedJob = true };
                CNC.Controls.ProgramView.LoadedJob = jobProgramView;   // the only ProgramView Stock/DeclaredStock are valid on
            }
            jobProgramView.Title = System.IO.Path.GetFileName(fileName.TrimEnd('\\', '/'));
            jobProgramView.SetProgram(null);   // null == the loaded job (GCode.File.Data) - the streamed collection
            jobProgramView.Connect();
        }

        // A program is just a list of G-code blocks; build one from generated text so a wizard program renders
        // in the same program view as a file/folder (no raw-text special case).
        // Host the connected ProgramView in the popup ONLY when it's genuinely transient (AutoShow - a wizard's
        // Generate output, or a plain macro run). The loaded job's own view (jobProgramView, AutoShow=false)
        // already has a persistent home in the docked Job-tab panel (ProgramPanel), so this popup must never
        // show it a second time - the "Program" button is disabled whenever there's nothing showable here.
        private void OnOverlayActiveChanged()
        {
            var active = CNC.Controls.ProgramView.Active;
            bool showable = active != null && active.AutoShow;

            overlayActiveHost.Content = showable ? active : null;
            btnProgramView.IsEnabled = showable;
            btnProgramView.IsChecked = showable;   // AutoShow pops the popup open as Generate feedback
            _programOverlay = showable;

            ApplyOverlayCompact();
            UpdateOverlay();
        }

        // Compact (3-line) run view: size the overlay to its content and drop it to the BOTTOM of the tab
        // content area (the title bar slides down so the 3 lines sit at the bottom, flush above the run bar);
        // stretch full height otherwise. Driven by ProgramView.CompactChanged and the active view.
        private void ApplyOverlayCompact()
        {
            bool compact = CNC.Controls.ProgramView.Active?.Compact == true;
            overlayProgram.VerticalAlignment = compact ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        }

        // The single fixed run control + MDI at the main-window bottom (Phase 2c). JobView and other tabs
        // reach them here instead of hosting their own.
        public CNC.Controls.JobControl RunControl { get { return runControl; } }
        public CNC.Controls.MDIControl MdiControl { get { return mdiControl; } }

        public string BaseWindowTitle { get; set; }

        public string WindowTitle
        {
            set
            {
                ui.Title = BaseWindowTitle + (string.IsNullOrEmpty(value) ? "" : " - " + value);
                // File open/save/close/transform state now lives on the program view + its right-click menu
                // (menu overhaul), which self-gate on GCode.File - no menu-bar items to toggle here.
            }
        }

        public bool JobRunning
        {
            get { return menuMain.IsEnabled != true; }
            set {
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

            // Lathe Wizards is never added to the tab bar at all while lathe mode is off (SetTabPresent
            // proactively omits it, rather than adding-then-pruning like other IAvailabilityGated views) -
            // so StretchTabControl.PruneUnavailable never sees it to report it. Note it explicitly here so
            // "Edit Main Page" still lists it as unavailable, with the same reason LatheWizardsView itself
            // would give if it were ever constructed to ask.
            if (!AppConfig.Settings.Base.Lathe.LatheEnabled)
                ComponentAvailability.Note(new[] { new UnavailableComponent {
                    Label = CNC.Controls.Lathe.LatheWizardsView.TabDisplayLabel,
                    Reason = CNC.Controls.Lathe.LatheWizardsView.NotEnabledReason } });

            // Lathe mode is no longer a manual ioSender-side toggle - the controller's own NEWOPT reply
            // reports a "LATHE" capability flag when its Mode of operation setting (Settings > Grbl) is
            // set to Lathe, and Grbl.cs's NEWOPT parsing pushes that through GrblViewModel.LatheModeEnabled
            // (firing PropertyChanged) every time it's (re-)parsed. Persist whatever the controller says so
            // NEXT startup's BuildTabs() above (which runs before a connection exists) shows/hides the
            // Lathe Wizards tab correctly without waiting on a fresh connection - same one-restart-to-take-
            // effect behavior the old manual checkbox had, just detected automatically instead.
            ((GrblViewModel)DataContext).PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(GrblViewModel.LatheModeEnabled) &&
                    AppConfig.Settings.Base.Lathe.LatheEnabled != GrblInfo.LatheModeEnabled)
                    AppConfig.Settings.Base.Lathe.LatheEnabled = GrblInfo.LatheModeEnabled;
            };

            // Config already loaded in the constructor; here we only open the connection (deferred so
            // the main window paints first). res == 2: user cancelled / no connection - stay open
            // (disconnected) so the user can connect later via the Connect menu item.
            SetSplashStatus("Connecting...");
            int res = AppConfig.Settings.OpenConnection(Title, (GrblViewModel)DataContext, App.Current.Dispatcher);
            bool connected = res == 0;

            UpdateSimulatorTint();   // pale-yellow background when the startup target is the simulator

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

            // Set the initial connection-gated tab state (Start Job etc. disabled until connect).
            UpdateConnectionGatedTabs();

            // Land on the Job (GRBL) tab. It is always enabled, and its Activate runs the controller handshake
            // (Controller.Restart -> OnBooted -> InitSystem -> $I: EXPR/ENUMS/ATC/WCSROT) and manages status
            // polling. Start Job is first in the tab strip but must NOT be the landing: its view does not boot the
            // controller, and activating Job separately just to boot corrupts the poller/init state (dead status
            // feedback, InitSystem re-firing mid-job). The operator clicks Start Job when they want it - by then
            // the controller is booted and its capabilities are known.
            var jobTab = getTab(ViewType.GRBL);
            int landing = jobTab != null ? tabMode.Items.IndexOf(jobTab) : 0;
            UIViewModel.CurrentView = getView((TabItem)tabMode.Items[tabMode.SelectedIndex = landing]);
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
            registerTabShortcuts();

            // UI-zoom shortcuts (assignable in Keyboard & Controller, "UI zoom" group) - seed real defaults
            // once, then register the actual in/out behaviour; dispatched globally like console/tab shortcuts
            // since ProcessKeypress is only ever called from specific views (Job/Probing/Jog flyout), never at
            // the window level, and zoom needs to work regardless of which tab is showing.
            ActionKeyBinder.SeedDefaults();
            ActionKeyBinder.Register("UiScaleUp", k => { AppConfig.Settings.Base.UiScale += 0.05; return true; });
            ActionKeyBinder.Register("UiScaleDown", k => { AppConfig.Settings.Base.UiScale -= 0.05; return true; });

            // Demo-shoot RTSP camera hotkeys - route through ObsBridge.SetCameraRecording, the same entry
            // point the RtspCamerasControl panel's toggles use, so either can drive the other's state.
            ActionKeyBinder.Register("ObsCamAStart", k => { CNC.Core.ObsBridge.SetCameraRecording(0, true); return true; });
            ActionKeyBinder.Register("ObsCamAStop", k => { CNC.Core.ObsBridge.SetCameraRecording(0, false); return true; });
            ActionKeyBinder.Register("ObsCamBStart", k => { CNC.Core.ObsBridge.SetCameraRecording(1, true); return true; });
            ActionKeyBinder.Register("ObsCamBStop", k => { CNC.Core.ObsBridge.SetCameraRecording(1, false); return true; });
            ActionKeyBinder.Register("ObsAppStart", k => { CNC.Core.ObsBridge.SetCameraRecording(2, true); return true; });
            ActionKeyBinder.Register("ObsAppStop", k => { CNC.Core.ObsBridge.SetCameraRecording(2, false); return true; });

#if DEBUG
            ActionKeyBinder.Register("Screenshot", Screenshot_Action);
#endif

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
            // The window is still invisible here - ForceMachineSetupIfNeeded reveals it (selecting the Machine
            // Setup tab first if any step is incomplete) once the controller has reported in.
            if (connected)
            {
                SetSplashStatus("Validating machine...");
                ForceMachineSetupIfNeeded();
            }
            else
                RevealMainWindow();   // no controller / cancelled: show the normal UI disconnected

            // Safety net: never leave the user staring at the splash if the controller never reports in.
            var revealSafety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            revealSafety.Tick += (s, e) => { revealSafety.Stop(); RevealMainWindow(); };
            revealSafety.Start();

            // Flag-gated UI test server (‑testserver / IOSENDER_TESTSERVER): the tabs/views are built and the
            // visual tree is realized by now, so an external script can drive the UI by x:Uid. Off by default.
            if (App.TestServerPort >= 0)
                WpfUiTestServer.UiTestServer.Start(this,
                    App.TestServerPort == 0 ? WpfUiTestServer.UiTestServer.DefaultPort : App.TestServerPort,
                    new GrblStatusProvider(this),
                    msg => CNC.Core.DebugLog.Write("testserver", msg));
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
                {
                    SetSplashStatus(string.IsNullOrEmpty(GrblInfo.Version) ? "Reading controller..." : "Reading settings...");
                    return;
                }
                timer.Stop();
                if (!ready)
                {
                    RevealMainWindow();   // controller never reported in: reveal the normal UI anyway
                    return;
                }
                // Skip the gate when connected to the simulator - it's not a real machine to set up.
                bool sim = Comms.com != null && Comms.com.IsOpen && AppConfig.Settings.Base.StartSimulator;
                int step = CNC.Controls.MachineSetupWizard.FirstIncompleteStep();
                if (step == 0 || sim)
                {
                    RevealMainWindow();   // setup complete (or simulator): straight to the normal UI
                    return;
                }
                // TEMP DIAGNOSTIC (2026-07-19) - ATC macro gate false-positive investigation.
                ConsoleLog.Write(string.Format("[MainWindow] ForceMachineSetupIfNeeded: initial check step={0}, re-checking after 1200ms settle...", step));

                // Confirm after a short settle: a connect/reset can momentarily yield a stale read - in
                // particular the macro check (step 6) does a synchronous filesystem listing that comes back
                // empty right after a reset, falsely flagging a step. Re-check once things have settled and
                // only gate if still incomplete (avoids the "prompt appears but every tab is green" race).
                SetSplashStatus("Validating setup...");
                var confirm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                confirm.Tick += (s2, e2) =>
                {
                    confirm.Stop();
                    if (GrblSettings.IsLoaded)
                    {
                        int step2 = CNC.Controls.MachineSetupWizard.FirstIncompleteStep();
                        // TEMP DIAGNOSTIC (2026-07-19) - see above. step2==0 means the settle re-check
                        // caught a transient race (as designed); step2!=0 means it's still incomplete after
                        // settling, i.e. NOT just the known post-reset stale-listing race.
                        ConsoleLog.Write(string.Format("[MainWindow] ForceMachineSetupIfNeeded: settled re-check step={0} (was {1})", step2, step));
                        if (step2 != 0)
                            ShowMachineSetup(step2);   // select the Machine Setup tab before revealing
                    }
                    RevealMainWindow();
                };
                confirm.Start();
            };
            timer.Start();
        }

        // Stream a tool's generated program through the flow-controlled streamer WITHOUT leaving the current tab
        // (the bottom run bar drives Feed Hold/Stop on any tab) and WITHOUT touching the loaded job: the program
        // is built as a standalone transient IProgramSource and the streamer is pointed at it for the run, then
        // reset to the job (GCode.File) when it finishes. So e.g. Load Stock's probe program never disturbs the job.
        private void RunStreamedJobInPlace(GrblViewModel m, string name, string[] code, bool isFinalBurst, System.Action onDone)
        {
            if (code == null || code.Length == 0)
            {
                onDone?.Invoke();
                return;
            }

            var prog = new CNC.Controls.GCode(m);                        // transient - does not mutate the job/Model
            prog.AddBlock(name, CNC.Core.Action.New);
            for (int i = 0; i < code.Length - 1; i++)
                prog.AddBlock(code[i], CNC.Core.Action.Add);
            prog.AddBlock(code[code.Length - 1], CNC.Core.Action.End);

            RunControl.Source = prog;          // stream this program instead of the loaded job
            // Mark the ACTUAL streamed program in a ProgramView so the live per-line markers ("@"/"ok") and scroll
            // track the run. A tool that owns its view (a wizard) marks its own; a plain macro - no tool view, or
            // only the loaded-job view is active - gets a dedicated run view, so a run never overwrites the job.
            var connected = CNC.Controls.ProgramView.Active;
            if (connected != null && connected != jobProgramView)
                connected.SetProgram(prog.Data);
            else
            {
                EnsureMacroRunView();
                _macroRunViewTimer.Stop();     // a run is (re)using the view - cancel any pending auto-dismiss
                _macroRunView.Title = string.IsNullOrEmpty(name) ? "Program" : name;
                _macroRunView.SetProgram(prog.Data);
                _macroRunView.Connect();
            }
            RestoreSourceOnEnd(m, prog, isFinalBurst, onDone);   // revert to the job source when THIS burst ends, then signal completion

            // Defer CycleStart to a clean dispatcher cycle. Starting it synchronously
            // from inside MacroProcessor.Run's streaming flush re-enters the dispatcher (CycleStart pumps events
            // in a DoEvents wait), which corrupts the run's state machine so it never reaches its terminal state -
            // the UI then stays "job running" (unresponsive) until Stop. Deferring runs it after Run() unwinds.
            //
            // Background is the lowest priority above idle, so on a macro that streams several short bursts back
            // to back (e.g. Start Job's park move, or Stepper Calibration's per-corner probes) this can be starved
            // behind a stream of Normal-priority work (status-report handling, THIS burst's own onDone dispatch)
            // long enough that it doesn't run until AFTER the burst it belongs to has already finished and
            // RestoreSourceOnEnd already cleared RunControl.Source. Firing CycleStart at that point still starts
            // real motion (StreamingState -> Send, GrblState -> Run) but with no RestoreSourceOnEnd handler left
            // subscribed to catch it (it already unsubscribed at the real terminal state) - MacroProcessor's very
            // next (WAITIDLE) then walks straight into that untracked stream and aborts ("controller did not
            // return to idle"), even though the real burst completed cleanly. Confirmed via DebugLog("macro")
            // tracing 2026-07-21: StreamProgram's wait loop exited with StreamingState already back to Send/Run,
            // with no corresponding RestoreSourceOnEnd trace for that transition - i.e. nobody's handler was even
            // watching it. Guard: only actually start if this burst's transient is still the active source: if
            // RestoreSourceOnEnd already reverted it, this CycleStart is stale and must be skipped.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new System.Action(() =>
                {
                    if (RunControl.Source == prog)
                        RunControl.Run(0, false);   // stream from the bottom run bar - no tab change, don't re-enter ActiveRun
                    else
                        CNC.Core.DebugLog.Write("macro", "RunStreamedJobInPlace: skipped stale deferred CycleStart - burst already finished");
                }));
        }

        // When the current run finishes, revert the streamer to the loaded-job source. Mirrors RestoreTabOnJobEnd:
        // arm on the first running state, fire on the next terminal one, then unsubscribe. onDone (may be null)
        // is MacroProcessor's completion signal for THIS burst - see Flush's 'wait' parameter.
        private void RestoreSourceOnEnd(GrblViewModel m, CNC.Controls.GCode prog, bool isFinalBurst, System.Action onDone)
        {
            bool started = false;
            System.ComponentModel.PropertyChangedEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.PropertyName != nameof(GrblViewModel.StreamingState))
                    return;
                var st = m.StreamingState;
                CNC.Core.DebugLog.Write("macro", string.Format("RestoreSourceOnEnd: StreamingState -> {0} (started={1}, GrblState={2})",
                    st, started, m.GrblState.State));
                if (st == StreamingState.Send || st == StreamingState.SendMDI)
                    started = true;
                // Wait for the TRUE terminal state (Idle/NoFile = streamer fully finalized), not JobFinished: the
                // streamer parks in AwaitIdle after the last ack until the controller reports Idle, and that final
                // transition is delivered by GrblStateChanged only while a program is active. Tearing down (which
                // clears ActiveRun) at JobFinished would close that gate mid-finalization and hang the run. Error/
                // Halted are ALSO terminal here (a failed burst - e.g. a probe miss - stays in Error until the
                // operator clicks Stop/Reset): MacroProcessor.Flush can block on onDone (see its 'wait' param), so
                // this must fire on a failure too, or a mid-macro error would hang Run() until manual intervention.
                //
                // NOTE: deliberately fires on the FIRST Idle/NoFile report, not a debounced Nth one - a
                // debounce here was tried (2026-07-21) and reverted: it requires a SECOND PropertyChanged
                // notification for StreamingState, but that property only raises PropertyChanged when its
                // VALUE actually changes - once it settles at Idle, no further report retriggers it, so a
                // per-report debounce here gets permanently stuck (Flush hangs forever, no message, no
                // timeout). The real "trailing motion after Idle" race this was trying to catch is instead
                // handled at the WaitForIdle end (see its comment) using report-driven polling, which DOES
                // correctly observe every incoming status report regardless of whether a property value
                // technically changed.
                if (!started || (st != StreamingState.Idle && st != StreamingState.NoFile && st != StreamingState.Error && st != StreamingState.Halted))
                    return;

                m.PropertyChanged -= handler;
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    // Revert the streamer to the loaded job, but ONLY if this burst's transient is still the
                    // source. A stay-put run (Load Stock) streams several bursts back-to-back (the park move,
                    // then each O<...> CALL); each reverts its own source at its idle, and the guard stops a
                    // finishing burst from clobbering the source a later burst already set. The tool's own
                    // ProgramView stays connected across ALL bursts (so each is marked in it and the Job view
                    // is never touched); the full teardown - disconnect the view, clear the active program -
                    // happens when the tool's tab is left (Activate(false)), not at a mid-run burst boundary.
                    if (RunControl.Source == prog)
                        RunControl.Source = null;

                    // The macro's whole run just finished (isFinalBurst) with no error: hide the program view
                    // completely rather than leaving it sitting in its Compact 3-line state showing wherever
                    // the last executed line happened to land - there's nothing actionable left to look at.
                    // Works uniformly for a tool's own preview pane (Start Job, Stepper Calibration, ...) and
                    // the shared _macroRunView alike, since both go through the same ProgramView.Active/
                    // Disconnect mechanism. On error (Halted/Error) the view is left up on purpose - see the
                    // fallback branch below - so the operator can see where/what failed.
                    if (isFinalBurst && (st == StreamingState.Idle || st == StreamingState.NoFile))
                    {
                        _macroRunViewTimer?.Stop();
                        CNC.Controls.ProgramView.Active?.Disconnect();

                        // A Generate-first tool tab's run just finished cleanly: drop the in-memory program and
                        // revert the Run bar back to "Generate" (see MacroProcessor's Generate-mode plumbing) -
                        // the operator re-generates for the next job rather than re-running a stale program.
                        // Left alone on error/halt (same condition as the program-view dismiss above) so the
                        // operator can still inspect/re-run the SAME generated program after fixing whatever
                        // interrupted it, without redoing Generate.
                        if (CNC.Controls.MacroProcessor.SupportsGenerateMode)
                            CNC.Controls.MacroProcessor.DiscardGenerated?.Invoke();
                    }
                    // A plain macro's run view auto-dismisses 20 s after it stops streaming (a re-use resets
                    // it); a tool's own view is left alone - it closes on tab-leave.
                    else if (_macroRunViewTimer != null && CNC.Controls.ProgramView.Active == _macroRunView)
                    {
                        _macroRunViewTimer.Stop();
                        _macroRunViewTimer.Start();
                    }
                    CNC.Core.DebugLog.Write("macro", string.Format("RestoreSourceOnEnd: about to invoke onDone, StreamingState={0} GrblState={1}",
                        m.StreamingState, m.GrblState.State));
                    onDone?.Invoke();
                }));
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

            AppDialogs.Show(this,
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

        // Reassert pinned flyouts as visible. The sidebar canvas is collapsed off the Job tab, so returning to
        // Job must re-show any pinned flyout (a pin means "stay open"); without this they only appeared via the
        // one-shot launch timer and read as "closed on tab switch".
        private void ShowPinnedFlyouts()
        {
            foreach (var child in sidebarCanvas.Children)
                if (child is IPinnableFlyout f && f.Pinned && child is UIElement el)
                    el.Visibility = Visibility.Visible;
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
            if (CNC.Core.Grbl.GrblViewModel.IsSDCardJob || !(e.Cancel = !menuMain.IsEnabled))
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

        // (The Exit menu item was dropped in the menu overhaul - the window X / Alt+F4 quit via Window_Closing.)

        private void generateResetTestCase_Click(object sender, RoutedEventArgs e)
        {
            new ResetReproViewModel().Show();
        }

        // Open the config folder (where App.config, KeyMap0.xml, grbl backups and restore points live) in Explorer,
        // so the user can find/back up/edit those files without hunting for the install directory.
        private void openConfigFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = CNC.Core.Resources.ConfigPath;
                if (string.IsNullOrEmpty(path))
                    path = AppDomain.CurrentDomain.BaseDirectory;
                System.Diagnostics.Process.Start("explorer.exe", "\"" + System.IO.Path.GetFullPath(path).TrimEnd('\\') + "\"");
            }
            catch (Exception ex)
            {
                AppDialogs.Show(ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        }

        void aboutWikiItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/terjeio/ioSender/wiki");
        }

        void tipsWikiItem_Click(object sender, EventArgs e)
        {
            // V2: open the user manual's "getting clean, repeatable results" page instead of the upstream wiki.
            ManualHelp.Open("clean-results");
        }

        void briefTour_Click(object sender, EventArgs e)
        {
            // V2: open the manual's "Intro to CNC" orientation page instead of the upstream blog tour.
            ManualHelp.Open("intro-to-cnc");
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

        // Silent startup check - same GitHub "latest release" comparison as the interactive Check for
        // Updates menu item below, but no dialogs and no dev-build release-picker flow: just resolves
        // whether a newer version exists, so Loaded (below) can append " (update available)" to the
        // title bar. Kept as its own small method (a little duplicated GET logic) rather than reusing
        // checkForUpdates_Click, so this new, low-stakes startup path can never regress that already
        // user-verified interactive flow. Returns null on ANY failure (dev build, no releases, offline,
        // timeout, parse failure) - a startup check must never surface a network hiccup to the user.
        private async Task<string> GetNewerVersionSilently()
        {
            if (BuildInfo.Version == "dev")
                return null;   // no fixed version to compare against the same way - see CheckForUpdatesDevBuild

            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ioSender/" + Version);
                    client.Timeout = TimeSpan.FromSeconds(15);

                    var httpResponse = await client.GetAsync("https://api.github.com/repos/ioSenderV2/ioSender/releases/latest");
                    if (!httpResponse.IsSuccessStatusCode)
                        return null;

                    var response = await httpResponse.Content.ReadAsStringAsync();
                    string latestVersion = ParseGitHubReleaseTag(response);
                    return !string.IsNullOrEmpty(latestVersion) && CompareVersions(BuildInfo.Version, latestVersion) < 0
                        ? latestVersion : null;
                }
            }
            catch { return null; }
        }

        // Check for updates: query GitHub's "latest release" for ioSenderV2/ioSender (a real,
        // versioned, non-prerelease release now - .github/workflows/release.yml publishes one on
        // every push to master, tag "v<version>") and compare its version against the one this
        // binary was built with (BuildInfo.Version, stamped by that same workflow).
        private async void checkForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (BuildInfo.Version == "dev")
            {
                await CheckForUpdatesDevBuild();
                return;
            }

            const string releasesUrl = "https://api.github.com/repos/ioSenderV2/ioSender/releases/latest";

            try
            {
                // GitHub requires TLS 1.2; .NET Framework 4.6.2 does not enable it by default
                // (same idiom as SimulatorManager's GitHub calls).
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

                using (var client = new System.Net.Http.HttpClient())
                {
                    // GitHub API requires a User-Agent header
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ioSender/" + Version);
                    client.Timeout = TimeSpan.FromSeconds(15);

                    var httpResponse = await client.GetAsync(releasesUrl);
                    // A 404 means the repository has no published release to compare against
                    // (distinct from a connectivity problem, which would throw below).
                    if (httpResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        AppDialogs.Show(string.Format("No published releases were found for ioSender, so there is nothing to compare against.\n\nYou are running version {0}.", BuildInfo.Version),
                            "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    httpResponse.EnsureSuccessStatusCode();
                    var response = await httpResponse.Content.ReadAsStringAsync();
                    string latestVersion = ParseGitHubReleaseTag(response);
                    if (string.IsNullOrEmpty(latestVersion))
                    {
                        AppDialogs.Show("Could not determine the latest release's version.", "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (CompareVersions(BuildInfo.Version, latestVersion) >= 0)
                    {
                        AppDialogs.Show(string.Format("You are running the latest version ({0}).", BuildInfo.Version),
                            "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        var result = AppDialogs.Show(
                            string.Format("A newer version of ioSender is available.\n\nYour version: {0}\nLatest version: {1}\n\nUpdate now? ioSender will close, download and install the new build, and relaunch.", BuildInfo.Version, latestVersion),
                            "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (result == MessageBoxResult.Yes)
                        {
                            LaunchInstaller(null);
                            Close();
                        }
                    }
                }
            }
            catch (System.Net.Http.HttpRequestException)
            {
                AppDialogs.Show("Could not connect to GitHub. Please check your internet connection and try again.",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (TaskCanceledException)
            {
                AppDialogs.Show("The request timed out. Please check your internet connection and try again.",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppDialogs.Show("An error occurred while checking for updates:\n" + ex.Message,
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // "Check for Updates" on a local dev build (BuildInfo.Version == "dev", not stamped by the
        // release workflow): there's no embedded version to compare against "latest", so instead of
        // just refusing, list every published release that has an installable ioSender.zip asset and
        // let the operator pick one to install OVER this dev build's own bin folder (not the normal
        // %LocalAppData% install - see install.ps1's -InstallDir) for quick comparison against a real
        // published build.
        private async Task CheckForUpdatesDevBuild()
        {
            List<ReleaseListEntry> releases;
            string error;
            try
            {
                releases = await FetchReleaseList();
                error = null;
            }
            catch (Exception ex)
            {
                releases = null;
                error = ex.Message;
            }

            if (error != null)
            {
                AppDialogs.Show("Could not fetch the release list:\n" + error, "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (releases == null || releases.Count == 0)
            {
                AppDialogs.Show("No published releases with an installable ioSender.zip were found.", "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var tags = releases.Select(r => r.Tag).ToList();
            var labels = releases.Select(r => r.PublishedAt == null ? r.Tag : string.Format("{0} - {1}", r.Tag, r.PublishedAt)).ToList();

            string chosenTag = ReleasePickerDialog.Show(this,
                "This is a local development build with no embedded version to compare against a release.\n\nPick a published release to install over this dev build:",
                "Check for Updates", tags, labels);
            if (chosenTag == null)
                return;

            string devDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            LaunchInstaller(string.Format("-Tag {0} -InstallDir \"{1}\"", chosenTag, devDir));
            Close();
        }

        private class ReleaseListEntry
        {
            public string Tag;
            public string PublishedAt;
        }

        // Fetch every published release for ioSenderV2/ioSender that carries an installable
        // ioSender.zip asset (newest first, GitHub's own array order).
        private static async Task<List<ReleaseListEntry>> FetchReleaseList()
        {
            const string releasesUrl = "https://api.github.com/repos/ioSenderV2/ioSender/releases?per_page=30";

            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ioSender/" + Version);
                client.Timeout = TimeSpan.FromSeconds(15);

                var httpResponse = await client.GetAsync(releasesUrl);
                if (httpResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new List<ReleaseListEntry>();
                httpResponse.EnsureSuccessStatusCode();
                var response = await httpResponse.Content.ReadAsStringAsync();
                return ParseReleaseList(response);
            }
        }

        // Parse a GitHub "list releases" JSON array response, keeping only releases that publish an
        // "ioSender.zip" asset (installable) and aren't drafts - same minimal hand-scanned parsing
        // idiom as ParseGitHubReleaseTag (no JSON library dependency). Segments the array by
        // successive "tag_name" occurrences: each release's own fields/assets are always emitted
        // between its tag_name and the next release's tag_name (confirmed against a real GitHub
        // releases-list response - assets always follow tag_name within the same object).
        private static List<ReleaseListEntry> ParseReleaseList(string json)
        {
            var result = new List<ReleaseListEntry>();
            const string tagKey = "\"tag_name\"";

            int idx = json.IndexOf(tagKey, StringComparison.Ordinal);
            while (idx >= 0)
            {
                int nextIdx = json.IndexOf(tagKey, idx + tagKey.Length, StringComparison.Ordinal);
                string segment = json.Substring(idx, (nextIdx >= 0 ? nextIdx : json.Length) - idx);

                string tag = ExtractJsonStringValue(segment, tagKey);
                if (!string.IsNullOrEmpty(tag))
                {
                    bool isDraft = segment.Contains("\"draft\":true");
                    bool hasZip = segment.Contains("\"name\":\"ioSender.zip\"");
                    if (!isDraft && hasZip)
                    {
                        result.Add(new ReleaseListEntry
                        {
                            Tag = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag,
                            PublishedAt = ExtractJsonStringValue(segment, "\"published_at\"")
                        });
                    }
                }

                idx = nextIdx;
            }

            return result;
        }

        // Extract the string value of a top-level "key":"value" pair from a JSON fragment.
        private static string ExtractJsonStringValue(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return null;
            idx = json.IndexOf(':', idx + key.Length);
            if (idx < 0)
                return null;
            int start = json.IndexOf('"', idx + 1);
            if (start < 0)
                return null;
            int end = json.IndexOf('"', start + 1);
            if (end < 0)
                return null;
            return json.Substring(start + 1, end - start - 1);
        }

        // Roll back to the build install.ps1 saved under ioSender\previous (the last update, one
        // version deep - see install.ps1's -Rollback). Reuses that one engine rather than
        // re-implementing the folder swap here.
        private void rollbackVersion_Click(object sender, RoutedEventArgs e)
        {
            var result = AppDialogs.Show(
                "Roll back to the previously installed ioSender build?\n\nioSender will close and the prior build will relaunch. This only works if you have updated at least once - it goes back one version, not further.",
                "Roll Back to Previous Version", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            LaunchInstaller("-Rollback");
            Close();
        }

        // Fetch install.ps1 fresh from GitHub and run it detached (visible PowerShell window, same as
        // the documented one-liner) so it can update/roll back files this running process has locked.
        // Always fetching fresh (rather than shipping a local copy) means a fixed installer bug is
        // picked up automatically instead of running a stale one baked into an old build.
        private static void LaunchInstaller(string arguments)
        {
            const string installScriptUrl = "https://raw.githubusercontent.com/ioSenderV2/ioSender/master/install.ps1";
            string command = string.IsNullOrEmpty(arguments)
                ? $"& ([scriptblock]::Create((irm '{installScriptUrl}')))"
                : $"& ([scriptblock]::Create((irm '{installScriptUrl}'))) {arguments}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"",
                UseShellExecute = true
            });
        }

        // Parse the "tag_name" field (e.g. "v2.8") out of a GitHub release JSON response and strip
        // the leading "v". Minimal JSON parsing to avoid adding a JSON library dependency.
        private static string ParseGitHubReleaseTag(string json)
        {
            const string key = "\"tag_name\"";
            int idx = json.IndexOf(key);
            if (idx < 0)
                return null;
            idx = json.IndexOf(':', idx + key.Length);
            if (idx < 0)
                return null;
            int start = json.IndexOf('"', idx + 1);
            if (start < 0)
                return null;
            int end = json.IndexOf('"', start + 1);
            if (end < 0)
                return null;
            string tag = json.Substring(start + 1, end - start - 1);
            return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
        }

        // Compares two "major.minor" version strings numerically (not lexically, so "2.9" < "2.10").
        // Returns <0 if a<b, 0 if equal, >0 if a>b.
        private static int CompareVersions(string a, string b)
        {
            int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            int[] pa = Parts(a), pb = Parts(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int va = i < pa.Length ? pa[i] : 0;
                int vb = i < pb.Length ? pb[i] : 0;
                if (va != vb) return va - vb;
            }
            return 0;
        }

        // Top-level Connect/Reconnect item: "Connect..." when disconnected, "Reconnect..." when connected
        // (a reconnect disconnects the current target first, then shows the dialog to pick another). Driven
        // by ConnectionTarget changes since, as a top-level click item, there is no submenu-open to hook.
        private void UpdateConnectMenuHeader()
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

        // Job tab Run dropdown "Simulate" support (registered onto SimulatorManager's hooks - see the
        // constructor). Remembers what was connected before switching to the simulator, so
        // RestoreConnectionAfterSimulate can put it back once the simulated run ends. Null (nothing to
        // restore) covers both "wasn't connected to anything real" and "already on the simulator, nothing
        // to switch" - both leave this unset.
        private string _preSimulateTarget = null;

        // Switches the live connection to the bundled simulator, remembering the current real-controller
        // target first. Blocking (launches/connects synchronously), same cost every other connect path here
        // already pays. Returns false - and leaves the connection exactly as it found it, i.e. still
        // disconnected if it had to drop the prior one first - if no simulator has been built yet.
        private bool SwitchToSimulatorForRun()
        {
            _preSimulateTarget = (Comms.com != null && Comms.com.IsOpen) ? AppConfig.Settings.Base.PortParams : null;

            if (Comms.com != null && Comms.com.IsOpen)
                Disconnect();

            var model = (GrblViewModel)DataContext;
            int res = AppConfig.Settings.ConnectToSimulator(Title, model, App.Current.Dispatcher);
            bool ok = res == 0 && Comms.com != null && Comms.com.IsOpen;
            if (ok)
            {
                Comms.com.PurgeQueue();
                if (getView(getTab(ViewType.GRBL)) is ICNCView grbl)
                    grbl.Activate(true, ViewType.Startup);
            }
            UpdateSimulatorTint();
            return ok;
        }

        // Reconnects to whatever was live before SwitchToSimulatorForRun, once the simulated run ends
        // (finish, error, or abort - see JobControl.ResetRunModeAfterJob). A failed reconnect is left as the
        // normal disconnected state (status bar reads "Not connected") rather than a popup - per design, the
        // operator is already looking at the screen right as the job just ended either way.
        private void RestoreConnectionAfterSimulate()
        {
            string target = _preSimulateTarget;
            _preSimulateTarget = null;

            if (Comms.com != null && Comms.com.IsOpen)
                Disconnect();

            if (string.IsNullOrEmpty(target))
                return;   // wasn't connected to anything real before Simulate - stay disconnected

            var model = (GrblViewModel)DataContext;
            int res = AppConfig.Settings.ConnectTo(Title, model, App.Current.Dispatcher, target);
            if (res == 0 && Comms.com != null && Comms.com.IsOpen)
            {
                Comms.com.PurgeQueue();
                if (getView(getTab(ViewType.GRBL)) is ICNCView grbl)
                    grbl.Activate(true, ViewType.Startup);
                ForceMachineSetupIfNeeded();
            }
            UpdateSimulatorTint();
        }

#if DEBUG
        // Debug-only diagnostic hotkey (registered onto ActionKeyBinder above, assignable in Keyboard &
        // Controller > UI zoom group). Captures the window via RenderTargetBitmap - an in-memory render of
        // the Visual, not a screen grab, same technique WpfUiTestServer's own /screenshot uses (see
        // RevealMainWindow's comment) - BEFORE showing the save dialog, so the dialog itself never ends up
        // in the image. Filename defaults to "app_<current tab>.png" (e.g. app_start_job.png).
        private bool Screenshot_Action(Key key)
        {
            byte[] png;
            try
            {
                double dpiX = 96.0, dpiY = 96.0;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                {
                    dpiX *= src.CompositionTarget.TransformToDevice.M11;
                    dpiY *= src.CompositionTarget.TransformToDevice.M22;
                }
                int w = Math.Max(1, (int)Math.Round(ActualWidth * dpiX / 96.0));
                int h = Math.Max(1, (int)Math.Round(ActualHeight * dpiY / 96.0));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpiX, dpiY, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var ms = new System.IO.MemoryStream())
                {
                    encoder.Save(ms);
                    png = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Screenshot failed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return true;
            }

            string tabName = "tab";
            var d = TabRegistry.DescriptorByName(UIViewModel.CurrentView?.ViewType.ToString());
            if (d != null && !string.IsNullOrWhiteSpace(d.Label))
                tabName = SanitizeForFilename(d.Label);

            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ioSenderV2", "Screenshots");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = dir,
                FileName = "app_" + tabName + ".png",
                Filter = "PNG image|*.png",
                DefaultExt = ".png",
                AddExtension = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                try { System.IO.File.WriteAllBytes(dlg.FileName, png); }
                catch (Exception ex) { AppDialogs.Show("Could not save screenshot: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Exclamation); }
            }

            return true;
        }

        // "Start Job" -> "start_job"; strips anything that isn't a letter/digit, collapsing runs of
        // whitespace/punctuation into a single underscore.
        private static string SanitizeForFilename(string label)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in label.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                    sb.Append('_');
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == '_')
                sb.Length--;
            return sb.Length > 0 ? sb.ToString() : "tab";
        }
#endif

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

        // Tint the bottom run-control bar pale yellow while connected to the simulator (Base.StartSimulator is
        // set to the chosen target's sim-ness on each connect) so a virtual machine is unmistakable at a glance.
        // Restored to its default light gray on a real target or when disconnected. Called from every connect path.
        private void UpdateSimulatorTint()
        {
            bool sim = Comms.com != null && Comms.com.IsOpen && AppConfig.Settings.Base.StartSimulator;
            runControlBorder.Background = new System.Windows.Media.SolidColorBrush(sim
                ? System.Windows.Media.Color.FromRgb(0xF7, 0xEF, 0xA8)   // pale yellow = simulator
                : System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF5)); // default light gray
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

        // Another launch was intercepted by the single-instance gate: surface this (the running) window.
        private void BringToForeground()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (!_revealed)         // still on the startup splash - don't yank it forward mid-init
                return;
            Activate();
            Topmost = true;         // brief topmost bounce reliably raises above the foreground-lock
            Topmost = false;
        }

        // (fileSave/Open/OpenFolder/Close menu handlers removed in the menu overhaul - Load/Load Folder/Close
        //  are now on the program-view header and Save is in its right-click menu, all via the static GCode.File.)

        private void TabMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bubbles - ignore it unless it's the TabControl's OWN tab change (a child
            // combobox, e.g. the 3D viewer's render-mode dropdown, raises it too with non-TabItem items).
            if (!Equals(e.OriginalSource, sender) || e.AddedItems.Count != 1)
                return;

            ICNCView nextView = getView((TabItem)e.AddedItems[0]);

            // The sidebar flyouts only act on the Job tab, so only show them when it's selected (done
            // regardless of IsReady so it tracks tab changes before a controller is connected too).
            // Skipped mid tab-reorder-drag: a live drag sets IsSelected on the dragged tab every tick, which
            // fires this handler repeatedly - without the guard this line clobbers the drag's own
            // Hidden (see tabMode.ReorderDragging in BuildTabs) back to Visible on the very next tick.
            bool onJob = nextView != null && nextView.ViewType == ViewType.GRBL;
            if (!tabReorderDragging)
                sidebarCanvas.Visibility = onJob ? Visibility.Visible : Visibility.Collapsed;

            if ((DataContext as GrblViewModel).IsReady &&
                UIViewModel.CurrentView != null && nextView != null && nextView != UIViewModel.CurrentView)
            {
                ICNCView prevView = UIViewModel.CurrentView;
                UIViewModel.CurrentView = nextView;
                prevView.Activate(false, nextView.ViewType);
                nextView.Activate(true, prevView.ViewType);
            }

            // Pinned flyouts persist across tab switches. Reassert them on return to Job AFTER the activation
            // above, and again deferred: a post-activation layout pass otherwise re-hides them (the same reason
            // the launch reopen uses a deferred timer instead of an inline Visibility set).
            if (onJob)
            {
                ShowPinnedFlyouts();
                Dispatcher.BeginInvoke(new System.Action(ShowPinnedFlyouts), System.Windows.Threading.DispatcherPriority.Background);
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

        private bool _connGatedTabsOn = false;

        // Enable/disable the connection-gated tabs (descriptor EnabledWhenDisconnected == false) as the controller
        // connects/disconnects. Only acts on a change of connection so it doesn't fight the JobRunning enable rules
        // during a job (a job never spans a connect transition). Idempotent; safe to call from the state handler.
        private void UpdateConnectionGatedTabs()
        {
            bool connected = Comms.com != null && Comms.com.IsOpen;
            if (connected == _connGatedTabsOn)
                return;
            _connGatedTabsOn = connected;

            foreach (TabItem tab in UIUtils.FindLogicalChildren<TabItem>(tabMode))
            {
                var view = getView(tab);
                if (view == null)
                    continue;
                var d = TabRegistry.DescriptorByName(view.ViewType.ToString());
                if (d != null && !d.EnabledWhenDisconnected)
                    tab.IsEnabled = connected;
            }
        }

        public static void ShowView(bool show, ViewType view)
        {
            TabItem tab = getTab(view);
            if (tab != null && !show)
                ui.tabMode.Items.Remove(tab);
        }

        // Safety net for tab enablement. The JobRunning setter disables every non-selected tab while a job/
        // probe holds the controller (driven by fragile IsGCLock/Hold event pairs); if the clearing event does
        // not line up - e.g. Start Job's auto 3D-probe locks G-code, then the user changes tab - a gated tab can
        // be left disabled with no path back. When the controller is genuinely idle and nothing is streaming,
        // reconcile every tab to its correct not-running state (connection + capability). Idempotent and cheap
        // (writes IsEnabled only when it differs), so it is safe to call on every idle status message. Never runs
        // during a real job - the call site gates on Idle && !IsJobRunning, and a running job is never Idle.
        public void RefreshTabsIdle()
        {
            bool connected = Comms.com != null && Comms.com.IsOpen;
            foreach (TabItem tab in UIUtils.FindLogicalChildren<TabItem>(tabMode))
            {
                var view = getView(tab);
                if (view == null)
                    continue;
                var d = TabRegistry.DescriptorByName(view.ViewType.ToString());
                bool want = (d == null || d.EnabledWhenDisconnected || connected) && view.CanEnable;
                if (tab.IsEnabled != want)
                    tab.IsEnabled = want;
            }
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
            // Availability is binary: every tab is selectable (enabledWhenDisconnected: true - no silent
            // "greyed until connected"), and a tab that needs a capability the controller lacks is REMOVED on
            // connect (JobView.Setup) and listed, with the reason, in Edit Main Page > Unavailable
            // (ComponentAvailability). Tabs that only need a live controller stay available and act on connect.
            // enabledWhenDisconnected: which tabs are usable before a controller connects. Job stays on for
            // offline g-code load/preview; Settings/Tools/Machine Setup are config/setup work. The operational
            // tabs (Start Job, Offsets, Probing, Height Map, SD Card, Lathe) need a live controller, so they are
            // disabled until connect and re-enabled by UpdateConnectionGatedTabs on the connect transition.
            TabRegistry.Register(new TabDescriptor(ViewType.GRBL, TabLabel("TabJob", "Job"), () => new JobView(), 10, enabledWhenDisconnected: true));
            TabRegistry.Register(new TabDescriptor(ViewType.StartJob, TabLabel("TabStartJob", "Start Job"), () => new StartJobView(), 5, enabledWhenDisconnected: false));
            TabRegistry.Register(new TabDescriptor(ViewType.Offsets, TabLabel("TabOffsets", "Offsets"), () => new OffsetView(), 30, enabledWhenDisconnected: false));
            TabRegistry.Register(new TabDescriptor(ViewType.GRBLConfig, TabLabel("TabSettings", "Settings"), () => new GrblConfigView(), 40, enabledWhenDisconnected: true, alwaysVisible: true));
            TabRegistry.Register(new TabDescriptor(ViewType.Probing, TabLabel("TabProbing", "Probing"), () => new CNC.Controls.Probing.ProbingView(), 50, enabledWhenDisconnected: false));
            TabRegistry.Register(new TabDescriptor(ViewType.HeightMap, TabLabel("TabHeightMap", "Height Map"), () => new HeightMapView(), 55, enabledWhenDisconnected: false));
            TabRegistry.Register(new TabDescriptor(ViewType.SDCard, TabLabel("TabSDCard", "SD Card"), () => new SDCardView(), 60, enabledWhenDisconnected: false,
                configure: ctl => ((SDCardView)ctl).FileSelected += SDCardView_FileSelected));
            TabRegistry.Register(new TabDescriptor(ViewType.LatheWizards, TabLabel("TabLatheWizards", "Lathe Tools"), () => new CNC.Controls.Lathe.LatheWizardsView(), 70, enabledWhenDisconnected: false));
            TabRegistry.Register(new TabDescriptor(ViewType.Tools, TabLabel("TabTools", "Tools"), () => new ToolsView(), 80, enabledWhenDisconnected: true, alwaysVisible: true));
            TabRegistry.Register(new TabDescriptor(ViewType.MachineSetup, TabLabel("TabMachineSetup", "Machine Setup"), () => new MachineSetupView(), 90, enabledWhenDisconnected: true, alwaysVisible: true));
        }

        // Localized tab label via LibStrings, falling back to the English literal if the resource is missing
        // (so a typo'd key or unloaded resource can never blank a tab header).
        private static string TabLabel(string key, string fallback)
        {
            string s = CNC.Controls.LibStrings.FindResource(key);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        // Instantiate the tabs into the (XAML-empty) TabControl in the order given by the layout tree
        // (AppConfig.Layout's "tabs" slot). Each node's component key maps to a registered TabDescriptor
        // that supplies the factory/label/enable/configure. The tree is the placement authority; the
        // descriptor is the build recipe. EnsureEssentials guarantees Settings stays reachable.
        private void BuildTabs()
        {
            tabMode.Items.Clear();

            var tabsSlot = AppConfig.Settings.Layout?.Slot(LayoutKeys.SlotTabs);
            if (tabsSlot == null)
                return;

            foreach (var node in tabsSlot.Items)
            {
                var d = TabRegistry.DescriptorByName(node.Component);
                if (d == null)
                    continue;   // unknown/foreign component key - skip (e.g. a tab not in this build)
                var ctl = d.Create?.Invoke();
                if (ctl == null)
                    continue;
                d.Configure?.Invoke(ctl);

                var tabItem = new TabItem
                {
                    Content = ctl,
                    IsEnabled = d.EnabledWhenDisconnected,
                    Tag = node.Component,
                    // x:Uid is a markup-only directive, and these tabs are built in code, so they have no
                    // authored Uid. Set it explicitly from the registry key (unique + stable) so the UI test
                    // server can address the nav tabs by Uid and select one via its SelectionItem peer.
                    Uid = "tab_" + node.Component
                };

                // Bindable main-page tabs get a live shortcut badge (upper-right) + a right-click "Bind to Key"
                // menu; other tabs (e.g. the Trinamic tuner, not an ICNCView) keep a plain text header.
                string tabId = ctl is ICNCView icv
                    ? tabViewIds.FirstOrDefault(kv => kv.Value == icv.ViewType).Key
                    : null;
                if (tabId != null)
                {
                    tabItem.Header = new CNC.Controls.TabHeaderControl(d.Label, tabId);
                    TabKeyBinder.AttachBindMenu(tabItem, tabId);
                }
                else
                    tabItem.Header = d.Label;

                tabMode.Items.Add(tabItem);
            }

            // Persist drag-reorder of the top-level bar into Config.Tabs + the layout tree (the same store the
            // "Edit Main Page" Tabs editor writes), so the two ways of ordering tabs stay in agreement.
            tabMode.TabsReordered += (s, e) =>
            {
                var order = new System.Collections.Generic.List<string>();
                foreach (TabItem t in tabMode.Items)
                    if (t.Tag is string key && !string.IsNullOrEmpty(key))
                        order.Add(key);
                AppConfig.Settings.ReorderTopLevelTabs(order);
            };

            // The pinned sidebar flyout icons (G28/G30/Set-position etc.) are docked as a SIBLING of tabMode
            // in this window's own layout, not inside it - a Clip on tabMode itself (see StretchTabControl's
            // tab-drag handling) can never reach them, so they kept live-repainting through a tab drag
            // alongside the (now-hidden) tab content. Hide/restore them directly here instead.
            // tabReorderDragging also gates TabMode_SelectionChanged (see there) - a LIVE reorder drag sets
            // IsSelected on the dragged tab every tick, which fires SelectionChanged repeatedly mid-drag;
            // that handler's own "show sidebar when the Job tab is selected" logic was clobbering this
            // Hidden right back to Visible on the very next tick (confirmed on real hardware - the sidebar
            // never actually stayed hidden despite this line running first).
            tabMode.ReorderDragging += (s, dragging) =>
            {
                tabReorderDragging = dragging;
                if (sidebarCanvas != null)
                    sidebarCanvas.Visibility = dragging ? Visibility.Hidden : Visibility.Visible;
            };
        }

        // See tabMode.ReorderDragging's own comment (BuildTabs) for why this exists.
        private bool tabReorderDragging;

        // Publish the tabs currently present (after InitSystem's capability filtering) so the "Edit Main
        // Page" Tabs editor can list them. Ordering/visibility is now driven by the layout tree (BuildTabs),
        // which is kept in sync with the legacy Config.Tabs at load (TabOrder.Apply) - so no reorder here.
        private void PublishAndApplyTabs()
        {
            var infos = new System.Collections.Generic.List<CNC.Controls.TabInfo>();
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (TabItem t in tabMode.Items)
            {
                // Most tabs host an ICNCView (key = ViewType); some (e.g. Trinamic tuner) host an IGrblConfigTab
                // with no ViewType, so fall back to the TabItem's x:Name so they still appear in the editor.
                var v = getView(t);
                string name = v != null ? v.ViewType.ToString() : t.Name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                    continue;
                infos.Add(new CNC.Controls.TabInfo(name, t.Header?.ToString() ?? name));
            }
            CNC.Controls.TabRegistry.Publish(infos);
        }

#if ADD_CAMERA
        private static bool enableCamera(MainWindow owner)
        {
            if (UIViewModel.Camera == null)
            {
                UIViewModel.Camera = new Camera();
                UIViewModel.Camera.Setup(UIViewModel);
                //        Camera.Owner = owner;
            }

            owner.UpdateCameraMenu();

            return UIViewModel.Camera != null;
        }

        // The in-app camera only sees local webcams (a laptop just shows its built-in, useless for CNC), so the
        // Camera menu is shown/enabled only when a specific device has been bound in the Connect dialog AND that
        // device is currently present. Re-run after the Connect dialog closes so a fresh bind takes effect.
        public void UpdateCameraMenu()
        {
            var cfg = AppConfig.Settings.Camera;
            bool bound = cfg != null && cfg.IsCameraBound &&
                         Camera.EnumerateDevices().Any(d => d.Moniker == cfg.SelectedCamera);
            menuCamera.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
            menuCamera.IsEnabled = bound;
        }

        private void CameraOpen_Click(object sender, RoutedEventArgs e)
        {
            UIViewModel.Camera.Open();
        }
#else
        private void CameraOpen_Click(object sender, RoutedEventArgs e)
        {
        }
#endif

        // Public entry point for the "pop out the console" gesture (double-clicking the Console tab -
        // JobWorkspace.BuildCenter wires it), replacing the removed "Open Console" menu item.
        public void OpenConsoleWindow()
        {
            openConsole();
        }

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

            // (The "Open Console" menu item that used to show this shortcut hint was removed in the menu
            //  overhaul; the shortcut still toggles the console, and the Console tab tooltip mentions it.)
        }

        // --- tab-switch shortcuts ----------------------------------------------------------------

        // A parsed tab-switch binding: key + modifiers -> the tab id it selects (see KeyMapEditor.TabTargets).
        private class TabHotkey { public Key Key; public ModifierKeys Modifiers; public string Id; }
        private readonly List<TabHotkey> tabHotkeys = new List<TabHotkey>();
        private bool tabShortcutsHooked = false;

        // Main-page tab id -> ViewType. Settings sub-tab ids ("Tab.Settings.*") are dispatched separately.
        private static readonly Dictionary<string, ViewType> tabViewIds = new Dictionary<string, ViewType>
        {
            { "Tab.Settings",     ViewType.GRBLConfig },
            { "Tab.StartJob",     ViewType.StartJob },
            { "Tab.Job",          ViewType.GRBL },
            { "Tab.Offsets",      ViewType.Offsets },
            { "Tab.SDCard",       ViewType.SDCard },
            { "Tab.Probing",      ViewType.Probing },
            { "Tab.Tools",        ViewType.Tools },
            { "Tab.MachineSetup", ViewType.MachineSetup },
            { "Tab.HeightMap",    ViewType.HeightMap },
            { "Tab.LatheWizard",  ViewType.LatheWizards },
        };

        // (Re)parse the saved tab-switch shortcuts. Called at startup (just after registerConsoleShortcut, which
        // hooks the window preview handler that dispatches them) and again whenever the editor saves changes.
        private void registerTabShortcuts()
        {
            tabHotkeys.Clear();

            var saved = AppConfig.Settings.Base.TabShortcuts;
            if (saved != null) foreach (var s in saved)
            {
                Key k;
                ModifierKeys m;
                if (!string.IsNullOrEmpty(s.Key) && ShortcutKey.TryParse(s.Key, out k, out m) && k != Key.None)
                    tabHotkeys.Add(new TabHotkey { Key = k, Modifiers = m, Id = s.Id });
            }

            if (!tabShortcutsHooked)
            {
                AppConfig.TabShortcutsChanged += registerTabShortcuts;
                tabShortcutsHooked = true;
            }
        }

        // Switch to the tab bound to this key, if any. Returns true when handled (so the key is consumed). A
        // hidden or disabled target is left unhandled (no-op); a bare, unmodified key is ignored while a text
        // box has focus so tab shortcuts never eat typed characters in a field.
        private bool dispatchTabShortcut(KeyEventArgs e)
        {
            if (tabHotkeys.Count == 0 || KeyMapEditor.IsCapturing)
                return false;   // don't hijack the key while the user is rebinding a shortcut

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys mods = Keyboard.Modifiers;

            // A text-producing combo (no modifier, or Shift only for a capital/symbol) must not be stolen from
            // a focused text box - e.g. typing "J" or "Shift+J" into the MDI field. Ctrl/Alt combos are commands.
            if ((mods == ModifierKeys.None || mods == ModifierKeys.Shift)
                 && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                return false;

            var hit = tabHotkeys.FirstOrDefault(h => h.Key == key && h.Modifiers == mods);
            if (hit == null)
                return false;

            // A nested "Tab.<Parent>.<Sub>" id selects the parent top-level tab, then drills into its inner
            // sub-tab; a plain "Tab.<Name>" id just selects the top-level tab. The parent (or the tab itself)
            // is resolved through tabViewIds; the inner selection is delegated to the view's ITabBindingHost.
            int firstDot = hit.Id.IndexOf('.');
            int secondDot = firstDot < 0 ? -1 : hit.Id.IndexOf('.', firstDot + 1);
            string lookupId = secondDot > 0 ? hit.Id.Substring(0, secondDot) : hit.Id;

            ViewType vt;
            if (!tabViewIds.TryGetValue(lookupId, out vt))
                return false;

            TabItem tab = getTab(vt);
            if (tab == null || !tab.IsEnabled)
                return false;   // top-level tab removed (missing capability) or disabled -> do nothing

            if (secondDot > 0)
            {
                // Nested: select the inner sub-tab first and only switch to the parent if it was actually
                // available. A binding to a sub-tab that has since been removed (e.g. a capability tool tab, or
                // a disabled lathe/probing view) does nothing at all rather than half-switching to the parent.
                var host = getView(tab) as ITabBindingHost;
                if (host == null || !host.SelectSubTab(hit.Id))
                    return false;
            }

            tabMode.SelectedItem = tab;
            return true;
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F1 - context help: open the user manual at the page for whatever view is current.
            if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
            {
                ManualHelp.Open(UIViewModel?.CurrentView?.ViewType ?? ViewType.Startup);
                e.Handled = true;
                return;
            }

            if (consoleKey != Key.None && e.Key == consoleKey && Keyboard.Modifiers == consoleModifiers)
            {
                openConsole();   // openConsole() toggles: shows when hidden/new, hides when visible
                e.Handled = true;
                return;
            }

            if (dispatchTabShortcut(e))
            {
                e.Handled = true;
                return;
            }

            if (ActionKeyBinder.Dispatch(e))
            {
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
