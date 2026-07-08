/*
 * NamedPositionConfig.cs - part of CNC Controls library
 *
 * App-side library of named machine-coordinate positions ("fixtures"). grblHAL stores exactly ONE G28
 * predefined position (NVS), so a named library of recurring reference points (a 90 deg sheet-goods fence
 * origin, a machinist-vice corner, ...) has to live in the app. Each preset holds the MACHINE coordinates
 * captured at Set time; recall rapids there via G53 (see GotoBaseControl.SafeGotoMachine) without touching
 * the firmware slot. Persisted as a ConfigStore OwnedSection ("NamedPositions") in %AppData%\ioSender.
 *
 * Currently only the G28 flyout uses this (G30/G59.3 are deliberate out-of-envelope park spots, not
 * fixtures); the Code field keeps the store open to other predefined positions without a schema change.
 */

using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace CNC.Controls
{
    // One named machine-coordinate position. Coords is an invariant comma-separated list of the enabled
    // axes' machine coordinates (round-trips through CNC.Core.Position: new Position(Coords) / Parse).
    public class NamedPosition
    {
        [XmlAttribute("code")]
        public string Code { get; set; } = "G28";

        [XmlAttribute("name")]
        public string Name { get; set; } = string.Empty;

        [XmlAttribute("coords")]
        public string Coords { get; set; } = string.Empty;
    }

    [XmlRoot("NamedPositions")]
    public class NamedPositionConfig
    {
        [XmlElement("Position")]
        public List<NamedPosition> Positions { get; set; } = new List<NamedPosition>();

        // Saved presets for a predefined-position code (e.g. "G28"), in insertion order.
        public IEnumerable<NamedPosition> For(string code)
        {
            return Positions.Where(p => p.Code == code);
        }

        public NamedPosition Find(string code, string name)
        {
            return Positions.FirstOrDefault(p => p.Code == code && p.Name == name);
        }

        // Upsert a preset by (code, name): overwrite an existing name's coords, else append.
        public void Set(string code, string name, string coords)
        {
            var p = Find(code, name);
            if (p == null)
                Positions.Add(p = new NamedPosition { Code = code, Name = name });
            p.Coords = coords;
        }

        public void Remove(string code, string name)
        {
            var p = Find(code, name);
            if (p != null)
                Positions.Remove(p);
        }

        // The next unused default name for a code: "Fixture-1", "Fixture-2", ... (lowest free index).
        public string NextDefaultName(string code, string prefix = "Fixture-")
        {
            var used = new HashSet<string>(For(code).Select(p => p.Name));
            int n = 1;
            while (used.Contains(prefix + n))
                n++;
            return prefix + n;
        }
    }
}
