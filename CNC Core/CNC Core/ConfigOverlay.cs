/*
 * ConfigOverlay.cs - part of CNC Core library
 *
 * Layering one config document on top of another.
 *
 * An OVERLAY is just a trimmed App.config: the same <AppConfig version="2"> root holding a subset of
 * the <section key="..."> elements ConfigStore composes (see ConfigStore.cs). There is deliberately no
 * second format - you can produce one by deleting sections from a working config, every section type
 * is supported for free (including keys this build does not own), and the merge needs no per-section
 * knowledge at all because it happens on the XML before ConfigStore.ReadDocument ever sees it.
 *
 * Why it exists: ConfigStore only falls back to the shipped Default-App.config template for a section
 * that is ABSENT from the user's own file. An upgraded install therefore keeps its own saved Layout
 * forever, so a new UI arrangement can never reach an existing user. An overlay is how you hand
 * someone a specific change - "layer this and you get the compressed three-tab UI" - without touching
 * their machine settings, profiles, fixtures or keymaps, and reversibly.
 *
 * Two per-section modes:
 *   replace (default) - the overlay's payload supplants that section wholesale. What a layout overlay wants.
 *   merge             - the overlay payload's top-level child elements are grafted onto the existing
 *                       payload by element name (in place, order preserved), so an overlay can flip three
 *                       fields of Core without carrying all ~80 of them.
 *
 * WPF-free by intent, same as ConfigStore: this is plain XDocument surgery.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CNC.Core
{
    // One section as named by an overlay document, for describing an overlay before applying it.
    public sealed class ConfigOverlayEntry
    {
        public string Key { get; set; }
        public bool Merge { get; set; }

        public override string ToString()
        {
            return Merge ? Key + " (merge)" : Key;
        }
    }

    public static class ConfigOverlay
    {
        private const string RootName = "AppConfig";
        private const string SectionName = "section";
        private const string KeyAttr = "key";
        private const string ModeAttr = "mode";
        private const string NameAttr = "name";
        private const string VersionAttr = "version";
        private const int CurrentVersion = 2;

        public const string MergeMode = "merge";
        public const string ReplaceMode = "replace";

        // File suffix used for overlay fragments, so the file dialogs and the staged file agree.
        public const string FileExtension = ".ioconfig";

        // Core elements that identify THIS machine/install rather than describing a preference. Stripped on
        // export so an overlay handed to someone else can never repoint their controller connection or
        // overwrite the machine they have set up. Same list build.ps1 -default-config scrubs.
        public static readonly string[] MachineIdentityElements =
        {
            "NetworkHost", "PortParams", "LastMachine", "LastFirmwareBuild"
        };

        // Sections that describe the UI arrangement - the export preset, and the reason this exists.
        // Core is included as a MERGE section carrying only the two UI lists (see Extract).
        public static readonly string[] UiLayoutSections = { "Layout", "TabOrder" };

        // Core child elements that belong to the UI arrangement rather than to the machine.
        //
        // These are NOT optional extras. Config.Tabs is a SECOND authority over the tab bar: on every load
        // AppConfig calls TabOrder.Apply(layoutTree, Base.Tabs), which rebuilds the tabs slot to contain
        // exactly that flat list and silently deletes any node the list does not name. A Layout-only overlay
        // therefore appears to apply (the tree really does change) and is then undone before the tabs are
        // built - observed 2026-08-12, an added tab vanished between ReadDocument and BuildTabs.
        //
        // Note the element names: Tabs/HiddenViews are [XmlIgnore] List<string>; what is actually persisted
        // are the comma-joined TabsKeys/HiddenViewsKeys strings, and those are what an overlay must carry.
        public static readonly string[] UiLayoutCoreElements = { "TabsKeys", "HiddenViewsKeys" };

        // True if the document looks like something Apply can consume: a v2 <AppConfig> root with at
        // least one keyed <section>. (A legacy v1 <Config> document has no sections to layer.)
        public static bool IsOverlay(XDocument doc)
        {
            return Describe(doc).Count > 0;
        }

        // The optional friendly name an overlay may carry (<AppConfig name="Compressed UI">), for the
        // confirmation prompt. Null when absent.
        public static string NameOf(XDocument doc)
        {
            return doc?.Root != null && doc.Root.Name.LocalName == RootName ? (string)doc.Root.Attribute(NameAttr) : null;
        }

        // The sections an overlay would touch, in document order. Empty for anything that is not an overlay.
        public static List<ConfigOverlayEntry> Describe(XDocument doc)
        {
            var list = new List<ConfigOverlayEntry>();
            var root = doc?.Root;
            if (root == null || root.Name.LocalName != RootName)
                return list;

            foreach (var sec in root.Elements(SectionName))
            {
                var key = (string)sec.Attribute(KeyAttr);
                if (string.IsNullOrEmpty(key) || sec.Elements().FirstOrDefault() == null)
                    continue;
                list.Add(new ConfigOverlayEntry
                {
                    Key = key,
                    Merge = string.Equals((string)sec.Attribute(ModeAttr), MergeMode, StringComparison.OrdinalIgnoreCase)
                });
            }

            return list;
        }

        /// <summary>
        /// Layer <paramref name="overlay"/> onto <paramref name="target"/>, in place. Returns the section
        /// keys actually applied (empty if the overlay had nothing usable), so the caller can report and log
        /// exactly what changed rather than claiming a merge it cannot see.
        /// </summary>
        public static List<string> Apply(XDocument target, XDocument overlay)
        {
            var applied = new List<string>();

            var root = target?.Root;
            if (root == null || root.Name.LocalName != RootName)
                return applied;   // legacy v1 or unrecognised - nothing to layer onto (caller reports)

            foreach (var sec in Describe(overlay))
            {
                var source = overlay.Root.Elements(SectionName)
                                         .FirstOrDefault(s => (string)s.Attribute(KeyAttr) == sec.Key);
                var payload = source?.Elements().FirstOrDefault();
                if (payload == null)
                    continue;

                var existing = root.Elements(SectionName)
                                   .FirstOrDefault(s => (string)s.Attribute(KeyAttr) == sec.Key);

                if (existing == null)
                {
                    // Section the target does not have at all: both modes reduce to adding it outright.
                    root.Add(new XElement(SectionName, new XAttribute(KeyAttr, sec.Key), new XElement(payload)));
                }
                else if (sec.Merge)
                {
                    var into = existing.Elements().FirstOrDefault();
                    if (into == null)
                        existing.Add(new XElement(payload));
                    else
                        MergeElements(into, payload);
                }
                else
                {
                    existing.Elements().Remove();
                    existing.Add(new XElement(payload));
                }

                applied.Add(sec.Key);
            }

            return applied;
        }

        // Graft the overlay payload's top-level children onto the target payload by element name. An element
        // the target already has is replaced IN PLACE (so the section keeps its original element order, which
        // matters only for readability - XmlSerializer sequences do care about order within a list, and
        // replacing in place is what preserves it); a new one is appended. Elements the overlay does not
        // mention are left untouched - that is the whole point of merge mode.
        private static void MergeElements(XElement into, XElement from)
        {
            foreach (var child in from.Elements())
            {
                var same = into.Elements(child.Name).ToList();
                if (same.Count > 0)
                {
                    same[0].ReplaceWith(new XElement(child));
                    for (int i = 1; i < same.Count; i++)   // collapse duplicates onto the first
                        same[i].Remove();
                }
                else
                    into.Add(new XElement(child));
            }
        }

        /// <summary>
        /// Build an overlay document from <paramref name="source"/> (a loaded App.config) containing just the
        /// named sections. Machine identity is stripped from Core unless <paramref name="scrubIdentity"/> is
        /// false, because an exported overlay is meant to be handed to someone else.
        /// </summary>
        public static XDocument Extract(XDocument source, IEnumerable<string> keys, string name = null, bool scrubIdentity = true)
        {
            var root = new XElement(RootName, new XAttribute(VersionAttr, CurrentVersion));
            if (!string.IsNullOrWhiteSpace(name))
                root.Add(new XAttribute(NameAttr, name.Trim()));

            var doc = new XDocument(root);
            if (source?.Root == null)
                return doc;

            foreach (var key in keys ?? Enumerable.Empty<string>())
            {
                var sec = source.Root.Elements(SectionName)
                                     .FirstOrDefault(s => (string)s.Attribute(KeyAttr) == key);
                if (sec?.Elements().FirstOrDefault() == null)
                    continue;

                var copy = new XElement(sec);
                if (scrubIdentity && key == "Core")
                    foreach (var name2 in MachineIdentityElements)
                        copy.Elements().FirstOrDefault()?.Elements(name2).Remove();

                root.Add(copy);
            }

            return doc;
        }

        /// <summary>
        /// The UI-arrangement preset: the layout sections wholesale, plus a MERGE section carrying only
        /// Core's two UI lists. Merge is essential here - a replace of Core would drag the exporter's entire
        /// preference set (and machine) along with the tab arrangement.
        /// </summary>
        public static XDocument ExtractUiLayout(XDocument source, string name = null)
        {
            var doc = Extract(source, UiLayoutSections, name);

            var core = source?.Root?.Elements(SectionName)
                                    .FirstOrDefault(s => (string)s.Attribute(KeyAttr) == "Core")
                                   ?.Elements().FirstOrDefault();
            if (core != null)
            {
                var payload = new XElement(core.Name);
                foreach (var el in UiLayoutCoreElements)
                    foreach (var found in core.Elements(el))
                        payload.Add(new XElement(found));

                if (payload.HasElements)
                    doc.Root.Add(new XElement(SectionName,
                                              new XAttribute(KeyAttr, "Core"),
                                              new XAttribute(ModeAttr, MergeMode),
                                              payload));
            }

            return doc;
        }
    }
}
