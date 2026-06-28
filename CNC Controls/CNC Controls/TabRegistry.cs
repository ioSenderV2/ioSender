/*
 * TabRegistry.cs - part of CNC Controls library for Grbl
 *
 * Registry of the main-window tabs (Grbl, Probing, SD Card, ...). Phase 1 of the registration
 * architecture refactor (see docs/Architecture-Registration-Refactor.md): a host that opts in
 * (ioSender XL) builds its TabControl from registered TabDescriptors instead of hardcoded XAML,
 * so MainWindow is a container and a feature/plugin contributes a tab via a descriptor.
 *
 * Two layers:
 *  - TabDescriptor: factory + placement metadata used by the host to BUILD the tabs.
 *  - TabInfo / Available / Publish: the lighter list the host PUBLISHES after building (post
 *    capability filtering) for the "Edit Main Page" Tabs editor to reorder/hide.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace CNC.Controls
{
    public class TabInfo
    {
        public string Name { get; }    // ViewType name - stored in Config.Tabs, stable across sessions
        public string Label { get; }   // tab header, shown in the editor

        public TabInfo(string name, string label)
        {
            Name = name;
            Label = label;
        }
    }

    // A registrable main-window tab. The host instantiates Create() into a TabItem, runs the
    // optional Configure() hook (e.g. to wire an event), and uses the placement flags to drive
    // initial enable state and the "always reachable" protection - replacing the scattered
    // hardcoded ViewType lists the host used to carry.
    public class TabDescriptor
    {
        public ViewType ViewType { get; }
        public string Name { get; }                  // stable key (defaults to ViewType name) for Config.Tabs
        public string Label { get; }                 // header text (localization of registered tabs is TBD)
        public int Order { get; }                    // display order; lower first
        public bool EnabledWhenDisconnected { get; } // initial IsEnabled before a controller connects
        public bool AlwaysVisible { get; }           // never hidden by a saved tab layout (e.g. Settings, Machine Setup)
        public Func<UserControl> Create { get; }
        public Action<UserControl> Configure { get; }

        public TabDescriptor(ViewType viewType, string label, Func<UserControl> create,
                             int order = 1000, bool enabledWhenDisconnected = false,
                             bool alwaysVisible = false, Action<UserControl> configure = null,
                             string name = null)
        {
            ViewType = viewType;
            Label = label;
            Create = create;
            Order = order;
            EnabledWhenDisconnected = enabledWhenDisconnected;
            AlwaysVisible = alwaysVisible;
            Configure = configure;
            Name = name ?? viewType.ToString();
        }
    }

    public static class TabRegistry
    {
        // ---- build layer (Phase 1): descriptors the host turns into TabItems ----

        private static readonly List<TabDescriptor> _descriptors = new List<TabDescriptor>();

        public static void Register(TabDescriptor descriptor)
        {
            if (descriptor == null)
                return;
            int i = _descriptors.FindIndex(d => d.ViewType == descriptor.ViewType && d.Name == descriptor.Name);
            if (i >= 0)
                _descriptors[i] = descriptor;
            else
                _descriptors.Add(descriptor);
        }

        // Registered tabs in display order.
        public static IEnumerable<TabDescriptor> Descriptors
        {
            get { return _descriptors.OrderBy(d => d.Order).ToList(); }
        }

        public static TabDescriptor DescriptorFor(ViewType viewType)
        {
            return _descriptors.FirstOrDefault(d => d.ViewType == viewType);
        }

        // Look up by stable Name (== layout-tree component key). Used when building tabs from the layout tree.
        public static TabDescriptor DescriptorByName(string name)
        {
            return _descriptors.FirstOrDefault(d => d.Name == name);
        }

        // ---- publish layer: the lighter list the editor reads (unchanged behaviour) ----

        // All tabs present in the host window (in their built-in order), published by the host at startup.
        public static List<TabInfo> Available = new List<TabInfo>();

        // True once a host that supports tab configuration has published its tabs (ioSender XL).
        public static bool Enabled { get { return Available.Count > 0; } }

        public static void Publish(IEnumerable<TabInfo> tabs)
        {
            Available = new List<TabInfo>(tabs);
        }
    }
}
