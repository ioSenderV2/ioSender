/*
 * AppConfigView.xaml.cs - part of CNC Controls library for Grbl
 *
 * v0.46 / 2025-02-14 / Io Engineering (Terje Io)
 *
 */
/*

Copyright (c) 2020-2025, Io Engineering (Terje Io)
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Serialization;
using CNC.Core;

namespace CNC.Controls
{
    public partial class AppConfigView : UserControl, ICNCView
    {
        private UIViewModel model;
        private GrblViewModel grblmodel;
        private string settingsSnapshot;    // serialized Config captured when the tab is entered (for autosave/diff)

        public AppConfigView()
        {
            InitializeComponent();
        }

        ObservableCollection<UserControl> ConfigControls { get { return model == null ? null : model.ConfigControls;  } }

        #region Methods and properties required by CNCView interface

        public ViewType ViewType { get { return ViewType.AppConfig; } }
        public bool CanEnable { get { return true; } }

        public void Activate(bool activate, ViewType chgMode)
        {
            if(activate) foreach(var control in model.ConfigControls) // TODO: use callback!
            {
                if (control is JogConfigControl) {
                    if (GrblSettings.GetString(grblHALSetting.JogStepSpeed) != null)
                        control.Visibility = Visibility.Collapsed;
                    else
                        (control as JogConfigControl).IsGrbl = !GrblInfo.IsGrblHAL;
                } else if (control is ICameraConfig && model.Camera != null && !model.Camera.HasCamera)
                    control.Visibility = Visibility.Collapsed;
            }

            if (activate)
                settingsSnapshot = SerializeConfig(AppConfig.Settings.Base);
            else
                AutoSaveOnLeave();

            grblmodel.Message = activate ? (string)FindResource("RestartMessage") : string.Empty;
        }

        public void CloseFile()
        {
        }

        public void Setup(UIViewModel model, AppConfig profile)
        {
            if (this.model == null)
            {
                this.model = model;
                grblmodel = DataContext as GrblViewModel;
                DataContext = profile.Base;
                xx.ItemsSource = model.ConfigControls;
                model.ConfigControls.Add(new BasicConfigControl());
                btnEditMainPage.Visibility = btnRestart.Visibility = MainPanelRegistry.LayoutEnabled ? Visibility.Visible : Visibility.Collapsed;
                // UI jogging and keyboard jogging are both always available, so always offer both config panels.
                model.ConfigControls.Add(new JogUiConfigControl());
                model.ConfigControls.Add(new JogConfigControl());
                model.ConfigControls.Add(new StripGCodeConfigControl());

                UpdateSaveButtonVisibility();
                AppConfig.Settings.Base.PropertyChanged += (s, e) => {
                    if (e.PropertyName == nameof(Config.AutoSaveSettings))
                        UpdateSaveButtonVisibility();
                };
            }
        }

        private void UpdateSaveButtonVisibility()
        {
            btnSave.Visibility = AppConfig.Settings.Base.AutoSaveSettings ? Visibility.Collapsed : Visibility.Visible;
        }

        #endregion

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if(AppConfig.Settings.Save())
                Grbl.GrblViewModel.Message = LibStrings.FindResource("SettingsSaved");
        }

        private void btnEditMacros_Click(object sender, RoutedEventArgs e)
        {
            if (AppConfig.Settings.Macros == null)
                return;

            var dlg = new MacroManagerDialog(AppConfig.Settings.Macros) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            AppConfig.Settings.Save();
        }

        private void btnEditKeyMap_Click(object sender, RoutedEventArgs e)
        {
            if (Grbl.GrblViewModel.Keyboard == null)
            {
                Grbl.GrblViewModel.Message = "Key mappings are not available until a controller is connected.";
                return;
            }

            var dlg = new KeyMapEditor(Grbl.GrblViewModel) { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() == true)
            {
                string filename = CNC.Core.Resources.ConfigPath + "KeyMap0.xml";
                Grbl.GrblViewModel.Keyboard.SaveMappings(filename);
                AppConfig.Settings.Save();
                AppConfig.NotifyConsoleShortcutChanged();
                Grbl.GrblViewModel.Message = string.Format(LibStrings.FindResource("KeymappingsSaved"), filename);
            }
        }

        private void btnEditMainPage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new MainPageEditor() { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                AppConfig.Settings.Save();
                if (dlg.Changed)
                {
                    // A layout/tab change needs a restart - enable the Restart button and flag it in the status line.
                    btnRestart.IsEnabled = true;
                    Grbl.GrblViewModel.Message = "Restart required to apply main page / tab layout changes.";
                }
            }
        }

        private void btnRestart_Click(object sender, RoutedEventArgs e)
        {
            AppConfig.Settings.Save();
            try
            {
                System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                Application.Current.Shutdown();
            }
            catch { }   // relaunch failed - leave the app open; changes are saved and apply on next manual restart
        }

        #region Autosave on tab-leave / close (opt-in)

        private void AutoSaveOnLeave()
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg == null || !cfg.AutoSaveSettings || settingsSnapshot == null)
                return;

            string current = SerializeConfig(cfg);
            if (current == null || current == settingsSnapshot)
                return;     // nothing changed

            if (cfg.PromptOnSave)
            {
                var changes = new List<string>();
                DiffObject(string.Empty, DeserializeConfig(settingsSnapshot), cfg, changes);

                if (changes.Count > 0)
                {
                    var msg = "Save these setting changes?\n\n" + string.Join("\n", changes);
                    if (MessageBox.Show(msg, "ioSender", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        AppConfig.Settings.Save();
                        settingsSnapshot = SerializeConfig(cfg);
                    }
                    else
                        CopyScalars(DeserializeConfig(settingsSnapshot), cfg);   // discard edits
                    return;
                }
            }

            AppConfig.Settings.Save();
            settingsSnapshot = current;
        }

        private static string SerializeConfig(Config c)
        {
            try
            {
                var xs = new XmlSerializer(typeof(Config));
                using (var sw = new StringWriter())
                {
                    xs.Serialize(sw, c);
                    return sw.ToString();
                }
            }
            catch { return null; }
        }

        private static Config DeserializeConfig(string xml)
        {
            var xs = new XmlSerializer(typeof(Config));
            using (var sr = new StringReader(xml))
                return (Config)xs.Deserialize(sr);
        }

        private static bool IsScalar(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(double) || t == typeof(decimal);
        }

        // List scalar property differences (incl. nested Jog / JogUi config) as "Name: old -> new".
        private static void DiffObject(string prefix, object oldO, object newO, List<string> changes)
        {
            if (oldO == null || newO == null)
                return;

            foreach (var p in oldO.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || Attribute.IsDefined(p, typeof(XmlIgnoreAttribute)))
                    continue;

                object ov, nv;
                try { ov = p.GetValue(oldO); nv = p.GetValue(newO); } catch { continue; }

                if (IsScalar(p.PropertyType))
                {
                    if (!Equals(ov, nv))
                        changes.Add(string.Format("  {0}{1}: {2} → {3}", prefix, p.Name, ov, nv));
                }
                else if (p.PropertyType == typeof(JogConfig) || p.PropertyType == typeof(JogUIConfig))
                    DiffObject(p.Name + ".", ov, nv, changes);
                else if (p.PropertyType == typeof(string[]))
                {
                    var oa = ov as string[];
                    var na = nv as string[];
                    if (oa != null && na != null && !oa.SequenceEqual(na))
                        changes.Add(string.Format("  {0}{1}: {2} → {3}", prefix, p.Name, string.Join(",", oa), string.Join(",", na)));
                }
            }
        }

        // Copy scalar property values from src into dst (used to discard unsaved edits on the live Config).
        private static void CopyScalars(object src, object dst)
        {
            if (src == null || dst == null)
                return;

            foreach (var p in src.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || Attribute.IsDefined(p, typeof(XmlIgnoreAttribute)))
                    continue;

                if (IsScalar(p.PropertyType))
                {
                    if (p.CanWrite)
                    {
                        try { var v = p.GetValue(src); if (!Equals(v, p.GetValue(dst))) p.SetValue(dst, v); } catch { }
                    }
                }
                else if (p.PropertyType == typeof(JogConfig) || p.PropertyType == typeof(JogUIConfig))
                    CopyScalars(p.GetValue(src), p.GetValue(dst));
            }
        }

        #endregion
    }
}
