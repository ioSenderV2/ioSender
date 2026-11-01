/*
 * UiSoundConfigControl.xaml.cs - part of CNC Controls library for Grbl
 *
 * Settings > User Interface > Sound. Three machine events, a sound each, all None by default.
 *
 * The ComboBoxes are NOT bound two-way to the settings, unlike every other panel here, because one
 * of the entries is not a value but an action: picking "Browse for a sound file..." has to open a
 * dialog and then store whatever it returns - or put the previous choice back when the dialog is
 * cancelled, or when the player cannot load the file that was chosen. A TwoWay binding would have
 * already written "Browse" into the setting by the time any of that was known. So selection is
 * handled here and written through explicitly.
 *
 * There is no summary line under the rows. There was one, and it read like debug output; what it
 * was there to say - that a custom file has gone missing - is now said by the list itself, which is
 * where the operator is already looking.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CNC.Controls
{
    public partial class UiSoundConfigControl : UserControl, ISettingsPanelCategory, ISettingsResettable
    {
        public string SettingsCategory { get { return SettingsCategories.UserInterface; } }

        // Between General (0) and Remote (15): the general page first, then the two that attach
        // behaviour to events - sound out, remote in.
        public int SettingsOrder { get { return 10; } }

        // Set while the combo boxes are being filled in from the settings. Assigning SelectedValue
        // raises SelectionChanged exactly as a click does, and without this the load would write the
        // settings straight back and - worse - a stored custom path would re-open the file dialog.
        private bool loading;

        public UiSoundConfigControl()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            var cfg = AppConfig.Settings?.Base;
            if (cfg == null)
                return;

            loading = true;
            try
            {
                // Any stored custom path has to exist in the catalogue before it can be selected -
                // a SelectedValue with no matching item selects nothing, which would read as None
                // and quietly discard the operator's choice the next time the page is shown.
                UiSounds.EnsureCustom(CustomPathOf(cfg.SoundOnLimit));
                UiSounds.EnsureCustom(CustomPathOf(cfg.SoundOnProbe));
                UiSounds.EnsureCustom(CustomPathOf(cfg.SoundOnAlarm));

                cboLimit.SelectedValue = cfg.SoundOnLimit;
                cboProbe.SelectedValue = cfg.SoundOnProbe;
                cboAlarm.SelectedValue = cfg.SoundOnAlarm;
            }
            finally
            {
                loading = false;
            }
        }

        /// <summary>The setting, if it is a custom file path rather than None or a built-in id.</summary>
        private static string CustomPathOf(string setting)
        {
            if (string.IsNullOrEmpty(setting) || setting == UiSounds.None || setting == UiSounds.Browse)
                return null;
            var known = UiSounds.Find(setting);
            return known != null && known.File != null ? null : setting;
        }

        private void Sound_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loading)
                return;

            var combo = sender as ComboBox;
            var cfg = AppConfig.Settings?.Base;
            if (combo == null || cfg == null)
                return;

            string id = combo.SelectedValue as string;

            if (id == UiSounds.Browse)
            {
                string picked = BrowseForSound();
                loading = true;
                try
                {
                    // Cancelled, or a file that cannot be played: put back what was there. Leaving
                    // "Browse" selected would store an action as a sound and read as nothing.
                    combo.SelectedValue = picked ?? SettingFor(combo, cfg);
                    if (picked != null)
                    {
                        UiSounds.EnsureCustom(picked);
                        combo.SelectedValue = picked;
                    }
                }
                finally
                {
                    loading = false;
                }
                id = combo.SelectedValue as string;
            }

            Store(combo, cfg, string.IsNullOrEmpty(id) ? UiSounds.None : id);

            // PLAY WHAT WAS JUST CHOSEN. This replaced a Test button per row, at the user's request
            // 2026-10-26: picking from a list of nine sounds whose names you do not know is exactly
            // when you want to hear one, and a button you must press afterwards is a second step for
            // something that has only one sensible outcome. None is silence, by definition.
            if (!string.IsNullOrEmpty(id) && id != UiSounds.None)
                UiSounds.Play(id);
        }

        /// <summary>
        /// Ask for a sound file. Returns null when cancelled, or when the player cannot load what
        /// was chosen - in which case the operator is told why, here, while they are choosing it,
        /// rather than finding out by silence when a limit switch is actually hit.
        /// </summary>
        /// <remarks>
        /// The dialog does NOT filter to .wav. Whether a file can be played is a question only the
        /// player can answer, so it is asked directly: the file is loaded, and a failure is reported
        /// with the reason the API gave. An extension filter would both exclude files the operator
        /// might reasonably try and wave through a .wav that is really something else renamed.
        /// </remarks>
        private string BrowseForSound()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose a sound",
                Filter = "Sound files|*.wav;*.mp3;*.wma;*.aac;*.m4a;*.ogg;*.flac;*.aiff;*.aif|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog() != true)
                return null;

            string error;
            if (!UiSounds.TryLoad(dlg.FileName, out error))
            {
                AppDialogs.Show("That file cannot be played, so it has not been used:\n\n"
                              + dlg.FileName + "\n\n" + error,
                                "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            return dlg.FileName;
        }

        private string SettingFor(ComboBox combo, Config cfg)
        {
            if (combo == cboLimit) return cfg.SoundOnLimit;
            if (combo == cboProbe) return cfg.SoundOnProbe;
            if (combo == cboAlarm) return cfg.SoundOnAlarm;
            return UiSounds.None;
        }

        private void Store(ComboBox combo, Config cfg, string id)
        {
            if (combo == cboLimit) cfg.SoundOnLimit = id;
            else if (combo == cboProbe) cfg.SoundOnProbe = id;
            else if (combo == cboAlarm) cfg.SoundOnAlarm = id;
        }

        /// <summary>Back to factory: silence, as the sibling panels' own ResetToDefaults do.</summary>
        public void ResetToDefaults()
        {
            var cfg = AppConfig.Settings?.Base;
            if (cfg == null)
                return;

            var d = AppConfig.GetFactoryDefaults();
            cfg.SoundOnLimit = d.SoundOnLimit;
            cfg.SoundOnProbe = d.SoundOnProbe;
            cfg.SoundOnAlarm = d.SoundOnAlarm;

            LoadFromSettings();
        }
    }
}
