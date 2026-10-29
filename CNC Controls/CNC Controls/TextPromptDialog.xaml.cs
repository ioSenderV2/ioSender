/*
 * TextPromptDialog.xaml.cs - part of CNC Controls library
 *
 * Minimal single-line text prompt (naming a UI preset, for one). The masked sibling
 * SecretPromptDialog covers credentials; this is deliberately a separate dialog rather than a flag on
 * that one, because a name has to be readable while it is typed.
 *
 * Mirrors AppMessageBox's UiScale-aware styling (DialogScaling.Apply) - WPF ships no input box.
 */

using System.Windows;
using System.Windows.Input;

namespace CNC.Controls
{
    public partial class TextPromptDialog : Window
    {
        public string Value { get; private set; }

        public TextPromptDialog(string prompt, string initial)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            txtPrompt.Text = prompt;
            txtValue.Text = initial ?? string.Empty;
            Loaded += (s, e) => { txtValue.Focus(); txtValue.SelectAll(); };
        }

        /// <summary>
        /// Prompt for a line of text. owner may be null. Returns false (value null) on cancel or when the
        /// box was left blank - a caller asking for a name never has to re-check for empty.
        /// </summary>
        public static bool Show(Window owner, string prompt, string initial, out string value)
        {
            var dlg = new TextPromptDialog(prompt, initial);
            if (owner != null && owner.IsLoaded)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            bool ok = dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value);
            value = ok ? dlg.Value.Trim() : null;
            return ok;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            Value = txtValue.Text;
            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void txtValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                btnOk_Click(sender, e);
        }
    }
}
