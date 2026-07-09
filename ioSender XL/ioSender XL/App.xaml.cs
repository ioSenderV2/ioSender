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
using System.Reflection;
using System.Text;
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
            bool debugLog = false;
            string debugCategories = null;
            bool demoMarker = false;
            bool crashTest = false;
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

                    default:
                        if (arg == "-debuglog" || arg.StartsWith("-debuglog=", StringComparison.OrdinalIgnoreCase))
                        {
                            debugLog = true;
                            int eq = arg.IndexOf('=');
                            if (eq >= 0)
                                debugCategories = arg.Substring(eq + 1);
                        }
                        // -testserver              start the UI test server on the default port
                        // -testserver=8760         ... on an explicit port
                        else if (arg == "-testserver" || arg.StartsWith("-testserver=", StringComparison.OrdinalIgnoreCase))
                        {
                            int eq = arg.IndexOf('=');
                            int tp;
                            TestServerPort = (eq >= 0 && int.TryParse(arg.Substring(eq + 1), out tp)) ? tp : 0;
                        }
                        break;
                }
            }

            // Also honour an env var (mirrors IOSENDER_HEADLESS) so unattended/headless launches can turn it on.
            // Value may be "1"/"true" (all categories) or a category allow-list ("settings,comms").
            string debugEnv = Environment.GetEnvironmentVariable("IOSENDER_DEBUGLOG");
            if (!string.IsNullOrEmpty(debugEnv))
            {
                debugLog = true;
                if (!(debugEnv == "1" || string.Equals(debugEnv, "true", StringComparison.OrdinalIgnoreCase)))
                    debugCategories = debugEnv;
            }

            // UI test server env mirror (matches the IOSENDER_* pattern). "1"/"true" = default port; a numeric
            // value = that port. A command-line -testserver flag takes precedence if already set.
            if (TestServerPort < 0)
            {
                string tsEnv = Environment.GetEnvironmentVariable("IOSENDER_TESTSERVER");
                if (!string.IsNullOrEmpty(tsEnv))
                {
                    int tp;
                    TestServerPort = int.TryParse(tsEnv, out tp) ? tp : 0;
                }
            }

            CNC.Core.DebugLog.Init(debugLog, debugCategories);
            CNC.Core.DebugLog.Write("app", "OnStartup - args: " + string.Join(" ", args));

            // Single instance: if another ioSender is already running, hand it our file arg (if any),
            // surface its window, and exit. Runs before any window/heavy init so this stays invisible.
            // (Relaunch supervision is AppLaunch's job - we only read its exit-code protocol, see DoRestart.)
            if (CNC.Controls.PipeServer.TryForwardToRunningInstance(FindFileArg(args)))
            {
                CNC.Core.DebugLog.Write("app", "another instance is running - forwarded and exiting");
                Environment.Exit(0);
                return;
            }

            // Demo-video sync markers (mirrors the -debuglog flag + IOSENDER_* env pattern).
            if (!demoMarker)
            {
                string demoEnv = Environment.GetEnvironmentVariable("IOSENDER_DEMOMARKER");
                demoMarker = demoEnv == "1" || string.Equals(demoEnv, "true", StringComparison.OrdinalIgnoreCase);
            }
            CNC.Core.DemoMarker.Init(demoMarker);

            // With demo markers on, also arm the OBS bridge so a shoot auto-records (program load ->
            // StartRecord, program end -> StopRecord). No-op unless an OBS WebSocket server is reachable.
            // Auth password (if OBS auth is on) comes from IOSENDER_OBSWS_PASSWORD; host/port default localhost:4455.
            if (demoMarker)
            {
                string obsHost = Environment.GetEnvironmentVariable("IOSENDER_OBSWS_HOST");
                if (string.IsNullOrWhiteSpace(obsHost))
                    obsHost = "localhost";
                int obsPort;
                if (!int.TryParse(Environment.GetEnvironmentVariable("IOSENDER_OBSWS_PORT"), out obsPort))
                    obsPort = 4455;
                CNC.Core.ObsBridge.Init(true, obsHost, obsPort, Environment.GetEnvironmentVariable("IOSENDER_OBSWS_PASSWORD"));
            }

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
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var splash = new SplashWindow();
            splash.Show();

            var main = new MainWindow();
            Current.MainWindow = main;
            main.AttachSplash(splash);
            main.Show();   // shown with Opacity 0 / not in taskbar; Window_Load -> CompleteStartup runs unseen

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
        private void HandleFatal(string source, Exception ex)
        {
            string logPath = WriteCrashLog(source, ex);

            // Capture a minidump + per-crash summary and flag it for report-on-next-launch. Never throws.
            string utcStamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            CrashReporter.Capture(source, ex, utcStamp);

            // Skip the modal dialog for unattended runs (build.ps1 -Headless sets IOSENDER_HEADLESS) so a crash
            // can't hang the process on an un-clicked message box; the log + exit code carry the signal.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IOSENDER_HEADLESS")))
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

            Environment.Exit(CrashExitCode);
        }

        // Append a timestamped crash entry to %AppData%\ioSender\ioSender.crash.log (falls back to the app folder
        // if the config dir isn't resolved yet, e.g. a crash during early startup). Returns the path written, or a
        // best-effort path string if the write itself failed. Never throws.
        private static string WriteCrashLog(string source, Exception ex)
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

            string path = Path.Combine(dir, "ioSender.crash.log");

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("========================================================================");
                sb.AppendLine("Time (UTC) : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                sb.AppendLine("Version    : " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"));
                sb.AppendLine("Source     : " + source);
                sb.AppendLine("Exception  :");
                sb.AppendLine(ex?.ToString() ?? "(no exception object)");
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch { /* last resort: nothing more we can do */ }

            return path;
        }
    }
}
