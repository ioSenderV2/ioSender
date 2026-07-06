/*
 * ConfigPanel.cs - part of CNC Controls library
 *
 * Base class for a tool/wizard settings panel whose parameters persist as a single App.config section.
 *
 * A panel derives from ConfigPanel<T> and supplies only the "config fragment": a serializable DTO T plus
 * how to copy it into/out of its own (usually dependency) properties. The base does the rest - on first
 * load it restores the saved fragment, and on any watched-property change it captures the fragment back to
 * the static holder and saves App.config. This replaces the per-wizard _paramsLoaded / LoadParams /
 * SaveParams / DependencyPropertyDescriptor.AddValueChanged boilerplate.
 *
 * The fragment holder is static (one per panel type) so the App.config section can read it even when the
 * panel isn't instantiated; AppConfig.RegisterFolded wires the section + one-time legacy-file import to it.
 *
 * WPF note: because the code-behind base is generic, the XAML root element is
 *   <local:ConfigPanel x:TypeArguments="local:TParams" x:Class="..." ...>
 * (x:TypeArguments on the x:Class root is the supported way to root on a generic base). Existing
 * RelativeSource AncestorType={x:Type UserControl} bindings still resolve - ConfigPanel<T> is a UserControl.
 */

using System.Windows;
using System.Windows.Controls;

namespace CNC.Controls
{
    public abstract class ConfigPanel<T> : UserControl where T : class, new()
    {
        private bool _configLoaded = false;

        protected ConfigPanel()
        {
            Loaded += ConfigPanel_Loaded;
        }

        // The static holder backing the App.config section for this panel type. The subclass forwards to its
        // own static field (which AppConfig.RegisterFolded reads/writes), e.g. => SectionConfig.
        protected abstract T Config { get; set; }

        // Copy the fragment's values into the panel's properties (restore). Called once, on first load, only
        // when a saved fragment exists - so property defaults stand in for a fresh install.
        protected abstract void ApplyConfig(T config);

        // Build a fresh fragment from the panel's current property values (for persistence).
        protected abstract T CaptureConfig();

        // The dependency properties whose change should auto-save the fragment.
        protected abstract DependencyProperty[] PersistedProperties { get; }

        // True once the initial restore has run - guards the auto-save handlers so restoring values
        // (and any construction-time property assignment) does not immediately re-persist.
        protected bool ConfigLoaded { get { return _configLoaded; } }

        private void ConfigPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (_configLoaded)
            {
                OnConfigReady();     // re-entry (tab re-shown): let the subclass refresh, but don't re-restore
                return;
            }
            _configLoaded = true;

            if (Config != null)
                ApplyConfig(Config);

            foreach (var dp in PersistedProperties)
            {
                if (dp == null)
                    continue;
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(dp, GetType())
                    .AddValueChanged(this, (s, ev) => OnPersistedPropertyChanged());
            }

            OnConfigReady();
        }

        // Called when a watched dependency property changes (after the initial restore). The default persists the
        // fragment; override to also refresh derived UI (a summary, a results grid) alongside the save.
        protected virtual void OnPersistedPropertyChanged()
        {
            Persist();
        }

        // Capture the current values and save App.config. No-op until the initial restore has completed.
        // Call this from a non-dependency-property change too (e.g. a ComboBox selection) to persist it.
        protected void Persist()
        {
            if (!_configLoaded)
                return;
            Config = CaptureConfig();
            AppConfig.Settings.Save();
        }

        // Hook run after the restore + watcher wiring on first load, and again each time the panel is
        // re-shown. Subclasses override to do post-restore work (sync a ComboBox, refresh a summary, etc.).
        protected virtual void OnConfigReady() { }
    }
}
