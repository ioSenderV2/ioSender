/*
 * ConfigStore.cs - part of CNC Controls library
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
 * logic can be exercised in isolation.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace CNC.Controls
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
                        s.Read(payload);
                }
                else if (s.ImportLegacy())   // section absent from the file: one-time legacy import
                    MigratedOnLoad = true;
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

        internal static XElement ToElement(XmlSerializer serializer, object o)
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

        internal static object FromElement(XmlSerializer serializer, XElement el)
        {
            if (el == null)
                return null;
            using (var r = el.CreateReader())
                return serializer.Deserialize(r);
        }
    }
}
