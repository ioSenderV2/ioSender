/*
 * FeedsSpeedsAdvisor.cs - part of CNC Core library
 *
 * Decision engine for the Feeds & Speeds feature: given a FeedsSpeedsOperation (raw
 * current values read from Fusion, see FeedsSpeedsExport.cs) and a chosen material,
 * decides which parameters need adjusting. Two independent checks, worse-of wins:
 *
 *  - Material engine: a straight port of the Fusion-side SRWCommands add-in's
 *    cam_feeds.py (MATERIAL_REFS chip-load/surface-speed tables + verdict
 *    classifiers) - reference values pulled from published Onsrud/Amana/Vortex
 *    carbide bit charts, tune as you accumulate empirical data.
 *  - Machine-limit check: NEW, not present in cam_feeds.py at all, because Python
 *    has no connection to the controller. Cross-references RPM/feed against the
 *    CONNECTED controller's actual grblHAL settings ($30 RpmMax, $110-$112
 *    MaxFeedRateBase) - a value the material chart says is fine but the machine
 *    physically cannot do is always a hard Change, regardless of the chart.
 *
 * This class only computes verdicts; it does not touch Fusion or write files -
 * that's feedsAndSpeeds.py (Fusion side, raw export/apply) and the UI/apply-file
 * writer (CNC Controls, WPF side) respectively.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace CNC.Core
{
    public enum FeedsSpeedsVerdict
    {
        None,    // no comparison possible (missing data / not applicable to this strategy)
        Ok,      // within tolerance
        Nudge,   // mildly off - advisory
        Change   // significantly off, or exceeds a hard machine limit
    }

    // One parameter's current/recommended/machine-limit values and the combined verdict.
    public class ParameterVerdict
    {
        public double? Current { get; set; }
        public double? Recommended { get; set; }
        public double? MachineLimit { get; set; }
        public FeedsSpeedsVerdict MaterialVerdict { get; set; } = FeedsSpeedsVerdict.None;
        public double? MaterialDeltaPct { get; set; }
        public FeedsSpeedsVerdict MachineVerdict { get; set; } = FeedsSpeedsVerdict.None;
        public List<string> Notes { get; } = new List<string>();

        public FeedsSpeedsVerdict Verdict => Worse(MaterialVerdict, MachineVerdict);

        private static FeedsSpeedsVerdict Worse(FeedsSpeedsVerdict a, FeedsSpeedsVerdict b)
        {
            return (FeedsSpeedsVerdict)Math.Max((int)a, (int)b);
        }
    }

    public class OperationRecommendation
    {
        public string Id { get; set; }
        public string ToolClass { get; set; }   // "drill", "vbit", or "mill"
        public ParameterVerdict Rpm { get; } = new ParameterVerdict();
        public ParameterVerdict CuttingFeed { get; } = new ParameterVerdict();
        public ParameterVerdict PlungeFeed { get; } = new ParameterVerdict();
        public ParameterVerdict AxialStep { get; } = new ParameterVerdict();
        public ParameterVerdict RadialStep { get; } = new ParameterVerdict();
        public string RecommendedCoolant { get; set; }
        public FeedsSpeedsVerdict CoolantVerdict { get; set; } = FeedsSpeedsVerdict.None;
        public List<string> Notes { get; } = new List<string>();

        public bool HasAnyChange =>
            Rpm.Verdict == FeedsSpeedsVerdict.Change || CuttingFeed.Verdict == FeedsSpeedsVerdict.Change ||
            PlungeFeed.Verdict == FeedsSpeedsVerdict.Change || AxialStep.Verdict == FeedsSpeedsVerdict.Change ||
            RadialStep.Verdict == FeedsSpeedsVerdict.Change;
    }

    // One material's reference data. Either RpmRange (wood - a fixed RPM band, tool
    // diameter doesn't change the right speed much) or SurfaceSpeed (metals - RPM is
    // derived from surface speed and tool diameter) is set, never both.
    public class MaterialRef
    {
        public (double Lo, double Ideal, double Hi)? RpmRange;
        public (double Lo, double Ideal, double Hi)? SurfaceSpeed;
        public Dictionary<double, double> ChipLoad;
        public double MaxAxialFrac;
        public double MaxRadialFrac;
        public bool Conductive;   // true for metals - gates touch-plate probing of internal/circle geometry (see StartJobView.StockConductive)
        public string Notes;
        public DrillRef Drill;      // brad point / twist drill (the original, single drill reference)
        public DrillRef DrillHss;   // HSS twist drill - null where no distinct data exists yet (metals)
    }

    public class DrillRef
    {
        public (double Lo, double Ideal, double Hi) SurfaceSpeed;
        public Dictionary<double, double> FeedPerRev;
        public double PeckFrac;
        public string Notes;
    }

    public static class FeedsSpeedsAdvisor
    {
        // Spindle RPM limits used as a last-resort clamp for surface-speed-derived RPM
        // when no connected-controller limit is available (see MachineRpmLimits below,
        // and ApplyMachineLimits for the real, controller-specific check).
        private const double FallbackRpmMin = 8000, FallbackRpmMax = 24000;

        // Coolant the machine actually has. Anything else on an op is flagged so it can
        // be switched. Matches cam_feeds.py's MACHINE_COOLANT - adjust if the shop's
        // coolant setup changes (e.g. to "flood" for a machine with a pump).
        public const string MachineCoolant = "air";

        private const double ToleranceOk = 0.10;      // +/-10% -> Ok
        private const double ToleranceNudge = 0.25;   // +/-25% -> Nudge; beyond -> Change

        private static readonly Dictionary<string, MaterialRef> _materialRefs = BuildMaterialRefs();

        public static IReadOnlyDictionary<string, MaterialRef> MaterialRefs => _materialRefs;

        private static Dictionary<string, MaterialRef> BuildMaterialRefs()
        {
            var woodDrill = new DrillRef
            {
                SurfaceSpeed = (30, 55, 90),
                FeedPerRev = new Dictionary<double, double> { { 3.0, 0.10 }, { 6.0, 0.20 }, { 10.0, 0.30 }, { 13.0, 0.40 } },
                PeckFrac = 3.0,
                Notes = "Twist/brad-point drill in wood: chips clear easily, so deep pecks are fine; " +
                        "back out on deep holes to avoid heat/burning.",
            };
            // HSS twist drill - added 2026-07-30 alongside the brad-point/twist split. HSS dulls faster and
            // runs hotter than a sharp brad-point/carbide bit, especially in MDF's abrasive resin - lower
            // surface speed and a shallower, more frequent peck reduce burning risk. NO empirical tuning
            // behind these numbers yet (unlike woodDrill, which came from real-machine notes) - a rough
            // starting point pending real cuts, same disclaimer as the untuned Brass/Steel entries below.
            var hssDrill = new DrillRef
            {
                SurfaceSpeed = (15, 25, 40),
                FeedPerRev = new Dictionary<double, double> { { 3.0, 0.05 }, { 6.0, 0.10 }, { 10.0, 0.15 }, { 13.0, 0.20 } },
                PeckFrac = 1.5,
                Notes = "HSS twist drill: UNTUNED reference data, treat as a rough starting point only. Dulls " +
                        "faster and runs hotter than carbide/brad-point, especially in MDF's abrasive resin - " +
                        "runs slower with a shallower, more frequent peck to keep the bit and material cool.",
            };

            var refs = new Dictionary<string, MaterialRef>
            {
                ["Hardwood"] = new MaterialRef
                {
                    RpmRange = (14000, 18000, 22000),
                    ChipLoad = new Dictionary<double, double> { { 1.5, 0.025 }, { 3.0, 0.05 }, { 6.0, 0.10 }, { 9.5, 0.15 }, { 12.7, 0.20 } },
                    MaxAxialFrac = 1.0,
                    MaxRadialFrac = 0.4,
                    Notes = "Hardwood (oak, maple, walnut, cherry) is dense and abrasive. Lower chip loads than " +
                            "softwood to avoid tear-out around grain reversals and to keep cutting forces in range.",
                    Drill = woodDrill,
                    DrillHss = hssDrill,
                },
                ["Softwood"] = new MaterialRef
                {
                    RpmRange = (14000, 18000, 22000),
                    ChipLoad = new Dictionary<double, double> { { 1.5, 0.030 }, { 3.0, 0.06 }, { 6.0, 0.13 }, { 9.5, 0.18 }, { 12.7, 0.25 } },
                    MaxAxialFrac = 1.0,
                    MaxRadialFrac = 0.5,
                    Notes = "Softwood (pine, cedar, fir) cuts easily; you can run higher chip loads to clear chips " +
                            "and keep heat down. Watch for fuzzy tear-out on cross-grain finishing passes.",
                    Drill = woodDrill,
                    DrillHss = hssDrill,
                },
                ["Plywood"] = new MaterialRef
                {
                    RpmRange = (15000, 18000, 22000),
                    ChipLoad = new Dictionary<double, double> { { 1.5, 0.025 }, { 3.0, 0.05 }, { 6.0, 0.10 }, { 9.5, 0.15 }, { 12.7, 0.20 } },
                    MaxAxialFrac = 1.0,
                    MaxRadialFrac = 0.4,
                    Notes = "Plywood's glue layers eat tooling fast. Run lower chip loads and pick downcut or " +
                            "compression bits to keep the top veneer clean. Allow generous chip evacuation.",
                    Drill = woodDrill,
                    DrillHss = hssDrill,
                },
                ["MDF"] = new MaterialRef
                {
                    RpmRange = (16000, 20000, 24000),
                    ChipLoad = new Dictionary<double, double> { { 1.5, 0.030 }, { 3.0, 0.06 }, { 6.0, 0.15 }, { 9.5, 0.20 }, { 12.7, 0.28 } },
                    MaxAxialFrac = 1.0,
                    MaxRadialFrac = 0.5,
                    Notes = "MDF dust is fine, abundant, and extremely abrasive - dust collection matters more than " +
                            "chip load. Higher RPMs help, but a sharp upcut bit + strong vacuum beats any chart value.",
                    Drill = woodDrill,
                    DrillHss = hssDrill,
                },
                ["Aluminum"] = new MaterialRef
                {
                    // Surface-speed driven: RPM computed from surface speed + tool diameter, clamped to the
                    // machine's actual spindle limit (ApplyMachineLimits / the connected controller's $30).
                    // Tuned for 6061-class aluminum, sharp carbide, air-blast-only cooling.
                    SurfaceSpeed = (120, 180, 250),   // m/min
                    Conductive = true,
                    ChipLoad = new Dictionary<double, double> { { 1.5, 0.012 }, { 3.0, 0.030 }, { 6.0, 0.060 }, { 9.5, 0.090 }, { 12.7, 0.120 } },
                    MaxAxialFrac = 0.5,
                    MaxRadialFrac = 0.4,
                    Notes = "Aluminum (6061-class), carbide, air blast only. Use a sharp 1-3 flute bit with " +
                            "polished/ZrN flutes to stop built-up edge, CLIMB mill, and keep the air blast on to " +
                            "clear chips. Prefer ramp/helical entry over straight plunging.",
                    Drill = new DrillRef
                    {
                        SurfaceSpeed = (55, 80, 110),
                        FeedPerRev = new Dictionary<double, double> { { 1.5, 0.03 }, { 3.0, 0.05 }, { 5.0, 0.07 }, { 8.0, 0.10 }, { 12.0, 0.14 } },
                        PeckFrac = 0.7,
                        Notes = "HSS jobber drill in aluminum: peck or chip-break to clear the stringy chips (no flood).",
                    },
                },
                // Brass/Steel: added 2026-07-24 alongside the setup-name-derived material convention -
                // NO empirical tuning behind these numbers yet (unlike Hardwood/Aluminum, which came from
                // published charts + real-machine notes). Rough, conservative starting points for a
                // router-class machine; treat these more skeptically than the others until tuned.
                ["Brass"] = new MaterialRef
                {
                    // Free-machining brass (C360-class) cuts easily with carbide - moderate surface speed,
                    // similar ballpark to aluminum but slightly gentler chip loads for a lighter router spindle.
                    SurfaceSpeed = (100, 150, 200),
                    Conductive = true,
                    ChipLoad = new Dictionary<double, double> { { 1.5, 0.010 }, { 3.0, 0.025 }, { 6.0, 0.050 }, { 9.5, 0.075 }, { 12.7, 0.100 } },
                    MaxAxialFrac = 0.5,
                    MaxRadialFrac = 0.4,
                    Notes = "Brass (free-machining, C360-class) - UNTUNED reference data, treat as a rough " +
                            "starting point only. Cuts easily with sharp carbide; watch for grabbing/chatter " +
                            "on a light router spindle since brass is far more rigid than wood.",
                },
                ["Steel"] = new MaterialRef
                {
                    // Mild steel (1018-class) is a stretch for a router-class machine with air-blast-only
                    // cooling - deliberately conservative surface speed to keep heat/tool wear in check.
                    SurfaceSpeed = (30, 45, 60),
                    Conductive = true,
                    ChipLoad = new Dictionary<double, double> { { 1.5, 0.006 }, { 3.0, 0.015 }, { 6.0, 0.030 }, { 9.5, 0.045 }, { 12.7, 0.060 } },
                    MaxAxialFrac = 0.3,
                    MaxRadialFrac = 0.3,
                    Notes = "Mild steel (1018-class) - UNTUNED reference data, treat as a rough starting point " +
                            "only. Marginal for a router-class machine/spindle; expect to run slower and shallower " +
                            "than these numbers if the machine struggles, and use cutting fluid if at all possible.",
                },
            };
            return refs;
        }

        /// <summary>
        /// Resolves a setup's material when the user picks "Derived": the first '_'-terminated token of
        /// the setup name, matched case-insensitively against a known material (e.g. "MDF_BottomSetup" ->
        /// "MDF"). Returns null if nothing matches - port of SRWCommands' cam_feeds.py
        /// derive_material_from_setup(), kept in lockstep with that naming convention so a design tagged
        /// for the Fusion side resolves to the same material here.
        /// </summary>
        public static string DeriveMaterialFromSetup(string setupName)
        {
            if (string.IsNullOrEmpty(setupName))
                return null;
            var token = setupName.Split('_')[0].Trim();
            if (token.Length == 0)
                return null;
            foreach (var key in _materialRefs.Keys)
                if (string.Equals(key, token, StringComparison.OrdinalIgnoreCase))
                    return key;
            return null;
        }

        // Strategy names Fusion returns (op.strategy) that actually consume axial/radial
        // step parameters - matches cam_feeds.py's *_APPLICABLE_STRATEGIES sets, since
        // some strategies expose a phantom stepover/stepdown value that the toolpath
        // generator neither reads nor honors.
        private static readonly string[] RadialApplicable = { "adaptive", "pocket", "pocket3d", "face", "parallel",
            "horizontal", "scallop", "spiral", "morphed_spiral", "radial", "flow", "pencil", "bore" };
        private static readonly string[] AxialApplicable = { "adaptive", "pocket", "pocket3d", "face", "contour2d", "horizontal" };

        private static bool StrategyMatches(string strategy, string[] applicable)
        {
            var s = (strategy ?? "").ToLowerInvariant();
            return s.Length > 0 && applicable.Any(b => s == b || s.StartsWith(b));
        }

        private static string ToolClass(FeedsSpeedsOperation op)
        {
            var t = (op.Tool?.Type ?? "").ToLowerInvariant();
            var s = (op.Strategy ?? "").ToLowerInvariant();
            if (t.Contains("drill") || s == "drill" || s.Contains("drill"))
                return "drill";
            if (t.Contains("chamfer") || t.Contains("engrav") || s.Contains("chamfer") || s.Contains("engrav"))
                return "vbit";
            return "mill";
        }

        private static (FeedsSpeedsVerdict Verdict, double? DeltaPct) Classify(double? current, double? recommended)
        {
            if (current == null || recommended == null || recommended == 0)
                return (FeedsSpeedsVerdict.None, null);
            var delta = (current.Value - recommended.Value) / recommended.Value;
            var pct = delta * 100.0;
            if (Math.Abs(delta) <= ToleranceOk) return (FeedsSpeedsVerdict.Ok, pct);
            if (Math.Abs(delta) <= ToleranceNudge) return (FeedsSpeedsVerdict.Nudge, pct);
            return (FeedsSpeedsVerdict.Change, pct);
        }

        private static (FeedsSpeedsVerdict Verdict, double? DeltaPct) ClassifyMax(double? current, double? maxValue)
        {
            if (current == null || maxValue == null || maxValue == 0)
                return (FeedsSpeedsVerdict.None, null);
            var delta = (current.Value - maxValue.Value) / maxValue.Value;
            var pct = delta * 100.0;
            if (current.Value <= maxValue.Value) return (FeedsSpeedsVerdict.Ok, pct);
            if (delta <= ToleranceNudge) return (FeedsSpeedsVerdict.Nudge, pct);
            return (FeedsSpeedsVerdict.Change, pct);
        }

        private static (FeedsSpeedsVerdict Verdict, double? DeltaPct) ClassifyRange(
            double? current, double? minValue, double? maxValue, double? target)
        {
            if (current == null || target == null || target == 0 || minValue == null || maxValue == null)
                return (FeedsSpeedsVerdict.None, null);
            var deltaPct = (current.Value - target.Value) / target.Value * 100.0;
            if (minValue.Value <= current.Value && current.Value <= maxValue.Value)
                return (FeedsSpeedsVerdict.Ok, deltaPct);
            var over = current.Value < minValue.Value
                ? (minValue.Value != 0 ? (minValue.Value - current.Value) / minValue.Value : 1.0)
                : (maxValue.Value != 0 ? (current.Value - maxValue.Value) / maxValue.Value : 1.0);
            return (over <= ToleranceNudge ? FeedsSpeedsVerdict.Nudge : FeedsSpeedsVerdict.Change, deltaPct);
        }

        private static (double ChipLoad, string Note) InterpChipLoad(Dictionary<double, double> table, double? diameterMm)
        {
            if (table == null || table.Count == 0 || diameterMm == null)
                return (0, "no tool diameter available");
            var keys = table.Keys.OrderBy(k => k).ToArray();
            var d = diameterMm.Value;
            if (d <= keys[0])
                return (table[keys[0]], $"tool diameter {d:F2}mm is below the smallest reference ({keys[0]}mm); using endpoint");
            if (d >= keys[keys.Length - 1])
                return (table[keys[keys.Length - 1]], $"tool diameter {d:F2}mm is above the largest reference ({keys[keys.Length - 1]}mm); using endpoint");
            for (int i = 0; i < keys.Length - 1; i++)
            {
                var lo = keys[i]; var hi = keys[i + 1];
                if (lo <= d && d <= hi)
                {
                    var frac = (d - lo) / (hi - lo);
                    return (table[lo] + frac * (table[hi] - table[lo]), "");
                }
            }
            return (table[keys[keys.Length - 1]], "");
        }

        // Converts a surface-speed band (m/min) to an RPM band for a tool diameter via
        // N = V / (pi * D), clamped to [rpmMin, rpmMax] (the machine's actual spindle
        // limits when connected, else FallbackRpmMin/Max).
        private static (double Lo, double Rec, double Hi, List<string> Notes) SurfaceSpeedRpm(
            (double Lo, double Ideal, double Hi) surfaceSpeed, double? diameterMm, double rpmMin, double rpmMax, string label = "surface speed")
        {
            var notes = new List<string>();
            if (diameterMm == null || diameterMm.Value <= 0)
            {
                notes.Add("no tool diameter; cannot derive surface-speed RPM");
                return (rpmMin, rpmMax, rpmMax, notes);
            }
            var circumference = Math.PI * (diameterMm.Value / 1000.0);   // m per rev
            double Clamp(double rpm) => Math.Min(Math.Max(rpm, rpmMin), rpmMax);
            var rawIdeal = surfaceSpeed.Ideal / circumference;
            var rec = Clamp(rawIdeal);
            if (rawIdeal > rpmMax)
                notes.Add($"tool too small for ideal {surfaceSpeed.Ideal:F0} m/min {label} at {rpmMax:F0} RPM cap; " +
                          $"running at the cap gives ~{rec * circumference:F0} m/min.");
            else if (rawIdeal < rpmMin)
                notes.Add($"tool large for the {rpmMin:F0} RPM floor; ideal {label} needs a lower RPM. Running at " +
                          $"the floor gives ~{rec * circumference:F0} m/min - keep feed up, watch heat.");
            return (Clamp(surfaceSpeed.Lo / circumference), rec, Clamp(surfaceSpeed.Hi / circumference), notes);
        }

        private static (double Rec, string Recommended, FeedsSpeedsVerdict Verdict, string Note) CoolantFields(string current)
        {
            var cur = (current ?? "").ToLowerInvariant();
            if (cur.Length == 0)
                return (0, MachineCoolant, FeedsSpeedsVerdict.None, null);
            if (cur == MachineCoolant)
                return (0, MachineCoolant, FeedsSpeedsVerdict.Ok, null);
            return (0, MachineCoolant, FeedsSpeedsVerdict.Change,
                    $"coolant set to \"{current}\" but the machine is {MachineCoolant}-blast only - switch to {MachineCoolant}");
        }

        /// <summary>
        /// Material-engine recommendation for one operation (no machine-limit check yet -
        /// call ApplyMachineLimits afterward to fold in the connected controller's limits).
        /// </summary>
        public static OperationRecommendation Evaluate(FeedsSpeedsOperation op, string material)
        {
            var rec = new OperationRecommendation { Id = op.Id, ToolClass = ToolClass(op) };
            if (!_materialRefs.TryGetValue(material, out var reference))
            {
                rec.Notes.Add($"no reference data for material \"{material}\"");
                return rec;
            }

            switch (rec.ToolClass)
            {
                case "drill": EvaluateDrill(op, reference, rec); break;
                case "vbit": EvaluateVBit(op, reference, rec); break;
                default: EvaluateMill(op, reference, rec); break;
            }

            var (coolRec, coolLabel, coolVerdict, coolNote) = CoolantFields(op.Current?.Coolant);
            rec.RecommendedCoolant = coolLabel;
            rec.CoolantVerdict = coolVerdict;
            if (coolNote != null) rec.Notes.Add(coolNote);
            return rec;
        }

        private static void EvaluateMill(FeedsSpeedsOperation op, MaterialRef reference, OperationRecommendation rec)
        {
            var dia = op.Tool?.DiameterMm;
            var (lo, recRpm, hi, rpmNotes) = reference.SurfaceSpeed != null
                ? SurfaceSpeedRpm(reference.SurfaceSpeed.Value, dia, FallbackRpmMin, FallbackRpmMax)
                : (reference.RpmRange.Value.Lo, reference.RpmRange.Value.Ideal, reference.RpmRange.Value.Hi, new List<string>());
            rec.Notes.AddRange(rpmNotes);

            var (chip, chipNote) = InterpChipLoad(reference.ChipLoad, dia);
            if (chipNote.Length > 0) rec.Notes.Add(chipNote);

            double? recFeed = null;
            var flutes = op.Tool?.Flutes;
            if (chip > 0 && flutes != null && flutes.Value > 0)
                recFeed = recRpm * flutes.Value * chip;
            else if (chip > 0)
                rec.Notes.Add("flute count missing on tool; cannot derive feed");

            const double plungeMinFrac = 0.25, plungeMaxFrac = 0.50, plungeRecFrac = 0.33;
            double? recPlunge = recFeed != null ? recFeed * plungeRecFrac : (double?)null;
            double? plungeMin = recFeed != null ? recFeed * plungeMinFrac : (double?)null;
            double? plungeMax = recFeed != null ? recFeed * plungeMaxFrac : (double?)null;

            double? axialMax = dia != null ? reference.MaxAxialFrac * dia : (double?)null;
            double? radialMax = dia != null ? reference.MaxRadialFrac * dia : (double?)null;

            rec.Rpm.Current = op.Current?.Rpm; rec.Rpm.Recommended = recRpm;
            (rec.Rpm.MaterialVerdict, rec.Rpm.MaterialDeltaPct) = Classify(op.Current?.Rpm, recRpm);

            rec.CuttingFeed.Current = op.Current?.CuttingFeed; rec.CuttingFeed.Recommended = recFeed;
            (rec.CuttingFeed.MaterialVerdict, rec.CuttingFeed.MaterialDeltaPct) = Classify(op.Current?.CuttingFeed, recFeed);

            rec.PlungeFeed.Current = op.Current?.PlungeFeed; rec.PlungeFeed.Recommended = recPlunge;
            (rec.PlungeFeed.MaterialVerdict, rec.PlungeFeed.MaterialDeltaPct) = ClassifyRange(op.Current?.PlungeFeed, plungeMin, plungeMax, recPlunge);

            var strategy = op.Strategy ?? "";
            var axialApplicable = StrategyMatches(strategy, AxialApplicable);
            var radialApplicable = StrategyMatches(strategy, RadialApplicable);

            rec.AxialStep.Current = op.Current?.AxialStep; rec.AxialStep.Recommended = axialMax;
            if (axialApplicable)
                (rec.AxialStep.MaterialVerdict, rec.AxialStep.MaterialDeltaPct) = ClassifyMax(op.Current?.AxialStep, axialMax);
            else
                rec.Notes.Add($"axial step not applicable to \"{strategy}\" strategy");

            rec.RadialStep.Current = op.Current?.RadialStep; rec.RadialStep.Recommended = radialMax;
            if (radialApplicable)
                (rec.RadialStep.MaterialVerdict, rec.RadialStep.MaterialDeltaPct) = ClassifyMax(op.Current?.RadialStep, radialMax);
            else
                rec.Notes.Add($"radial step not applicable to \"{strategy}\" strategy");
        }

        private static void EvaluateDrill(FeedsSpeedsOperation op, MaterialRef reference, OperationRecommendation rec)
        {
            // Tool.Type carries the drill style ("drill-hss" vs the default "drill" = brad point/twist - see
            // OddJobsFeedsSpeedsDialog.ToolType()). Falls back to the brad-point reference if a material has
            // no distinct HSS data yet (e.g. metals, where Drill itself is often null too).
            bool hss = (op.Tool?.Type ?? "").ToLowerInvariant().Contains("hss");
            var dref = (hss ? reference.DrillHss : null) ?? reference.Drill;
            var dia = op.Tool?.DiameterMm;
            if (dref == null)
            {
                rec.Notes.Add("no drill reference data for this material");
                return;
            }
            var (lo, recRpm, hi, rpmNotes) = SurfaceSpeedRpm(dref.SurfaceSpeed, dia, FallbackRpmMin, FallbackRpmMax, "drill surface speed");
            rec.Notes.AddRange(rpmNotes);
            var (fpr, fNote) = InterpChipLoad(dref.FeedPerRev, dia);
            if (fNote.Length > 0) rec.Notes.Add(fNote);
            double? recFeed = fpr > 0 ? recRpm * fpr : (double?)null;
            double? peck = dia != null ? dref.PeckFrac * dia : (double?)null;
            if (fpr > 0)
                rec.Notes.Add($"Drilling feed = RPM x {fpr:F3} mm/rev. The \"feed\" here is the Z drilling feed.");
            if (peck != null)
                rec.Notes.Add($"Use a peck/chip-break cycle; peck depth ~{peck:F1} mm.");
            if (!string.IsNullOrEmpty(dref.Notes)) rec.Notes.Add(dref.Notes);

            rec.Rpm.Current = op.Current?.Rpm; rec.Rpm.Recommended = recRpm;
            (rec.Rpm.MaterialVerdict, rec.Rpm.MaterialDeltaPct) = Classify(op.Current?.Rpm, recRpm);

            // The real drilling (Z) feed is plunge_feed; Fusion leaves cutting_feed at an
            // unused default for drills, so compare against plunge (falling back to
            // cutting_feed only if plunge wasn't exposed).
            var drillFeed = op.Current?.PlungeFeed ?? op.Current?.CuttingFeed;
            rec.CuttingFeed.Current = drillFeed; rec.CuttingFeed.Recommended = recFeed;
            (rec.CuttingFeed.MaterialVerdict, rec.CuttingFeed.MaterialDeltaPct) = Classify(drillFeed, recFeed);
            // Plunge/axial/radial aren't separately meaningful for drills.
        }

        private static void EvaluateVBit(FeedsSpeedsOperation op, MaterialRef reference, OperationRecommendation rec)
        {
            var dia = op.Tool?.DiameterMm;
            var (lo, recRpm, hi, rpmNotes) = reference.SurfaceSpeed != null
                ? SurfaceSpeedRpm(reference.SurfaceSpeed.Value, dia, FallbackRpmMin, FallbackRpmMax)
                : (reference.RpmRange.Value.Lo, reference.RpmRange.Value.Ideal, reference.RpmRange.Value.Hi, new List<string>());
            rec.Notes.AddRange(rpmNotes);
            var (chip, chipNote) = InterpChipLoad(reference.ChipLoad, dia);
            if (chipNote.Length > 0) rec.Notes.Add(chipNote);
            const double vbitFactor = 0.5;
            var flutes = op.Tool?.Flutes;
            double? recFeed = (chip > 0 && flutes != null && flutes.Value > 0)
                ? recRpm * flutes.Value * chip * vbitFactor : (double?)null;
            rec.Notes.Add("V-bit/chamfer: cutting diameter near the tip is small, so run high RPM and a light " +
                          "feed, and keep the point out of the cut.");

            rec.Rpm.Current = op.Current?.Rpm; rec.Rpm.Recommended = recRpm;
            (rec.Rpm.MaterialVerdict, rec.Rpm.MaterialDeltaPct) = Classify(op.Current?.Rpm, recRpm);
            rec.CuttingFeed.Current = op.Current?.CuttingFeed; rec.CuttingFeed.Recommended = recFeed;
            (rec.CuttingFeed.MaterialVerdict, rec.CuttingFeed.MaterialDeltaPct) = Classify(op.Current?.CuttingFeed, recFeed);
        }

        /// <summary>
        /// Folds the CONNECTED controller's actual grblHAL limits into a recommendation's
        /// verdicts (a value the material chart says is fine but the machine can't
        /// physically reach is always a hard Change). No-op (verdicts left as the material
        /// engine set them) when GrblSettings hasn't loaded any settings - e.g. analyzing
        /// an export offline, with no controller connected.
        /// </summary>
        public static void ApplyMachineLimits(OperationRecommendation rec)
        {
            if (!GrblSettings.IsLoaded)
                return;

            double? rpmMax = TryGetSetting(GrblSetting.RpmMax);
            if (rpmMax != null && rec.Rpm.Current != null)
            {
                rec.Rpm.MachineLimit = rpmMax;
                rec.Rpm.MachineVerdict = rec.Rpm.Current.Value <= rpmMax.Value
                    ? FeedsSpeedsVerdict.Ok : FeedsSpeedsVerdict.Change;
                if (rec.Rpm.MachineVerdict == FeedsSpeedsVerdict.Change)
                    rec.Rpm.Notes.Add($"exceeds the connected controller's $30 RpmMax ({rpmMax.Value:F0})");
            }

            // XY cutting moves are bounded by whichever of X/Y is slower; Z-only moves
            // (plunge) by Z's own max rate. MaxFeedRateBase: X=110, Y=111, Z=112.
            double? xMax = TryGetSetting(GrblSetting.MaxFeedRateBase);
            double? yMax = TryGetSetting(GrblSetting.MaxFeedRateBase + 1);
            double? zMax = TryGetSetting(GrblSetting.MaxFeedRateBase + 2);
            double? xyMax = (xMax != null && yMax != null) ? Math.Min(xMax.Value, yMax.Value) : (xMax ?? yMax);

            ApplyFeedLimit(rec.CuttingFeed, xyMax, "cutting feed", "X/Y");
            ApplyFeedLimit(rec.PlungeFeed, zMax, "plunge feed", "Z");
        }

        private static void ApplyFeedLimit(ParameterVerdict pv, double? maxRate, string label, string axisLabel)
        {
            if (maxRate == null || pv.Current == null)
                return;
            pv.MachineLimit = maxRate;
            pv.MachineVerdict = pv.Current.Value <= maxRate.Value ? FeedsSpeedsVerdict.Ok : FeedsSpeedsVerdict.Change;
            if (pv.MachineVerdict == FeedsSpeedsVerdict.Change)
                pv.Notes.Add($"{label} exceeds the connected controller's {axisLabel} max feed rate ({maxRate.Value:F0} mm/min)");
        }

        private static double? TryGetSetting(GrblSetting key)
        {
            try { return GrblSettings.GetDouble(key); }
            catch { return null; }
        }
    }
}
