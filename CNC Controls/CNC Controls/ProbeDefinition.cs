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
    // Kinds of probe the CNC may have. Extensible - add a value here + its field set/defaults in
    // ProbeDefinitionEditDialog (FieldsFor/ApplyDefaults). EdgeFinder is an XY-only variant of a probe.
    public enum ProbeType
    {
        ThreeDProbe,
        TouchPlate,
        ToolSetter,
        EdgeFinder
    }

    // One physical probe on the CNC and its parameters. The model holds every field; the editor shows only
    // the ones relevant to the selected ProbeType. Public get/set props for XmlSerializer; INotifyPropertyChanged
    // so the editor/grid reflect edits live. Names mirror ProbingViewModel to ease the later convergence.
    public class ProbeDefinition : INotifyPropertyChanged
    {
        private string _name = "Probe";
        private ProbeType _type = ProbeType.ThreeDProbe;
        private double _diameter = 2d, _searchFeed = 200d, _latchFeed = 50d, _rapidsFeed = 0d,
                       _probeDistance = 25d, _latchDistance = 1d, _xyClearance = 5d, _depth = 10d,
                       _offsetX = 0d, _offsetY = 0d, _plateThickness = 12d, _lipWidth = 10d, _setterHeight = 0d, _spinRPM = 0d;

        public string Name { get { return _name; } set { _name = value; OnChanged(); } }
        public ProbeType ProbeType { get { return _type; } set { _type = value; OnChanged(); OnChanged(nameof(TypeName)); } }

        // Friendly type name for the list grid (derived, not persisted).
        [System.Xml.Serialization.XmlIgnore]
        public string TypeName
        {
            get
            {
                switch (_type)
                {
                    case ProbeType.ThreeDProbe: return "3D probe";
                    case ProbeType.TouchPlate: return "Touch plate";
                    case ProbeType.ToolSetter: return "Tool setter";
                    case ProbeType.EdgeFinder: return "Edge finder";
                    default: return _type.ToString();
                }
            }
        }
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
        public double PlateThickness { get { return _plateThickness; } set { _plateThickness = value; OnChanged(); } } // touch plate Z offset (work Z0 = top - thickness)
        public double LipWidth { get { return _lipWidth; } set { _lipWidth = value; OnChanged(); } }                  // touch plate lip XY offset from the stock edge
        public double SetterHeight { get { return _setterHeight; } set { _setterHeight = value; OnChanged(); } }      // tool setter trigger height
        public double SpinRPM { get { return _spinRPM; } set { _spinRPM = value; OnChanged(); } }                     // spinning edge finder RPM (0 = none)

        public ProbeDefinition Clone()
        {
            var c = new ProbeDefinition();
            c.CopyFrom(this);
            return c;
        }

        public void CopyFrom(ProbeDefinition o)
        {
            Name = o.Name; ProbeType = o.ProbeType; ProbeDiameter = o.ProbeDiameter; ProbeFeedRate = o.ProbeFeedRate;
            LatchFeedRate = o.LatchFeedRate; RapidsFeedRate = o.RapidsFeedRate; ProbeDistance = o.ProbeDistance;
            LatchDistance = o.LatchDistance; XYClearance = o.XYClearance; Depth = o.Depth;
            ProbeOffsetX = o.ProbeOffsetX; ProbeOffsetY = o.ProbeOffsetY;
            PlateThickness = o.PlateThickness; LipWidth = o.LipWidth; SetterHeight = o.SetterHeight; SpinRPM = o.SpinRPM;
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
