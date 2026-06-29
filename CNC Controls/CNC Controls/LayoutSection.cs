/*
 * LayoutSection.cs - part of CNC Controls library
 *
 * ConfigStore section for the hierarchical layout tree (Phase 2b). Persists the LayoutNode tree as
 * <section key="Layout"> in App.config and applies the recovery invariant (LayoutTree.EnsureEssentials)
 * on every load/import, so a corrupt or self-stranding saved layout can never lock the user out.
 *
 * Migration (decision-B style importer): when the Layout section is absent (first run on a build that
 * has it), ImportLegacy() builds the tree from defaults overlaid with the old flat Config.Tabs.
 */

using System;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace CNC.Controls
{
    public sealed class LayoutSection : IConfigSection
    {
        private static readonly XmlSerializer ser = new XmlSerializer(typeof(LayoutNode));
        private readonly Func<LayoutNode> importLegacy;

        public string Key { get { return "Layout"; } }

        // The live layout tree - always passed through EnsureEssentials so it is safe to consume.
        public LayoutNode Root { get; private set; } = DefaultLayout.Build();

        public LayoutSection(Func<LayoutNode> importLegacy = null)
        {
            this.importLegacy = importLegacy;
        }

        public XElement Write()
        {
            return ConfigStore.ToElement(ser, Root);
        }

        public void Read(XElement payload)
        {
            Root = LayoutTree.EnsureEssentials((LayoutNode)ConfigStore.FromElement(ser, payload));
        }

        public bool ImportLegacy()
        {
            if (importLegacy == null)
                return false;
            Root = LayoutTree.EnsureEssentials(importLegacy());
            return true;
        }
    }
}
