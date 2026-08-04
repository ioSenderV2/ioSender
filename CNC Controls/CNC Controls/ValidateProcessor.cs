/*
 * ValidateProcessor.cs - part of CNC Controls library
 *
 * The desktop face of "Validate controller". Everything that talks to the CONTROLLER - check mode,
 * the lock-step ack streaming, error recovery, the NVRAM snapshot/restore, the capability-tailored
 * test set, the safe visualisation job and the report text - lives in CNC.Core.ControllerValidator.
 * What is left here is what talks to the operator:
 *
 *   - the live progress panel and the results window (both hand-built WPF)
 *   - the clipboard export
 *   - every message and prompt.
 *
 * That last one is not just tidiness. The "validation aborted" strings live in THIS assembly's
 * LibStrings.xaml, and this solution has three different LibStrings classes - a FindResource call
 * moved into Core would bind to Core's dictionary, compile perfectly, and silently resolve nothing.
 * So ControllerValidator returns a ValidationOutcome and this file decides what to say about it.
 */

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNC.Core;

namespace CNC.Controls
{
    public static class ValidateProcessor
    {
        private static bool _running = false;

        /// <summary>
        /// Run the validation against the connected controller. Returns false if it could not run
        /// (not connected, busy, cancelled); the reason is shown to the user. Must be called on the UI thread.
        /// </summary>
        public static bool Run(GrblViewModel model)
        {
            if (_running)
                return false;

            var validator = new ControllerValidator();

            string reason = validator.NotReady(model);
            if (reason != null)
            {
                AppDialogs.Show(reason, "Validate controller", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // When homing is configured, require it to be done first so the position-dependent tests
            // (G28/G30/G53) are accurate - offer to home now (the user's machine, so always ask).
            if (validator.NeedsHoming(model))
            {
                var ans = AppDialogs.Show(
                    "The machine is not homed.\r\n\r\nValidation needs it homed so position-dependent commands (G28/G30/G53) are tested accurately. Home the machine now and continue?",
                    "Validate controller", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (ans != MessageBoxResult.Yes)
                    return false;
                if (!validator.HomeMachine(model))
                {
                    AppDialogs.Show(LibStrings.FindResource("ValHomingCancelled"), "Validate controller", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Small non-modal progress panel (bottom-right): live pass/fail tally and current test, with a
            // "View Summary" button that enables when the run finishes. Streaming stays off-screen and fast.
            // Raised by the validator once it knows how many real feature tests this controller gets.
            ValidateProgress progress = null;
            validator.Started = total => { progress = new ValidateProgress(total); progress.Show(); };
            validator.Progress = (n, pass, fail) => { if (progress != null) progress.Update(n, pass, fail); };

            ValidationOutcome outcome;
            _running = true;
            try
            {
                outcome = validator.Run(model);
            }
            finally
            {
                _running = false;
            }

            // Could not even start - nothing to summarise. The validator has already left check mode and
            // put the work offsets back by now; the old code showed this message from INSIDE the run, so
            // the restore waited on the operator dismissing a dialog. Cleanup first, then report.
            if (outcome != ValidationOutcome.Completed)
            {
                if (progress != null)
                    progress.Close();

                AppDialogs.Show(
                    LibStrings.FindResource(outcome == ValidationOutcome.NoCheckMode ? "ValNoCheckMode" : "ValSetupRejected"),
                    "Validate controller", MessageBoxButton.OK, MessageBoxImage.Warning);

                return true;
            }

            // Nothing is loaded into the program buffer. Validation used to build a runnable "passed moves"
            // program out of the motion tests and leave it ready for Cycle Start; it drove a real machine
            // into its front right corner at full speed and was removed (see ControllerValidator). Check
            // mode is where this feature exercises the controller - the results window is the deliverable.
            string status = validator.Aborted ? "Completed (stopped early)." : "Completed.";

            // Enable "View Summary" on the (non-modal) panel - the user opens the full report when ready.
            progress.SetCompleted(status, () => ShowResults(validator));

            return true;
        }

        // Small non-modal progress panel shown bottom-right during a validation run: live "Test n of M",
        // a pass/fail tally, and a "View Summary" button that stays disabled until the run completes.
        private class ValidateProgress
        {
            private readonly Window win;
            private readonly TextBlock testLine, tally;
            private readonly Button summary;
            private readonly int total;

            public ValidateProgress(int total)
            {
                this.total = total;

                testLine = new TextBlock { Text = "Starting...", Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
                tally = new TextBlock { Text = "Pass: 0    Fail: 0" };
                summary = new Button { Content = "View Summary", IsEnabled = false, MinWidth = 110,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };

                var panel = new StackPanel { Margin = new Thickness(12) };
                panel.Children.Add(testLine);
                panel.Children.Add(tally);
                panel.Children.Add(summary);

                win = new Window {
                    Title = "Validating controller",
                    Content = panel,
                    SizeToContent = SizeToContent.Height,
                    Width = 230,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.ToolWindow,
                    ShowInTaskbar = false,
                    Topmost = true
                };
                if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
                    win.Owner = Application.Current.MainWindow;

                // Park it in the bottom-right of the work area so it doesn't cover the 3D view / DRO.
                win.Loaded += (s, e) => {
                    var wa = SystemParameters.WorkArea;
                    win.Left = wa.Right - win.ActualWidth - 16;
                    win.Top = wa.Bottom - win.ActualHeight - 16;
                };
            }

            public void Show() => win.Show();

            public void Update(int testNum, int pass, int fail)
            {
                testLine.Text = string.Format("Test {0} of {1}", testNum, total);
                tally.Text = string.Format("Pass: {0}    Fail: {1}", pass, fail);
            }

            // Run finished: show the final status and enable the summary button.
            public void SetCompleted(string status, System.Action onViewSummary)
            {
                testLine.Text = status;
                summary.IsEnabled = true;
                summary.Click += (s, e) => { win.Close(); onViewSummary(); };   // dismiss the panel, then show the report
            }

            public void Close() => win.Close();
        }

        #region Results window

        private static void ShowResults(ControllerValidator validator)
        {
            var tests = validator.Tests;

            // Helper lines only appear if they failed (which would mean a set-up/restore problem).
            var shown = tests.Where(x => !x.Helper || !x.Passed).ToList();
            int total = tests.Count(x => !x.Helper);
            int passed = tests.Count(x => !x.Helper && x.Passed);
            int failed = total - passed;

            var win = new Window {
                Title = "Validate controller",
                SizeToContent = SizeToContent.Width,
                Height = 560,
                MinWidth = 440,
                ResizeMode = ResizeMode.CanResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                win.Owner = Application.Current.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { Margin = new Thickness(12) };

            // Header: firmware / axes / summary.
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(new TextBlock {
                Text = string.Format("{0}{1}  -  {2} axes",
                    GrblInfo.Firmware,
                    string.IsNullOrEmpty(GrblInfo.Version) ? "" : " " + GrblInfo.Version,
                    GrblInfo.NumAxes),
                FontWeight = FontWeights.Bold
            });
            header.Children.Add(new TextBlock {
                Text = string.Format("{0} of {1} features passed{2}", passed, total,
                    failed > 0 ? string.Format("  -  {0} failed", failed) : ""),
                Foreground = failed > 0 ? Brushes.Firebrick : Brushes.ForestGreen,
                Margin = new Thickness(0, 2, 0, 0)
            });
            if (validator.Aborted)
                header.Children.Add(NoteBlock("Validation stopped early: the controller could not return to check mode after an error. Remaining features were not tested."));
            if (validator.Unhomed)
                header.Children.Add(NoteBlock("A recovery reset left the machine un-homed; position-dependent results (G28/G30/G53) may be affected. Re-home before running a job."));
            header.Children.Add(new TextBlock {
                Text = "Work offsets and tool table were restored after the run; machine settings ($$) are not modified.",
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            root.Children.Add(header);

            // Buttons (docked bottom so they stay visible as the list scrolls).
            var buttons = new StackPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var copy = new Button { Content = "Copy", MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
            var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 75 };
            copy.Click += (s, e) => {
                try { Clipboard.SetText(validator.BuildReportText()); }
                catch { /* clipboard occasionally busy - ignore */ }
            };
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            root.Children.Add(buttons);

            // Results list grouped by category.
            var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            string lastCat = null;
            foreach (var test in shown)
            {
                string cat = test.Helper ? "Set-up" : test.Category;
                if (cat != lastCat)
                {
                    list.Children.Add(new TextBlock {
                        Text = cat,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, lastCat == null ? 0 : 10, 0, 3)
                    });
                    lastCat = cat;
                }
                list.Children.Add(BuildRow(test));
            }

            root.Children.Add(new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = list
            });

            win.Content = root;
            win.ShowDialog();
        }

        private static TextBlock NoteBlock(string text)
        {
            return new TextBlock {
                Text = text,
                Foreground = Brushes.DarkOrange,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private static UIElement BuildRow(ValidationTest test)
        {
            var row = new Grid { Margin = new Thickness(8, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });        // marker
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180, GridUnitType.Pixel) }); // feature
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });        // gcode / detail

            var marker = new TextBlock {
                Text = test.Passed ? "✓" : "✗",
                Foreground = test.Passed ? Brushes.ForestGreen : Brushes.Firebrick,
                FontWeight = FontWeights.Bold,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(marker, 0);

            var feature = new TextBlock {
                Text = test.Feature,
                Foreground = test.Passed ? Brushes.Black : Brushes.Firebrick,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = test.Code,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0)
            };
            Grid.SetColumn(feature, 1);

            string detailText;
            if (test.Passed)
                detailText = test.Code;
            else if (test.TimedOut)
                detailText = "(no response)";
            else
            {
                string msg = ControllerValidator.ErrorMessage(test.Response);
                detailText = msg == null ? test.Response : test.Response + " - " + msg;
            }

            var detail = new TextBlock {
                Text = detailText,
                Foreground = test.Passed ? Brushes.Gray : Brushes.Firebrick,
                FontFamily = new FontFamily("Consolas, Courier New"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(detail, 2);

            row.Children.Add(marker);
            row.Children.Add(feature);
            row.Children.Add(detail);

            return row;
        }

        #endregion
    }
}
