/*
 * SecretStore.cs - part of CNC Core library
 *
 * Simple per-user registry-backed secret storage (HKCU\Software\ioSenderV2\Secrets\<name>),
 * replacing ad-hoc "read a credential out of an environment variable" call sites
 * (FeedsSpeedsAiReview's old ANTHROPIC_API_KEY lookup). Plain string storage under the
 * current Windows user's registry hive - the same trust level the env-var precedent already
 * had (not DPAPI-encrypted), just visible/settable from Settings > App instead of hidden
 * OS-level state the app never surfaces.
 */

using System;
using Microsoft.Win32;

namespace CNC.Core
{
    public static class SecretStore
    {
        private const string RegistryPath = @"Software\ioSenderV2\Secrets";

        // Null when unset/unreadable - never throws.
        public static string Get(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    string v = key?.GetValue(name) as string;
                    return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                }
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
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    if (key == null)
                        return;
                    if (string.IsNullOrEmpty(value))
                    {
                        try { key.DeleteValue(name, false); } catch { }
                    }
                    else
                    {
                        key.SetValue(name, value.Trim(), RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }
    }
}
