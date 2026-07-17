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

        public AppMessageBox(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            InitializeComponent();
            DialogScaling.Apply(this);

            Title = string.IsNullOrEmpty(caption) ? "ioSender" : caption;
            txtMessage.Text = message;
            imgIcon.Source = IconFor(icon);
            imgIcon.Visibility = imgIcon.Source == null ? Visibility.Collapsed : Visibility.Visible;

            ConfigureButtons(buttons, defaultResult);
        }

        public static void Register()
        {
            AppDialogs.CustomMessageBox = (owner, message, caption, buttons, icon, defaultResult) =>
                Show(owner, message, caption, buttons, icon, defaultResult);
        }

        public static MessageBoxResult Show(string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return Show(null, message, caption, buttons, icon, defaultResult);
        }

        public static MessageBoxResult Show(Window owner, string message, string caption = "",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            var box = new AppMessageBox(message, caption, buttons, icon, defaultResult);
            if (owner != null && owner.IsLoaded)
                box.Owner = owner;
            else
                box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            box.ShowDialog();
            return box.result;
        }

        private void ConfigureButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
        {
            btnYes.Visibility = Visibility.Collapsed;
            btnNo.Visibility = Visibility.Collapsed;
            btnOk.Visibility = Visibility.Collapsed;
            btnCancel.Visibility = Visibility.Collapsed;

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
                var r = (MessageBoxResult)Enum.Parse(typeof(MessageBoxResult), (string)btn.Content);
                btn.IsDefault = r == defaultResult;
                btn.IsCancel = r == MessageBoxResult.Cancel || (buttons == MessageBoxButton.YesNo && r == MessageBoxResult.No);
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            result = (MessageBoxResult)Enum.Parse(typeof(MessageBoxResult), (string)((System.Windows.Controls.Button)sender).Content);
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
