/*
 * MacroManagerDialog.xaml.cs - part of CNC Controls library
 *
 * Macro manager presented as a DataGrid (one row per macro: Name, File reference if any,
 * Prompt-before-run, and the F-key that runs it). Name and Prompt are editable in-line; the F-key
 * dropdown (F1-F12 or "-" for none, kept unique across macros) is also editable in-line. Create and
 * Edit both open MacroCreateDialog (Name/Prompt/Add-to-menu/Key/Code, commit-on-OK only). View opens
 * what the macro points to - the referenced file for an "@<path>" macro (created if missing),
 * otherwise the code (no read-back). Opened from the Settings:Macros tab; the caller persists on close.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;

using CNC.Core;
namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for MacroManagerDialog.xaml
    /// </summary>
    public partial class MacroManagerDialog : UserControl, ISettingsEditorTab, IRestartRequired
    {
        private readonly ObservableCollection<CNC.GCode.Macro> macros;
        private readonly List<string> tempFiles = new List<string>();
        private Dictionary<int, bool> baselineAddToMenu;

        /// <summary>F-key choices for the Key column dropdown (— / F1..F12). Bound from XAML.</summary>
        public List<FKeyOption> FKeyOptions { get; } = FKeyOption.All();

        // The menu items built from "Add to menu" macros are only built once at startup (MainWindow's
        // BuildMacroMenuItems) - there's no live rebuild, so any change needs a restart to take effect.
        public event EventHandler<RestartRequiredEventArgs> RestartRequired;

        public MacroManagerDialog(ObservableCollection<CNC.GCode.Macro> macros)
        {
            InitializeComponent();

            this.macros = macros;

            grdMacros.ItemsSource = macros;
            if (macros != null && macros.Count > 0)
                grdMacros.SelectedIndex = 0;

            baselineAddToMenu = SnapshotAddToMenu();

            UpdateButtons();
            Unloaded += MacroManagerDialog_Unloaded;
        }

        private Dictionary<int, bool> SnapshotAddToMenu()
        {
            var snapshot = new Dictionary<int, bool>();
            foreach (var m in macros)
                snapshot[m.Id] = m.AddToMenu;
            return snapshot;
        }

        // Save-on-leave: edits mutate the shared macros collection live, so persistence is all that's needed
        // when the Macros tab is left. Called by the settings host on tab-switch / view-leave. If any
        // macro's "Add to menu" changed (including a new/deleted macro that had it set), signal the host
        // to surface its Restart button - the main menu can't be rebuilt live.
        public void Commit()
        {
            var current = SnapshotAddToMenu();
            bool changed = current.Count != baselineAddToMenu.Count
                || current.Any(kv => !baselineAddToMenu.TryGetValue(kv.Key, out var was) || was != kv.Value);

            AppConfig.Settings.Save();

            if (changed)
            {
                RestartRequired?.Invoke(this, new RestartRequiredEventArgs("Restart required to apply Macros \"Add to menu\" changes."));
                baselineAddToMenu = current;
            }
        }

        private CNC.GCode.Macro Selected { get { return grdMacros.SelectedItem as CNC.GCode.Macro; } }

        private void UpdateButtons()
        {
            bool sel = Selected != null;
            btnView.IsEnabled = btnEdit.IsEnabled = btnDelete.IsEnabled = sel;
        }

        private void grdMacros_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        // An F-key can be on only one macro: when one is assigned a key, take it off any other macro
        // that had it. (None/0 is exempt - several macros may have no key.)
        private void FKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ComboBox cb) || !(cb.DataContext is CNC.GCode.Macro macro) || !(cb.SelectedValue is int key) || key == 0)
                return;

            foreach (var m in macros)
                if (!ReferenceEquals(m, macro) && m.FKey == key)
                    m.FKey = 0;
        }

        // Keep names non-empty (they label the row, the run prompt and the macro flyout button).
        private void grdMacros_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit && e.Column == grdMacros.Columns[0] &&
                 e.Row.Item is CNC.GCode.Macro macro && e.EditingElement is TextBox tb && string.IsNullOrWhiteSpace(tb.Text))
                tb.Text = "Macro " + macro.Id;
        }

        private void btnCreate_Click(object sender, RoutedEventArgs e)
        {
            int id = 0;
            foreach (var m in macros)
                id = Math.Max(id, m.Id);
            id++;

            var macro = new CNC.GCode.Macro { Id = id, Name = "Macro " + id, ConfirmOnExecute = true, Code = string.Empty, FKey = 0 };

            var dlg = new MacroCreateDialog(macro, macros) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true)
                return;

            macros.Add(macro);

            grdMacros.SelectedItem = macro;
            grdMacros.ScrollIntoView(macro);
        }

        // View: open what the macro points to. For an "@<path>" reference that's the referenced
        // file itself (created if missing, so a new external macro can be authored); otherwise the
        // macro's G-code. Edits made here are not read back into the macro - for a referenced file
        // that's fine (the file is the live source, re-read on each run).
        private void btnView_Click(object sender, RoutedEventArgs e)
        {
            var macro = Selected;
            if (macro == null)
                return;

            string refPath = GetReferencedFilePath(macro.Code);
            if (refPath != null)
            {
                try
                {
                    if (!File.Exists(refPath))
                        File.WriteAllText(refPath, string.Empty);   // allow authoring a new referenced file
                }
                catch (Exception ex)
                {
                    AppDialogs.Show("Could not create the referenced macro file:\r\n\r\n" + refPath + "\r\n\r\n" + ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                LaunchEditor(refPath);
            }
            else
                LaunchEditor(WriteTempMacro(macro));
        }

        // Edit: reopens the same Name/Prompt/Add-to-menu/Key/Code dialog used by Create, pre-filled from
        // the selected macro. If its body is an "@<path>" reference, the dialog offers to jump straight
        // to editing the real file in a text editor before showing the fields.
        private void btnEdit_Click(object sender, RoutedEventArgs e)
        {
            var macro = Selected;
            if (macro == null)
                return;

            new MacroCreateDialog(macro, macros) { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            var macro = Selected;
            if (macro == null)
                return;

            if (AppDialogs.Show(string.Format("Delete macro \"{0}\"?", macro.Name), "ioSender",
                                 MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                macros.Remove(macro);
        }

        // If 'code' is a single "@<path>" line and <path> has no extension, appends the default
        // ".macro" extension to the path portion - so what's typed/stored/displayed is unambiguous
        // rather than relying on resolve-time defaulting alone.
        // The implementation moved to CNC.Core.MacroRunner: the run-time resolver
        // (MacroRunner.ResolveFileReference) applies the same rule as a safety net for references
        // normalized before this existed, and it lives in Core now - so this is the one definition
        // both use rather than two that can drift.
        internal static string NormalizeMacroReference(string code)
        {
            return MacroRunner.NormalizeMacroReference(code);
        }

        // If the macro is an "@<path>" reference, return the resolved file path (relative paths
        // against the config folder; extensionless paths default to ".macro" - see
        // NormalizeMacroReference); otherwise null. Mirrors MacroProcessor's run-time resolver.
        internal static string GetReferencedFilePath(string code)
        {
            code = NormalizeMacroReference(code);

            if (string.IsNullOrEmpty(code))
                return null;

            string trimmed = code.TrimStart();
            if (!trimmed.StartsWith("@"))
                return null;

            string path = trimmed.Substring(1);
            int nl = path.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0)
                path = path.Substring(0, nl);
            path = path.Trim();
            if (path.Length == 0)
                return null;

            try
            {
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, path);
            }
            catch { /* path has characters not valid for the filesystem - use it as typed */ }

            return path;
        }

        // Write the macro's G-code to a temp .txt named after the macro (so the editor's title is meaningful).
        private string WriteTempMacro(CNC.GCode.Macro macro)
        {
            string name = string.IsNullOrEmpty(macro.Name) ? "macro" : macro.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            string path = Path.Combine(Path.GetTempPath(), name + ".txt");
            File.WriteAllText(path, macro.Code ?? string.Empty);
            if (!tempFiles.Contains(path))
                tempFiles.Add(path);

            return path;
        }

        private bool LaunchEditor(string path)
        {
            try
            {
                // UseShellExecute opens the file with whatever app is associated with .txt (Notepad by default).
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Could not open the macro in a text editor:\r\n\r\n" + ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        // Fires when the Macros tab is switched away from (control leaves the visual tree). Clean up the temp
        // files written for external editing; they're recreated on demand when the tab is used again.
        private void MacroManagerDialog_Unloaded(object sender, RoutedEventArgs e)
        {
            foreach (var path in tempFiles)
            {
                try { File.Delete(path); } catch { /* still open / already gone - leave it for the OS to clean */ }
            }
            tempFiles.Clear();
        }
    }

    // For the macro grid's "File" column: when a macro's body is an "@<path>" reference, returns
    // the file name for display (or, with ConverterParameter "path", the full resolved path for a
    // tooltip); returns null for an inline macro so the cell stays blank.
    public class MacroReferenceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string path = MacroManagerDialog.GetReferencedFilePath(value as string);
            if (path == null)
                return null;

            if ((parameter as string) == "path")
                return "References: " + path;

            try { return Path.GetFileName(path); } catch { return path; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>An entry in the Key-column dropdown: 0 = none ("—"), 1-12 = F1-F12.</summary>
    public class FKeyOption
    {
        public int Value { get; set; }
        public string Label { get; set; }

        // F1 listed last - it's already the app's global Help hotkey (MainWindow's PreviewKeyDown),
        // so it's still selectable but not the first thing offered.
        public static List<FKeyOption> All()
        {
            var list = new List<FKeyOption> { new FKeyOption { Value = 0, Label = "—" } };
            for (int i = 2; i <= 12; i++)
                list.Add(new FKeyOption { Value = i, Label = "F" + i });
            list.Add(new FKeyOption { Value = 1, Label = "F1" });
            return list;
        }
    }
}
