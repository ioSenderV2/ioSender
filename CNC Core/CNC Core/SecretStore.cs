/*
 * SecretStore.cs - part of CNC Core library
 *
 * Per-user secret storage (currently just the optional Anthropic API key for Feeds & Speeds' AI review),
 * replacing ad-hoc "read a credential out of an environment variable" call sites.
 *
 * Was HKCU\Software\ioSenderV2\Secrets via Microsoft.Win32.Registry - the last Windows-only API in
 * CNC.Core. It is now a small provider interface with a portable file-backed default, so a .NET 8 host
 * (and eventually a non-Windows one) works without change. A host that wants something better - a
 * platform keychain, Credential Manager, an encrypted store - registers its own provider; same pattern as
 * UserPrompt.Handler, UiContext, EventUtils.Pump and GrblViewModel.KeyboardFactory.
 *
 * TRUST LEVEL, unchanged and deliberately so: values are stored in PLAIN TEXT, exactly as the registry
 * version did (its own header said as much) and as the ANTHROPIC_API_KEY environment variable before it.
 * This is not a hardening change and should not be mistaken for one - same secret, same per-user trust
 * boundary, portable location. Encrypting at rest (e.g. ASP.NET Core Data Protection) is a separate
 * decision, and the provider is the seam to do it behind.
 *
 * The file is deliberately NOT App.config: that is snapshotted into Backups on every startup and is the
 * thing a user is most likely to hand over when asking for support. A credential should not ride along.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CNC.Core
{
    /// <summary>Pluggable backing store. Implementations must never throw - a missing or unreadable
    /// store means "no secret", not a broken app.</summary>
    public interface ISecretStore
    {
        string Get(string name);
        void Set(string name, string value);
    }

    public static class SecretStore
    {
        private static ISecretStore provider;

        /// <summary>Install a host-specific store (platform keychain, encrypted store, a test double).
        /// Must be called before the first Get/Set.</summary>
        public static void Register(ISecretStore store)
        {
            provider = store;
        }

        public static ISecretStore Provider
        {
            get { return provider ?? (provider = new FileSecretStore()); }
        }

        // Null when unset/unreadable - never throws.
        public static string Get(string name)
        {
            try
            {
                var v = Provider.Get(name);
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }

        public static bool Has(string name)
        {
            return !string.IsNullOrEmpty(Get(name));
        }

        // Empty/null value clears the entry instead of storing a blank string. Never throws.
        public static void Set(string name, string value)
        {
            try
            {
                Provider.Set(name, string.IsNullOrEmpty(value) ? null : value.Trim());
            }
            catch { }
        }

        /// <summary>
        /// One-time migration helper: adopt a value from a legacy store only if nothing is held here yet.
        /// The legacy reader is supplied by the host (the WPF app reads the old registry key), so CNC.Core
        /// stays free of platform APIs. Returns true if a value was imported.
        /// </summary>
        public static bool ImportIfAbsent(string name, Func<string> readLegacy)
        {
            if (readLegacy == null || Has(name))
                return false;

            string legacy = null;
            try { legacy = readLegacy(); } catch { }

            if (string.IsNullOrWhiteSpace(legacy))
                return false;

            Set(name, legacy);

            return true;
        }
    }

    /// <summary>
    /// Portable default: a plain "name=value" file, one entry per line, in the profile's config directory.
    /// Deliberately not XML/JSON - the content is a handful of opaque strings, and a trivial format keeps
    /// this free of any serializer dependency. Values cannot contain a newline (API keys do not).
    /// </summary>
    public class FileSecretStore : ISecretStore
    {
        private const string FileName = "secrets.txt";

        private static string FilePath
        {
            get
            {
                var dir = Resources.ConfigPath;
                return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, FileName);
            }
        }

        private static Dictionary<string, string> Read()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = FilePath;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return map;

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq > 0)
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }

            return map;
        }

        public string Get(string name)
        {
            string v;
            return Read().TryGetValue(name, out v) ? v : null;
        }

        public void Set(string name, string value)
        {
            var path = FilePath;
            if (string.IsNullOrEmpty(path))
                return;

            var map = Read();

            if (value == null)
                map.Remove(name);
            else
                map[name] = value;

            if (map.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("# ioSender secrets - plain text, per-user. Do not share this file.");
            foreach (var kv in map)
                sb.AppendLine(kv.Key + "=" + kv.Value);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}
