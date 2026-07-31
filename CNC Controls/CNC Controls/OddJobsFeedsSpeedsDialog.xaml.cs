/*
 * OddJobsFeedsSpeedsDialog.xaml.cs - part of CNC Controls library
 *
 * Shared "Feeds and Speeds" dialog for the Odd Jobs job wizards - consolidates the bit/RPM/feed/depth-of-
 * cut fields that used to sit inline on each tab behind one button, and calls the SAME recommendation
 * engine the Feeds & Speeds tab uses (FeedsSpeedsAdvisor.Evaluate) so Odd Jobs gets real published
 * chip-load/surface-speed reference data instead of an ad hoc guess - no separate/duplicated advisor logic.
 *
 * Material is NOT picked here - it lives on the Setup tab (OddJobsSetupConfig.Section.Material) since it's
 * a property of the STOCK, not of any one operation; this dialog just reads it (read-only) for the lookup.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    // The router bits Odd Jobs offers. Flute count/style and diameter are what actually change the
    // feed/speed math (via FeedsSpeedsAdvisor's chip-load table) and suits different operations/materials.
    //
    // APPEND ONLY - never insert or reorder. The int value is persisted (WorkOrderOperation.Tool), is the key
    // OddJobsToolMemory records dialed-in feeds against, and is what WorkOrderCompiler.ToolNumberFor turns
    // into the T-number an M6 asks for; renumbering would silently repoint saved work orders at other tools.
    public enum OddJobsTool
    {
        EndMill2Flute, RoughingEndMill3Flute, OFlute, BallEnd, SurfacingBit25mm, VBit45,
        // 1/8" pair, for small pockets and holes a 1/4" bit can't get into.
        EndMill2Flute18, BallEnd18,
        // A real twist drill - straight-plunge drilling now picks the bit that matches the hole (see
        // WorkOrderRules.TryMatchDrill), rather than being approximated with an O-flute.
        DrillBit,
        // 90-deg single-flute countersink bits (e.g. a QWORK-style graduated cobalt countersink set) - the
        // operator's own actual 4 sizes (3/8", 9/16", 13/16", 1-1/8"), not a generic placeholder, so each
        // shows up as its own preset diameter in the dropdown rather than one entry the operator retypes the
        // diameter into every time. Only usable for the dedicated Countersink operation kind (a round-hole
        // toolpath small enough for the bit's own diameter to span it) - see
        // WorkOrderCompiler.BuildCountersink / IsCountersinkBit. Same 45-deg-per-side cone geometry as
        // VBit45 for feeds/speeds purposes (ToolType()), but a distinct operation from Chamfer: the operator
        // specifies the target finished diameter, not a raw depth - WorkOrderCompiler converts it
        // (depth = diameter / 2).
        CountersinkBit38, CountersinkBit916, CountersinkBit1316, CountersinkBit118
    }

    public partial class OddJobsFeedsSpeedsDialog : Window
    {
        private const double QuarterInchMm = 6.35d;
        private const double EighthInchMm = 3.175d;
        private const double TwentyFiveMm = 25.0d;
        // The operator's actual 4 countersink bit sizes - exact inch-to-mm conversions, not the rounded
        // 10/14/21/28mm they're commonly quoted as, since the reach-depth math (BitDiameter/2) cares about
        // the real number.
        private const double ThreeEighthInchMm = 9.525d;
        private const double NineSixteenthInchMm = 14.2875d;
        private const double ThirteenSixteenthInchMm = 20.6375d;
        private const double OneOneEighthInchMm = 28.575d;
        private OperationRecommendation lastRecommendation;
        private string material = string.Empty;
        private bool showDoc = true;

        // The exact wording shown in cbxTool's own dropdown - kept as one lookup so a caller with no
        // WorkOrderOperation to read a name off of (Settings:App > Odd Jobs' tool table) can list every tool
        // with the same names the operator already recognizes from this dialog. Update alongside the
        // ComboBoxItems in OddJobsFeedsSpeedsDialog.xaml if either ever changes.
        public static string DisplayName(OddJobsTool tool)
        {
            switch (tool)
            {
                case OddJobsTool.EndMill2Flute: return "1/4\" 2-flute endmill";
                case OddJobsTool.RoughingEndMill3Flute: return "1/4\" 3-flute roughing endmill";
                case OddJobsTool.OFlute: return "1/4\" O-flute";
                case OddJobsTool.BallEnd: return "1/4\" Ball end";
                case OddJobsTool.SurfacingBit25mm: return "25mm 2-flute surfacing bit";
                case OddJobsTool.VBit45: return "45 deg V-bit (chamfer)";
                case OddJobsTool.EndMill2Flute18: return "1/8\" 2-flute endmill";
                case OddJobsTool.BallEnd18: return "1/8\" Ball end";
                case OddJobsTool.DrillBit: return "Drill bit (size = hole)";
                case OddJobsTool.CountersinkBit38: return "3/8\" (10mm) countersink bit";
                case OddJobsTool.CountersinkBit916: return "9/16\" (14mm) countersink bit";
                case OddJobsTool.CountersinkBit1316: return "13/16\" (21mm) countersink bit";
                case OddJobsTool.CountersinkBit118: return "1-1/8\" (28mm) countersink bit";
                default: return tool.ToString();
            }
        }

        // True for any of the 4 countersink bit sizes - shared by WorkOrderCompiler (BuildChamfer's
        // plunge-vs-trace switch, ToolDeclarations' TYPE= tag) and WorkOrderView (ParameterWarnings) so the
        // "is this a countersink bit" check lives in exactly one place.
        public static bool IsCountersinkBit(OddJobsTool tool)
        {
            return tool == OddJobsTool.CountersinkBit38 || tool == OddJobsTool.CountersinkBit916
                || tool == OddJobsTool.CountersinkBit1316 || tool == OddJobsTool.CountersinkBit118;
        }

        public double BitDiameter { get { return fldDiameter.Value; } set { fldDiameter.Value = value; } }
        public double Flutes { get { return fldFlutes.Value; } set { fldFlutes.Value = value; } }
        public double SpindleRPM { get { return fldRpm.Value; } set { fldRpm.Value = value; } }
        public double Feed { get { return fldFeed.Value; } set { fldFeed.Value = value; } }
        public double PlungeFeed { get { return fldPlunge.Value; } set { fldPlunge.Value = value; } }
        public double DepthOfCut { get { return fldDoc.Value; } set { fldDoc.Value = value; } }

        // Optional: for a ball end cutting near its TIP (e.g. a pocket's bottom-finish pass), the effective
        // cutting diameter at a given engagement depth is much smaller than the nominal ball diameter - a
        // ball barely skimming the surface behaves like a much smaller (faster-spinning) cutter. When set,
        // ComputeRecommendation() uses EffectiveBallDiameter(BitDiameter, this) for the advisor lookup
        // instead of the raw nominal BitDiameter - the DISPLAYED "Tool diameter" field still shows the real
        // nominal diameter the operator entered, only the recommendation MATH uses the smaller effective one.
        // Leave null for anything engaging near its side/equator (a normal end mill, or a ball's own
        // side-finishing pass) - there the effective diameter is close enough to nominal that no adjustment
        // is needed.
        public double? EngagementDepthMm { get; set; }

        // Effective cutting diameter of a ball nose at engagement depth d (mm) into a ball of diameter
        // ballDiameterMm - the standard CAM formula: De = 2 * sqrt(d * (2R - d)), R = ballDiameterMm / 2.
        // Clamped so d can never exceed the ball's own radius (a deeper "engagement" than that isn't
        // physically the tip anymore - falls back to the full nominal diameter).
        public static double EffectiveBallDiameter(double ballDiameterMm, double engagementDepthMm)
        {
            double r = ballDiameterMm / 2d;
            double d = Math.Max(0d, Math.Min(engagementDepthMm, r));
            return 2d * Math.Sqrt(d * (2d * r - d));
        }

        public OddJobsTool SelectedTool
        {
            get { return (OddJobsTool)cbxTool.SelectedIndex; }
            set { cbxTool.SelectedIndex = (int)value; }
        }

        // Suggests a default tool from the operation being performed and (where it matters - Aluminum
        // specifically calls for a single-flute bit, see FeedsSpeedsAdvisor's own Aluminum notes) the
        // material. A starting point, not a hard rule - the operator can always pick a different tool.
        public static OddJobsTool SuggestTool(string operation, string material)
        {
            if (operation == "facing")
                return OddJobsTool.SurfacingBit25mm;
            if (operation == "chamfer")
                return OddJobsTool.VBit45;
            if (operation == "finishing")
                return OddJobsTool.BallEnd;
            if (operation == "drilling")
                return OddJobsTool.DrillBit;
            // "countersink" deliberately NOT handled here - SmallestCountersinkBitFor (below) picks by the
            // operation's own target diameter, a strictly better answer than a generic operation-name guess.
            if (string.Equals(material, "Aluminum", StringComparison.OrdinalIgnoreCase))
                return OddJobsTool.OFlute;
            if (operation == "roughing")
                return OddJobsTool.RoughingEndMill3Flute;
            return OddJobsTool.EndMill2Flute;
        }

        // The smallest of the 4 countersink bits that can actually reach the given target diameter (a bit's
        // own diameter is the widest it can cut - see BuildCountersink's own comment, and IsCountersinkBit
        // above). Falls back to the largest bit if even that one is too small - ParameterWarnings still flags
        // that case once a real bit is picked; this just gives the closest starting point rather than
        // silently understating what's needed. Used both for a new Countersink operation's initial tool and
        // whenever the operator edits the target diameter afterward (see WorkOrderView.CaptureFields) -
        // confirmed on real hardware 2026-07-30: the operator wants the diameter to drive the bit choice, not
        // the other way around.
        public static OddJobsTool SmallestCountersinkBitFor(double targetDiameterMm)
        {
            if (targetDiameterMm <= ThreeEighthInchMm) return OddJobsTool.CountersinkBit38;
            if (targetDiameterMm <= NineSixteenthInchMm) return OddJobsTool.CountersinkBit916;
            if (targetDiameterMm <= ThirteenSixteenthInchMm) return OddJobsTool.CountersinkBit1316;
            return OddJobsTool.CountersinkBit118;
        }

        // Restricts the tool dropdown to only the tools that make sense for the given operation kind - added
        // 2026-07-30 after Drill was showing V-bit/ball end/surfacing bit/countersink choices that can never
        // apply to a straight-plunge drill. Called once, right after construction (order doesn't matter
        // relative to SelectedTool - Visibility=Collapsed on a ComboBoxItem only hides it from the dropdown
        // LIST, not from being displayed as the current selection when closed).
        public void RestrictToolsFor(WorkOrderOpKind kind)
        {
            bool isDrill = kind == WorkOrderOpKind.Drill;
            bool isCountersink = kind == WorkOrderOpKind.Countersink;
            bool isChamfer = kind == WorkOrderOpKind.Chamfer;
            bool isMill = !isDrill && !isCountersink && !isChamfer;

            Show(itemEndMill2Flute, isMill);
            Show(itemRoughingEndMill3Flute, isMill);
            Show(itemOFlute, isMill);
            Show(itemBallEnd, isMill);
            Show(itemSurfacingBit25mm, isMill);
            Show(itemEndMill2Flute18, isMill);
            Show(itemBallEnd18, isMill);
            Show(itemVBit45, isChamfer);
            Show(itemDrillBit, isDrill);
            Show(itemCountersinkBit38, isCountersink);
            Show(itemCountersinkBit916, isCountersink);
            Show(itemCountersinkBit1316, isCountersink);
            Show(itemCountersinkBit118, isCountersink);
        }

        private static void Show(UIElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // docLabel: "Depth of cut:" for milling operations, "Peck depth:" for Drill/Bore's drill mode -
        // same field either way, just labeled for what it means to the caller.
        // showDoc: false for a single-lap finishing pass (Pocket's side/bottom finish) - those don't step
        // down at all (side retraces at the roughing's own Z levels, bottom cuts one lap at true depth), so
        // there is no axial-step value for this dialog to read OR write; showing the advisor's generic
        // "Depth of cut" recommendation there was confusing (a number that looks like it should relate to
        // wall/floor stock-to-leave, but doesn't - that's a different concept entirely).
        public OddJobsFeedsSpeedsDialog(OddJobsTool preferredTool, string docLabel = "Depth of cut:", bool showDoc = true)
        {
            InitializeComponent();
            fldDoc.Label = docLabel;
            if (!showDoc)
                fldDoc.Visibility = Visibility.Collapsed;
            this.showDoc = showDoc;

            // Live highlight refresh: recompute whenever anything that affects the comparison changes - the
            // 4 value fields themselves (typing toward/away from the recommendation) and the tool geometry
            // fields (diameter/flutes change what gets recommended in the first place).
            var valueProp = NumericField.ValueProperty;
            foreach (var field in new[] { fldDiameter, fldFlutes, fldRpm, fldFeed, fldPlunge, fldDoc })
                DependencyPropertyDescriptor.FromProperty(valueProp, typeof(NumericField)).AddValueChanged(field, (s, e) => ComputeRecommendation());

            SelectedTool = preferredTool;
        }

        // Switches Material from the normal read-only display (bound to the Setup tab's stock material) to an
        // editable dropdown - for a caller with no work order/Setup context at all, e.g. Settings:App > Odd
        // Jobs' tool table, where the point is previewing/tuning a tool's feeds and speeds in a material of
        // the operator's choosing rather than reading one off a job in progress. Starts with nothing selected
        // (Material stays empty, same as an unset Setup material) - the operator picks one to see anything.
        public void EnableMaterialPicker()
        {
            txtMaterial.Visibility = Visibility.Collapsed;
            pnlMaterialPicker.Visibility = Visibility.Visible;

            cbxMaterialPicker.Items.Clear();
            foreach (var m in FeedsSpeedsAdvisor.MaterialRefs.Keys.OrderBy(k => k))
                cbxMaterialPicker.Items.Add(m);
            cbxMaterialPicker.SelectedIndex = -1;
        }

        private void cbxMaterialPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Material = cbxMaterialPicker.SelectedItem as string ?? string.Empty;

            // No live WorkOrderOperation to clobber here (unlike the normal caller, which already seeded these
            // fields from the operation before Material is ever set) - recalling the operator's last settled
            // values for this tool/diameter/material, same idiom NewOperation uses, is exactly the point of
            // browsing by material in the first place.
            var remembered = OddJobsToolMemory.Find(SelectedTool, BitDiameter, Material);
            if (remembered != null)
            {
                SpindleRPM = remembered.Rpm;
                Feed = remembered.Feed;
                PlungeFeed = remembered.PlungeFeed;
                if (showDoc)
                    DepthOfCut = remembered.DepthOfCut;
            }
        }

        // Read-only from here - Material lives on the Setup tab (OddJobsSetupConfig.Section.Material).
        // Setting it (the caller does this once, right after construction) computes and SHOWS the
        // recommendation readout, but does NOT apply it - the dialog opens with the wizard's own CURRENT
        // values (already set by the caller before this) untouched, same as the fields it was constructed
        // with. Auto-applying used to overwrite them unconditionally (2026-07-27 change, reverted): every
        // reopen silently discarded a manually-tuned value (e.g. a lower DOC deliberately chosen for a
        // weaker machine) and forced picking the tool + recommend + re-edit sequence over again each time.
        // The operator explicitly applies via "Use recommended" (btnUseRecommended_Click) when they want it.
        public string Material
        {
            get { return material; }
            set
            {
                material = value ?? string.Empty;
                txtMaterial.Text = string.IsNullOrEmpty(material)
                    ? "Material: (not set - pick one on the Setup tab)"
                    : "Material: " + material;
                ComputeRecommendation();
            }
        }

        // "mill" for FeedsSpeedsAdvisor.Evaluate's ToolClass() unless the V-bit is selected (routes into
        // EvaluateVBit, matched on Tool.Type containing "chamfer") or the Drill Bit is selected (routes into
        // EvaluateDrill, matched on Tool.Type containing "drill" - see ToolClass()). Confirmed missing on real
        // use 2026-07-30: without this, a drill op fell through to the generic end-mill formula (RPM x flutes
        // x chip-load, sized for radial milling engagement) instead of the drill-specific mm/rev + peck-frac
        // table, producing wildly inflated numbers (e.g. an 8mm HSS twist drill in MDF getting a 2-flute
        // milling chip load applied to it).
        // "-hss" suffix (still containing "drill", so ToolClass() routing is unaffected) picks
        // FeedsSpeedsAdvisor's DrillHss reference instead of the default brad-point/twist one - see
        // EvaluateDrill's own comment and pnlDrillStyle (visible only for the Drill Bit tool).
        private string ToolType()
        {
            if (SelectedTool == OddJobsTool.DrillBit)
                return IsHssDrill ? "drill-hss" : "drill";
            // The countersink bits are the same 45-deg-per-side conical geometry as VBit45 (see the enum's
            // own comment) - route into the same EvaluateVBit advisor path.
            return SelectedTool == OddJobsTool.VBit45 || IsCountersinkBit(SelectedTool) ? "chamfer" : "mill";
        }

        // pnlDrillStyle's own SelectedIndex, 1 = HSS twist, 0 (or unset) = brad point/twist (the default,
        // preserving old behavior for anyone not yet using the new selector). Public (not just read via
        // ToolType()) so the caller can both seed it from WorkOrderOperation.DrillHss on open and read it
        // back to persist on OK - session-only state until WorkOrderView started saving it 2026-07-30.
        public bool IsHssDrill
        {
            get { return cbxDrillStyle.SelectedIndex == 1; }
            set { cbxDrillStyle.SelectedIndex = value ? 1 : 0; }
        }

        private void cbxDrillStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComputeRecommendation();
        }

        private void cbxTool_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch (SelectedTool)
            {
                case OddJobsTool.EndMill2Flute: BitDiameter = QuarterInchMm; Flutes = 2; break;
                case OddJobsTool.RoughingEndMill3Flute: BitDiameter = QuarterInchMm; Flutes = 3; break;
                case OddJobsTool.OFlute: BitDiameter = QuarterInchMm; Flutes = 1; break;
                case OddJobsTool.BallEnd: BitDiameter = QuarterInchMm; Flutes = 2; break;
                case OddJobsTool.SurfacingBit25mm: BitDiameter = TwentyFiveMm; Flutes = 2; break;
                case OddJobsTool.VBit45: BitDiameter = QuarterInchMm; Flutes = 2; break;
                case OddJobsTool.EndMill2Flute18: BitDiameter = EighthInchMm; Flutes = 2; break;
                case OddJobsTool.BallEnd18: BitDiameter = EighthInchMm; Flutes = 2; break;
                // Diameter deliberately left alone: a drill's size IS the hole being cut, so the caller sets
                // it from the geometry rather than this dropdown supplying a nominal.
                case OddJobsTool.DrillBit: Flutes = 2; break;
                // Single-flute, and the operator suspects (not confirmed against the actual bit spec) a max
                // RPM well under typical router speeds - the material table would otherwise recommend
                // 14000-22000 (Hardwood/MDF RpmRange), which could exceed a real rating for a bit like this.
                // 8000 is a conservative STARTING value, not a verified safe number - confirmed on real
                // hardware 2026-07-30 (operator: "not sure about RPM rating but I suspect is below 10,000").
                case OddJobsTool.CountersinkBit38: BitDiameter = ThreeEighthInchMm; Flutes = 1; SpindleRPM = 8000d; break;
                case OddJobsTool.CountersinkBit916: BitDiameter = NineSixteenthInchMm; Flutes = 1; SpindleRPM = 8000d; break;
                case OddJobsTool.CountersinkBit1316: BitDiameter = ThirteenSixteenthInchMm; Flutes = 1; SpindleRPM = 8000d; break;
                case OddJobsTool.CountersinkBit118: BitDiameter = OneOneEighthInchMm; Flutes = 1; SpindleRPM = 8000d; break;
            }
            pnlDrillStyle.Visibility = SelectedTool == OddJobsTool.DrillBit ? Visibility.Visible : Visibility.Collapsed;
            if (SelectedTool == OddJobsTool.DrillBit && cbxDrillStyle.SelectedIndex < 0)
                cbxDrillStyle.SelectedIndex = 0;   // default brad point/twist - preserves old behavior

            // A drill only ever moves sideways as a rapid between pecks (see WorkOrderCompiler.BuildDrill) -
            // every actual cutting move is the peck plunge itself, fed at Plunge feed. "Feed rate" has nothing
            // to drive for this tool, so showing it invites setting a number that's silently ignored.
            fldFeed.Visibility = SelectedTool == OddJobsTool.DrillBit ? Visibility.Collapsed : Visibility.Visible;
            ComputeRecommendation();
        }

        // Runs FeedsSpeedsAdvisor.Evaluate and refreshes each field's highlight/tooltip against it. Called
        // whenever anything that could change the recommendation OR the fields being compared against it
        // changes (tool, diameter, flutes, or any of the 4 value fields themselves) - cheap, and keeps the
        // highlights live instead of needing a manual "recompute" step.
        private void ComputeRecommendation()
        {
            if (string.IsNullOrEmpty(material))
            {
                lastRecommendation = null;
                SetFieldHighlight(fldRpm, null, null);
                SetFieldHighlight(fldFeed, null, null);
                SetFieldHighlight(fldPlunge, null, null);
                if (showDoc)
                    SetFieldHighlight(fldDoc, null, null);
                btnUseRecommended.IsEnabled = false;
                txtRecommendation.Visibility = Visibility.Collapsed;
                UpdateNudgeReadout();
                return;
            }

            // See EngagementDepthMm's own comment - a ball cutting near its tip effectively behaves like a
            // much smaller cutter than its nominal diameter.
            double diaForLookup = EngagementDepthMm.HasValue ? EffectiveBallDiameter(BitDiameter, EngagementDepthMm.Value) : BitDiameter;

            var op = new FeedsSpeedsOperation
            {
                Id = "oddjob",
                Strategy = SelectedTool == OddJobsTool.DrillBit ? "drill" : "adaptive",
                Tool = new FeedsSpeedsTool { Type = ToolType(), DiameterMm = diaForLookup, Flutes = Flutes },
                Current = new FeedsSpeedsCurrent { Rpm = SpindleRPM, CuttingFeed = Feed, PlungeFeed = PlungeFeed, AxialStep = DepthOfCut }
            };
            lastRecommendation = FeedsSpeedsAdvisor.Evaluate(op, material);
            if (EngagementDepthMm.HasValue)
            {
                // Per-field Notes (not the shared OperationRecommendation.Notes) is what SetFieldHighlight's
                // tooltip actually shows - add to each affected field so whichever one is hovered explains why.
                string note = string.Format(CultureInfo.InvariantCulture,
                    "Ball engaging {0:0.##} mm deep near its tip - treated as an effective Ø{1:0.##} mm cutter for this lookup (nominal Ø{2:0.##} mm).",
                    EngagementDepthMm.Value, diaForLookup, BitDiameter);
                lastRecommendation.Rpm.Notes.Insert(0, note);
                lastRecommendation.CuttingFeed.Notes.Insert(0, note);
                lastRecommendation.PlungeFeed.Notes.Insert(0, note);
            }

            bool r1 = SetFieldHighlight(fldRpm, lastRecommendation.Rpm, "rpm");
            bool r2 = SetFieldHighlight(fldFeed, lastRecommendation.CuttingFeed, "mm/min");
            bool r3 = SetFieldHighlight(fldPlunge, lastRecommendation.PlungeFeed, "mm/min");
            bool r4 = showDoc && SetFieldHighlight(fldDoc, lastRecommendation.AxialStep, "mm");

            btnUseRecommended.IsEnabled = lastRecommendation.Rpm.Recommended != null || lastRecommendation.CuttingFeed.Recommended != null;
            txtRecommendation.Visibility = (r1 || r2 || r3 || r4) ? Visibility.Visible : Visibility.Collapsed;
            UpdateNudgeReadout();
        }

        // Compares a field's CURRENT value to its recommendation; highlights + tooltips it when they differ
        // beyond rounding noise. Returns whether it ended up highlighted (used to show/hide the hint text).
        private static bool SetFieldHighlight(NumericField field, ParameterVerdict verdict, string unit)
        {
            if (verdict?.Recommended == null)
            {
                field.HasRecommendation = false;
                field.ToolTip = null;
                return false;
            }

            double rec = verdict.Recommended.Value;
            bool differs = Math.Abs(field.Value - rec) > Math.Max(0.05d, Math.Abs(rec) * 0.005d);
            field.HasRecommendation = differs;

            if (!differs)
            {
                field.ToolTip = null;
                return false;
            }

            var tip = new System.Text.StringBuilder();
            tip.AppendFormat(CultureInfo.InvariantCulture, "Recommended: {0:0.##} {1} (current: {2:0.##})", rec, unit, field.Value);
            foreach (var note in verdict.Notes)
                tip.Append("\n").Append(note);
            tip.Append("\nDouble-click to apply.");
            field.ToolTip = tip.ToString();
            return true;
        }

        // Double-click applies just THIS field's own recommendation, not all 4 at once (see
        // btnUseRecommended_Click for the bulk version).
        private void Field_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || lastRecommendation == null)
                return;

            var field = sender as NumericField;
            ParameterVerdict verdict =
                ReferenceEquals(field, fldRpm) ? lastRecommendation.Rpm :
                ReferenceEquals(field, fldFeed) ? lastRecommendation.CuttingFeed :
                ReferenceEquals(field, fldPlunge) ? lastRecommendation.PlungeFeed :
                ReferenceEquals(field, fldDoc) ? lastRecommendation.AxialStep : null;

            if (verdict?.Recommended == null)
                return;

            field.Value = Math.Round(verdict.Recommended.Value, field == fldDoc ? 1 : 0);
            e.Handled = true;
            ComputeRecommendation();
        }

        private void ApplyRecommendation()
        {
            if (lastRecommendation == null)
                return;
            if (lastRecommendation.Rpm.Recommended != null)
                SpindleRPM = Math.Round(lastRecommendation.Rpm.Recommended.Value);
            if (lastRecommendation.CuttingFeed.Recommended != null)
                Feed = Math.Round(lastRecommendation.CuttingFeed.Recommended.Value);
            if (lastRecommendation.PlungeFeed.Recommended != null)
                PlungeFeed = Math.Round(lastRecommendation.PlungeFeed.Recommended.Value);
            if (showDoc && lastRecommendation.AxialStep.Recommended != null)
                DepthOfCut = Math.Round(lastRecommendation.AxialStep.Recommended.Value, 1);
            ComputeRecommendation();
        }

        private void btnUseRecommended_Click(object sender, RoutedEventArgs e)
        {
            ApplyRecommendation();
        }

        // Step the feed and plunge by +/-10% per click, spindle RPM left alone - the practical "back it off
        // until it sounds right" move, which is how a bit actually gets dialed in for a specific machine.
        // Published chip-load charts assume industrial rigidity and acceleration, so a chart-correct feed can
        // still be well beyond what a router will hold through a corner (MDF's own table, for instance, puts a
        // 1/4" 2-flute at 6200 mm/min - arithmetically right, ~244 ipm in practice). Whatever the operator
        // settles on here is what OddJobsToolMemory records for this tool/diameter/material, so the dialed-in
        // value becomes the starting point next time instead of the chart value.
        private void btnNudge_Click(object sender, RoutedEventArgs e)
        {
            double percent = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            double factor = 1d + percent / 100d;

            // Floored rather than allowed to collapse toward zero - repeated clicks should asymptote to a
            // slow-but-real feed, not to a stall.
            Feed = Math.Max(10d, Math.Round(Feed * factor));
            PlungeFeed = Math.Max(5d, Math.Round(PlungeFeed * factor));
            // The field setters already re-trigger ComputeRecommendation (wired in the constructor), which
            // refreshes the readout - no explicit call needed here.
        }

        // Shows where the current feed sits relative to the chart recommendation, plus the resulting chip load
        // per tooth - the number that actually matters for tool life, and which changes as soon as the feed is
        // nudged away from the chart value.
        private void UpdateNudgeReadout()
        {
            if (txtNudge == null)
                return;

            var parts = new List<string>();
            double? recommended = lastRecommendation?.CuttingFeed?.Recommended;
            if (recommended > 0d)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0}% of the chart feed", Feed / recommended.Value * 100d));
            if (SpindleRPM > 0d && Flutes > 0d)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.000} mm/tooth chip load", Feed / (SpindleRPM * Flutes)));

            txtNudge.Text = string.Join(" - ", parts);
            txtNudge.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
