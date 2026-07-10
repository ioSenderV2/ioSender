/*
 * BasicConfigControl.xaml.cs - part of CNC Controls library
 *
 * v0.09 / 2020-02-28 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020, Io Engineering (Terje Io)
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
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    // One row in the "Assignable shortcuts" list (UI zoom, job feed-rate override). Rebuilt wholesale
    // on each refresh rather than made individually observable - simpler, and this list changes rarely.
    public class ActionShortcutRow
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string CurrentDisplay { get; set; }
    }

    /// <summary>
    /// Interaction logic for BasicConfigControl.xaml
    /// </summary>
    public partial class BasicConfigControl : UserControl, IRestartRequired, ISettingsResettable
    {
        public event EventHandler<RestartRequiredEventArgs> RestartRequired;

        public BasicConfigControl()
        {
            InitializeComponent();

            RefreshActionShortcuts();
            ActionKeyBinder.ActionShortcutsChanged += RefreshActionShortcuts;
            Unloaded += (s, e) => ActionKeyBinder.ActionShortcutsChanged -= RefreshActionShortcuts;

            // Max buffer size (streamer flow-control window) is latched once at job-view init, and reset delay is
            // captured into the serial stream at connect - both only take effect at startup, so a change needs a
            // restart. The other basic settings here are applied live / on next use.
            if (AppConfig.Settings.Base != null)
            {
                AppConfig.Settings.Base.PropertyChanged += (s, e) => {
                    if (e.PropertyName == nameof(Config.MaxBufferSize) || e.PropertyName == nameof(Config.ResetDelay))
                        RestartRequired?.Invoke(this, new RestartRequiredEventArgs("Restart required to apply the buffer/reset-delay change."));
                };

                // Lathe mode gates a top-level tab (LatheWizards) that is only built at startup from
                // GrblInfo.LatheModeEnabled, so toggling it here adds/removes the tab in the persisted layout and
                // asks for a restart. Placed after SDCard to match the default layout order.
                if (AppConfig.Settings.Base.Lathe != null)
                    AppConfig.Settings.Base.Lathe.PropertyChanged += (s, e) => {
                        if (e.PropertyName == nameof(LatheConfig.LatheEnabled))
                        {
                            AppConfig.Settings.SetTabPresent(LayoutKeys.LatheWizards, AppConfig.Settings.Base.Lathe.LatheEnabled, LayoutKeys.SDCard);
                            RestartRequired?.Invoke(this, new RestartRequiredEventArgs("Restart required to apply the lathe-mode change."));
                        }
                    };
            }
        }

        // Reset the Main-panel settings this control owns to their factory defaults (from a fresh Config).
        public void ResetToDefaults()
        {
            var cfg = AppConfig.Settings.Base;
            if (cfg == null)
                return;

            var d = AppConfig.GetFactoryDefaults();
            cfg.Theme = d.Theme;
            cfg.ResetDelay = d.ResetDelay;
            cfg.PollInterval = d.PollInterval;
            cfg.MaxBufferSize = d.MaxBufferSize;
            cfg.UseBuffering = d.UseBuffering;
            cfg.KeepMdiFocus = d.KeepMdiFocus;
            cfg.FilterOkResponse = d.FilterOkResponse;
            cfg.AutoCompress = d.AutoCompress;
            cfg.KeepWindowSize = d.KeepWindowSize;
            cfg.SendComments = d.SendComments;
            cfg.PreferNetwork = d.PreferNetwork;
            cfg.AddLineNumbers = d.AddLineNumbers;
            cfg.AutoSaveSettings = d.AutoSaveSettings;
            cfg.PromptOnSave = d.PromptOnSave;
            cfg.AutoSaveGrblSettings = d.AutoSaveGrblSettings;
            cfg.PromptOnGrblSave = d.PromptOnGrblSave;
            cfg.SafeGotoZ = d.SafeGotoZ;
            cfg.RestoreFusionRapids = d.RestoreFusionRapids;

            // Force the bound controls to re-read (covers any plain auto-properties that don't notify).
            var dc = DataContext;
            DataContext = null;
            DataContext = dc;
        }

        private void RefreshActionShortcuts()
        {
            lstActionShortcuts.ItemsSource = ActionKeyBinder.Catalog.Select(a => new ActionShortcutRow
            {
                Id = a.Id,
                Label = a.Label,
                CurrentDisplay = ActionKeyBinder.CurrentDisplay(a.Id) ?? "(unbound)"
            }).ToList();
        }

        private void ChangeShortcut_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            ActionKeyBinder.PromptAndBind(Window.GetWindow(this), id);
        }

        private void ClearShortcut_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            ActionKeyBinder.Clear(id);
        }
    }
}
