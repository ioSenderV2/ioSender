/*
 * AppMessageBox.xaml.cs - part of CNC Controls library
 *
 * The app's own message box. System.Windows.MessageBox is a native OS dialog outside the WPF visual tree,
 * so it can't pick up DialogScaling/UiScale like every other dialog (Ctrl+Alt+Plus/Minus) - on a high-DPI
 * laptop it stays small and unreadable while the rest of the app scales. This window mirrors MessageBox.Show's
 * API/behavior (buttons, icon, default result) but applies DialogScaling.Apply(this) like any other dialog.
 *
 * Register() wires this in as AppDialogs.Show's real-user fallback (in place of the native MessageBox) -
 * called once from App.xaml.cs at startup. Nothing else needs to change; existing AppDialogs.Show callers
 * get a scaled box for free.
 */

using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CNC.Core;

namespace CNC.Controls
{
    public partial class AppMessageBox : Window
    {
        private MessageBoxResult result = MessageBoxResult.None;

        public AppMessageBox(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon,
            MessageBoxResult defaultResult, string yesText = null, string noText = null)
        {
            InitializeComponent();
            DialogScaling.Apply(this);

            Title = string.IsNullOrEmpty(caption) ? "ioSender" : caption;
            txtMessage.Text = message;
            imgIcon.Source = IconFor(icon);
            imgIcon.Visibility = imgIcon.Source == null ? Visibility.Collapsed : Visibility.Visible;

            ConfigureButtons(buttons, defaultResult, yesText, noText);
        }

        public static void Register()
        {
            AppDialogs.CustomMessageBox = (owner, message, caption, buttons, icon, defaultResult, yesText, noText) =>
                Show(owner, message, caption, buttons, icon, defaultResult, yesText, noText);
        }

        public static MessageBoxResult Show(string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string yesText = null, string noText = null)
        {
            return Show(null, message, caption, buttons, icon, defaultResult, yesText, noText);
        }

        public static MessageBoxResult Show(Window owner, string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None, string yesText = null, string noText = null)
        {
            var box = new AppMessageBox(message, caption, buttons, icon, defaultResult, yesText, noText);
            if (owner != null && owner.IsLoaded)
                box.Owner = owner;
            else
                box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            box.ShowDialog();
            return box.result;
        }

        // yesText/noText override the VISIBLE label only (e.g. "Flash Firmware"/"Cancel" instead of
        // "Yes"/"No") - the underlying MessageBoxResult (and what the UI test server sees/answers with,
        // via AppDialogs' own generic "Yes"/"No" protocol) is unchanged, so callers still just check
        // Yes/No as normal. Each button's logical result is carried on Tag, not parsed back out of
        // Content, specifically so Content can say anything without breaking Button_Click.
        private void ConfigureButtons(MessageBoxButton buttons, MessageBoxResult defaultResult, string yesText, string noText)
        {
            btnYes.Visibility = Visibility.Collapsed;
            btnNo.Visibility = Visibility.Collapsed;
            btnOk.Visibility = Visibility.Collapsed;
            btnCancel.Visibility = Visibility.Collapsed;

            btnYes.Tag = MessageBoxResult.Yes;
            btnNo.Tag = MessageBoxResult.No;
            btnOk.Tag = MessageBoxResult.OK;
            btnCancel.Tag = MessageBoxResult.Cancel;

            if (!string.IsNullOrEmpty(yesText)) btnYes.Content = yesText;
            if (!string.IsNullOrEmpty(noText)) btnNo.Content = noText;

            switch (buttons)
            {
                case MessageBoxButton.OKCancel:
                    btnOk.Visibility = Visibility.Visible;
                    btnCancel.Visibility = Visibility.Visible;
                    if (defaultResult == MessageBoxResult.None) defaultResult = MessageBoxResult.OK;
                    break;

                case MessageBoxButton.YesNo:
                    btnYes.Visibility = Visibility.Visible;
                    btnNo.Visibility = Visibility.Visible;
                    if (defaultResult == MessageBoxResult.None) defaultResult = MessageBoxResult.Yes;
                    break;

                case MessageBoxButton.YesNoCancel:
                    btnYes.Visibility = Visibility.Visible;
                    btnNo.Visibility = Visibility.Visible;
                    btnCancel.Visibility = Visibility.Visible;
                    if (defaultResult == MessageBoxResult.None) defaultResult = MessageBoxResult.Yes;
                    break;

                default:
                    btnOk.Visibility = Visibility.Visible;
                    defaultResult = MessageBoxResult.OK;
                    break;
            }

            foreach (var btn in new[] { btnYes, btnNo, btnOk, btnCancel })
            {
                if (btn.Visibility != Visibility.Visible)
                    continue;
                var r = (MessageBoxResult)btn.Tag;
                btn.IsDefault = r == defaultResult;
                btn.IsCancel = r == MessageBoxResult.Cancel || (buttons == MessageBoxButton.YesNo && r == MessageBoxResult.No);
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            result = (MessageBoxResult)((System.Windows.Controls.Button)sender).Tag;
            DialogResult = true;
            Close();
        }

        private static BitmapSource IconFor(MessageBoxImage icon)
        {
            Icon src;
            switch (icon)
            {
                case MessageBoxImage.Error: src = SystemIcons.Hand; break;             // Error == Hand == Stop
                case MessageBoxImage.Warning: src = SystemIcons.Warning; break;         // Warning == Exclamation
                case MessageBoxImage.Question: src = SystemIcons.Question; break;
                case MessageBoxImage.Information: src = SystemIcons.Information; break; // Information == Asterisk
                default: return null;
            }
            return Imaging.CreateBitmapSourceFromHIcon(src.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
    }
}
