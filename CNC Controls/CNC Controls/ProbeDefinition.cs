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
using System.Linq;
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
        private double _diameter = 2d, _bodyDiameter = 42d, _searchFeed = 200d, _latchFeed = 50d, _rapidsFeed = 0d,
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
        // Tip diameter - the stylus tip / bit that actually contacts the work; its radius is the edge
        // radius compensation applied to face touches.
        public double ProbeDiameter { get { return _diameter; } set { _diameter = value; OnChanged(); } }

        // 3D-probe ball/body diameter - the large part that must clear the work; its radius is the
        // minimum standoff held during G28 / rapid clearance moves so the body never strikes the stock.
        public double BodyDiameter { get { return _bodyDiameter; } set { _bodyDiameter = value; OnChanged(); OnChanged(nameof(MinStandoff)); } }

        // Minimum XY standoff (body radius) to keep clear of the work on G28/rapid clearance moves.
        [System.Xml.Serialization.XmlIgnore]
        public double MinStandoff { get { return _bodyDiameter / 2d; } }
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
            Name = o.Name; ProbeType = o.ProbeType; ProbeDiameter = o.ProbeDiameter; BodyDiameter = o.BodyDiameter; ProbeFeedRate = o.ProbeFeedRate;
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

            Renumber(_items);   // names are derived from type, not stored
        }

        // Now persisted as the "Probes" section of App.config (folded in from the old standalone file). Saving
        // writes the whole sectioned config.
        public static void Save()
        {
            AppConfig.Settings.Save();
        }

        // Snapshot for the App.config "Probes" section serializer.
        public static ProbeDefinitionList Export()
        {
            return new ProbeDefinitionList { Items = new List<ProbeDefinition>(Items) };
        }

        // Load the library from the App.config section (called by ConfigStore at startup).
        public static void SetItems(ProbeDefinitionList list)
        {
            _items = new ObservableCollection<ProbeDefinition>();
            if (list?.Items != null)
                foreach (var d in list.Items)
                    _items.Add(d);
            Renumber(_items);
        }

        // One-time importer: read the legacy standalone ProbeDefinitions.xml if present, so an existing library
        // is folded into App.config on first run. Returns null when there's nothing to import.
        public static ProbeDefinitionList ReadLegacyFile()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var xs = new XmlSerializer(typeof(ProbeDefinitionList));
                    using (var fs = File.OpenRead(FilePath))
                        return (ProbeDefinitionList)xs.Deserialize(fs);
                }
            }
            catch { }
            return null;
        }

        // Derive each probe's name from its type: "3D probe" when it's the only one of that type,
        // "Tool setter (1)" / "(2)" ... when there are several. Call after add/delete/type-change.
        public static void Renumber() { Renumber(Items); }

        public static void Renumber(IList<ProbeDefinition> items)
        {
            if (items == null)
                return;
            foreach (var grp in items.GroupBy(d => d.ProbeType))
            {
                var list = grp.ToList();
                for (int i = 0; i < list.Count; i++)
                    list[i].Name = list.Count == 1 ? list[i].TypeName : string.Format("{0} ({1})", list[i].TypeName, i + 1);
            }
        }
    }
}
