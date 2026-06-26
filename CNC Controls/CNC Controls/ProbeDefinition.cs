/*
 * ProbeDefinition.cs - part of CNC Controls library
 *
 * A library of probe definitions (one per physical probe on the CNC), edited from
 * Settings: App > Edit Probe Definitions and selected by the Load Stock tab. Persisted to
 * ProbeDefinitions.xml in the config folder.
 *
 */

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;

namespace CNC.Controls
{
    // One physical probe on the CNC and its motion parameters. Public get/set props for XmlSerializer;
    // INotifyPropertyChanged so the editor/grid reflect edits live. Property names mirror ProbingViewModel
    // to ease the later convergence of the probing tabs onto this model.
    public class ProbeDefinition : INotifyPropertyChanged
    {
        private string _name = "Probe";
        private double _diameter = 2d, _searchFeed = 200d, _latchFeed = 50d, _rapidsFeed = 0d,
                       _probeDistance = 25d, _latchDistance = 1d, _xyClearance = 5d, _depth = 10d,
                       _offsetX = 0d, _offsetY = 0d;

        public string Name { get { return _name; } set { _name = value; OnChanged(); } }
        public double ProbeDiameter { get { return _diameter; } set { _diameter = value; OnChanged(); } }
        public double ProbeFeedRate { get { return _searchFeed; } set { _searchFeed = value; OnChanged(); } }     // search (initial) feed
        public double LatchFeedRate { get { return _latchFeed; } set { _latchFeed = value; OnChanged(); } }       // second slow probe feed
        public double RapidsFeedRate { get { return _rapidsFeed; } set { _rapidsFeed = value; OnChanged(); } }    // 0 = use controller setting
        public double ProbeDistance { get { return _probeDistance; } set { _probeDistance = value; OnChanged(); } } // max probing move
        public double LatchDistance { get { return _latchDistance; } set { _latchDistance = value; OnChanged(); } } // retract before slow probe; 0 = skip
        public double XYClearance { get { return _xyClearance; } set { _xyClearance = value; OnChanged(); } }
        public double Depth { get { return _depth; } set { _depth = value; OnChanged(); } }                       // Z drop below start before an XY probe
        public double ProbeOffsetX { get { return _offsetX; } set { _offsetX = value; OnChanged(); } }            // probe tip -> spindle centre offset
        public double ProbeOffsetY { get { return _offsetY; } set { _offsetY = value; OnChanged(); } }

        public ProbeDefinition Clone()
        {
            var c = new ProbeDefinition();
            c.CopyFrom(this);
            return c;
        }

        public void CopyFrom(ProbeDefinition o)
        {
            Name = o.Name; ProbeDiameter = o.ProbeDiameter; ProbeFeedRate = o.ProbeFeedRate; LatchFeedRate = o.LatchFeedRate;
            RapidsFeedRate = o.RapidsFeedRate; ProbeDistance = o.ProbeDistance; LatchDistance = o.LatchDistance;
            XYClearance = o.XYClearance; Depth = o.Depth; ProbeOffsetX = o.ProbeOffsetX; ProbeOffsetY = o.ProbeOffsetY;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string property = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    // XmlSerializer root container (can't serialize a bare List<T> cleanly).
    [XmlRoot("ProbeDefinitions")]
    public class ProbeDefinitionList
    {
        [XmlElement("Probe")]
        public List<ProbeDefinition> Items { get; set; } = new List<ProbeDefinition>();
    }

    // App-wide probe library. Lazily loaded from ProbeDefinitions.xml; edited via the Settings: App dialog;
    // read by the Load Stock tab (and later the probing tabs).
    public static class ProbeDefinitions
    {
        private static ObservableCollection<ProbeDefinition> _items;

        public static ObservableCollection<ProbeDefinition> Items
        {
            get { if (_items == null) Load(); return _items; }
        }

        private static string FilePath
        {
            get { return Path.Combine(CNC.Core.Resources.ConfigPath ?? string.Empty, "ProbeDefinitions.xml"); }
        }

        public static void Load()
        {
            _items = new ObservableCollection<ProbeDefinition>();
            try
            {
                if (File.Exists(FilePath))
                {
                    var xs = new XmlSerializer(typeof(ProbeDefinitionList));
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var list = (ProbeDefinitionList)xs.Deserialize(fs);
                        if (list != null && list.Items != null)
                            foreach (var d in list.Items)
                                _items.Add(d);
                    }
                }
            }
            catch { /* ignore - start with an empty library */ }
        }

        public static void Save()
        {
            try
            {
                var list = new ProbeDefinitionList { Items = new List<ProbeDefinition>(Items) };
                var xs = new XmlSerializer(typeof(ProbeDefinitionList));
                using (var fs = File.Create(FilePath))
                    xs.Serialize(fs, list);
            }
            catch { }
        }
    }
}
