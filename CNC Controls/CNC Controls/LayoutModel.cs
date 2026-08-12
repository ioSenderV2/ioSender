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
        // root container + its slots. The main bar is no longer the only destination: a component may
        // sit in the tab strip OR in one of the menus, and the TREE decides which (2026-08-03). The
        // registry's Presentation/Menu only SEED this - so the split we shipped is a default the user
        // can drag back the other way in Settings > Main Page.
        public const string Root = "MainWindow";
        public const string SlotTabs = "tabs";
        public const string SlotMenuFile = "menuFile";
        public const string SlotMenuTools = "menuTools";

        // Every slot on the root that can hold a top-level component, in display order.
        public static readonly string[] RootSlots = { SlotTabs, SlotMenuFile, SlotMenuTools };

        // top-level tabs (== ViewType names)
        public const string Grbl = "GRBL", StartJob = "StartJob", Offsets = "Offsets",
                            Settings = "GRBLConfig", Probing = "Probing", SDCard = "SDCard",
                            LatheWizards = "LatheWizards", Tools = "Tools", MachineSetup = "MachineSetup",
                            HeightMap = "HeightMap", FeedsAndSpeeds = "FeedsAndSpeeds", WorkOrder = "WorkOrder";
        // OddJobs (the old container tab) is retired (2026-07-31) - Work Order was its only remaining sub-tab
        // (Setup fixup already folded into the Setup tab itself), so it's promoted to a bare top-level leaf
        // (WorkOrder, above) instead of a tab-inside-a-tab. OddJobs/SlotOddJobs/OddJobsWorkOrder/OddJobsSetup
        // stay defined ONLY for AppConfig.ApplyOneTimeFixups' migration of already-persisted layouts - nothing
        // current registers or reads them.
        public const string OddJobs = "OddJobs";

        // Grbl tab's center container (JobWorkspace) + slot
        public const string SlotCenter = "center";
        public const string Program = "Program", Toolpath3D = "Toolpath3D", Console = "Console";

        // Tools container slot + tool components
        public const string SlotTools = "tools";
        public const string ToolTable = "ToolTable", Trinamic = "Trinamic", PID = "PID";
        // Retired Tools components, kept defined ONLY so AppConfig.ApplyOneTimeFixups can strip them from
        // already-persisted layouts (same precedent as OddJobs above) - nothing registers or reads them now.
        //   2026-08-01: StepperCal (manual measurement) and StepperScratch (V-bit scratch marks) - the probe
        //               method replaced both.
        //   2026-08-02: StepperCalProbe and Squareness moved into Machine Setup's Calibration step, and
        //               SurfaceSpoilboard became a Work Order toolpath kind. Tools now holds only the three
        //               hardware-gated tools above, and hides itself when none of them are available.
        public const string StepperCal = "StepperCal", StepperScratch = "StepperScratch",
                            StepperCalProbe = "StepperCalProbe",
                            SurfaceSpoilboard = "SurfaceSpoilboard", Squareness = "Squareness";

        // Odd Jobs container slot + job components
        public const string SlotOddJobs = "oddjobs";
        public const string OddJobsSetup = "Setup", OddJobsWorkOrder = "WorkOrder";

        // Components that must always remain reachable (recovery invariant).
        public static readonly string[] Essential = { Grbl, Settings, MachineSetup };
    }

    public static class DefaultLayout
    {
        // The canonical layout - also the "reset to default" source. MUST stay in step with the Layout
        // section of Default-App.config (what a fresh install is seeded with) AND with its TabsKeys: a
        // fresh install that disagreed with "Reset to default" would be its own bug, and Config.Tabs is a
        // second authority that prunes this tree on every load (TabOrder.Apply).
        //
        // 2026-08-12: back to the FULL tab bar as it stood before the 2026-08-03 cutback, by request - the
        // compressed three-tab arrangement ships as a config OVERLAY you can apply instead (see
        // ConfigOverlay.cs), which is the right way round: an existing install keeps its saved Layout for
        // ever, so a crowded default that can be slimmed down beats a slim default nobody can opt out of.
        // The one thing NOT restored is the Tools CONTAINER tab - the Tools menu replaced it and stays.
        public static LayoutNode Build()
        {
            return new LayoutNode(LayoutKeys.Root,
                // Order is the pre-cutback bar (449c3b19^), minus the retired Tools container. Start Job
                // then Job: the flow is Start Job (set origin / TLO / measure) then Job (run). All three
                // slots are drop targets in Settings > Main Page, so any of this can be moved.
                new LayoutSlot(LayoutKeys.SlotTabs,
                    new LayoutNode(LayoutKeys.Settings),
                    new LayoutNode(LayoutKeys.FeedsAndSpeeds),
                    new LayoutNode(LayoutKeys.StartJob),
                    new LayoutNode(LayoutKeys.Grbl,
                        new LayoutSlot(LayoutKeys.SlotCenter, new[] { LayoutKeys.Program, LayoutKeys.Toolpath3D, LayoutKeys.Console })),
                    new LayoutNode(LayoutKeys.Offsets),
                    new LayoutNode(LayoutKeys.SDCard),
                    new LayoutNode(LayoutKeys.WorkOrder),
                    new LayoutNode(LayoutKeys.MachineSetup),
                    new LayoutNode(LayoutKeys.LatheWizards)),

                // Empty, not absent: EnforceMenuPlacement returns early when a menu slot is MISSING (it
                // reads that as "not migrated yet" and leaves the arrangement to the one-shot migration),
                // so the slot has to exist for the invariants to run at all.
                new LayoutSlot(LayoutKeys.SlotMenuFile),

                // The Tools CONTAINER is gone: its three hardware-gated tools are listed directly here,
                // one menu entry each, instead of being sub-tabs of a wrapper tab. Probing and Height Map
                // stay here too - they came off the default bar on 2026-07-26 (Start Job's Dynamic mode
                // folds their functionality in), so restoring the old bar does not bring them back.
                new LayoutSlot(LayoutKeys.SlotMenuTools,
                    new LayoutNode(LayoutKeys.Probing),
                    new LayoutNode(LayoutKeys.HeightMap),
                    new LayoutNode(LayoutKeys.ToolTable),
                    new LayoutNode(LayoutKeys.Trinamic),
                    new LayoutNode(LayoutKeys.PID)));
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

    public static class TabOrder
    {
        // Reorder/filter the top-level tabs slot to match a saved flat tab order (legacy Config.Tabs):
        // empty = keep the tree's current order; non-empty = exactly those, in order. Existing nodes are
        // reused (their nested slots are preserved) and essentials are re-added. Transitional - keeps the
        // old Edit-Main-Page editor (which writes Config.Tabs) working until the editor edits the tree.
        public static void Apply(LayoutNode root, IEnumerable<string> savedTabs)
        {
            var tabs = root?.Slot(LayoutKeys.SlotTabs);
            if (tabs == null)
                return;

            var list = savedTabs?.Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (list != null && list.Count > 0)
            {
                var byKey = tabs.Items.GroupBy(n => n.Component).ToDictionary(g => g.Key, g => g.First());
                var ordered = new List<LayoutNode>();
                foreach (var key in list)
                {
                    // A saved tab key with no node yet (e.g. a newly introduced top-level tab) gets a fresh leaf
                    // node so it is built rather than silently dropped.
                    if (!byKey.TryGetValue(key, out var node))
                        node = new LayoutNode(key);
                    if (!ordered.Contains(node))
                        ordered.Add(node);
                }
                if (ordered.Count > 0)
                    tabs.Items = ordered;
            }

            LayoutTree.EnsureEssentials(root);
        }
    }

    public static class LayoutMigration
    {
        // Build the initial tree from defaults, applying a saved top-level tab order/visibility - the old
        // flat Config.Tabs (Phase 1 semantics: empty = default order/all shown; non-empty = exactly these,
        // in order). The nested slots (Tools sub-tabs, Grbl center) take their defaults - they were not
        // configurable before. EnsureEssentials guarantees the result is safe.
        public static LayoutNode FromFlat(IEnumerable<string> savedTabs)
        {
            var tree = DefaultLayout.Build();
            var list = savedTabs?.Where(s => !string.IsNullOrEmpty(s)).ToList();

            if (list != null && list.Count > 0)
            {
                var tabsSlot = tree.Slot(LayoutKeys.SlotTabs);
                var byKey = tabsSlot.Items
                    .GroupBy(n => n.Component)
                    .ToDictionary(g => g.Key, g => g.First());

                var ordered = new List<LayoutNode>();
                foreach (var key in list)
                    if (byKey.TryGetValue(key, out var node) && !ordered.Contains(node))
                        ordered.Add(node);

                if (ordered.Count > 0)
                    tabsSlot.Items = ordered;
            }

            return LayoutTree.EnsureEssentials(tree);
        }
    }
}
