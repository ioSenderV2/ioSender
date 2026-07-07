/*
 * TabOrderConfig.cs - part of CNC Controls library
 *
 * Persisted user tab ordering for StretchTabControls that have no other order authority (the Probing
 * and Settings sub-tab strips). Each entry maps a control's PersistKey to the ordered list of tab ids
 * (a tab's x:Name, else its Tag string, else its header text) as the user last arranged them by drag.
 *
 * The main tab bar and the Tools sub-tabs are NOT stored here - their order lives in the layout tree /
 * legacy Config.Tabs (the same store the "Edit Main Page" editor writes), so drag-reorder on those bars
 * updates that store instead and the two never disagree.
 */

using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace CNC.Controls
{
    [XmlRoot("TabOrder")]
    public class TabOrderConfig
    {
        public class Bar
        {
            [XmlAttribute("key")]
            public string Key { get; set; }

            [XmlElement("Tab")]
            public List<string> Order { get; set; } = new List<string>();
        }

        [XmlElement("Bar")]
        public List<Bar> Bars { get; set; } = new List<Bar>();

        // Saved order for a control's PersistKey, or null when the user has never reordered that bar.
        public List<string> Get(string key)
        {
            return Bars.FirstOrDefault(b => b.Key == key)?.Order;
        }

        public void Set(string key, IEnumerable<string> ids)
        {
            var bar = Bars.FirstOrDefault(b => b.Key == key);
            if (bar == null)
                Bars.Add(bar = new Bar { Key = key });
            bar.Order = ids.ToList();
        }
    }
}
