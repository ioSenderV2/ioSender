/*
 * LayoutModel.cs - part of CNC Controls library
 *
 * Hierarchical layout model (Phase 2b of the registration architecture refactor, see
 * docs/Architecture-Registration-Refactor.md). The layout is pure data: a tree of components
 * placed into named slots. A component carries NO placement capability - any component may go in
 * any slot; the slot's kind only governs how it is presented (tab header / stacked / flyout).
 *
 * Safety: a user can reorganise freely, but DefaultLayout.Build() is the canonical reset source and
 * EnsureEssentials() repairs a loaded tree so the Settings/Machine Setup tabs (and a Grbl tab) are
 * always reachable - the layout can't lock the user out of the editor.
 *
 * This file is free of WPF/app dependencies so the compose/migrate/repair logic is testable alone.
 */

using System.Collections.Generic;
using System.Linq;

namespace CNC.Controls
{
    // A placed component, plus (when it is a container) its slots. Leaf components have no slots.
    public sealed class LayoutNode
    {
        public string Component { get; set; }                  // registry Key (e.g. "GRBL", "Tools", "SurfaceSpoilboard")
        public List<LayoutSlot> Slots { get; set; } = new List<LayoutSlot>();

        public LayoutNode() { }
        public LayoutNode(string component) { Component = component; }
        public LayoutNode(string component, params LayoutSlot[] slots) { Component = component; Slots = slots.ToList(); }

        public LayoutSlot Slot(string name)
        {
            return Slots.FirstOrDefault(s => s.Name == name);
        }
    }

    // A named region within a container component, holding an ordered list of child components.
    public sealed class LayoutSlot
    {
        public string Name { get; set; }                       // slot id within the parent (e.g. "tabs", "tools", "center")
        public List<LayoutNode> Items { get; set; } = new List<LayoutNode>();

        public LayoutSlot() { }
        public LayoutSlot(string name, params LayoutNode[] items) { Name = name; Items = items.ToList(); }
        public LayoutSlot(string name, IEnumerable<string> componentKeys)
        {
            Name = name;
            Items = componentKeys.Select(k => new LayoutNode(k)).ToList();
        }
    }

    // Stable component keys + slot names. Tab keys reuse the ViewType names (Phase 1 TabRegistry).
    public static class LayoutKeys
    {
        // root container + its single slot
        public const string Root = "MainWindow";
        public const string SlotTabs = "tabs";

        // top-level tabs (== ViewType names)
        public const string Grbl = "GRBL", LoadStock = "LoadStock", Offsets = "Offsets",
                            Settings = "GRBLConfig", Probing = "Probing", SDCard = "SDCard",
                            LatheWizards = "LatheWizards", Tools = "Tools", MachineSetup = "MachineSetup";

        // Grbl tab's center container (JobWorkspace) + slot
        public const string SlotCenter = "center";
        public const string Program = "Program", Toolpath3D = "Toolpath3D", Console = "Console";

        // Tools container slot + tool components
        public const string SlotTools = "tools";
        public const string ToolTable = "ToolTable", StepperCal = "StepperCal", StepperScratch = "StepperScratch",
                            SurfaceSpoilboard = "SurfaceSpoilboard", Squareness = "Squareness",
                            Trinamic = "Trinamic", PID = "PID";

        // Components that must always remain reachable (recovery invariant).
        public static readonly string[] Essential = { Grbl, Settings, MachineSetup };
    }

    public static class DefaultLayout
    {
        // The canonical layout - also the "reset to default" source. Shows how the pieces nest:
        // MainWindow.tabs -> [ Grbl(center: Program/3D/Console), ..., Tools(tools: 7 tools), MachineSetup ].
        public static LayoutNode Build()
        {
            return new LayoutNode(LayoutKeys.Root,
                new LayoutSlot(LayoutKeys.SlotTabs,
                    new LayoutNode(LayoutKeys.Grbl,
                        new LayoutSlot(LayoutKeys.SlotCenter, new[] { LayoutKeys.Program, LayoutKeys.Toolpath3D, LayoutKeys.Console })),
                    new LayoutNode(LayoutKeys.LoadStock),
                    new LayoutNode(LayoutKeys.Offsets),
                    new LayoutNode(LayoutKeys.Settings),
                    new LayoutNode(LayoutKeys.Probing),
                    new LayoutNode(LayoutKeys.SDCard),
                    new LayoutNode(LayoutKeys.LatheWizards),
                    new LayoutNode(LayoutKeys.Tools,
                        new LayoutSlot(LayoutKeys.SlotTools, new[] {
                            LayoutKeys.ToolTable, LayoutKeys.StepperCal, LayoutKeys.StepperScratch,
                            LayoutKeys.SurfaceSpoilboard, LayoutKeys.Squareness, LayoutKeys.Trinamic, LayoutKeys.PID })),
                    new LayoutNode(LayoutKeys.MachineSetup)));
        }
    }

    public static class LayoutTree
    {
        // Depth-first enumeration of every node in the tree.
        public static IEnumerable<LayoutNode> Flatten(LayoutNode root)
        {
            if (root == null)
                yield break;
            yield return root;
            foreach (var slot in root.Slots)
                foreach (var child in slot.Items)
                    foreach (var n in Flatten(child))
                        yield return n;
        }

        public static bool Contains(LayoutNode root, string component)
        {
            return Flatten(root).Any(n => n.Component == component);
        }

        // Recovery invariant: a loaded/edited tree must never strand the user. If the root is missing or
        // the tabs slot is gone, fall back to the default. Then guarantee every Essential component still
        // exists somewhere - re-appending any that were removed to the top-level tabs slot - so the user
        // can always reach Settings (the layout editor) and Machine Setup. Returns the safe tree.
        public static LayoutNode EnsureEssentials(LayoutNode root)
        {
            if (root == null || root.Component != LayoutKeys.Root || root.Slot(LayoutKeys.SlotTabs) == null)
                return DefaultLayout.Build();

            var tabs = root.Slot(LayoutKeys.SlotTabs);
            foreach (var key in LayoutKeys.Essential)
                if (!Contains(root, key))
                    tabs.Items.Add(new LayoutNode(key));

            return root;
        }
    }
}
