/*
 * ComponentRegistry.cs - part of CNC Controls library
 *
 * A general registry of placeable components (Phase 2b). A component is just a stable Key, a display
 * Label, and a factory - it carries NO placement info; the layout tree decides where it goes and the
 * slot decides how it is presented. Hosts (e.g. ToolsView) build a slot's children by looking each
 * layout-node's Component key up here. Adding a component (e.g. a new tool) is then a Register call
 * from its own code + a node in the default layout - no edit to the host.
 */

using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace CNC.Controls
{
    public sealed class ComponentDescriptor
    {
        public string Key { get; }
        public string Label { get; }                 // display text (localization of registered components TBD)
        public Func<UserControl> Create { get; }

        public ComponentDescriptor(string key, string label, Func<UserControl> create)
        {
            Key = key;
            Label = label;
            Create = create;
        }
    }

    public static class ComponentRegistry
    {
        private static readonly Dictionary<string, ComponentDescriptor> _byKey = new Dictionary<string, ComponentDescriptor>();

        public static void Register(ComponentDescriptor descriptor)
        {
            if (descriptor != null && !string.IsNullOrEmpty(descriptor.Key))
                _byKey[descriptor.Key] = descriptor;   // last registration wins
        }

        public static void Register(string key, string label, Func<UserControl> create)
        {
            Register(new ComponentDescriptor(key, label, create));
        }

        public static ComponentDescriptor Get(string key)
        {
            return key != null && _byKey.TryGetValue(key, out var d) ? d : null;
        }
    }
}
