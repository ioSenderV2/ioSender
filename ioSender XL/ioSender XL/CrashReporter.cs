/*
 * CrashReporter.cs - part of Grbl Code Sender
 *
 * Zero-infrastructure crash reporting. On a fatal unhandled exception (App.HandleFatal) this:
 *   - writes a real minidump to %AppData%\ioSender\crashes\<ts>.dmp (MiniDumpWriteDump),
 *   - writes a text summary  to %AppData%\ioSender\crashes\<ts>.txt, and
 *   - drops a "crash.pending" sentinel naming that crash.
 *
 * On the next launch, if the sentinel is present, a consent dialog shows the summary and offers to
 * open a pre-filled GitHub "new issue" page plus an Explorer window with the .dmp selected (drag it
 * onto the issue). There is no embedded token and no backend - reporting is entirely user-driven.
 *
 * Dedup-by-stack-hash is intentionally out of scope (needs a backend to be meaningful).
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace GCode_Sender
{
    internal static class CrashReporter
    {
        // GitHub repo that crash reports are filed against.
        private const string IssuesNewUrl = "https://github.com/ioSenderV2/ioSender/issues/new";

        private static string CrashDir
        {
            get
            {
                string dir;
                try
                {
                    dir = CNC.Core.Resources.ConfigPath;
                    if (string.IsNullOrEmpty(dir) || dir == "./" || !Path.IsPathRooted(dir))
                        dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ioSender");
                }
                catch { dir = AppDomain.CurrentDomain.BaseDirectory; }
                return Path.Combine(dir, "crashes");
            }
        }

        private static string PendingSentinelPath { get { return Path.Combine(CrashDir, "crash.pending"); } }

        // --- capture side (called from the crash handler; never throws) ---

        // Write a minidump + text summary for this crash and flag it for report-on-next-launch. utcStamp
        // is the crash time string used by the caller elsewhere, kept consistent for the filenames.
        public static void Capture(string source, Exception ex, string utcStamp)
        {
            try
            {
                string dir = CrashDir;
                Directory.CreateDirectory(dir);

                // File-safe base name from the timestamp (e.g. 2026-07-09_02-18-34).
                string baseName = SafeStamp(utcStamp);
                string dmpPath = Path.Combine(dir, baseName + ".dmp");
                string txtPath = Path.Combine(dir, baseName + ".txt");

                string summary = BuildSummary(source, ex, utcStamp);
                try { File.WriteAllText(txtPath, summary); } catch { }

                bool dumped = TryWriteMinidump(dmpPath);

                // Sentinel: line 1 = the .txt path, line 2 = the .dmp path (blank if the dump failed).
                try { File.WriteAllText(PendingSentinelPath, txtPath + "\n" + (dumped ? dmpPath : "")); } catch { }
            }
            catch { /* crash reporting must never mask the original crash */ }
        }

        public static string BuildSummary(string source, Exception ex, string utcStamp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ioSender crash report");
            sb.AppendLine("Time (UTC) : " + utcStamp);
            sb.AppendLine("Version    : " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"));
            sb.AppendLine("OS         : " + Environment.OSVersion + " (" + (Environment.Is64BitProcess ? "x64" : "x86") + ")");
            sb.AppendLine("CLR        : " + Environment.Version);
            sb.AppendLine("Source     : " + source);
            sb.AppendLine("Exception  :");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            return sb.ToString();
        }

        private static string SafeStamp(string utcStamp)
        {
            var sb = new StringBuilder(utcStamp.Length);
            foreach (char c in utcStamp)
                sb.Append(c == ':' || c == ' ' ? '-' : (c == '\'' || c == 'Z' ? '\0' : c));
            return sb.ToString().Replace("\0", "");
        }

        // --- minidump (dbghelp) ---

        [Flags]
        private enum MiniDumpType : uint
        {
            Normal = 0x00000000,
            WithThreadInfo = 0x00001000
        }

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeHandle hFile,
            MiniDumpType dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

        private static bool TryWriteMinidump(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    var proc = Process.GetCurrentProcess();
                    return MiniDumpWriteDump(proc.Handle, (uint)proc.Id, fs.SafeFileHandle,
                        MiniDumpType.Normal | MiniDumpType.WithThreadInfo, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { return false; }
        }

        // --- report side (called once on the next successful launch) ---

        public static bool HasPendingReport()
        {
            try { return File.Exists(PendingSentinelPath); } catch { return false; }
        }

        // Show the consent dialog if a crash from a previous run is pending. Reports once (the sentinel is
        // cleared when the dialog closes), leaving the crash files on disk. Owner keeps the dialog modal.
        public static void PromptIfPending(Window owner)
        {
            if (!HasPendingReport())
                return;

            string txtPath = null, dmpPath = null, summary = null;
            try
            {
                var lines = File.ReadAllLines(PendingSentinelPath);
                if (lines.Length > 0) txtPath = lines[0].Trim();
                if (lines.Length > 1) dmpPath = lines[1].Trim();
                if (!string.IsNullOrEmpty(txtPath) && File.Exists(txtPath))
                    summary = File.ReadAllText(txtPath);
            }
            catch { }

            if (string.IsNullOrEmpty(summary))
            {
                try { File.Delete(PendingSentinelPath); } catch { }
                return;
            }

            bool send = ShowDialog(owner, summary);

            // Reported (or explicitly dismissed) - don't prompt for this crash again.
            try { File.Delete(PendingSentinelPath); } catch { }

            if (!send)
                return;

            // Bundle the dump (+ summary) into a single .zip: GitHub accepts .zip attachments but not raw
            // .dmp, so this gives the user a file they can actually drag onto the issue.
            string zipPath = BuildReportZip(txtPath, dmpPath);

            OpenGithubIssue(summary, zipPath != null);

            // Open Explorer with the attachable file selected so it's ready to drag onto the issue page.
            string reveal = zipPath ?? (!string.IsNullOrEmpty(dmpPath) && File.Exists(dmpPath) ? dmpPath : txtPath);
            if (!string.IsNullOrEmpty(reveal) && File.Exists(reveal))
            {
                // The banner sits at the very top; dock the Explorer strip just below it. The banner is a
                // WPF window (DIPs) but the Explorer window is positioned in physical pixels, so convert the
                // banner's height with the display scale (this is the high-DPI case that started all this).
                double scale = GetDpiScale(owner);
                int stripTopPx = (int)Math.Round((BannerHeightDip + 10) * scale);
                RevealInExplorer(reveal, stripTopPx);
                ShowInstructionBanner(owner);
            }
        }

        private const double BannerHeightDip = 60;

        private static double GetDpiScale(Window owner)
        {
            try
            {
                var src = owner != null ? System.Windows.PresentationSource.FromVisual(owner) : null;
                if (src?.CompositionTarget != null)
                {
                    double m11 = src.CompositionTarget.TransformToDevice.M11;
                    if (m11 > 0)
                        return m11;
                }
            }
            catch { }
            return 1.0;
        }

        // A slim, always-on-top amber banner across the top of the screen with the one instruction the user
        // needs at the moment they're looking at the browser - because nobody reads the dialog before clicking
        // Send. Non-modal; closes on its button or after a couple of minutes.
        private static void ShowInstructionBanner(Window owner)
        {
            try
            {
                var wa = System.Windows.SystemParameters.WorkArea; // DIPs, matches WPF Left/Top/Width
                double width = Math.Min(900, wa.Width - 20);

                var win = new Window
                {
                    Title = "ioSender crash report",
                    Width = width,
                    Height = BannerHeightDip,
                    Left = wa.Left + (wa.Width - width) / 2,
                    Top = wa.Top + 4,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };

                var border = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x73, 0x00)),
                    BorderThickness = new Thickness(1)
                };
                var dock = new DockPanel { Margin = new Thickness(12, 0, 8, 0) };

                var btnClose = new Button
                {
                    Content = "Got it ✕",
                    MinWidth = 84,
                    Margin = new Thickness(8, 8, 0, 8),
                    Padding = new Thickness(8, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnClose.Click += (s, e) => { try { win.Close(); } catch { } };
                DockPanel.SetDock(btnClose, Dock.Right);

                var text = new TextBlock
                {
                    Text = "⬇  Crash report: drag the highlighted ZIP file (in the folder strip below) DOWN into the GitHub issue's comment box, then click “Submit new issue”.",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    Foreground = System.Windows.Media.Brushes.Black,
                    VerticalAlignment = VerticalAlignment.Center
                };

                dock.Children.Add(btnClose);
                dock.Children.Add(text);
                border.Child = dock;
                win.Content = border;

                // Auto-dismiss so an ignored banner doesn't float forever.
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
                timer.Tick += (s, e) => { timer.Stop(); try { win.Close(); } catch { } };
                win.Loaded += (s, e) => timer.Start();

                if (owner != null && owner.IsVisible)
                    win.Owner = owner;
                win.Show();
            }
            catch { }
        }

        // Zip the crash summary + dump into <base>.zip next to them. Returns the zip path, or null if there
        // was nothing to zip / it failed (caller falls back to revealing the raw files).
        private static string BuildReportZip(string txtPath, string dmpPath)
        {
            try
            {
                string basePath = !string.IsNullOrEmpty(txtPath) ? txtPath
                                : !string.IsNullOrEmpty(dmpPath) ? dmpPath : null;
                if (basePath == null)
                    return null;

                string zipPath = Path.ChangeExtension(basePath, ".zip");
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    if (!string.IsNullOrEmpty(txtPath) && File.Exists(txtPath))
                        zip.CreateEntryFromFile(txtPath, Path.GetFileName(txtPath));
                    if (!string.IsNullOrEmpty(dmpPath) && File.Exists(dmpPath))
                        zip.CreateEntryFromFile(dmpPath, Path.GetFileName(dmpPath));
                }
                return File.Exists(zipPath) ? zipPath : null;
            }
            catch { return null; }
        }

        // Small code-built dialog (no XAML/x:Uid, so it needs no LocBaml rows). Crash reports go to an
        // English GitHub tracker, so the dialog is deliberately English-only. Returns true for "Send".
        private static bool ShowDialog(Window owner, string summary)
        {
            bool send = false;

            var win = new Window
            {
                Title = "ioSender closed unexpectedly",
                Width = 640,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                ShowInTaskbar = false
            };
            if (owner != null && owner.IsVisible)
                win.Owner = owner;

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            intro.Inlines.Add(new System.Windows.Documents.Run("The previous ioSender session ended with an unhandled error. To report it:"));
            intro.Inlines.Add(new System.Windows.Documents.LineBreak());
            intro.Inlines.Add(new System.Windows.Documents.Run("  1.  Click ") { });
            intro.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run("Send report")));
            intro.Inlines.Add(new System.Windows.Documents.Run(" — your browser opens a pre-filled GitHub issue, and a small Explorer strip opens across the top of the screen with a crash-report ZIP selected."));
            intro.Inlines.Add(new System.Windows.Documents.LineBreak());
            intro.Inlines.Add(new System.Windows.Documents.Run("  2.  Drag that ZIP down from the top strip into the issue’s comment box below it, then submit."));
            intro.Inlines.Add(new System.Windows.Documents.LineBreak());
            intro.Inlines.Add(new System.Windows.Documents.Run("The details below are what the report will contain (no data is sent automatically)."));
            Grid.SetRow(intro, 0);

            var preview = new TextBox
            {
                Text = summary,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11
            };
            Grid.SetRow(preview, 1);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var btnSend = new Button { Content = "Send report…", MinWidth = 120, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
            var btnDismiss = new Button { Content = "Not now", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
            btnSend.Click += (s, e) => { send = true; win.Close(); };
            btnDismiss.Click += (s, e) => { send = false; win.Close(); };
            buttons.Children.Add(btnSend);
            buttons.Children.Add(btnDismiss);
            Grid.SetRow(buttons, 2);

            grid.Children.Add(intro);
            grid.Children.Add(preview);
            grid.Children.Add(buttons);
            win.Content = grid;

            try { win.ShowDialog(); } catch { }
            return send;
        }

        private static void OpenGithubIssue(string summary, bool haveZip)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            string title = "Crash: " + FirstExceptionLine(summary) + " (" + version + ")";

            var body = new StringBuilder();
            body.AppendLine("**What I was doing when it crashed:**");
            body.AppendLine("_(please describe)_");
            body.AppendLine();
            if (haveZip)
            {
                body.AppendLine("**⚠️ Please drag the crash-report ZIP** (selected in the small Explorer strip at the top of your screen) " +
                                "**down into this comment box to attach the dump, then submit.**");
                body.AppendLine();
            }
            body.AppendLine("**Crash details:**");
            body.AppendLine();
            body.AppendLine("```");
            body.AppendLine(Truncate(summary, 5000));
            body.AppendLine("```");

            string url = IssuesNewUrl + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body.ToString());
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        }

        // Open Explorer with the file selected, then dock that window to a thin strip across the top of the
        // screen (just below the instruction banner) so it doesn't cover the GitHub issue below it (the drop
        // target). On a high-DPI display the default /select window is large enough to hide the whole page.
        // Best-effort: if the reposition can't run (COM blocked, window not found) the window stays put.
        private static void RevealInExplorer(string path, int topPx)
        {
            string folder = null;
            try { folder = Path.GetDirectoryName(path); } catch { }

            try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + path + "\"", UseShellExecute = true }); }
            catch { return; }

            if (!string.IsNullOrEmpty(folder))
                System.Threading.Tasks.Task.Run(() => DockExplorerToTop(folder, topPx));
        }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int SW_RESTORE = 9, SW_SHOW = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // Pull a window to the actual foreground, bypassing Windows' foreground lock (which otherwise just
        // flashes the taskbar button) by briefly attaching our input queue to the current foreground thread.
        private static void ForceForeground(IntPtr hWnd)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint pid;
                uint fgThread = GetWindowThreadProcessId(fg, out pid);
                uint myThread = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
                try
                {
                    ShowWindow(hWnd, SW_SHOW);
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(myThread, fgThread, false);
                }
            }
            catch { }
        }

        // Move the Explorer window showing `folder` to a centered top strip and pin it topmost so the browser
        // can't bury it. Everything here is in PHYSICAL pixels (the monitor work area comes from GetMonitorInfo,
        // which shares SetWindowPos's coordinate space) - Screen.WorkingArea reports LOGICAL pixels and would be
        // wrong on a scaled display. The window appears asynchronously, so retry briefly; we target the
        // FRONT-MOST match (EnumWindows walks top-to-bottom in Z-order) since Win11's tabbed Explorer can leave
        // several windows on one folder and the visible one is highest in Z.
        private static void DockExplorerToTop(string folder, int topPx)
        {
            try
            {
                string full = folder.TrimEnd('\\', '/');
                string leaf = Path.GetFileName(full);

                for (int attempt = 0; attempt < 25; attempt++)
                {
                    System.Threading.Thread.Sleep(120);
                    IntPtr target = FindFrontExplorerWindow(full, leaf);
                    if (target == IntPtr.Zero)
                        continue;

                    // Work area of the monitor the window is on, in physical pixels (SetWindowPos units).
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    RECT wa;
                    if (GetMonitorInfo(MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST), ref mi))
                        wa = mi.rcWork;
                    else
                        wa = new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };

                    int waW = wa.right - wa.left, waH = wa.bottom - wa.top;
                    int w = (int)(waW * 0.62);
                    int h = (int)((waH - topPx) * 0.45);
                    int left = wa.left + (waW - w) / 2;
                    int top = wa.top + topPx;

                    if (IsIconic(target))
                        ShowWindow(target, SW_RESTORE); // "highlighted on taskbar" = minimized; bring it back

                    SetWindowPos(target, HWND_TOPMOST, left, top, w, h, SWP_SHOWWINDOW);
                    ForceForeground(target); // actually raise it (topmost alone leaves it flashing on the taskbar)
                    return;
                }
            }
            catch { }
        }

        // First (top Z-order) visible Explorer frame whose folder title matches `full` (or its leaf name).
        private static IntPtr FindFrontExplorerWindow(string full, string leaf)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, l) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                var cn = new StringBuilder(64);
                GetClassName(hWnd, cn, cn.Capacity);
                if (cn.ToString() != "CabinetWClass")
                    return true; // File Explorer frame window class

                var tb = new StringBuilder(512);
                GetWindowText(hWnd, tb, tb.Capacity);
                string title = tb.ToString();
                int suffix = title.IndexOf(" - File Explorer", StringComparison.OrdinalIgnoreCase);
                if (suffix >= 0)
                    title = title.Substring(0, suffix);
                title = title.Trim();

                if (title.Equals(full, StringComparison.OrdinalIgnoreCase) ||
                    title.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false; // stop at first match (front-most)
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Pull the first exception "Type: message" line out of the summary for a concise issue title.
        private static string FirstExceptionLine(string summary)
        {
            foreach (var line in summary.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0 && (t.Contains("Exception") && t.Contains(":")) && !t.StartsWith("Exception  :"))
                    return Truncate(t, 120);
            }
            return "unhandled exception";
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s ?? "";
            return s.Substring(0, max) + "\n…(truncated)";
        }
    }
}
