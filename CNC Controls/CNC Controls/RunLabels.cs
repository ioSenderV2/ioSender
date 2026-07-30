/*
 * RunLabels.cs - part of CNC Controls library
 *
 * Single source of truth for the "Cycle Start"/"Start" and "Feed Hold"/"Pause" wording used across the app -
 * grbl's own terminology by default, the friendlier pair when AppConfig.Base.UseFriendlyRunLabels is on (see
 * that property's own comment). Every user-facing string that names these two actions (button labels, signal
 * pin tooltips, key-binding names/descriptions, status messages) should read through here instead of
 * hardcoding either name directly, so flipping the setting stays consistent everywhere at once.
 *
 * Computed fresh on every access, never cached - the setting can change at runtime (Settings panel), and a
 * few callers (Converters.cs's Lazy<Dictionary> state-help text) build their strings once at first use, so a
 * cached value here would go stale the moment the operator flips the checkbox.
 */

namespace CNC.Controls
{
    public static class RunLabels
    {
        // Base can be null very early in startup (JobControl_DataContextChanged fires from XAML load before
        // AppConfig's config section is loaded) - confirmed as a real crash on real hardware 2026-07-30.
        private static bool Friendly => AppConfig.Settings.Base != null && AppConfig.Settings.Base.UseFriendlyRunLabels;

        public static string CycleStart => Friendly ? "Start" : "Cycle Start";
        public static string FeedHold => Friendly ? "Pause" : "Feed Hold";
    }
}
