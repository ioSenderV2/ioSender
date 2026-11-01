/*
 * LibStrings.cs - part of CNC Core library for Grbl
 *
 * Portable (non-WPF) string table for CNC.Core.
 *
 * Was a WPF ResourceDictionary loaded over a pack:// URI, which required CNC.Core to be a WPF
 * project (PresentationFramework + the WPF ProjectTypeGuids/<Page> item). That is the single
 * reason LibStrings.xaml was markup-compiled, and it blocked targeting .NET 8.
 *
 * LibStrings.xaml is now an <EmbeddedResource> instead of a <Page>: same file, same x:Uid/x:Key
 * pairs, so the Locale/<loc>/csv/CNC.Core.resources.*.csv translation rows and tools/locadd.py
 * are unaffected. It is parsed here with System.Xml.Linq, which is portable.
 *
 * FindResource(key) keeps its exact old signature and semantics - unknown key yields
 * string.Empty - so no call site changed.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace CNC.Core
{
    public class LibStrings
    {
        const string ResourceName = "CNC.Core.LibStrings.xaml";

        static readonly object sync = new object();
        static Dictionary<string, string> strings = null;

        /// <summary>
        /// Optional override, for a host that localizes at runtime (e.g. the WPF client resolving
        /// through its own translated resources). Return null or an empty string to fall through
        /// to the embedded English baseline.
        /// </summary>
        public static Func<string, string> Resolver { get; set; } = null;

        public static string FindResource(string key)
        {
            var resolver = Resolver;

            if (resolver != null) try
            {
                var resolved = resolver(key);
                if (!string.IsNullOrEmpty(resolved))
                    return resolved;
            }
            catch
            {
            }

            var table = Strings;

            return table.TryGetValue(key, out string value) ? value : string.Empty;
        }

        static Dictionary<string, string> Strings
        {
            get
            {
                if (strings == null) lock (sync)
                {
                    if (strings == null)
                        strings = Load();
                }

                return strings;
            }
        }

        static Dictionary<string, string> Load()
        {
            var table = new Dictionary<string, string>();

            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                        return table;

                    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

                    foreach (var element in XDocument.Load(stream).Root.Elements())
                    {
                        var key = (string)element.Attribute(x + "Key");
                        if (!string.IsNullOrEmpty(key))
                            table[key] = element.Value;
                    }
                }
            }
            catch
            {
                // Same posture as the old pack:// load - a failure yields an empty table and
                // FindResource() returns string.Empty, rather than taking the app down.
            }

            return table;
        }
    }
}
