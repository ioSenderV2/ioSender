/*
 * HangWatchdogReporter.cs - part of Grbl Code Sender
 *
 * Zero-infrastructure report-it dialog for a controller restart caused by the iMXRT1062 driver's
 * hang watchdog (WDOG1 timeout mid-dispatch - see grblHAL's protocol.c/usb_serial_ard.cpp). Modeled
 * on CrashReporter.cs's pattern: no backend, no auto-send - offers a pre-filled GitHub "new issue"
 * page against the firmware repo (the g-code line the controller hung on is a firmware-side bug to
 * chase, not an ioSender one) and leaves sending entirely up to the user.
 */

using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace GCode_Sender
{
    internal static class HangWatchdogReporter
    {
        // Firmware repo that hang reports are filed against - this is where the actual bug (whatever
        // g-code/command construct caused the hang) would get fixed.
        private const string IssuesNewUrl = "https://github.com/stevenrwood/iMXRT1062/issues/new";

        // Shows a consent dialog naming the line/command the controller was stuck on when the hang
        // watchdog fired. Send opens a pre-filled GitHub issue; either button just closes the dialog -
        // no data is sent automatically. Best-effort: never throws into the caller (a connect-time hook).
        public static void Report(Window owner, string line)
        {
            try
            {
                ShowDialog(owner, line);
            }
            catch { }
        }

        private static void ShowDialog(Window owner, string line)
        {
            var win = new Window
            {
                Title = "Controller restarted after a hang",
                Width = 560,
                Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
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
            intro.Inlines.Add(new System.Windows.Documents.Run(
                "The controller just restarted on its own - the firmware's hang watchdog detected it was stuck " +
                "processing a line and reset itself rather than staying wedged. This is a firmware bug report " +
                "candidate: whatever the line below was doing made the controller stop responding."));
            Grid.SetRow(intro, 0);

            var preview = new TextBox
            {
                Text = line,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(preview, 1);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnSend = new Button { Content = "Report on GitHub…", MinWidth = 140, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
            var btnDismiss = new Button { Content = "Not now", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
            btnSend.Click += (s, e) => { OpenGithubIssue(line); win.Close(); };
            btnDismiss.Click += (s, e) => win.Close();
            buttons.Children.Add(btnSend);
            buttons.Children.Add(btnDismiss);
            Grid.SetRow(buttons, 2);

            grid.Children.Add(intro);
            grid.Children.Add(preview);
            grid.Children.Add(buttons);
            win.Content = grid;
            CNC.Controls.DialogScaling.Apply(win);

            win.ShowDialog();
        }

        private static void OpenGithubIssue(string line)
        {
            string title = "Hang watchdog fired: " + Truncate(line, 80);

            var body = new StringBuilder();
            body.AppendLine("**What I was doing when it hung:**");
            body.AppendLine("_(please describe - what job/macro was running, any recent commands)_");
            body.AppendLine();
            body.AppendLine("**Line/command the controller was stuck on:**");
            body.AppendLine("```");
            body.AppendLine(line);
            body.AppendLine("```");
            body.AppendLine();
            body.AppendLine("**Firmware build:** " + (string.IsNullOrEmpty(CNC.Core.GrblInfo.BuildStamp) ? CNC.Core.GrblInfo.Version : CNC.Core.GrblInfo.BuildStamp));
            body.AppendLine("**ioSender version:** " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"));

            string url = IssuesNewUrl + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body.ToString());
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s ?? "";
            return s.Substring(0, max) + "…";
        }
    }
}
