/*
 * UiRemoteConfigControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Settings > User Interface > Remote. The shutter-remote enable, and a plain statement of what a press
 * actually does.
 *
 * The setting itself is not new - it was born on the Height map tab (2026-09-18), because that is where
 * the need was: place the plate, press Continue, walk back, sixteen times, with the laptop across the
 * shop. It stayed there while that was the only place a press meant anything.
 *
 * It means something in several places now - any prompt, any running job, any hold (see RemoteActions) -
 * so it is an input setting rather than a height-map setting, and it belongs next to Keyboard and
 * Controller. The Height map checkbox binds the same Config.ShutterRemoteEnabled property and the two
 * stay in step automatically; it is left in place deliberately, since that is still where an operator
 * first discovers they want it.
 *
 * The table is here because the policy lived only in RemoteActions' header comment, which is the one
 * place the operator standing at the machine cannot read.
 */

using System.Windows.Controls;

namespace CNC.Controls
{
    public partial class UiRemoteConfigControl : UserControl, ISettingsPanelCategory
    {
        // Where this panel sits in the settings navigation tree (ISettingsPanelCategory).
        public string SettingsCategory { get { return SettingsCategories.UserInterface; } }

        // Between Keyboard (10) and Controller (20) - it is another way of talking to the app, and it
        // reads as one of the input pages rather than as an editor. AddPanelNode places by this order
        // against the nodes declared in GrblConfigView.BuildNav.
        public int SettingsOrder { get { return 15; } }

        public UiRemoteConfigControl()
        {
            InitializeComponent();
        }
    }
}
