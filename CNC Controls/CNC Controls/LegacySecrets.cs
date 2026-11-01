/*
 * LegacySecrets.cs - part of CNC Controls library
 *
 * Reads the pre-portable secret location - HKCU\Software\ioSenderV2\Secrets - so an existing user's
 * stored API key survives the move to CNC.Core's portable file store.
 *
 * Lives here, not in CNC.Core, because Microsoft.Win32.Registry is Windows-only and Core is now free of
 * platform APIs. SecretStore.ImportIfAbsent takes this as a delegate, so the dependency points the right
 * way: the Windows host knows about the registry, the portable core does not.
 *
 * The old registry value is READ, not deleted. Deleting it would make a downgrade lose the key, and the
 * import is guarded on "nothing stored yet" so a stale registry value can never overwrite a newer one.
 * Worth revisiting once no one is running a build older than this.
 */

using Microsoft.Win32;
using CNC.Core;

namespace CNC.Controls
{
    public static class LegacySecrets
    {
        private const string RegistryPath = @"Software\ioSenderV2\Secrets";

        /// <summary>Read a secret from the legacy registry location. Null if absent/unreadable.</summary>
        public static string Read(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    var v = key?.GetValue(name) as string;
                    return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// One-time migration for every secret the app knows about. Call once the config directory is
        /// known (Resources.ConfigPath), since that is where the portable store writes.
        /// </summary>
        public static void MigrateAll()
        {
            SecretStore.ImportIfAbsent(FeedsSpeedsAiReview.ApiKeySecretName,
                                       () => Read(FeedsSpeedsAiReview.ApiKeySecretName));
        }
    }
}
