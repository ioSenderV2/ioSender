/*
 * SettingsPanelRegistry.cs - part of CNC Controls library
 *
 * Registration for Settings:App panels (Phase 0.5 of the registration architecture refactor,
 * see docs/Architecture-Registration-Refactor.md).
 *
 * A feature contributes a settings panel without editing AppConfigView: either call
 * SettingsPanelRegistry.Register(...) from its own startup code, or implement
 * ISettingsPanelProvider on any public parameterless-constructible type (auto-discovered from
 * the loaded assemblies). AppConfigView drains the registry into its panel list at setup.
 *
 * This is purely additive: the built-in panels and the existing plugin pattern of adding to
 * UIViewModel.ConfigControls directly are unchanged.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;

namespace CNC.Controls
{
    // The settings navigation categories. Public so a panel in any assembly can name the category it
    // belongs to without the settings host having to know the panel's type.
    public static class SettingsCategories
    {
        public const string Controller = "Cat.Controller";
        public const string Application = "Cat.Application";
        public const string Jogging = "Cat.Jogging";
        public const string GCode = "Cat.GCode";
        public const string UserInterface = "Cat.Interface";
    }

    // A config panel declares where it belongs in the settings tree, and how it sorts among its
    // siblings. Implement it on the panel itself, so placement travels with the panel however it
    // reaches the host - registered through SettingsPanelRegistry, auto-discovered via
    // ISettingsPanelProvider, or added straight to UIViewModel.ConfigControls by a feature view.
    //
    // This is what retires the last of the hardcoded placement: the host used to carry a switch that
    // matched panels in other assemblies by full type name ("CNC.Controls.Camera.ConfigControl"),
    // because CNC Controls cannot reference them. A panel that doesn't implement this still lands in
    // Application, so nothing has to be updated in lockstep.
    public interface ISettingsPanelCategory
    {
        string SettingsCategory { get; }
        int SettingsOrder { get; }
    }

    // One registrable Settings:App panel. Order sorts the registry-contributed panels among
    // themselves (built-ins are added first by AppConfigView); lower Order shows higher.
    public sealed class SettingsPanelDescriptor
    {
        public string Key { get; }
        public int Order { get; }
        public Func<UserControl> Create { get; }

        public SettingsPanelDescriptor(string key, Func<UserControl> create, int order = 1000)
        {
            Key = key;
            Create = create;
            Order = order;
        }
    }

    // Implement on any public type with a parameterless constructor to have its settings panels
    // auto-discovered (no explicit Register call, no edit to AppConfigView).
    public interface ISettingsPanelProvider
    {
        IEnumerable<SettingsPanelDescriptor> GetSettingsPanels();
    }

    public class RestartRequiredEventArgs : EventArgs
    {
        public string Message { get; }
        public RestartRequiredEventArgs(string message) { Message = message; }
    }

    // A settings tab whose edits persist when the tab is left (rather than via an OK button). The host calls
    // Commit() when switching away from the tab or leaving the settings view. Implemented by the editor tabs
    // (key bindings / macros / main page) that were converted from modal dialogs to inline tabs.
    public interface ISettingsEditorTab
    {
        void Commit();
    }

    // One page contributed by an editor that used to carry its own tab strip.
    public sealed class SettingsSubPage
    {
        public string Key { get; }
        public string Label { get; }
        public System.Windows.FrameworkElement Content { get; }

        public SettingsSubPage(string key, string label, System.Windows.FrameworkElement content)
        {
            Key = key;
            Label = label;
            Content = content;
        }
    }

    // An editor that hosts its own tabs exposes them as separate nav pages instead, so the navigation
    // tree never ends up with a tab strip inside it - which is the thing the overhaul exists to remove.
    // The editor instance stays whole and keeps owning Commit()/ResetToDefaults() for all of its pages;
    // the nav node records it as the page's Owner.
    public interface ISettingsPageProvider
    {
        IEnumerable<SettingsSubPage> GetPages();

        // Several pages can share one editor instance as their content (the editor keeps its own
        // hooks on the control, so it must stay whole and in the visual tree). The host calls this on
        // entering a page so the editor can show the matching section.
        void ShowPage(string key);
    }

    // A settings panel or editor tab implements this to opt in to the shared footer's "Reset to Default" button.
    // The host shows Reset only on tabs that contain at least one resettable, and calls ResetToDefaults() on each
    // when clicked - so each panel owns what "default" means for its own settings (App/G Code Config values, jog
    // config, key bindings, or $RST=$ for the controller settings). Panels that don't implement it are left alone.
    public interface ISettingsResettable
    {
        void ResetToDefaults();
    }

    // Shared helper for ISettingsResettable panels: copy scalar property values (incl. nested config objects) from
    // a fresh-default source into the live target in place, so the target instance is preserved (its setters
    // notify, and anything else holding the same reference keeps working).
    public static class ConfigReset
    {
        private static bool IsScalar(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(double) || t == typeof(decimal);
        }

        public static void CopyScalars(object src, object dst)
        {
            if (src == null || dst == null)
                return;

            foreach (var p in src.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0)
                    continue;

                if (IsScalar(p.PropertyType))
                {
                    if (p.CanWrite)
                        try { p.SetValue(dst, p.GetValue(src)); } catch { }
                }
                else if (p.PropertyType.IsArray)
                {
                    if (p.CanWrite)
                        try { p.SetValue(dst, (p.GetValue(src) as Array)?.Clone()); } catch { }
                }
            }
        }
    }

    // A Settings:App panel implements this to declare that one of its settings only takes effect at startup,
    // so changing it needs an app restart. The panel raises RestartRequired (with a reason) when such a setting
    // changes; AppConfigView then surfaces the Restart button. This keeps the "needs restart" knowledge with the
    // feature that owns the setting, and is precise - a panel raises it only for its restart-only settings, not
    // its live ones. Works whether the panel is added via the registry or directly to UIViewModel.ConfigControls.
    public interface IRestartRequired
    {
        event EventHandler<RestartRequiredEventArgs> RestartRequired;
    }

    public static class SettingsPanelRegistry
    {
        private static readonly List<SettingsPanelDescriptor> _explicit = new List<SettingsPanelDescriptor>();

        public static void Register(SettingsPanelDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Key))
                return;
            _explicit.RemoveAll(d => d.Key == descriptor.Key);
            _explicit.Add(descriptor);
        }

        public static void Register(string key, Func<UserControl> create, int order = 1000)
        {
            Register(new SettingsPanelDescriptor(key, create, order));
        }

        // Explicitly-registered descriptors plus auto-discovered ones (deduped by Key,
        // explicit wins), ordered by Order.
        public static IEnumerable<SettingsPanelDescriptor> Collect()
        {
            var byKey = new Dictionary<string, SettingsPanelDescriptor>();
            foreach (var d in _explicit)
                byKey[d.Key] = d;
            foreach (var d in Discover())
                if (d != null && !string.IsNullOrEmpty(d.Key) && !byKey.ContainsKey(d.Key))
                    byKey[d.Key] = d;
            return byKey.Values.OrderBy(d => d.Order).ToList();
        }

        private static IEnumerable<SettingsPanelDescriptor> Discover()
        {
            var result = new List<SettingsPanelDescriptor>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface)
                        continue;
                    if (!typeof(ISettingsPanelProvider).IsAssignableFrom(t))
                        continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null)
                        continue;

                    ISettingsPanelProvider provider;
                    try { provider = (ISettingsPanelProvider)Activator.CreateInstance(t); }
                    catch { continue; }

                    try
                    {
                        var panels = provider.GetSettingsPanels();
                        if (panels != null)
                            result.AddRange(panels.Where(p => p != null));
                    }
                    catch { /* a misbehaving provider must not break the settings tab */ }
                }
            }

            return result;
        }
    }
}
