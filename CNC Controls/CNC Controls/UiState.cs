/*
 * UiState.cs - part of CNC Controls library
 *
 * The jog panel's last distance/speed selection, moved out of Properties.Settings - the generated
 * ApplicationSettingsBase wrapper over user.config.
 *
 * Why it moved: user.config is keyed by a hash of the EXE PATH and then by assembly version, and
 * Settings.Upgrade() is never called anywhere. So every install location and every version bump silently
 * started from defaults - this machine had 23 separate
 * Io_Engineering\ioSender.exe_Url_<hash>\<version>\user.config directories, each with its own idea of the
 * user's jog selection. Folding it into App.config gives one profile-aware copy that survives upgrades
 * and is covered by the existing config backups.
 *
 * The old code deliberately kept this "isolated from the Base config file"; that isolation is what
 * produced the fragmentation, so it is given up on purpose. Saving now writes App.config, which is a
 * compose + atomic replace - fine for a human-speed click, and gated on Jog.KeepUiJogSelection.
 *
 * NOTE - the theme is deliberately NOT here. Config.Theme already persists in App.config's Core section,
 * and Settings.Default.ColorMode was only ever a mirror of it (verified: both read "default" on a real
 * profile). AppConfig.ColorMode now reads Base.Theme directly. The first attempt at this did mirror it
 * into a UiState field, which meant Config.Theme's setter had to save - and XmlSerializer CALLS THAT
 * SETTER during deserialization, so the save ran mid-load with Base half-replaced and made the whole
 * config unloadable. Don't reintroduce a side-effecting setter on a serialized Config property.
 */

using CNC.Core;

namespace CNC.Controls
{
    public class UiState
    {
        // Defaults mirror the old Settings.settings entries exactly.
        public int UiJogStep { get; set; } = 1;
        public int UiJogFeed { get; set; } = 1;
        public bool UiJogContinuous { get; set; } = false;

        /// <summary>The live instance from the config store. Never null - falls back to defaults if the
        /// section has not been registered yet (helper tools that never construct AppConfig).</summary>
        public static UiState Current
        {
            get { return ConfigStore.Get<UiState>() ?? fallback; }
        }

        private static readonly UiState fallback = new UiState();

        /// <summary>
        /// One-time migration, run by ConfigStore only when the section is absent from App.config: adopt
        /// whatever this install's user.config still holds, so an existing user keeps their jog selection
        /// on first run of a build that has this section.
        /// </summary>
        internal static UiState ImportLegacy()
        {
            try
            {
                var s = Properties.Settings.Default;

                return new UiState
                {
                    UiJogStep = s.UiJogStep,
                    UiJogFeed = s.UiJogFeed,
                    UiJogContinuous = s.UiJogContinuous
                };
            }
            catch
            {
                return null;   // unreadable user.config - start from defaults rather than failing the load
            }
        }
    }
}
