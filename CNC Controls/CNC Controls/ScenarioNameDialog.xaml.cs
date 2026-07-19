/*
 * ScenarioNameDialog.xaml.cs - part of CNC Controls library
 *
 * Minimal scaled text-input dialog (name, no other field) - the app had message boxes
 * (AppMessageBox) but nothing for "ask the user to type a short string", needed by the
 * OBS Control panel's "All" stop action (name the take before its 3 recordings are filed
 * into a folder). Mirrors AppMessageBox's DialogScaling.Apply pattern so it scales with
 * UiScale like every other dialog instead of appearing at native (unscaled) size.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public partial class ScenarioNameDialog : Window
    {
        public string ResultText { get; private set; }

        public ScenarioNameDialog(string prompt, string title)
        {
            InitializeComponent();
            DialogScaling.Apply(this);

            Title = string.IsNullOrEmpty(title) ? "ioSender" : title;
            txtPrompt.Text = prompt;
            Loaded += (s, e) => txtName.Focus();
        }

        /// <summary>Shows the dialog and returns the typed text, or null if cancelled/closed without input.</summary>
        public static string Show(Window owner, string prompt, string title = "")
        {
            var dlg = new ScenarioNameDialog(prompt, title);
            if (owner != null && owner.IsLoaded)
                dlg.Owner = owner;
            else
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dlg.ShowDialog() == true ? dlg.ResultText : null;
        }

        private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
        {
            btnOk.IsEnabled = !string.IsNullOrWhiteSpace(txtName.Text);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ResultText = txtName.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
