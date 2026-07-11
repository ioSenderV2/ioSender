/*
 * Fixture.cs - part of CNC Controls library
 *
 * A library of user-named workholding fixtures (one per physical fixture on the machine), edited from
 * Machine Setup: Fixture definitions and selected by Start Job. Mirrors ProbeDefinition's shape: FixtureKind
 * is a fixed, code-defined set (like ProbeType) - not user-addable - and Fixture holds every field, with the
 * editor showing only the ones relevant to the selected Kind. Persisted as the "Fixtures" section of App.config.
 *
 */

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public enum FixtureOriginCorner
    {
        Outside,
        Inside
    }

    // Kinds of workholding fixture ioSender knows about. Fixed/code-defined - extensible by adding a value
    // here + its shape in FixtureKinds (below) + FixtureEditDialog (FieldsFor/ApplyDefaults), same idiom as
    // ProbeType. Only CornerFence has a working macro today (see FixtureKinds.Implemented).
    public enum FixtureKind
    {
        CornerFence,
        MachinistVise,
        DogHoleGridCorner,
        VacuumTableZeroCorner
    }

    // Per-kind facts (fixed, not user-editable) - which probing stages a kind uses, its origin-corner
    // convention, whether it has a working macro yet, and its display name.
    public static class FixtureKinds
    {
        public static string DisplayName(FixtureKind k)
        {
            switch (k)
            {
                case FixtureKind.CornerFence: return "Corner fence";
                case FixtureKind.MachinistVise: return "Machinist vise";
                case FixtureKind.DogHoleGridCorner: return "Dog-hole/grid corner";
                case FixtureKind.VacuumTableZeroCorner: return "Vacuum table zero-corner";
                default: return k.ToString();
            }
        }

        // A known-position kind (vise) has no edge probing at all - its saved Coords ARE the precise origin
        // (captured via a probe cycle, not this jog+Set flow - not yet built, see Implemented). Every other
        // kind re-probes precisely each job, so ProbesEdges == true there.
        public static bool ProbesEdges(FixtureKind k) { return k != FixtureKind.MachinistVise; }

        // Whether this kind's macro probes the spoilboard to establish a floor reference (pcorner.macro's
        // DISCOVER phase). A known-position kind has no such reference.
        public static bool ProbesSpoilboard(FixtureKind k) { return k != FixtureKind.MachinistVise; }

        public static FixtureOriginCorner OriginCorner(FixtureKind k)
        {
            return k == FixtureKind.MachinistVise ? FixtureOriginCorner.Inside : FixtureOriginCorner.Outside;
        }

        // Only Corner Fence generates real NGC today; the rest are defined but not wired to a working macro.
        public static bool Implemented(FixtureKind k) { return k == FixtureKind.CornerFence; }
    }

    public class Fixture : INotifyPropertyChanged
    {
        private string _name = "Fixture";
        private FixtureKind _kind = FixtureKind.CornerFence;
        private double _spoilOffsetX = 0d, _spoilOffsetY = 0d;
        private double _topOffsetX = 30d, _topOffsetY = 30d;
        private double _edgeOffsetX = 30d, _edgeOffsetY = 30d;
        private string _coords = string.Empty;
        private string _pictureFile = string.Empty;

        public string Name { get { return _name; } set { _name = value; OnChanged(); } }
        public FixtureKind Kind { get { return _kind; } set { _kind = value; OnChanged(); OnChanged(nameof(KindName)); OnChanged(nameof(Implemented)); } }

        // Friendly kind name for the list grid (derived, not persisted).
        [XmlIgnore]
        public string KindName { get { return FixtureKinds.DisplayName(_kind); } }

        [XmlIgnore]
        public bool Implemented { get { return FixtureKinds.Implemented(_kind); } }

        public double SpoilProbeOffsetX { get { return _spoilOffsetX; } set { _spoilOffsetX = value; OnChanged(); } }
        public double SpoilProbeOffsetY { get { return _spoilOffsetY; } set { _spoilOffsetY = value; OnChanged(); } }
        public double TopProbeOffsetX { get { return _topOffsetX; } set { _topOffsetX = value; OnChanged(); } }
        public double TopProbeOffsetY { get { return _topOffsetY; } set { _topOffsetY = value; OnChanged(); } }
        public double EdgeProbeOffsetX { get { return _edgeOffsetX; } set { _edgeOffsetX = value; OnChanged(); } }
        public double EdgeProbeOffsetY { get { return _edgeOffsetY; } set { _edgeOffsetY = value; OnChanged(); } }

        // Captured machine position (jog there, click "Set position"). Empty = not yet set. Invariant CSV of
        // the enabled axes' machine coords (round-trips through CNC.Core.Position), includes Z so pcorner's
        // spoilboard-probe Z-start comes from this instead of the firmware's single-slot G28 Z.
        public string Coords { get { return _coords; } set { _coords = value; OnChanged(); OnChanged(nameof(HasPosition)); } }

        [XmlIgnore]
        public bool HasPosition { get { return !string.IsNullOrEmpty(_coords); } }

        // Optional reference picture (path, blank = none).
        [XmlIgnore]
        public string PictureFile { get { return _pictureFile; } set { _pictureFile = value; OnChanged(); } }

        public Fixture Clone()
        {
            var c = new Fixture();
            c.CopyFrom(this);
            return c;
        }

        public void CopyFrom(Fixture o)
        {
            Name = o.Name; Kind = o.Kind;
            SpoilProbeOffsetX = o.SpoilProbeOffsetX; SpoilProbeOffsetY = o.SpoilProbeOffsetY;
            TopProbeOffsetX = o.TopProbeOffsetX; TopProbeOffsetY = o.TopProbeOffsetY;
            EdgeProbeOffsetX = o.EdgeProbeOffsetX; EdgeProbeOffsetY = o.EdgeProbeOffsetY;
            Coords = o.Coords; PictureFile = o.PictureFile;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string property = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    [XmlRoot("Fixtures")]
    public class FixtureList
    {
        [XmlElement("Fixture")]
        public List<Fixture> Items { get; set; } = new List<Fixture>();
    }

    // App-wide fixture library. Lazily loaded; edited via Machine Setup: Fixture definitions; read by Start
    // Job. Starts EMPTY - unlike ProbeDefinitions there is no sensible built-in default (a fixture is
    // inherently a specific, physical, per-machine thing), so nothing is prepopulated.
    public static class Fixtures
    {
        private static ObservableCollection<Fixture> _items;

        public static ObservableCollection<Fixture> Items
        {
            get { if (_items == null) Load(); return _items; }
        }

        public static void Load()
        {
            _items = new ObservableCollection<Fixture>();
        }

        // Persisted as the "Fixtures" section of App.config. Saving writes the whole sectioned config.
        public static void Save()
        {
            AppConfig.Settings.Save();
        }

        // Snapshot for the App.config "Fixtures" section serializer.
        public static FixtureList Export()
        {
            return new FixtureList { Items = new List<Fixture>(Items) };
        }

        // Load the library from the App.config section (called by ConfigStore at startup).
        public static void SetItems(FixtureList list)
        {
            _items = new ObservableCollection<Fixture>();
            if (list?.Items != null)
                foreach (var d in list.Items)
                    _items.Add(d);
        }

        // The enabled axes' current machine position as an invariant CSV (the stored-coords format), or null
        // when the position is unknown (disconnected / not homed).
        public static string CurrentCoordsCsv(GrblViewModel grbl)
        {
            if (grbl == null)
                return null;
            var idx = GrblInfo.AxisFlags.ToIndices().ToList();
            if (idx.Any(i => double.IsNaN(grbl.MachinePosition.Values[i])))
                return null;
            return string.Join(",", idx.Select(i => grbl.MachinePosition.Values[i].ToInvariantString("F3")));
        }
    }
}
