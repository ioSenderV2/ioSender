/*
 * GrblConfigControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.46 / 2025-05-23 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2018-2025, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CNC.Core;

namespace CNC.Controls
{
    public partial class GrblConfigControl : UserControl, IGrblConfigTab
    {
        private bool active = false;
        private Widget curSetting = null;
        private GrblViewModel model = null;

        private string retval;

        public GrblConfigControl()
        {
            InitializeComponent();
        }

        private void ConfigView_Loaded(object sender, RoutedEventArgs e)
        {
            if(!(DataContext is WidgetViewModel))
                DataContext = new WidgetViewModel(DataContext as GrblViewModel);

            model = (DataContext as WidgetViewModel).Grbl;

            dgrSettings.Visibility = GrblInfo.HasEnums ? Visibility.Collapsed : Visibility.Visible;
            searchField.Visibility = !GrblInfo.HasEnums ? Visibility.Collapsed : Visibility.Visible;
            treeView.Visibility = !GrblInfo.HasEnums ? Visibility.Collapsed : Visibility.Visible;
            details.Visibility = GrblInfo.HasEnums && curSetting == null ? Visibility.Hidden : Visibility.Visible;

            if (GrblInfo.HasEnums)
            {
                treeView.ItemsSource = GrblSettingGroups.Groups;
            }
            else
            {
                dgrSettings.DataContext = GrblSettings.Settings;
                dgrSettings.SelectedIndex = 0;
            }

            UpdateValidation();
        }

        #region Methods required by GrblConfigTab interface

        public GrblConfigType GrblConfigType { get { return GrblConfigType.Base; } }

        public void Activate(bool activate)
        {
            if (model != null)
            {
                btnSave.IsEnabled = !model.IsCheckMode;
                model.Message = string.Empty;

                if (activate)
                {
                    if (active) return;

                    active = true;

                    using (new UIUtils.WaitCursor())
                    {
                        GrblSettings.Load();
                    }

                    if(treeView.SelectedItem != null && treeView.SelectedItem is GrblSettingDetails)
                        ShowSetting(treeView.SelectedItem as GrblSettingDetails, false);
                    else if (dgrSettings.SelectedItem != null)
                        ShowSetting(dgrSettings.SelectedItem as GrblSettingDetails, false);
                }
                else
                {
                    active = false;
                    if (curSetting != null)
                        curSetting.Assign();

                    if (GrblSettings.HasChanges())
                    {
                        if (MessageBox.Show((string)FindResource("SaveSettings"), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                            GrblSettings.Save();
                    }
                }
            }
        }

        #endregion

        #region UIEvents

        void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if (curSetting != null)
                curSetting.Assign();

            model.Message = string.Empty;

            GrblSettings.Save();
        }

        void btnReload_Click(object sender, RoutedEventArgs e)
        {
            using(new UIUtils.WaitCursor()) {
                GrblSettings.ClearPendingEdits();    // Reload discards unsaved editor edits
                GrblSettings.Load();
                if (curSetting != null)
                    ShowSetting(curSetting.Setting, false);
            }
        }

        void btnBackup_Click(object sender, RoutedEventArgs e)
        {
            if(GrblSettings.Backup(string.Format("{0}settings.txt", Core.Resources.ConfigPath)))
                model.Message = string.Format((string)FindResource("SettingsWritten"), "settings.txt");
            GrblWorkParameters.Backup(string.Format("{0}offsets.nc", Core.Resources.ConfigPath));
        }

        // Mirror the current settings into the bundled simulator's "My Machine" EEPROM (same action as the
        // Machine Setup Wizard), so a later simulator connection boots with this machine's configuration.
        // Runs off the UI thread - it briefly drives a headless simulator instance.
        private async void CopyToSim_Click(object sender, RoutedEventArgs e)
        {
            var cmds = GrblSettings.Settings.Select(s => "$" + s.Id + "=" + s.Value).ToList();
            if (cmds.Count == 0)
            {
                model.Message = "No settings to copy - reload settings from the controller first.";
                return;
            }

            btnCopyToSim.IsEnabled = false;
            model.Message = "Copying settings to the simulator...";
            string err = null;
            bool ok = await Task.Run(() => SimulatorManager.BuildMyMachineEeprom(cmds, out err));
            btnCopyToSim.IsEnabled = true;
            model.Message = ok ? "Copied settings to the simulator (My Machine)." : ("Copy to simulator failed - " + err);
        }

        private void ShowSetting(GrblSettingDetails setting, bool assign)
        {
            details.Visibility = Visibility.Visible;

            if (curSetting != null)
            {
                if (assign)
                    curSetting.Assign();
                canvas.Children.Clear();
                curSetting.Dispose();
            }
            searchField.Value = setting.Id;
            txtDescription.Text = setting.Description;
            curSetting = new Widget(this, new WidgetProperties(setting), canvas);
            curSetting.IsEnabled = true;
            UpdateValidation();
        }

        private bool SetSetting (KeyValuePair<int, string> setting)
        {
            bool? res = null;
            CancellationToken cancellationToken = new CancellationToken();
            var scmd = string.Format("${0}={1}", setting.Key, setting.Value);

            retval = string.Empty;

            new Thread(() =>
            {
                res = WaitFor.AckResponse<string>(
                    cancellationToken,
                    response => Process(response),
                    a => model.OnResponseReceived += a,
                    a => model.OnResponseReceived -= a,
                    400, () => Comms.com.WriteCommand(scmd));
            }).Start();

            while (res == null)
                EventUtils.DoEvents();

            if (retval != string.Empty)
            {
                if(retval.StartsWith("error:"))
                {
                    var msg = GrblErrors.GetMessage(retval.Substring(6));
                    if(msg != retval)
                        retval += " - \"" + msg + "\"";
                }

                var details = GrblSettings.Get((GrblSetting)setting.Key);

                if (MessageBox.Show(string.Format((string)FindResource("SettingsError"), scmd, retval), "ioSender" + (details == null ? "" : " - " + details.Name), MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.No)
                    return false;
            }
            else if (res == false && MessageBox.Show(string.Format((string)FindResource("SettingsTimeout"), scmd), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.No)
                return false;

            return true;
        }

        public bool LoadFile(string filename)
        {
            int pos, id, mismatch = 0;
            List<string> lines = new List<string>();
            List<int> dep = new List<int>();
            Dictionary<int, string> settings = new Dictionary<int, string>();
            FileInfo file = new FileInfo(filename);
            StreamReader sr = file.OpenText();

            string block = sr.ReadLine();

            while (block != null)
            {
                block = block.Trim();
                try
                {
                    if (lines.Count == 0 && model.IsGrblHAL && block == "%")
                        lines.Add(block);
                    else if (block.StartsWith("$") && (pos = block.IndexOf('=')) > 1)
                    {
                        if (int.TryParse(block.Substring(1, pos - 1), out id))
                            settings.Add(id, block.Substring(pos + 1));
                        else
                            lines.Add(block);
                    }

                    block = sr.ReadLine();
                }
                catch (Exception e)
                {
                    if (MessageBox.Show(((string)FindResource("SettingsFail")).Replace("\\n", "\r\r"), e.Message, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        block = sr.ReadLine();
                    else
                    {
                        block = null;
                        settings.Clear();
                        lines.Clear();
                    }
                }
            }

            sr.Close();

            if (settings.Count == 0)
                MessageBox.Show((string)FindResource("SettingsInvalid"));
            else
            {
                bool? res = null;
                CancellationToken cancellationToken = new CancellationToken();

                // List of settings that have other dependent settings and have to be set before them
                dep.Add((int)GrblSetting.HomingEnable);

                foreach (var cmd in lines)
                {
                    res = null;
                    retval = string.Empty;

                    new Thread(() =>
                    {
                        res = WaitFor.AckResponse<string>(
                            cancellationToken,
                            response => Process(response),
                            a => model.OnResponseReceived += a,
                            a => model.OnResponseReceived -= a,
                            400, () => Comms.com.WriteCommand(cmd));
                    }).Start();

                    while (res == null)
                        EventUtils.DoEvents();

                    if (retval != string.Empty)
                    {
                        if (MessageBox.Show(string.Format((string)FindResource("SettingsError"), cmd, retval), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.No)
                            break;
                    }
                    else if (res == false && MessageBox.Show(string.Format((string)FindResource("SettingsTimeout"), cmd), "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.No)
                        break;
                }

                foreach (var d in dep)
                {
                    if (settings.ContainsKey(d))
                    {
                        var setting = new KeyValuePair<int, string>(d, settings[d]);
                        if (GrblSettings.HasSetting((GrblSetting)setting.Key))
                        {
                            if (!SetSetting(setting))
                            {
                                settings.Clear();
                                break;
                            }
                        }
                        else
                            mismatch++;
                    }
                }

                foreach (var setting in settings)
                {
                    if (GrblSettings.HasSetting((GrblSetting)setting.Key))
                    {
                        if (!dep.Contains(setting.Key))
                        {
                            if (!SetSetting(setting))
                                break;
                        }
                    }
                    else
                        mismatch++;
                }

                if (lines.Count > 0 && lines[0] == "%")
                    Comms.com.WriteCommand("%");

                using (new UIUtils.WaitCursor())
                {
                    GrblSettings.ClearPendingEdits();    // restored-from-file values supersede unsaved edits
                    GrblSettings.Load();
                }
            }

            model.Message = string.Empty;

            if (mismatch > 0)
                MessageBox.Show(string.Format((string)FindResource("SettingsReloadMismatch"), mismatch), "ioSender", MessageBoxButton.OK, MessageBoxImage.Exclamation);

            return settings.Count > 0;
        }

        private void Process(string data)
        {
            if (data != "ok")
                retval = data;
        }

        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            // Pick a restore point (auto-snapshot written on each Save), newest first; the dialog's
            // Browse... button falls back to choosing an arbitrary backup file.
            RestorePointDialog dlg = new RestorePointDialog { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedFile))
            {
                using (new UIUtils.WaitCursor())
                {
                    LoadFile(dlg.SelectedFile);
                }
            }
        }

        private void dgrSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 1)
                ShowSetting(e.AddedItems[0] as GrblSettingDetails, true);
        }

        private void settingRevertStartup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is GrblSettingDetails setting)
                RevertSetting(setting);
        }

        private void groupRevertStartup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is GrblSettingGroup group)
            {
                foreach (var setting in new List<GrblSettingDetails>(group.Settings))
                    RevertSetting(setting);
            }
        }

        private void RevertSetting(GrblSettingDetails setting)
        {
            if (setting == null || !setting.IsModified)
                return;

            // The currently displayed widget holds an uncommitted edit; commit it first so the
            // revert isn't immediately overwritten when the widget is torn down/reassigned.
            if (curSetting != null && curSetting.Setting == setting)
                curSetting.Assign();

            setting.RevertToStartup();

            // Refresh the editor pane if the reverted setting is the one being shown.
            if (curSetting != null && curSetting.Setting == setting)
                ShowSetting(setting, false);
        }
        #endregion

        private void treeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e != null && e.NewValue is GrblSettingDetails && (e.NewValue as GrblSettingDetails).Value != null)
                ShowSetting(e.NewValue as GrblSettingDetails, true);
            else
                details.Visibility = Visibility.Hidden;
        }

        private void searchField_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if(e.Key == System.Windows.Input.Key.Return && e.IsDown)
            {
                var setting = GrblSettings.Get((GrblSetting)searchField.Value);

                if (setting != null)
                {
                    foreach (object g in treeView.Items)
                    {
                        if ((g as GrblSettingGroup).Id == setting.GroupId)
                        {
                            TreeViewItem gitm = (TreeViewItem)treeView.ItemContainerGenerator.ContainerFromItem(g);
                            gitm.IsExpanded = true;
                            gitm.UpdateLayout();
                            gitm.BringIntoView();
                            foreach (object s in gitm.Items)
                            {
                                if ((s as GrblSettingDetails).Id == setting.Id)
                                {
                                    TreeViewItem sitm = (TreeViewItem)gitm.ItemContainerGenerator.ContainerFromItem(s);
                                    if (sitm != null)
                                    {
                                        sitm.IsSelected = true;
                                        sitm.BringIntoView();
//                                        sitm.Focus();
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ConfigView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Description box now fills via the details Grid (* row) - no manual height sizing needed.
        }

        // Live sanity check on the Basic settings tab: the step pulse time ($0) must fit inside one step
        // period at the worst-case step rate (highest steps/mm x max feed rate across axes). grblHAL needs
        // a minimum low time between pulses, so we warn before the driver actually starts missing steps.
        private void UpdateValidation()
        {
            string warning = ValidateStepPulse();
            if (string.IsNullOrEmpty(warning))
                txtValidation.Visibility = Visibility.Collapsed;
            else
            {
                txtValidation.Text = warning;
                txtValidation.Visibility = Visibility.Visible;
            }
        }

        // Called by Widget when a setting edit commits. Re-run the live check only if the edited setting is
        // one of its inputs ($0 step pulse, $10x steps/mm, $11x max feed rate) so unrelated edits are cheap.
        public void OnSettingEdited(int id)
        {
            int axes = GrblInfo.NumAxes;
            if (id == (int)GrblSetting.PulseMicroseconds ||
                (id >= (int)GrblSetting.TravelResolutionBase && id < (int)GrblSetting.TravelResolutionBase + axes) ||
                (id >= (int)GrblSetting.MaxFeedRateBase && id < (int)GrblSetting.MaxFeedRateBase + axes))
                UpdateValidation();
        }

        private string ValidateStepPulse()
        {
            double pulse = GrblSettings.GetDouble(GrblSetting.PulseMicroseconds);
            if (double.IsNaN(pulse) || pulse <= 0d)
                return null;

            double worstHz = 0d;
            int worstAxis = -1;
            for (int i = 0; i < GrblInfo.NumAxes; i++)
            {
                double stepsmm = GrblSettings.GetDouble(GrblSetting.TravelResolutionBase + i);
                double maxrate = GrblSettings.GetDouble(GrblSetting.MaxFeedRateBase + i);   // mm/min
                if (double.IsNaN(stepsmm) || double.IsNaN(maxrate))
                    continue;
                double hz = stepsmm * maxrate / 60d;     // steps per second
                if (hz > worstHz) { worstHz = hz; worstAxis = i; }
            }
            if (worstAxis < 0 || worstHz <= 0d)
                return null;

            const double minLowUs = 2d;                  // conservative minimum low time between pulses
            double periodUs = 1e6 / worstHz;
            if (pulse + minLowUs <= periodUs)
                return null;                             // fits - OK

            double safePulse = Math.Floor((periodUs - minLowUs) * 10d) / 10d;
            return string.Format(
                "⚠ Step rate reaches ~{0:0} kHz on {1} ({2:0} steps/mm × {3:0} mm/min → {4:0.0} µs period). " +
                "At $0 = {5:0.#} µs the pulse is too long - reduce $0 to ≤ {6:0.#} µs.",
                worstHz / 1000d,
                GrblInfo.AxisIndexToLetter(worstAxis),
                GrblSettings.GetDouble(GrblSetting.TravelResolutionBase + worstAxis),
                GrblSettings.GetDouble(GrblSetting.MaxFeedRateBase + worstAxis),
                periodUs, pulse, Math.Max(0.1d, safePulse));
        }
    }
}
