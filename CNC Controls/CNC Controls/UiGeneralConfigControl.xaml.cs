/*
 * UiGeneralConfigControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Settings > User Interface > General. Split out of BasicConfigControl (Settings > Application) on
 * 2026-08-03: that page is otherwise about how ioSender talks to the CONTROLLER (poll interval, max
 * buffer, reset delay, buffering, line numbers), and these settings are about the app's own
 * appearance and behaviour.
 *
 * The grbl auto-save pair deliberately stayed on the Application page: it decides whether changed $
 * settings are written to the machine, which is controller interaction with real consequences, not
 * an interface preference.
 */

using System;
using System.Windows.Controls;

namespace CNC.Controls
{
    public partial class UiGeneralConfigControl : UserControl, ISettingsPanelCategory
    {
        // Where this panel sits in the settings navigation tree (ISettingsPanelCategory).
        public string SettingsCategory { get { return SettingsCategories.UserInterface; } }

        // First page under User Interface - it is the general one; the rest are specific editors
        // (Keyboard, Controller, Macros, layouts).
        public int SettingsOrder { get { return 0; } }

        public UiGeneralConfigControl()
        {
            InitializeComponent();
        }
    }
}
