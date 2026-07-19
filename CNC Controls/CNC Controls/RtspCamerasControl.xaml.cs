/*
 * RtspCamerasControl.xaml.cs - part of CNC Controls library
 *
 * Demo-shoot RTSP camera recording panel (see docs/demo-videos). One row per
 * CNC.Core.ObsBridge.Cameras entry plus a master "All" row, each a record/stop
 * glyph toggle (red circle = idle, red square = recording) that starts/stops that
 * camera's OBS Source Record filter independent of the main Record button. Only
 * meaningful with -demomarker's OBS bridge armed - see MainPanelRegistry
 * (visibility/placement gating) and ActionKeyBinder (the ObsCamA/BStart/Stop
 * keyboard shortcuts that drive the same ObsBridge.SetCameraRecording entry point
 * as these toggles).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public partial class RtspCamerasControl : UserControl
    {
        private const int AllTag = -1;   // Tag value marking the "All" master row, vs. a camera index

        // OBS's Source Record filters write here (set in OBS's own recording-path setting - ioSender has
        // no way to read that back, so this must match it; see docs/demo-videos).
        private static readonly string VideosFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ioSender", "Videos");

        // Static: shared between every instance of this panel AND MainWindow's own run-bar "All" button
        // (btnAllRecord), so whichever one you use, only one "session" is tracked and the file-naming
        // prompt only fires once regardless of which control triggered the stop.
        private static DateTime? allRecordingStartedAt;

        private readonly ToggleButton[] toggles;
        private ToggleButton allToggle;
        private bool suppressToggled;

        public RtspCamerasControl()
        {
            InitializeComponent();

            toggles = new ToggleButton[ObsBridge.Cameras.Length];
            for (int i = 0; i < ObsBridge.Cameras.Length; i++)
            {
                var row = new DockPanel { Margin = new Thickness(20, 2, 0, 2) };
                row.Children.Add(new Label { Content = ObsBridge.Cameras[i].Label, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });

                var toggle = new ToggleButton
                {
                    Uid = "tgl_rtspCam" + i,   // addressable for the UI test server; not a localized string (dynamic per-camera content)
                    ToolTip = ObsBridge.Cameras[i].Label + ": click to start/stop recording",
                    Style = (Style)FindResource("RecordToggleStyle"),
                    Tag = i,
                    Focusable = false
                };
                toggle.Checked += Toggle_Changed;
                toggle.Unchecked += Toggle_Changed;
                DockPanel.SetDock(toggle, Dock.Right);
                row.Children.Add(toggle);

                toggles[i] = toggle;
                rows.Children.Add(row);
            }

            // Master row: toggles all cameras together. Checked = "turn everything on" regardless of the
            // current mixed state; unchecked likewise turns everything off. Its own IsChecked reflects
            // "all recording" (set in ObsBridge_CamerasChanged), not the last action taken.
            rows.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
            var allRow = new DockPanel { Margin = new Thickness(20, 2, 0, 2) };
            allRow.Children.Add(new Label { Content = "All", Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            allToggle = new ToggleButton
            {
                Uid = "tgl_rtspCamAll",
                ToolTip = "All: click to start/stop every camera together",
                Style = (Style)FindResource("RecordToggleStyle"),
                Tag = AllTag,
                Focusable = false
            };
            allToggle.Checked += Toggle_Changed;
            allToggle.Unchecked += Toggle_Changed;
            DockPanel.SetDock(allToggle, Dock.Right);
            allRow.Children.Add(allToggle);
            rows.Children.Add(allRow);

            ObsBridge.CamerasChanged += ObsBridge_CamerasChanged;
            Unloaded += (s, e) => ObsBridge.CamerasChanged -= ObsBridge_CamerasChanged;
        }

        private void Toggle_Changed(object sender, RoutedEventArgs e)
        {
            if (suppressToggled)
                return;
            var toggle = (ToggleButton)sender;
            bool on = toggle.IsChecked == true;
            int tag = (int)toggle.Tag;
            if (tag == AllTag)
                ToggleAll(Window.GetWindow(this), on);
            else
                ObsBridge.SetCameraRecording(tag, on);
        }

        // Shared entry point for "turn every camera on/off together" - called by this panel's own "All"
        // row AND MainWindow's run-bar btnAllRecord, so either one drives the same ObsBridge state (and,
        // on stop, the same naming-prompt-and-move flow) regardless of which triggered it.
        public static void ToggleAll(Window owner, bool on)
        {
            for (int i = 0; i < ObsBridge.Cameras.Length; i++)
                ObsBridge.SetCameraRecording(i, on);

            if (on)
                allRecordingStartedAt = DateTime.Now;
            else
                PromptAndFileRecordings(owner);
        }

        // Ask for a scenario name (e.g. "Start Job") and move the 3 cameras' just-stopped recordings into
        // VideosFolder\<name>. Matches each camera by its OBS source name appearing in the filename (Source
        // Record's default naming includes it) and picks the newest match since the "All" row was last
        // switched on - not just the newest file overall, so a stale recording never gets grabbed by mistake.
        // async void (event-handler pattern): every wait below is a Task.Delay, NOT Thread.Sleep, so the UI
        // thread keeps pumping messages/rendering throughout - a blocking Sleep here previously froze the
        // whole window for the wait's duration, which also meant the individual toggles' IsChecked=false
        // (already set by the SetCameraRecording loop in ToggleAll, before this method was even called)
        // never got a chance to paint until everything finished, looking like they hadn't updated.
        private static async void PromptAndFileRecordings(Window owner)
        {
            var startedAt = allRecordingStartedAt;
            allRecordingStartedAt = null;

            string name = ScenarioNameDialog.Show(owner, "Name this recording (e.g. \"Start Job\"):", "OBS Control");
            if (string.IsNullOrWhiteSpace(name))
                return;   // cancelled - leave the files where OBS put them

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            string destFolder = Path.Combine(VideosFolder, name);

            // Neither OBS (Hybrid MP4's remux-on-stop) nor the RTSP cameras' own encoders finish
            // writing/closing the file the instant the filter reports stopped - give them a head start
            // before even looking for the files, on top of MoveWithRetry's per-file retry below.
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                await Task.Delay(5000);

                var moved = new List<string>();
                var missing = new List<string>();

                foreach (var cam in ObsBridge.Cameras)
                {
                    if (string.IsNullOrEmpty(cam.SourceName))
                        continue;

                    string file = NewestRecording(new[] { cam.SourceName, cam.Label }, startedAt);
                    if (file == null)
                    {
                        missing.Add(cam.Label);
                        continue;
                    }
                    try
                    {
                        Directory.CreateDirectory(destFolder);
                        await MoveWithRetryAsync(file, Path.Combine(destFolder, Path.GetFileName(file)));
                        moved.Add(cam.Label);
                    }
                    catch (Exception ex)
                    {
                        missing.Add(cam.Label + " (" + ex.Message + ")");
                    }
                }

                string msg = moved.Count > 0
                    ? "Moved " + moved.Count + " recording(s) to:\n" + destFolder
                    : "No recordings were found in:\n" + VideosFolder;
                if (missing.Count > 0)
                    msg += "\n\nNot moved: " + string.Join(", ", missing);
                AppDialogs.Show(owner, msg, "OBS Control");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        // Matches on ANY of the given candidates (source name, display label, ...) appearing in the
        // filename - what Source Record actually bakes in is whatever literal text is in that filter's
        // own Filename Formatting field, which isn't necessarily the OBS source name (e.g. this rig's two
        // camera filters were named "Front Left"/"Front Right" - the panel's Label - while the App filter
        // happens to say "ioSender" - its SourceName. Try both rather than assuming one convention.
        private static string NewestRecording(string[] candidates, DateTime? after)
        {
            if (!Directory.Exists(VideosFolder))
                return null;
            return Directory.EnumerateFiles(VideosFolder, "*.mp4")
                .Where(f => candidates.Any(c => !string.IsNullOrEmpty(c) &&
                    Path.GetFileName(f).IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(f => new FileInfo(f))
                // 5s slack: OBS's own filename timestamp/flush can trail the moment we told it to start.
                .Where(f => !after.HasValue || f.LastWriteTime >= after.Value.AddSeconds(-5))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        // OBS/the camera encoder can hold the file for a moment after the filter reports stopped - retry
        // instead of failing the whole move over a transient lock. Task.Delay (not Thread.Sleep) between
        // attempts so the UI stays responsive while this plays out.
        private static async Task MoveWithRetryAsync(string source, string dest, int attempts = 8, int delayMs = 750)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    File.Move(source, dest);
                    return;
                }
                catch (IOException) when (i < attempts - 1)
                {
                    await Task.Delay(delayMs);
                }
            }
        }

        // Resync all rows (including "All") from ObsBridge's own state - fired after SetCameraRecording
        // changes anything, regardless of whether the click came from this panel or a keyboard shortcut.
        private void ObsBridge_CamerasChanged()
        {
            suppressToggled = true;
            bool allRecording = true;
            for (int i = 0; i < toggles.Length; i++)
            {
                bool recording = ObsBridge.IsCameraRecording(i);
                toggles[i].IsChecked = recording;
                allRecording &= recording;
            }
            allToggle.IsChecked = allRecording;
            suppressToggled = false;
        }
    }
}
