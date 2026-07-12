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

        // A known-position kind (vise) has no PER-JOB edge probing - its saved Coords ARE the precise origin,
        // captured ONCE via a real pcorner.macro corner probe against the fixed jaw when Set position is
        // clicked (FixtureEditDialog.RunViseCornerProbe), not re-probed by Start Job every run. Every other
        // kind re-probes the STOCK's own corner precisely each job, so ProbesEdges == true there.
        public static bool ProbesEdges(FixtureKind k) { return k != FixtureKind.MachinistVise; }

        // Whether Test position's capped Z-search makes sense for this kind's saved Coords. Vise's Coords is
        // the resolved jaw corner (+ a small Z safety margin, same "~10 mm above" convention as every other
        // kind), so the same search works unchanged - re-probing the jaw's own top, not the stock's.
        public static bool ProbesSpoilboard(FixtureKind k) { return true; }

        public static FixtureOriginCorner OriginCorner(FixtureKind k)
        {
            return k == FixtureKind.MachinistVise ? FixtureOriginCorner.Inside : FixtureOriginCorner.Outside;
        }

        // Whether Start Job's BuildProgram can generate a real per-job program for this kind. Corner Fence
        // (BuildProgram) and Machinist Vise (BuildViseProgram, StartJobView.xaml.cs) are wired; Dog-hole/grid
        // corner and Vacuum table zero-corner are still defined but not wired to a working macro.
        public static bool Implemented(FixtureKind k) { return k == FixtureKind.CornerFence || k == FixtureKind.MachinistVise; }
    }

    public class Fixture : INotifyPropertyChanged
    {
        private string _name = "Fixture";
        private FixtureKind _kind = FixtureKind.CornerFence;
        private string _coords = string.Empty;
        private bool _positionValidated = false;

        public string Name { get { return _name; } set { _name = value; OnChanged(); } }
        public FixtureKind Kind { get { return _kind; } set { _kind = value; OnChanged(); OnChanged(nameof(KindName)); OnChanged(nameof(Implemented)); } }

        // Friendly kind name for the list grid (derived, not persisted).
        [XmlIgnore]
        public string KindName { get { return FixtureKinds.DisplayName(_kind); } }

        [XmlIgnore]
        public bool Implemented { get { return FixtureKinds.Implemented(_kind); } }

        // Captured machine position (jog there, click "Set position") - the single reference pcorner.macro
        // probes everything from: it must clear the corner in BOTH X and Y by the current 3D probe's body
        // diameter (a probe-sized clearance circle drawn in the edit dialog is the jog target) AND sit within
        // ~10 mm above the spoilboard in Z (the spoilboard probe's search is capped to 12 mm below this point
        // - see pcorner.macro), same idiom as the old firmware G28 slot but per-fixture-instance and not
        // written to firmware. Empty = not yet set. Invariant CSV of the enabled axes' machine coords
        // (round-trips through CNC.Core.Position). Changing it invalidates any prior "Test position" result -
        // a re-jogged position hasn't actually been probed yet.
        public string Coords
        {
            get { return _coords; }
            set { _coords = value; PositionValidated = false; OnChanged(); OnChanged(nameof(HasPosition)); }
        }

        [XmlIgnore]
        public bool HasPosition { get { return !string.IsNullOrEmpty(_coords); } }

        // Set true only when "Test position" (FixtureEditDialog) has actually run the real spoilboard probe
        // search from this Coords and the controller did NOT alarm - i.e. the saved position is proven to
        // work, not just captured. Start Job's fixture dropdown only offers validated fixtures (see
        // StartJobView.RefreshFixtures) - a merely-captured-but-untested position is exactly what caused the
        // Alarm:5 probe fail this was added to prevent.
        public bool PositionValidated { get { return _positionValidated; } set { _positionValidated = value; OnChanged(); } }

        public Fixture Clone()
        {
            var c = new Fixture();
            c.CopyFrom(this);
            return c;
        }

        public void CopyFrom(Fixture o)
        {
            Name = o.Name; Kind = o.Kind;
            Coords = o.Coords; PositionValidated = o.PositionValidated;
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
