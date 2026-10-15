/*
 * SerialPortDescriptions.cs - part of CNC Controls library
 *
 * Supplies friendly names for serial ports ("COM3" -> "USB Serial Device") to CNC.Core's port list, via
 * WMI (Win32_PnPEntity). This is the Windows-specific half of what SerialStream.Refresh used to do
 * inline: WMI compiles on any target but only works on Windows at runtime, so it could not stay in Core.
 *
 * Note the split is deliberate and is NOT "port enumeration is client-side". Enumerating the ports has to
 * happen where the hardware is - the server, once there is one - and SerialPort.GetPortNames() is portable,
 * so that stays in Core. Only the cosmetic description is host-specific, and a host that installs nothing
 * gets bare "COM3" entries rather than losing the port.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using CNC.Core;

namespace CNC.Controls
{
    public static class SerialPortDescriptions
    {
        /// <summary>Install the WMI-backed description lookup. Called once at startup.</summary>
        public static void Register()
        {
            SerialPorts.DescriptionProvider = Lookup;
        }

        // Returns only the ports it could describe; callers fall back to the bare name. Never throws -
        // WMI can be slow, disabled, or unavailable, and a missing description must not cost you the port.
        private static IDictionary<string, string> Lookup(IEnumerable<string> portNames)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption like '%(COM%'"))
                {
                    var captions = searcher.Get().Cast<ManagementBaseObject>()
                                                 .Select(p => p["Caption"] as string)
                                                 .Where(c => !string.IsNullOrEmpty(c))
                                                 .ToList();

                    foreach (var name in portNames)
                    {
                        var tag = '(' + name + ')';
                        var caption = captions.FirstOrDefault(c => c.Contains(tag));

                        if (caption != null)
                            map[name] = caption.Replace(tag, string.Empty).Trim();
                    }
                }
            }
            catch { }

            return map;
        }
    }
}
