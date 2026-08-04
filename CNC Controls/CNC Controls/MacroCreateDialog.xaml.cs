/*
 * MacroCreateDialog.xaml.cs - part of CNC Controls library
 *
 * Create/Edit dialog for a macro (opened from MacroManagerDialog's Create and Edit buttons): Name,
 * Prompt-before-run, Add-to-main-menu, an F1-F12 key assignment (captured by keypress, not a
 * dropdown), and the G-code body itself - either inline, or a single "@<path>" line referencing an
 * external file (see MacroProcessor's @<path> indirection - re-read on every run). Leaving the code
 * box on an @<path> line whose file doesn't exist yet offers to create it; opening the dialog on a
 * macro that already references one offers to jump straight to editing it in a text editor.
 * Edits the passed-in Macro only on OK - Cancel leaves it untouched, so reopening on an existing
 * macro is a true edit-a-copy/commit-on-OK flow, not live two-way binding.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CNC.Core;

namespace CNC.Controls
{
    public partial class MacroCreateDialog : Window
    {
        private readonly CNC.GCode.Macro macro;
        private readonly ObservableCollection<CNC.GCode.Macro> macros;

        /// <summary>F-key choices for the Key dropdown (— / F1..F12) - same list/order as the grid's Key column.</summary>
        public List<FKeyOption> FKeyOptions { get; } = FKeyOption.All();

        public MacroCreateDialog(CNC.GCode.Macro macro, ObservableCollection<CNC.GCode.Macro> macros)
        {
            InitializeComponent();
            DialogScaling.Apply(this);

            this.macro = macro;
            this.macros = macros;

            txtName.Text = macro.Name;
            chkPrompt.IsChecked = macro.ConfirmOnExecute;
            chkAddToMenu.IsChecked = macro.AddToMenu;
            txtCode.Text = macro.Code ?? string.Empty;
            cboKey.SelectedValue = macro.FKey;

            Loaded += MacroCreateDialog_Loaded;
        }

        private void MacroCreateDialog_Loaded(object sender, RoutedEventArgs e)
        {
            txtName.Focus();
            txtName.SelectAll();

            // Already references an external file (Edit on an existing macro) - offer to jump straight
            // to it rather than editing the "@<path>" line inline.
            string existingPath = MacroManagerDialog.GetReferencedFilePath(macro.Code);
            if (existingPath != null)
                OfferOpenReferencedFile(existingPath);
        }

        private void OfferOpenReferencedFile(string path)
        {
            if (AppDialogs.Show(
                    string.Format("This macro references an external file:\r\n\r\n{0}\r\n\r\nOpen it in your text editor now?", path),
                    "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            try
            {
                if (!File.Exists(path))
                    File.WriteAllText(path, string.Empty);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppDialogs.Show("Could not open the referenced file:\r\n\r\n" + ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Leaving the code box on an "@<path>" line: normalize the extension (default ".macro"), then
        // if that file doesn't exist yet, offer to create it. Declining puts focus back with the whole
        // reference line selected, ready to fix or retype.
        private void txtCode_LostFocus(object sender, RoutedEventArgs e)
        {
            string normalized = MacroManagerDialog.NormalizeMacroReference(txtCode.Text);
            if (normalized != txtCode.Text)
                txtCode.Text = normalized;

            string path = MacroManagerDialog.GetReferencedFilePath(txtCode.Text);
            if (path == null || File.Exists(path))
                return;

            var result = AppDialogs.Show(
                string.Format("This macro references a file that doesn't exist yet:\r\n\r\n{0}\r\n\r\nCreate it now?", path),
                "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(path, string.Empty);
                }
                catch (Exception ex)
                {
                    AppDialogs.Show("Could not create the referenced macro file:\r\n\r\n" + path + "\r\n\r\n" + ex.Message, "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                // Deferred: calling Focus() synchronously from within LostFocus fights the focus system.
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    txtCode.Focus();
                    txtCode.SelectAll();
                }), DispatcherPriority.Input);
            }
        }

        // An F-key can be on only one macro: take it off any other macro that had it (mirrors
        // MacroManagerDialog's grid-side FKey_SelectionChanged).
        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            string name = txtName.Text.Trim();
            if (!string.IsNullOrEmpty(name))
                macro.Name = name;
            macro.ConfirmOnExecute = chkPrompt.IsChecked == true;
            macro.AddToMenu = chkAddToMenu.IsChecked == true;
            macro.Code = txtCode.Text;

            int fkey = (cboKey.SelectedValue as int?) ?? 0;
            if (fkey != 0)
                foreach (var m in macros)
                    if (m.FKey == fkey)
                        m.FKey = 0;
            macro.FKey = fkey;

            DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
