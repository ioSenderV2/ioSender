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
        private bool _canProbeCorner = true;
        private double _diameter = 2d, _bodyDiameter = 42d, _overallLength = 100d, _searchFeed = 200d, _latchFeed = 50d, _rapidsFeed = 0d,
                       _probeDistance = 25d, _latchDistance = 1d, _xyClearance = 5d, _depth = 10d,
                       _offsetX = 0d, _offsetY = 0d, _plateThickness = 12d, _lipWidth = 10d, _setterHeight = 0d, _spinRPM = 0d, _bitLength = 40d;

        public string Name { get { return _name; } set { _name = value; OnChanged(); } }
        public ProbeType ProbeType { get { return _type; } set { _type = value; OnChanged(); OnChanged(nameof(TypeName)); } }

        /// <summary>
        /// Touch plates come in two shapes and only one of them can find a corner.
        ///
        /// A CORNER plate has two lips meeting at 90 degrees: sit it over the stock's corner, the lips
        /// register it against the two edges, and it can probe X, Y and Z. A FLAT plate is a slab - it
        /// lies on top of the work and can only give you Z.
        ///
        /// Worth saying because the flat one is the cheaper, commoner object and nothing in the model
        /// used to distinguish them: a flat plate looked corner-capable, was offered for corner probing,
        /// and the macro would have driven it at the side of the stock looking for an edge touch that
        /// cannot happen.
        ///
        /// Defaults to TRUE, which is deliberate for an install that predates this field: every plate was
        /// implicitly treated as corner-capable before, so true is the value that changes nothing for an
        /// existing library. XmlSerializer leaves an absent element at the C# default, the same way Wcs
        /// and the work order's stock fields pick theirs up.
        ///
        /// Only meaningful for <see cref="ProbeType.TouchPlate"/>; ignored for every other type.
        /// </summary>
        public bool CanProbeCorner { get { return _canProbeCorner; } set { _canProbeCorner = value; OnChanged(); OnChanged(nameof(TypeName)); } }

        /// <summary>True when this definition can be used to find a stock corner in X and Y.</summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool IsCornerCapable
        {
            get { return _type == ProbeType.ThreeDProbe || _type == ProbeType.EdgeFinder || (_type == ProbeType.TouchPlate && _canProbeCorner); }
        }

        // Friendly type name for the list grid (derived, not persisted).
        [System.Xml.Serialization.XmlIgnore]
        public string TypeName
        {
            get
            {
                switch (_type)
                {
                    case ProbeType.ThreeDProbe: return "3D probe";
                    // Says which of the two plates it is, because that is the difference that decides
                    // whether Setup can use it to find a corner - and the list is where an operator
                    // looks to check.
                    case ProbeType.TouchPlate: return _canProbeCorner ? "Touch plate (corner)" : "Touch plate (Z only)";
                    case ProbeType.ToolSetter: return "Tool setter";
                    case ProbeType.EdgeFinder: return "Edge finder";
                    default: return _type.ToString();
                }
            }
        }
        // Tip diameter - the stylus tip / bit that actually contacts the work; its radius is the edge
        // radius compensation applied to face touches.
        public double ProbeDiameter { get { return _diameter; } set { _diameter = value; OnChanged(); } }

        /// <summary>
        /// The tip as the operator would go and pick one up: "6.354 mm (1/4")" when the diameter really is
        /// a standard imperial size, plain "6 mm" when it is not. For the install prompt, where the number
        /// on file has to be matched against a physical object - a mismatch there shifts the work origin by
        /// half the diameter difference and shows no symptom until a finished part is measured.
        /// </summary>
        public string TipDescription { get { return DescribeTip(ProbeDiameter); } }

        public static string DescribeTip(double mm)
        {
            string frac = ImperialFraction(mm);
            return mm.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture)
                   + " mm" + (frac == null ? string.Empty : " (" + frac + ")");
        }

        /// <summary>
        /// Nearest imperial fraction, or null when the diameter is not really an imperial size. Two guards,
        /// because a bare "nearest 64th" will happily label a 2 mm metric stylus "5/64"" and a 6 mm one
        /// "15/64"" - true to the arithmetic, useless to the operator, and actively misleading next to a
        /// prompt telling them to match it: the reduced denominator must be 16 or coarser (real gauge-pin
        /// and dowel sizes are), AND the fraction must land within 0.03 mm of the stored diameter.
        /// </summary>
        private static string ImperialFraction(double mm)
        {
            if (mm <= 0d)
                return null;

            int sixtyfourths = (int)System.Math.Round(mm / 25.4d * 64d);
            if (sixtyfourths <= 0 || System.Math.Abs(sixtyfourths / 64d * 25.4d - mm) > 0.03d)
                return null;

            int num = sixtyfourths, den = 64;
            while (num % 2 == 0 && den > 1) { num /= 2; den /= 2; }

            return den > 16 ? null : (den == 1 ? num + "\"" : num + "/" + den + "\"");
        }

        // 3D-probe body diameter - the large part that must clear the work; its radius is the
        // minimum standoff held during G28 / rapid clearance moves so the body never strikes the stock.
        public double BodyDiameter { get { return _bodyDiameter; } set { _bodyDiameter = value; OnChanged(); OnChanged(nameof(MinStandoff)); } }

        // 3D-probe overall length: top of the body to the end of the tip. Informational/clearance reference,
        // not consumed by any macro today.
        public double OverallLength { get { return _overallLength; } set { _overallLength = value; OnChanged(); } }

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
        public double BitLength { get { return _bitLength; } set { _bitLength = value; OnChanged(); } }                // overall length of the bit touching the plate (informational reference)
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
            Name = o.Name; ProbeType = o.ProbeType; CanProbeCorner = o.CanProbeCorner; ProbeDiameter = o.ProbeDiameter; BodyDiameter = o.BodyDiameter; OverallLength = o.OverallLength; ProbeFeedRate = o.ProbeFeedRate;
            LatchFeedRate = o.LatchFeedRate; RapidsFeedRate = o.RapidsFeedRate; ProbeDistance = o.ProbeDistance;
            LatchDistance = o.LatchDistance; XYClearance = o.XYClearance; Depth = o.Depth;
            ProbeOffsetX = o.ProbeOffsetX; ProbeOffsetY = o.ProbeOffsetY;
            PlateThickness = o.PlateThickness; LipWidth = o.LipWidth; BitLength = o.BitLength; SetterHeight = o.SetterHeight; SpinRPM = o.SpinRPM;
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
            get
            {
                if (_items == null)
                {
                    CNC.Core.DebugLog.Write("probes", "Items read before the Probes section loaded - falling back to Load()");
                    Load();
                }
                return _items;
            }
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
            CNC.Core.DebugLog.Write("probes", string.Format("Export: writing {0} definition(s)", _items?.Count ?? -1));
            return new ProbeDefinitionList { Items = new List<ProbeDefinition>(Items) };
        }

        // Load the library from the App.config section (called by ConfigStore at startup).
        public static void SetItems(ProbeDefinitionList list)
        {
            _items = new ObservableCollection<ProbeDefinition>();
            if (list?.Items != null)
                foreach (var d in list.Items)
                    _items.Add(d);
            // Fresh install (no Probes section at all yet): seed the ONE probe it is safe to assume, so the
            // Machine Setup gate does not have to force a stop over an empty library (step 5 in
            // MachineSetupWizard.FirstIncompleteStep) and a new operator can go straight into Setup, which
            // prompts them once to review these generic numbers against their real hardware (see
            // AppConfig.Settings.Base.ProbeDefinitionsReviewed).
            //
            // A touch plate, and ONLY a touch plate. This used to seed a 3D probe alongside it, and that was
            // the wrong kind of help: most hobby machines do not have one, so a new user arrived at a library
            // already listing hardware they do not own, configured, looking reviewed. A probe in the list is
            // a claim about the machine - an empty slot invites the question, a wrong entry answers it.
            // Machine Setup step 5 asks what is actually fitted instead.
            bool seeded = _items.Count == 0;
            if (seeded)
                _items.Add(new ProbeDefinition { Name = "Touch plate", ProbeType = ProbeType.TouchPlate, CanProbeCorner = true });
            Renumber(_items);
            CNC.Core.DebugLog.Write("probes", string.Format(
                "SetItems: incoming={0} seeded={1} now={2}", list?.Items?.Count ?? -1, seeded, _items.Count));
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
            // A throw here (corrupt or locked file) returned null silently, which downstream reads as
            // "nothing to import" - identical to the file not existing. Log it, or a failed import
            // looks exactly like a fresh install with no library to carry over.
            catch (System.Exception ex) { CNC.Core.DebugLog.Write("probes", "ReadLegacyFile failed, treating as no library to import - " + ex.Message); }
            return null;
        }

        /// <summary>
        /// The probe that measures tool length at the G59.3 position: the operator's own choice from
        /// Machine Setup step 5, falling back to the first toolsetter and then the first touch plate -
        /// which is what every caller did before the question was asked.
        ///
        /// ONE place resolves this, because three separate pieces of code need the same answer and they
        /// must not disagree: the Reference TLO step, the tool-length macro's probe-input select, and the
        /// step-5 panel that describes what will happen.
        /// </summary>
        public static ProbeDefinition TloTarget
        {
            get
            {
                string name = AppConfig.Settings.Base.TloProbeName;
                return (string.IsNullOrEmpty(name) ? null : Items.FirstOrDefault(p => p.Name == name))
                       ?? Items.FirstOrDefault(p => p.ProbeType == ProbeType.ToolSetter)
                       ?? Items.FirstOrDefault(p => p.ProbeType == ProbeType.TouchPlate);
            }
        }

        /// <summary>
        /// True when tool length is measured with a TOUCH PLATE rather than a toolsetter puck - which
        /// decides which probe INPUT the probe move must select.
        ///
        /// A plate has no switch of its own: it closes the circuit through the tool, and in practice every
        /// plate on the machine is wired together onto the one main probe input, alongside a 3D probe if
        /// there is one. So a plate is always the MAIN input.
        ///
        /// Deliberately NOT "does the controller have toolsetter hardware". A machine can have a toolsetter
        /// fitted and still use a plate at G59.3, and answering from the hardware alone would select the
        /// toolsetter input for a plate that is not wired to it - a probe move that can never trigger, which
        /// ends as a crash into the plate rather than a touch on it.
        /// </summary>
        public static bool TloTargetIsTouchPlate
        {
            get
            {
                var p = TloTarget;
                return p != null && p.ProbeType == ProbeType.TouchPlate;
            }
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
