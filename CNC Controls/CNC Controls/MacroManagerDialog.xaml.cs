/*
 * MacroManagerDialog.xaml.cs - part of CNC Controls library
 *
 * Macro manager presented as a DataGrid (one row per macro: Name, Prompt-before-run,
 * and the F-key that runs it). Name and Prompt are edited in-line. Edit opens the macro's
 * stored definition (inline G-code, or the "@<path>" reference line) in the default .txt
 * editor and reads it back; View opens what it points to - the referenced file for an
 * "@<path>" macro (created if missing), otherwise the code (no read-back). Opened from the
 * Settings:App page; the caller persists on close.
 *
 * The F-key column is derived from the macro Id (Id n is run by Fn, per
 * JobControl.FnKeyHandler) - it is shown read-only.
 *
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;

namespace CNC.Controls
{
    /// <summary>
    /// Interaction logic for MacroManagerDialog.xaml
    /// </summary>
    public partial class MacroManagerDialog : Window
    {
        private readonly ObservableCollection<CNC.GCode.Macro> macros;
        private readonly List<string> tempFiles = new List<string>();

        /// <summary>F-key choices for the Key column dropdown (— / F1..F12). Bound from XAML.</summary>
        public List<FKeyOption> FKeyOptions { get; } = FKeyOption.All();

        public MacroManagerDialog(ObservableCollection<CNC.GCode.Macro> macros)
        {
            InitializeComponent();

            this.macros = macros;

            grdMacros.ItemsSource = macros;
            if (macros.Count > 0)
                grdMacros.SelectedIndex = 0;

            UpdateButtons();
            Closed += MacroManagerDialog_Closed;
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

        private int FirstFreeFKey()
        {
            var used = new HashSet<int>();
            foreach (var m in macros)
                if (m.FKey >= 1 && m.FKey <= 12)
                    used.Add(m.FKey);

            for (int i = 1; i <= 12; i++)
                if (!used.Contains(i))
                    return i;

            return 0;   // all twelve taken
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

            var macro = new CNC.GCode.Macro { Id = id, Name = "Macro " + id, ConfirmOnExecute = true, Code = string.Empty, FKey = FirstFreeFKey() };
            macros.Add(macro);

            grdMacros.SelectedItem = macro;
            grdMacros.ScrollIntoView(macro);
            grdMacros.Focus();
            grdMacros.CurrentCell = new DataGridCellInfo(macro, grdMacros.Columns[0]);
            grdMacros.BeginEdit();   // let the user name it straight away
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
                    MessageBox.Show("Could not create the referenced macro file:\r\n\r\n" + refPath + "\r\n\r\n" + ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                LaunchEditor(refPath);
            }
            else
                LaunchEditor(WriteTempMacro(macro));
        }

        // Edit: edit the macro's stored definition - the inline G-code, or the "@<path>" reference
        // line itself (so the path can be changed, repointed, or converted back to inline). Read back.
        private void btnEdit_Click(object sender, RoutedEventArgs e)
        {
            var macro = Selected;
            if (macro == null)
                return;

            string path = WriteTempMacro(macro);
            if (!LaunchEditor(path))
                return;

            var result = MessageBox.Show(
                string.Format("Editing \"{0}\" in your text editor.\r\n\r\nSave your changes there, then click OK to apply them to the macro.\r\nClick Cancel to discard.", macro.Name),
                "ioSender", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (result == MessageBoxResult.OK)
            {
                try
                {
                    macro.Code = File.ReadAllText(path).TrimEnd('\r', '\n');
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not read the edited macro back:\r\n\r\n" + ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            var macro = Selected;
            if (macro == null)
                return;

            if (MessageBox.Show(string.Format("Delete macro \"{0}\"?", macro.Name), "ioSender",
                                 MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                macros.Remove(macro);
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // If the macro is an "@<path>" reference, return the resolved file path (relative paths
        // against the config folder); otherwise null. Mirrors MacroProcessor's run-time resolver.
        private static string GetReferencedFilePath(string code)
        {
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

            if (!Path.IsPathRooted(path))
                path = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, path);

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
                MessageBox.Show("Could not open the macro in a text editor:\r\n\r\n" + ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private void MacroManagerDialog_Closed(object sender, EventArgs e)
        {
            foreach (var path in tempFiles)
            {
                try { File.Delete(path); } catch { /* still open / already gone - leave it for the OS to clean */ }
            }
        }
    }

    // For the macro grid's "File" column: when a macro's body is an "@<path>" reference, returns
    // the file name for display (or, with ConverterParameter "path", the full resolved path for a
    // tooltip); returns null for an inline macro so the cell stays blank.
    public class MacroReferenceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string code = value as string;
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

            if ((parameter as string) == "path")
            {
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, path);
                return "References: " + path;
            }

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

        public static List<FKeyOption> All()
        {
            var list = new List<FKeyOption> { new FKeyOption { Value = 0, Label = "—" } };
            for (int i = 1; i <= 12; i++)
                list.Add(new FKeyOption { Value = i, Label = "F" + i });
            return list;
        }
    }
}
