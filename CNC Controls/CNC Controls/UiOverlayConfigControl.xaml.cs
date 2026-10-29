/*
 * UiOverlayConfigControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Settings > User Interface > Overlay. A library front end for the overlay engine in
 * CNC Core/ConfigOverlay.cs, which until now was reachable only as a file dialog behind
 * Help > Support > Apply configuration overlay.
 *
 * Nothing about the merge itself lives here. Apply stages the fragment and restarts through the same
 * AppConfig.StageOverlay + GrblConfigView.DoRestart pair the menu item uses, for the reason set out in
 * AppConfig's overlay region: a running instance must never write App.config, because dozens of call
 * sites save the whole in-memory state during a session and would clobber it moments later. The merge
 * therefore happens in TryLoad, at the one point where the document on disk is authoritative.
 *
 * The consequence that shapes this page: APPLY RESTARTS THE APP. So selection does nothing but describe,
 * and the apply is always an explicit button press behind a confirmation that names the sections.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using CNC.Core;

namespace CNC.Controls
{
    public partial class UiOverlayConfigControl : UserControl, ISettingsPanelCategory
    {
        // One row in the dropdown: a readable label, where the file is, and what reading it found.
        // Sections are read once at list time rather than on selection, so a file that is not an overlay
        // at all (or has become unreadable) is caught before it can be offered as applicable.
        private sealed class OverlayItem
        {
            public string Label { get; set; }
            public string Path { get; set; }
            public bool Shipped { get; set; }
            public string Name { get; set; }
            public List<ConfigOverlayEntry> Sections { get; set; }
            public string Error { get; set; }
            public DateTime Modified { get; set; }
        }

        public string SettingsCategory { get { return SettingsCategories.UserInterface; } }

        // After "Top-level tabs" (50): an overlay is mostly a way to adopt a tab/panel arrangement, so it
        // belongs at the end of the layout pages rather than up among the general preferences.
        public int SettingsOrder { get { return 60; } }

        public UiOverlayConfigControl()
        {
            InitializeComponent();
            Loaded += (s, e) => Refresh();
            // A settings page is built once and re-shown, so Loaded alone would leave the list showing
            // whatever was on disk when Settings was first opened - stale after a Browse, or after the
            // operator drops a file into the folder with the app running.
            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); };
        }

        private Window Owner { get { return Window.GetWindow(this); } }

        #region Listing

        private void Refresh()
        {
            var selected = (cboOverlay.SelectedItem as OverlayItem)?.Path;

            var items = new List<OverlayItem>();
            items.AddRange(Read(CNC.Core.Resources.OverlaysFolder, false));
            items.AddRange(Read(CNC.Core.Resources.ShippedOverlaysFolder, true));

            cboOverlay.ItemsSource = items;
            cboOverlay.SelectedItem = items.FirstOrDefault(i => i.Path == selected) ?? items.FirstOrDefault();

            btnUndo.IsEnabled = AppConfig.CanUndoOverlay;
            Describe(cboOverlay.SelectedItem as OverlayItem);
            ShowLastApplied();
        }

        // Every .ioconfig in a folder, described. A missing folder is simply empty - the per-user folder is
        // created when something is actually put in it, and no build ships the other one yet.
        private static IEnumerable<OverlayItem> Read(string folder, bool shipped)
        {
            var list = new List<OverlayItem>();

            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    return list;

                foreach (var file in Directory.GetFiles(folder, "*" + ConfigOverlay.FileExtension).OrderBy(f => f))
                {
                    var item = new OverlayItem
                    {
                        Path = file,
                        Shipped = shipped,
                        Modified = File.GetLastWriteTime(file),
                        Sections = new List<ConfigOverlayEntry>()
                    };

                    try
                    {
                        var doc = XDocument.Load(file);
                        item.Name = ConfigOverlay.NameOf(doc);
                        item.Sections = ConfigOverlay.Describe(doc);
                        if (item.Sections.Count == 0)
                            item.Error = "contains no configuration sections";
                    }
                    catch (Exception ex)
                    {
                        item.Error = ex.Message;
                    }

                    string label = string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileNameWithoutExtension(file) : item.Name;
                    if (shipped)
                        label += "  (shipped)";
                    if (item.Error != null)
                        label += "  (unreadable)";
                    item.Label = label;

                    list.Add(item);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("config", "Overlay folder listing failed (" + folder + ") - " + ex.Message);
            }

            return list;
        }

        private void Describe(OverlayItem item)
        {
            if (item == null)
            {
                lblOverlayName.Text = "No overlays available.";
                lblOverlaySource.Text = "Use \"Browse PC...\" to load one, or export your own current layout from Help > Support.";
                lblOverlaySections.Text = string.Empty;
                btnApply.IsEnabled = false;
                return;
            }

            lblOverlayName.Text = string.IsNullOrWhiteSpace(item.Name)
                                ? System.IO.Path.GetFileNameWithoutExtension(item.Path)
                                : item.Name;
            lblOverlaySource.Text = (item.Shipped ? "Shipped with this build" : "Your Overlays folder")
                                  + " - " + System.IO.Path.GetFileName(item.Path)
                                  + ", " + item.Modified.ToString("d MMM yyyy HH:mm");

            if (item.Error != null)
            {
                lblOverlaySections.Text = "This file could not be read as an overlay: " + item.Error;
                btnApply.IsEnabled = false;
                return;
            }

            lblOverlaySections.Text = string.Join("\n", item.Sections.Select(
                s => s.Merge ? s.Key + "  (merged into your existing values)" : s.Key));
            btnApply.IsEnabled = true;
        }

        // What the LAST load actually layered on, as recorded by AppConfig while reading the document.
        // Worth showing: an overlay's whole point is that it changed the screen, so after the restart the
        // operator is looking at a different app and this is the only place that says why.
        private void ShowLastApplied()
        {
            if (AppConfig.LastOverlayApplied == null || AppConfig.LastOverlayApplied.Count == 0)
            {
                lblLastApplied.Text = string.Empty;
                return;
            }

            lblLastApplied.Text = "Applied on this launch: "
                                + (string.IsNullOrWhiteSpace(AppConfig.LastOverlayName) ? "an overlay" : "\"" + AppConfig.LastOverlayName + "\"")
                                + " - " + string.Join(", ", AppConfig.LastOverlayApplied) + ".";
        }

        private void Overlay_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Describe(cboOverlay.SelectedItem as OverlayItem);
        }

        #endregion

        #region Commands

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load a configuration overlay",
                Filter = "Configuration overlay|*" + ConfigOverlay.FileExtension + ";*.config|All files|*.*",
                DefaultExt = ConfigOverlay.FileExtension,
                CheckFileExists = true
            };
            if (dlg.ShowDialog(Owner) != true)
                return;

            // Validate BEFORE copying: a file that is not an overlay must not end up in the library looking
            // like one. Same test and same wording as the Help menu item, so both routes agree on what an
            // overlay is.
            List<ConfigOverlayEntry> sections;
            try
            {
                sections = ConfigOverlay.Describe(XDocument.Load(dlg.FileName));
            }
            catch (Exception ex)
            {
                AppDialogs.Show(Owner, "That file could not be read as a configuration overlay:\n\n" + ex.Message,
                                "Load Configuration Overlay", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            if (sections.Count == 0)
            {
                AppDialogs.Show(Owner,
                    "That file contains no configuration sections, so there is nothing to apply.\n\n"
                    + "An overlay is a trimmed App.config: an <AppConfig> document holding just the "
                    + "<section> entries it means to change.",
                    "Load Configuration Overlay", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            string target;
            try
            {
                Directory.CreateDirectory(CNC.Core.Resources.OverlaysFolder);
                target = System.IO.Path.Combine(CNC.Core.Resources.OverlaysFolder,
                                                System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) + ConfigOverlay.FileExtension);

                if (File.Exists(target) && !string.Equals(target, dlg.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (AppDialogs.Show(Owner,
                            "Your Overlays folder already holds \"" + System.IO.Path.GetFileName(target) + "\".\n\nReplace it?",
                            "Load Configuration Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        return;
                }

                File.Copy(dlg.FileName, target, true);
            }
            catch (Exception ex)
            {
                AppDialogs.Show(Owner, "That overlay could not be copied into your Overlays folder:\n\n" + ex.Message,
                                "Load Configuration Overlay", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            Refresh();
            var loaded = (cboOverlay.ItemsSource as IEnumerable<OverlayItem>)?.FirstOrDefault(i => i.Path == target);
            if (loaded != null)
                cboOverlay.SelectedItem = loaded;

            // Deliberately NOT applied here. Loading is reversible and instant; applying restarts the app,
            // so it stays a separate, deliberate press.
            AppDialogs.Show(Owner,
                "Loaded into your Overlays folder, and selected above.\n\n"
                + "Press Apply to layer it onto your configuration - that is when ioSender restarts.",
                "Load Configuration Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var item = cboOverlay.SelectedItem as OverlayItem;
            if (item == null || item.Error != null || item.Sections == null || item.Sections.Count == 0)
                return;

            string what = string.Join("\n", item.Sections.Select(
                s => "    " + (s.Merge ? s.Key + "  (merged into your existing values)" : s.Key)));

            if (AppDialogs.Show(Owner,
                    (string.IsNullOrWhiteSpace(item.Name) ? "This overlay" : "\"" + item.Name + "\"")
                    + " will change these parts of your configuration:\n\n" + what
                    + "\n\nEverything else is left exactly as it is. Your current configuration is backed up first, "
                    + "and \"Undo last overlay\" puts it back.\n\nioSender will restart. Apply it?",
                    "Apply Configuration Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Save first: the backup StageOverlay takes is a copy of the file on DISK, so anything changed
            // this session and not yet written would be silently missing from what Undo restores.
            AppConfig.Settings.Save();

            if (!AppConfig.StageOverlay(item.Path))
            {
                AppDialogs.Show(Owner, "The overlay could not be staged - see the debug log for the reason. Nothing has been changed.",
                                "Apply Configuration Overlay", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            GrblConfigView.DoRestart();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (!AppConfig.CanUndoOverlay)
            {
                AppDialogs.Show(Owner, "There is no pre-overlay configuration to go back to.",
                                "Undo Configuration Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (AppDialogs.Show(Owner,
                    "Restore your configuration as it was immediately before the last overlay was applied?\n\n"
                    + "Any settings changed since then are lost. ioSender will restart.",
                    "Undo Configuration Overlay", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            if (!AppConfig.StageOverlayUndo())
            {
                AppDialogs.Show(Owner, "The backup could not be staged for restore - see the debug log. Nothing has been changed.",
                                "Undo Configuration Overlay", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            GrblConfigView.DoRestart();
        }

        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(CNC.Core.Resources.OverlaysFolder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + CNC.Core.Resources.OverlaysFolder.TrimEnd('\\') + "\"");
            }
            catch (Exception ex)
            {
                AppDialogs.Show(Owner, "That folder could not be opened:\n\n" + ex.Message,
                                "Overlays Folder", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        }

        #endregion
    }
}
