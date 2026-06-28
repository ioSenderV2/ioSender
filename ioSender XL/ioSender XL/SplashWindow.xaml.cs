/*
 * SplashWindow.xaml.cs - part of Grbl Code Sender (ioSender XL)
 *
 * A small status splash shown during startup: the main window is created invisible and only revealed
 * once the controller has connected, reported its info/settings and the machine-setup state is known.
 * Until then this splash reports the current phase (connecting, reading settings, validating setup).
 */

using System.Windows;

namespace GCode_Sender
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            txtVersion.Text = "Version " + MainWindow.Version;
        }

        // Update the status line; safe to call from any thread.
        public void SetStatus(string status)
        {
            if (Dispatcher.CheckAccess())
                txtStatus.Text = status;
            else
                Dispatcher.Invoke(new System.Action(() => txtStatus.Text = status));
        }
    }
}
