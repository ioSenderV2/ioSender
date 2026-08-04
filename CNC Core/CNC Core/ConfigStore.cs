/*
 * ConfigStore.cs - part of CNC Core library
 *
 * Registration-based application config (Phase 0 of the registration architecture refactor,
 * see docs/Architecture-Registration-Refactor.md).
 *
 * Components register an IConfigSection; the store composes every section into a single
 * App.config document and routes each <section> back to its owner on load. Sections from
 * builds not present in this binary are preserved verbatim (subset-build safety), and an
 * absent section can pull its values from a legacy standalone file via a one-time importer.
 *
 * This file is intentionally free of WPF / app dependencies so the compose/parse/migrate
 * logic can be exercised in isolation - which is what let it move here from CNC.Controls unchanged
 * (pr/portable-core). It is plain XDocument + XmlSerializer, NOT System.Configuration, so it already
 * worked on .NET 8; it was only ever in the WPF assembly by accident of history.
 *
 * Note the on-disk format is deliberately untouched by that move: App.config holds real user data
 * (profiles, layout, keymaps, probe definitions, fixtures, wizard params) on installs that are not ours.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace CNC.Core
{
    // One registrable unit of configuration. Write() returns the serialized payload element
    // (e.g. <JogConfig>...); the store wraps it in <section key="...">. Read() receives that
    // same payload element. ImportLegacy() is the one-time migration hook (decision B): it is
    // called only when the section is absent from the loaded document, and returns true if it
    // populated itself from a legacy standalone file (so the store can flag a post-load save).
    public interface IConfigSection
    {
        string Key { get; }
        XElement Write();
        void Read(XElement payload);
        bool ImportLegacy();
    }

    // Section whose data lives elsewhere (e.g. on AppConfig.Base) - accessed via get/set delegates.
    // Used for the built-in sections carved out of the monolithic Config so the AppConfig.Base.X
    // facade keeps returning the same instances.
    public sealed class XmlObjectSection<T> : IConfigSection where T : class
    {
        private readonly Func<T> _get;
        private readonly Action<T> _set;
        private readonly XmlSerializer _ser;
        private readonly Func<T> _importLegacy;

        public string Key { get; }

        public XmlObjectSection(string key, Func<T> get, Action<T> set, XmlSerializer serializer = null, Func<T> importLegacy = null)
        {
            Key = key;
            _get = get;
            _set = set;
            _ser = serializer ?? new XmlSerializer(typeof(T));
            _importLegacy = importLegacy;
        }

        public XElement Write()
        {
            return ConfigStore.ToElement(_ser, _get());
        }

        public void Read(XElement payload)
        {
            var v = (T)ConfigStore.FromElement(_ser, payload);
            if (v != null)
                _set(v);
        }

        public bool ImportLegacy()
        {
            if (_importLegacy == null)
                return false;
            var v = _importLegacy();
            if (v == null)
                return false;
            _set(v);
            return true;
        }
    }

    // Section that owns its instance inside the store (the pattern for new features: define a
    // config class in your own file, register an OwnedSection, read it via ConfigStore.Get<T>()).
    public sealed class OwnedSection<T> : IConfigSection where T : class, new()
    {
        private readonly XmlSerializer _ser;
        private readonly Func<T> _importLegacy;

        public string Key { get; }
        public T Value { get; private set; }

        public OwnedSection(string key, Func<T> importLegacy = null)
        {
            Key = key;
            _ser = new XmlSerializer(typeof(T));
            _importLegacy = importLegacy;
            Value = new T();
        }

        public XElement Write()
        {
            return ConfigStore.ToElement(_ser, Value);
        }

        public void Read(XElement payload)
        {
            var v = (T)ConfigStore.FromElement(_ser, payload);
            if (v != null)
                Value = v;
        }

        public bool ImportLegacy()
        {
            if (_importLegacy == null)
                return false;
            var v = _importLegacy();
            if (v == null)
                return false;
            Value = v;
            return true;
        }
    }

    public static class ConfigStore
    {
        private const string RootName = "AppConfig";
        private const string LegacyRootName = "Config";
        private const string SectionName = "section";
        private const string KeyAttr = "key";
        private const string VersionAttr = "version";
        private const int CurrentVersion = 2;

        private static readonly List<IConfigSection> _sections = new List<IConfigSection>();
        // <section> elements from the file with no registered owner (a feature not in this build).
        // Preserved verbatim and re-emitted on save so a subset build never wipes another's config.
        private static readonly Dictionary<string, XElement> _unknown = new Dictionary<string, XElement>();

        // True when the last ReadDocument()/legacy load populated a section from a legacy standalone
        // file (decision B) - the caller should persist immediately so the data lands in App.config.
        public static bool MigratedOnLoad { get; private set; }

        // Sections whose Read() threw on the last ReadDocument() call, with a short reason - e.g. a
        // persisted enum value that a later code change removed/renamed (confirmed as a real case
        // 2026-08-02: a custom Work Order tool saved with Kind="Mill" before that value was split into
        // EndMill/OFlute/BallEnd/Surfacing). That section is left at whatever default its owner already
        // had (silently discarding just its own data), rather than letting the exception escape ONE
        // section's Read() and abort the whole document - which, before this existed, meant every OTHER
        // section registered after the failing one in Register() order silently never loaded either (fell
        // back to defaults too) even though its own data in the file was perfectly fine. The caller
        // (AppConfig) surfaces this list to the operator rather than resetting/losing data silently.
        public static readonly List<string> LoadWarnings = new List<string>();

        // AppConfig wires this once (before the first ReadDocument) to the shipped read-only
        // Default-App.config template - keeps this file free of that file-path/AppConfig-specific
        // knowledge (see the header comment) while still letting a section absent from the user's own
        // file recover the shop-curated default instead of just the bare C# field-initializer default.
        // Returns the section's payload XElement (same shape ReadDocument hands to s.Read()), or null.
        public static Func<string, XElement> TemplateSectionLookup;

        // Register (or replace, by Key) a section. Registration order is the on-disk order; register
        // "Core" first so it rebuilds AppConfig.Base before the nested sections assign into it.
        public static void Register(IConfigSection section)
        {
            if (section == null)
                return;
            int i = _sections.FindIndex(s => s.Key == section.Key);
            if (i >= 0)
                _sections[i] = section;
            else
                _sections.Add(section);
        }

        // Retrieve a feature's owned instance.
        public static T Get<T>() where T : class, new()
        {
            foreach (var s in _sections)
                if (s is OwnedSection<T> owned)
                    return owned.Value;
            return null;
        }

        public static bool IsLegacy(XDocument doc)
        {
            return doc?.Root != null && doc.Root.Name.LocalName == LegacyRootName;
        }

        // Compose every registered section plus any preserved unknown sections into one document.
        public static XDocument WriteDocument()
        {
            var root = new XElement(RootName, new XAttribute(VersionAttr, CurrentVersion));

            foreach (var s in _sections)
            {
                var payload = s.Write();
                if (payload == null)
                    continue;
                root.Add(new XElement(SectionName, new XAttribute(KeyAttr, s.Key), payload));
            }

            // Re-emit sections we don't own (unless a later build has since registered that key).
            foreach (var kv in _unknown)
            {
                if (_sections.Any(s => s.Key == kv.Key))
                    continue;
                root.Add(new XElement(kv.Value));
            }

            return new XDocument(root);
        }

        // Parse a v2 (<AppConfig>) document: route each <section> to its owner, stash unknowns, and
        // run legacy importers for any registered section the document doesn't contain. Caller handles
        // the v1 (<Config>) format separately (see IsLegacy).
        public static void ReadDocument(XDocument doc)
        {
            MigratedOnLoad = false;
            _unknown.Clear();
            LoadWarnings.Clear();

            var root = doc?.Root;
            if (root == null)
                return;

            // Index the file's sections by key.
            var byKey = new Dictionary<string, XElement>();
            foreach (var sec in root.Elements(SectionName))
            {
                var key = (string)sec.Attribute(KeyAttr);
                if (!string.IsNullOrEmpty(key))
                    byKey[key] = sec;   // last wins on duplicate
            }

            // Process registered sections in REGISTRATION order, not document order, so a section
            // that depends on an earlier one (e.g. the nested sections assigning into the Base that
            // "Core" rebuilds) is never read before its dependency regardless of on-disk ordering.
            foreach (var s in _sections)
            {
                if (byKey.TryGetValue(s.Key, out var sec))
                {
                    var payload = sec.Elements().FirstOrDefault();
                    if (payload != null)
                    {
                        // Isolated per section - a single section whose saved data no longer deserializes
                        // (a persisted value a later code change removed/renamed) must not take any OTHER
                        // section down with it. That section's owner simply keeps whatever default it
                        // already had; see LoadWarnings' own comment for why this matters and how it's
                        // surfaced.
                        try { s.Read(payload); }
                        catch (Exception ex) { LoadWarnings.Add(string.Format("{0}: {1}", s.Key, ex.Message)); }
                    }
                }
                else
                {
                    // Section absent from the user's own file. Prefer a real legacy standalone file (the
                    // operator's own actual prior data) over the shipped template; only fall back to the
                    // template's curated default - not just the section owner's bare C# field-initializer
                    // default - when there's no legacy data to recover. Neither counts as "migrated" (a
                    // save-worthy change) if it silently fails; a template value merged in IS worth an
                    // immediate save, same as a legacy import, so an upgrade's curated defaults actually
                    // land in the user's own App.config rather than being re-derived from the template
                    // every single launch.
                    bool recovered = s.ImportLegacy();
                    if (!recovered)
                    {
                        var tmplPayload = TemplateSectionLookup?.Invoke(s.Key);
                        if (tmplPayload != null)
                        {
                            try { s.Read(tmplPayload); recovered = true; }
                            catch (Exception ex) { LoadWarnings.Add(string.Format("{0} (template default): {1}", s.Key, ex.Message)); }
                        }
                    }
                    if (recovered)
                        MigratedOnLoad = true;
                }
            }

            // Preserve any file sections we don't own (a feature not present in this build).
            foreach (var kv in byKey)
            {
                if (!_sections.Any(s => s.Key == kv.Key))
                    _unknown[kv.Key] = new XElement(kv.Value);
            }
        }

        // Run one-time legacy importers for every registered section. Used on the v1 (<Config>) -> v2
        // load path, where the sectioned ReadDocument is bypassed: sections whose values came from the
        // legacy blob have no importer (no-op), while new-concept sections (e.g. Layout) import.
        public static void ImportLegacyForAbsentSections()
        {
            foreach (var s in _sections)
                if (s.ImportLegacy())
                    MigratedOnLoad = true;
        }

        // Clear registrations + preserved unknowns. For test isolation only.
        public static void Reset()
        {
            _sections.Clear();
            _unknown.Clear();
            MigratedOnLoad = false;
        }

        // ---- serialization helpers (object <-> XElement, no xsi/xsd namespace noise) ----

        // public, not internal: IConfigSection is an extension point implemented outside this assembly
        // (CNC.Controls' LayoutSection, CNC.Controls.Lathe's WizardConfig), and those implementors need
        // these helpers to produce/consume their payload element.
        public static XElement ToElement(XmlSerializer serializer, object o)
        {
            if (o == null)
                return null;

            var tmp = new XDocument();
            var ns = new XmlSerializerNamespaces();
            ns.Add(string.Empty, string.Empty);
            using (var w = tmp.CreateWriter())
                serializer.Serialize(w, o, ns);
            return tmp.Root;
        }

        public static object FromElement(XmlSerializer serializer, XElement el)
        {
            if (el == null)
                return null;
            using (var r = el.CreateReader())
                return serializer.Deserialize(r);
        }
    }
}
