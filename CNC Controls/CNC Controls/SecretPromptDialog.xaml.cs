/*
 * SecretPromptDialog.xaml.cs - part of CNC Controls library
 *
 * Minimal masked-input prompt for entering a credential (AI review key, GitHub token, etc.)
 * to be stored via CNC.Core.SecretStore. Mirrors AppMessageBox's UiScale-aware styling
 * (DialogScaling.Apply) rather than the native input-box equivalents WPF doesn't ship with.
 */

using System.Windows;
using System.Windows.Input;

namespace CNC.Controls
{
    public enum SecretPromptResult { Cancel, Set, Clear }

    public partial class SecretPromptDialog : Window
    {
        public string Value { get; private set; }
        public SecretPromptResult Result { get; private set; } = SecretPromptResult.Cancel;

        public SecretPromptDialog(string prompt)
        {
            InitializeComponent();
            DialogScaling.Apply(this);
            txtPrompt.Text = prompt;
        }

        // owner may be null. On return, value is the entered text when Result == Set, else null.
        public static SecretPromptResult Show(Window owner, string prompt, out string value)
        {
            var dlg = new SecretPromptDialog(prompt);
            if (owner != null && owner.IsLoaded)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            value = dlg.Value;
            return dlg.Result;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            Value = pwdValue.Password;
            Result = SecretPromptResult.Set;
            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            Result = SecretPromptResult.Cancel;
            DialogResult = false;
            Close();
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            Result = SecretPromptResult.Clear;
            DialogResult = true;
            Close();
        }

        private void pwdValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                btnOk_Click(sender, e);
        }
    }
}
