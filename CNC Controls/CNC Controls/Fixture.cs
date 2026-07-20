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

        // Whether Start Job's Measure checkbox has ANYTHING to offer for this kind. Edge-probing kinds get the
        // full 4-corner measure (ProbesEdges). MachinistVise gets a PARTIAL measure (StartJobView.
        // BuildViseProgram's own measure block) - corners 2/4 face-probed, 1/3 Z-only, no skew - since its
        // origin corner is already known exactly, not itself probed. Distinct from ProbesEdges: a vise never
        // probes its OWN origin corner, but it CAN still offer Measure.
        public static bool CanMeasure(FixtureKind k) { return ProbesEdges(k) || k == FixtureKind.MachinistVise; }

        public static FixtureOriginCorner OriginCorner(FixtureKind k)
        {
            return k == FixtureKind.MachinistVise ? FixtureOriginCorner.Inside : FixtureOriginCorner.Outside;
        }

        // Whether Start Job's BuildProgram can generate a real per-job program for this kind. Corner Fence
        // (BuildProgram) and Machinist Vise (BuildViseProgram, StartJobView.xaml.cs) are wired; Dog-hole/grid
        // corner and Vacuum table zero-corner are still defined but not wired to a working macro.
        public static bool Implemented(FixtureKind k) { return k == FixtureKind.CornerFence || k == FixtureKind.MachinistVise; }

        // A vise fixture's saved Coords.Z sits this many mm ABOVE the actual probed jaw top (FixtureEditDialog.
        // RunViseCornerProbe stores the resolved corner + this margin, not the literal touched height - a bare
        // rapid straight to the exact probed surface would plunge onto solid jaw metal). Anything computing a
        // safe height FROM the jaw's true top (not the saved reference) must back this out first - see
        // StartJobView.BuildViseProgram.
        public const double VisePositionMarginMm = 8d;
    }

    public class Fixture : INotifyPropertyChanged
    {
        private string _name = "Fixture";
        private FixtureKind _kind = FixtureKind.CornerFence;
        private string _coords = string.Empty;
        private bool _positionValidated = false;
        private double _jawWidth = 0d;
        private double _maxOpening = 0d;
        private double _cornerOffsetX = 0d;
        private double _cornerOffsetY = 0d;
        private double _spoilboardZ = 0d;

        public string Name { get { return _name; } set { _name = value; OnChanged(); } }
        public FixtureKind Kind { get { return _kind; } set { _kind = value; OnChanged(); OnChanged(nameof(KindName)); OnChanged(nameof(Implemented)); } }

        // Vise-only, both DRAWING-only (Start Job's stock drawing + this fixture's own schematic) - neither
        // is read by pvisecorner.macro, which finds the jaw corner by probing, not by knowing its extent.
        // 0 = not set: the Start Job drawing falls back to sizing the jaw bar from the entered stock instead.
        public double JawWidth { get { return _jawWidth; } set { _jawWidth = value; OnChanged(); } }
        // Maximum jaw opening (mm) - how far the moving jaw can travel from the fixed jaw. 0 = not set: the
        // drawing places the moving jaw right at the stock's edge instead of at the vise's true throat depth.
        public double MaxOpening { get { return _maxOpening; } set { _maxOpening = value; OnChanged(); } }

        // Edge-probing kinds only (CornerFence today - see FixtureKinds.ProbesEdges/Implemented). The true
        // stock corner's XY, relative to Coords, captured ONCE by FixtureEditDialog's "Test position" via a
        // real pcorner.macro probe (same as Start Job's own corner-1 DISCOVER pass used to do every run) -
        // the fence is bolted down, so this offset is reproducible run to run. Start Job then points corner
        // 1's SINGLE probe directly at the tight ~5mm-inset anchor (StartJobView.BuildProgram) instead of a
        // loose locate pass followed by a tight re-probe - see the "double probe of corner 1" backlog item.
        // 0/0 means "never captured under this scheme" (fresh fixture, or one saved before this feature) -
        // BuildProgram refuses to generate until Test position has been re-run (real 0,0 offsets never occur
        // in practice - Coords is always jogged well clear of the corner).
        public double CornerOffsetX { get { return _cornerOffsetX; } set { _cornerOffsetX = value; OnChanged(); } }
        public double CornerOffsetY { get { return _cornerOffsetY; } set { _cornerOffsetY = value; OnChanged(); } }

        // Edge-probing kinds only, same scheme as CornerOffsetX/Y: the spoilboard's machine Z at Coords,
        // captured ONCE by Test position's own spoilboard search. The fence is bolted down and the spoilboard
        // doesn't move, so this is exactly as reproducible run to run as CornerOffsetX/Y already is - Start
        // Job reuses it directly (StartJobView.BuildProgram) instead of re-probing the spoilboard every job,
        // same "trust the once-tested fixture reference" model already applied to X/Y. 0 means "never
        // captured" (see CornerOffsetX/Y's own comment - real 0 never occurs in practice).
        public double SpoilboardZ { get { return _spoilboardZ; } set { _spoilboardZ = value; OnChanged(); } }

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
            set
            {
                _coords = value; PositionValidated = false;
                // NOT the place to clear CornerOffsetX/Y (that was tried and broke on real hardware): this
                // setter also runs during XML deserialization (XmlSerializer assigns CornerOffsetX/Y, then
                // Coords, then PositionValidated, in declared order - see Fixture.cs's own property order),
                // so clearing here would zero a just-loaded, perfectly valid offset EVERY app load, moments
                // before PositionValidated's own element deserializes and restores "true" over top of it -
                // silently corrupting Start Job's saved reference while showing a green check. Callers that
                // genuinely re-jog the position (FixtureEditDialog.btnSetPosition_Click) clear the offset
                // themselves, right there, not through this setter.
                OnChanged(); OnChanged(nameof(HasPosition)); OnChanged(nameof(X)); OnChanged(nameof(Y)); OnChanged(nameof(Z));
            }
        }

        [XmlIgnore]
        public bool HasPosition { get { return !string.IsNullOrEmpty(_coords); } }

        // Direct per-axis edit of Coords (FixtureEditDialog's X/Y/Z Axis fields - an alternative to jogging
        // there and clicking Set position). Reads/writes through the same CSV Coords holds (CurrentCoordsCsv's
        // format: comma-joined values for GrblInfo.AxisFlags's enabled axes, in index order) - X/Y/Z are
        // always indices 0/1/2. Setting any axis re-invalidates PositionValidated via the Coords setter, same
        // as a fresh Set position jog.
        [XmlIgnore]
        public double X { get { return GetAxis(0); } set { SetAxis(0, value); } }
        [XmlIgnore]
        public double Y { get { return GetAxis(1); } set { SetAxis(1, value); } }
        [XmlIgnore]
        public double Z { get { return GetAxis(2); } set { SetAxis(2, value); } }

        private double GetAxis(int index)
        {
            if (!HasPosition)
                return 0d;
            double v = new Position(_coords).Values[index];
            return double.IsNaN(v) ? 0d : v;
        }

        private void SetAxis(int index, double value)
        {
            var pos = HasPosition ? new Position(_coords) : new Position();
            pos.Values[index] = value;
            var idx = GrblInfo.AxisFlags.ToIndices();
            Coords = string.Join(",", idx.Select(i => (double.IsNaN(pos.Values[i]) ? 0d : pos.Values[i]).ToInvariantString("F3")));
        }

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
            JawWidth = o.JawWidth; MaxOpening = o.MaxOpening;
            CornerOffsetX = o.CornerOffsetX; CornerOffsetY = o.CornerOffsetY;
            SpoilboardZ = o.SpoilboardZ;
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
            if (_items == null)
                _items = new ObservableCollection<Fixture>();
            else
                _items.Clear();
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

        // Load the library from the App.config section (called by ConfigStore at startup). Mutates the
        // EXISTING collection in place (Clear + re-add) instead of replacing the _items reference: MainWindow's
        // InitializeComponent() builds the whole tab tree - including MachineSetupWizard, which binds
        // grdFixtures.ItemsSource = Fixtures.Items in its constructor - BEFORE AppConfig.Settings.LoadConfig()
        // runs (see MainWindow.xaml.cs). A premature Items access there used to lazily create an EMPTY
        // collection via Load() and bind the grid to THAT object; this method then created a brand new object
        // with the real 4 fixtures and reassigned the static field, orphaning the grid's already-bound
        // reference - the grid stayed on the stale empty collection for the rest of the session (confirmed on
        // real hardware: Fixture definitions showed only fixtures added/mutated in-session, nothing from the
        // loaded file). Mutating in place means whichever collection object got bound early is the SAME one
        // this populates.
        public static void SetItems(FixtureList list)
        {
            if (_items == null)
                _items = new ObservableCollection<Fixture>();
            else
                _items.Clear();
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
