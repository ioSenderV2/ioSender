/*
 * App.xaml.cs - part of Grbl Code Sender
 *
 * v0.37 / 2021-02-20 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2019-2022, Io Engineering (Terje Io)
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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace GCode_Sender
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // UI test server: -1 = off. Set >= 0 by -testserver[=PORT] / IOSENDER_TESTSERVER; a value of 0 means
        // "on, use the default port". Read by MainWindow.CompleteStartup once the UI is fully built.
        public static int TestServerPort = -1;

        // -message=text - shows an informational popup once the main window is revealed. Null = no popup.
        // Diagnostic/dev convenience: lets whoever is launching the app for someone to test a specific change
        // tell them what to look for, without a separate out-of-band message. Skipped for a -testserver launch
        // (no one is watching that window interactively). Set by OnStartup below, read by MainWindow.RevealMainWindow.
        public static string StartupMessage = null;

        // -headless - suppresses the modal crash dialog (HandleFatal below). Was IOSENDER_HEADLESS env var;
        // set by OnStartup's arg parse, read from here since HandleFatal can fire from any thread/method.
        public static bool Headless = false;

        public App()
        {
            // Pin the resource assembly to this exe BEFORE InitializeComponent loads App.xaml (the first
            // pack-resource access). Under the VS debugger / hosting process GetEntryAssembly() can resolve to a
            // different assembly, so the default lookup fails to find ioSender's compiled BAML and startup dies
            // with "Cannot locate resource 'splashwindow.xaml'". Setting it explicitly fixes that regardless of
            // how the process was launched.
            if (System.Windows.Application.ResourceAssembly == null)
                System.Windows.Application.ResourceAssembly = System.Reflection.Assembly.GetExecutingAssembly();

            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
            Application.Current.DispatcherUnhandledException += DispatcherOnUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();

            int p = 0, lng = 0;
#if DEBUG
            // Debug builds trace by default - no need to remember -debuglog on every launch while iterating.
            // Release builds stay opt-in (the flag/parsing below still works the same in both configurations).
            bool debugLog = true;
#else
            bool debugLog = false;
#endif
            string debugCategories = null;
            bool wireLog = false;   // -wirelog; forced on in Debug builds by WireLog.Init itself
            bool demoMarker = false;
            bool crashTest = false;
            bool headless = false;
            bool selfRelaunch = false;
            while (p < args.GetLength(0))
            {
                string arg = args[p++];
                switch (arg)
                {
                    case "-locale":
                        if (p < args.GetLength(0))
                            lng = p;
                        break;

                    // -debuglog                enable the diagnostic trace log (all categories)
                    // -debuglog=settings,comms  enable, but only for the listed categories
                    // -demomarker  timestamp job-state transitions for demo-video sync (docs/demo-videos)
                    case "-demomarker":
                        demoMarker = true;
                        break;

                    // -crashtest  throw a synthetic unhandled exception shortly after startup, to exercise
                    // the crash -> minidump -> report-on-next-launch pipeline. Diagnostic only.
                    case "-crashtest":
                        crashTest = true;
                        break;

                    // -headless  suppress the modal crash dialog (log + exit code only) - for unattended
                    // runs (build.ps1 -Headless). Was IOSENDER_HEADLESS env var.
                    case "-headless":
                        headless = true;
                        break;

                    // -self-relaunch  set by the app on ITSELF (GrblConfigView.DoRestart) when relaunching
                    // after a restart-only settings change - tells the new instance to skip the single-
                    // instance-forward check since the old instance may still be mid-teardown. Was
                    // IOSENDER_SELF_RELAUNCH=1 env var; not meant to be passed by a human.
                    case "-self-relaunch":
                        selfRelaunch = true;
                        break;

                    // -notoolsetter  test the touch-plate TLO path (2026-07-31) without a toolsetter puck
                    // mounted: flips tc.macro's own #<_tc_touchplate> flag to 1 before it's provisioned to the
                    // controller (AtcMacros.ReadEmbedded), so every tool - including T8's 3D-probe stylus -
                    // probes via the main probe input instead of switching to the toolsetter input.
                    case "-notoolsetter":
                        CNC.Core.AtcMacros.NoToolsetter = true;
                        break;

                    default:
                        if (arg == "-debuglog" || arg.StartsWith("-debuglog=", StringComparison.OrdinalIgnoreCase))
                        {
                            debugLog = true;
                            int eq = arg.IndexOf('=');
                            if (eq >= 0)
                                debugCategories = arg.Substring(eq + 1);
                        }
                        // -wirelog  unfiltered trace of what actually crossed the link, in its own file.
                        // console.log mirrors the on-screen Console tab and inherits that view's filter -
                        // this does not. See WireLog.cs. Already on in Debug builds.
                        else if (arg == "-wirelog")
                        {
                            wireLog = true;
                        }
                        // -testserver              start the UI test server on the default port
                        // -testserver=8760         ... on an explicit port
                        else if (arg == "-testserver" || arg.StartsWith("-testserver=", StringComparison.OrdinalIgnoreCase))
                        {
                            int eq = arg.IndexOf('=');
                            int tp;
                            TestServerPort = (eq >= 0 && int.TryParse(arg.Substring(eq + 1), out tp)) ? tp : 0;
                        }
                        // -message=text  show an informational popup once the main window is up (see StartupMessage)
                        else if (arg.StartsWith("-message=", StringComparison.OrdinalIgnoreCase))
                        {
                            StartupMessage = arg.Substring("-message=".Length);
                        }
                        break;
                }
            }

            Headless = headless;

            // Clear any stale test-server exit-status file so it reflects only this run's exit.
            if (TestServerPort >= 0)
                try { File.Delete(ExitStatusPath()); } catch { }

            CNC.Core.DebugLog.Init(debugLog, debugCategories);
            CNC.Core.DebugLog.Write("app", "OnStartup - args: " + string.Join(" ", args));

            CNC.Core.ConsoleLog.Init();
            CNC.Core.WireLog.Init(wireLog);
            if (CNC.Core.WireLog.Enabled)
                CNC.Core.DebugLog.Write("app", "wire log: " + CNC.Core.WireLog.LogPath);
            // CNC.Core marshals to the UI thread and pumps messages through this instead of
            // Application.Current.Dispatcher. Installs both, at Normal dispatcher priority.
            CNC.Controls.UiPump.Register();
            CNC.Controls.SerialPortDescriptions.Register();   // WMI friendly names for the port picker
            CNC.Controls.AppMessageBox.Register();
            // GrblViewModel.Keyboard is the portable JogController by default; point it at the WPF
            // keypress handler before the first model is built (MainWindow.xaml instantiates one).
            CNC.Controls.KeypressHandler.Register();
            CNC.Controls.ButtonClickSound.Init();

            // Single instance: if another ioSender is already running, hand it our file arg (if any),
            // surface its window, and exit. Runs before any window/heavy init so this stays invisible.
            // SKIPPED for a -testserver launch: an automation-driven instance is never meant to be a
            // singleton - it must be able to run alongside a normal interactive instance (or another
            // -testserver instance on a different port) without colliding, and must never steal focus
            // from whatever the user is doing by forwarding into it (see MainWindow's matching skip of
            // becoming a pipe listener - both sides must agree, or a later normal launch would silently
            // forward into a hidden test instance instead of starting its own window).
            // ALSO SKIPPED when GrblConfigView.DoRestart launched us as a self-relaunch: the prior instance
            // is still mid-teardown and may still be listening on the single-instance pipe for a moment
            // (NamedPipeServerStream.WaitForConnection() isn't reliably cancelable by Dispose on .NET
            // Framework, so the old listener can't be relied on to have closed by the time we probe it) -
            // without this we'd detect it, forward into a process that's about to disappear, and exit
            // ourselves, so "Restart" would just close the app instead of relaunching it.
            if (!selfRelaunch && TestServerPort < 0 && CNC.Controls.PipeServer.TryForwardToRunningInstance(FindFileArg(args)))
            {
                CNC.Core.DebugLog.Write("app", "another instance is running - forwarded and exiting");
                Environment.Exit(0);
                return;
            }

            CNC.Core.DemoMarker.Init(demoMarker);
            // OBS bridge arming (needs Settings > App > Camera's Obs* fields) is deferred until after
            // AppConfig loads - see the demoMarker block right after `var main = new MainWindow();` below.

            if (lng > 0)
            {
                Thread.CurrentThread.CurrentUICulture =
                 Thread.CurrentThread.CurrentCulture =
                  CultureInfo.DefaultThreadCurrentCulture =
                   CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(args[lng]); ;

                FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
            }

            base.OnStartup(e);

            // Show a status splash, then create the main window invisible (StartupUri removed from App.xaml).
            // CompleteStartup connects/reads settings/validates the machine-setup steps with the splash up, then
            // reveals the main window (on the Machine Setup tab if setup is incomplete) and closes the splash.
            // A -testserver launch skips the splash entirely and never reveals (see MainWindow.RevealMainWindow) -
            // it's driven by UIAutomation peers/routed events, neither of which need the window shown or
            // focused, so an automation-driven instance never pops over the operator's desktop or steals
            // keyboard focus just by starting up.
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Every popup this app opens hands focus back to ioSender when it closes, instead of
            // letting Windows activate whatever is next in ITS z-order - which is regularly another
            // application. Installed before the first window is shown so nothing is missed.
            CNC.Controls.WindowFocusReturn.Install();

            var splash = TestServerPort < 0 ? new SplashWindow() : null;
            splash?.Show();

            var main = new MainWindow();
            Current.MainWindow = main;

            // With demo markers on, also arm the OBS bridge so a shoot auto-records (program load ->
            // StartRecord, program end -> StopRecord). No-op unless an OBS WebSocket server is reachable.
            // Host/port/password/camera source names come from Settings > App > Camera > "Demo recording
            // (OBS)" (AppConfig.Settings.Base.Camera, loaded synchronously inside the MainWindow ctor
            // above) - was IOSENDER_OBSWS_*/IOSENDER_OBS_* env vars.
            if (demoMarker)
            {
                var cam = CNC.Controls.AppConfig.Settings.Base.Camera;
                CNC.Controls.ObsBridge.Init(true, cam.ObsHost, cam.ObsPort, cam.ObsPassword);
                CNC.Controls.ObsBridge.ConfigureCameras(cam.ObsCamASource, cam.ObsCamAFilter, cam.ObsCamBSource, cam.ObsCamBFilter, cam.ObsAppSource, cam.ObsAppFilter);
            }

            main.AttachSplash(splash);
            main.Show();   // shown with Opacity 0 / not in taskbar; Window_Load -> CompleteStartup runs unseen

            // A hang-watchdog restart is discovered mid-connect (GrblInfo.Get(), on the connect thread),
            // so defer the dialog to ApplicationIdle same as the crash-report prompt below.
            CNC.Core.GrblInfo.HangDetectedHook = line =>
                Dispatcher.BeginInvoke(new Action(() => HangWatchdogReporter.Report(main, line)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // If a previous run crashed, offer to report it - deferred to ApplicationIdle so it appears
            // after the main window is up, not competing with the splash. Fires once (sentinel cleared).
            if (CrashReporter.HasPendingReport())
                Dispatcher.BeginInvoke(new Action(() => CrashReporter.PromptIfPending(main)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // -crashtest: fire a synthetic unhandled exception once the window is up, to validate the
            // crash-capture + report-on-next-launch flow end to end.
            if (crashTest)
                Dispatcher.BeginInvoke(new Action(() => { throw new InvalidOperationException("Synthetic crash (-crashtest)"); }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Find the file the user is trying to open, matching AppConfig's own arg handling: an explicit
        // --loadfile <path>, or a bare existing (non-.exe) path. Returns null if there is none.
        private static string FindFileArg(string[] args)
        {
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--loadfile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
                if (!a.StartsWith("-") && !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                    return a;
            }
            return null;
        }

        // Exit code written on a fatal unhandled exception. Distinct non-zero sentinel (0xFA11 = "FAIL") so a
        // headless launcher/harness can tell "crashed - read the log" apart from a normal quit (0) or a build
        // failure. The crash detail is dumped to CrashLogPath() first.
        private const int CrashExitCode = 0xFA11;

        // When the UI test server is running, feed unhandled exceptions to it (readable at GET /exceptions) and,
        // for the recoverable ones (Dispatcher / unobserved Task), keep the app alive so the harness can observe
        // the error and carry on instead of the app vanishing. Off in a normal run - crash behaviour is unchanged.
        private static void RecordToTestServer(string source, Exception ex)
        {
            try { WpfUiTestServer.UiTestServer.RecordException(source, ex); } catch { }
        }

        private void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            // Background/non-UI thread: the event is notification-only (can't be marked handled), so the process
            // is going down regardless. Log, tell an interactive user, then exit with the crash sentinel - the
            // previous handler showed a box but never exited, leaving a wedged process.
            if (TestServerPort >= 0) RecordToTestServer("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            HandleFatal("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        }

        private void DispatcherOnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            args.Handled = true; // stop the default WER/JIT path; we terminate deterministically below
            if (TestServerPort >= 0)
            {
                // Test mode: record + log, but do NOT exit - let the harness read it at /exceptions and continue.
                RecordToTestServer("Dispatcher.UnhandledException", args.Exception);
                WriteCrashLog("Dispatcher.UnhandledException (test-server: continued)", args.Exception);
                return;
            }
            HandleFatal("Dispatcher.UnhandledException", args.Exception);
        }

        private void TaskSchedulerOnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            args.SetObserved();
            if (TestServerPort >= 0)
            {
                RecordToTestServer("TaskScheduler.UnobservedTaskException", args.Exception?.GetBaseException());
                return;   // already observed; in test mode just record and keep running
            }
            HandleFatal("TaskScheduler.UnobservedTaskException", args.Exception?.GetBaseException());
        }

        // Common fatal path: dump the exception to a known log file, surface it to an interactive user (unless
        // running headless), then exit with CrashExitCode. Never throws.
        // One crash per process. A fatal exception on one thread does not stop another thread from hitting
        // its own a moment later, and each entry here opened a FRESH log file and re-pointed the
        // latest_crash.log link at it. Seen 2026-08-06: the real report landed in the 15:07:35 file, a
        // second fatal at 15:08:05 created an empty one, and latest_crash.log - the file anyone actually
        // reads - resolved to the empty one. The first exception is the one that matters; later ones are
        // usually its wreckage.
        private static int fatalHandled;

        private void HandleFatal(string source, Exception ex)
        {
            if (Interlocked.CompareExchange(ref fatalHandled, 1, 0) != 0)
            {
                // Already going down. Don't touch the log the first crash wrote - just block here so this
                // thread can't race ahead, and let the Environment.Exit below take the process.
                Thread.Sleep(Timeout.Infinite);
                return;
            }

            // Format the report ONCE and give the same string to both consumers. They used to build their
            // own, which is wasted work on the ordinary crash and actively harmful on an
            // OutOfMemoryException, where each allocation is a chance to come away with nothing.
            string utcStamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            string summary = null;
            try { summary = CrashReporter.BuildSummary(source, ex, utcStamp); } catch { }

            string logPath = WriteCrashLog(source, ex, summary);

            // Capture a minidump + per-crash summary and flag it for report-on-next-launch. Never throws.
            CrashReporter.Capture(source, ex, utcStamp, summary);

            // Skip the modal dialog for unattended runs (build.ps1 -Headless -> -headless) so a crash
            // can't hang the process on an un-clicked message box; the log + exit code carry the signal.
            if (!Headless)
            {
                try
                {
                    MessageBox.Show(
                        (ex?.Message ?? "Unknown error") + "\n\nDetails written to:\n" + logPath,
                        "ioSender - unhandled exception (" + source + ")",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { /* UI may be gone on a background-thread crash - the log already has it */ }
            }

            if (TestServerPort >= 0) WriteExitStatus(CrashExitCode, source + ": " + (ex?.Message ?? "unknown"));
            Environment.Exit(CrashExitCode);
        }

        // Test-server exit channel: since the in-process server dies with the app, the harness can't be told over
        // HTTP that the app is exiting. Instead we drop a tiny JSON exit-status file the harness reads AFTER the
        // socket goes away, pairing it with the OS process exit code. Written only when the test server is enabled;
        // cleared at startup so it reflects only the current run.
        private static string ExitStatusPath()
        {
            string dir;
            try
            {
                dir = CNC.Core.Resources.ConfigPath;
                if (string.IsNullOrEmpty(dir) || dir == "./" || !Path.IsPathRooted(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ioSender");
                Directory.CreateDirectory(dir);
            }
            catch { dir = AppDomain.CurrentDomain.BaseDirectory; }
            return Path.Combine(dir, "ioSender.exit.json");
        }

        private static void WriteExitStatus(int code, string reason)
        {
            try
            {
                string when = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                string json = "{\"code\":" + code + ",\"crash\":" + (code == CrashExitCode ? "true" : "false") +
                              ",\"reason\":\"" + (reason ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") +
                              "\",\"when\":\"" + when + "\"}";
                File.WriteAllText(ExitStatusPath(), json);
            }
            catch { /* best-effort */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (TestServerPort >= 0) WriteExitStatus(e.ApplicationExitCode, "normal exit");
            base.OnExit(e);
        }

        private const string CrashLogSeparator = "========================================================================\r\n";

        // Write a fresh, timestamped crash entry under %AppData%\ioSender\logs\<DayOfWeek>\ (falls back to
        // the app folder if the config dir isn't resolved yet, e.g. a crash during early startup), with
        // "latest_crash.log" (hard-)linked in the top-level logs folder. File creation, day-of-week folder
        // placement, the 8MB/.1 size rollover and per-folder retention are all handled by LogFile - the
        // same primitive ConsoleLog/DebugLog use. Returns the path written, or a best-effort path string if
        // the write itself failed. Never throws.
        private static string WriteCrashLog(string source, Exception ex, string summary = null)
        {
            var log = CNC.Core.LogFile.Open("ioSender.crash", latestLinkName: "latest_crash.log");
            string path = log?.Path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ioSender.crash.log");
            if (log == null)
                return path;

            // Two attempts, with a forced collection between them. An OutOfMemoryException is at once the
            // crash that most needs recording and the one least able to record itself - formatting the text
            // allocates, and File.AppendAllText needs a StreamWriter and an encoder buffer. On 2026-08-06 an
            // OOM got none of that and the log was left as LogFile.Open had created it: three bytes of UTF-8
            // BOM, indistinguishable from a run that crashed with nothing to say. The retry is not wishful
            // thinking - the crash reporter's own .txt, written seconds later with no explicit collect, went
            // through fine, because the OOM was thrown for one request and smaller ones still succeeded.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string body = summary ?? CrashReporter.BuildSummary(source, ex,
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    if (log.Write(CrashLogSeparator + body + "\r\n"))
                        return path;
                }
                catch { /* out of memory, most likely - fall through and try again with more of it */ }

                // Reclaim whatever the failed attempt, and whatever overran before it, left behind. This is
                // a terminal path, so the cost of a blocking collection buys more than it spends.
                try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }
            }

            // Both attempts failed. Leave a marker rather than the BOM-only stub: the exception's TYPE is
            // most of the diagnosis and, for an OOM, very nearly all of it - and this needs one short
            // concatenation to say so.
            try
            {
                File.AppendAllText(path, "CRASH LOG WRITE FAILED - " + source + " - " +
                    (ex == null ? "(no exception object)" : ex.GetType().FullName) + "\r\n");
            }
            catch { /* last resort: nothing more we can do */ }

            return path;
        }
    }
}
