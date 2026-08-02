/*
 * OddJobsFeedsSpeedsDialog.xaml.cs - part of CNC Controls library
 *
 * Shared "Feeds and Speeds" dialog for the Odd Jobs job wizards - consolidates the bit/RPM/feed/depth-of-
 * cut fields that used to sit inline on each tab behind one button, and calls the SAME recommendation
 * engine the Feeds & Speeds tab uses (FeedsSpeedsAdvisor.Evaluate) so Odd Jobs gets real published
 * chip-load/surface-speed reference data instead of an ad hoc guess - no separate/duplicated advisor logic.
 *
 * Material is NOT picked here - it lives on the Setup tab (StartJobConfig.Section.Material) since it's
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
    public partial class OddJobsFeedsSpeedsDialog : Window
    {
        private OperationRecommendation lastRecommendation;
        private string material = string.Empty;
        private bool showDoc = true;

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

        // The tool behind the current selection - every row (factory-default or operator-added) is a real
        // CustomTool now, tagged on its ComboBoxItem (see PopulateToolItems). Null only if nothing is
        // selected yet (dialog mid-construction, before SelectToolValue runs).
        public CustomTool SelectedTool => (cbxTool.SelectedItem as ComboBoxItem)?.Tag as CustomTool;

        // op.Tool-compatible form of the current selection. Use this (not SelectedTool) wherever the value
        // is being stored back onto a WorkOrderOperation.
        public int SelectedToolValue => SelectedTool?.Id ?? 0;

        // Every tool ComboBoxItem this dialog instance populated, in list (= CustomTools.SectionConfig)
        // order. Kept so RestrictToolsFor can filter them by Kind.
        private readonly List<ComboBoxItem> toolItems = new List<ComboBoxItem>();

        private void PopulateToolItems()
        {
            var tools = CustomTools.SectionConfig?.Entries;
            if (tools == null)
                return;
            foreach (var ct in tools)
            {
                var item = new ComboBoxItem { Content = ct.Name, Tag = ct };
                cbxTool.Items.Add(item);
                toolItems.Add(item);
            }
        }

        // Select by op.Tool value (a CustomTool.Id) - the inverse of SelectedToolValue.
        public void SelectToolValue(int opToolValue)
        {
            var ct = CustomTools.Find(opToolValue);
            foreach (var item in toolItems)
                if (ReferenceEquals(item.Tag, ct)) { cbxTool.SelectedItem = item; return; }
        }

        public WorkOrderCutDirection CutDirection
        {
            get { return cbxDirection.SelectedIndex == 1 ? WorkOrderCutDirection.Climb : WorkOrderCutDirection.Conventional; }
            set { cbxDirection.SelectedIndex = value == WorkOrderCutDirection.Climb ? 1 : 0; }
        }

        // Suggests a default tool from the operation being performed and (where it matters - Aluminum
        // specifically calls for a single-flute bit, see FeedsSpeedsAdvisor's own Aluminum notes) the
        // material. A starting point, not a hard rule - the operator can always pick a different tool.
        // Picked by Kind (+ Flutes, to tell the roughing bit apart from an ordinary 2-flute end mill) rather
        // than by a specific Id, so this keeps working if the operator retunes/renames the factory defaults -
        // it just returns the FIRST list entry matching the bucket, which is the seeded default's own
        // position in Default-App.config (see that file's CustomTools section) unless the operator reorders.
        public static int SuggestTool(string operation, string material)
        {
            var entries = CustomTools.SectionConfig?.Entries;
            if (entries == null || entries.Count == 0)
                return 0;

            CustomTool pick = null;
            if (operation == "facing")
                pick = entries.FirstOrDefault(t => t.Kind == CustomToolKind.Surfacing);
            else if (operation == "chamfer")
                pick = entries.FirstOrDefault(t => t.Kind == CustomToolKind.VBitOrChamfer);
            else if (operation == "finishing")
                pick = entries.FirstOrDefault(t => t.Kind == CustomToolKind.BallEnd);
            else if (operation == "drilling")
                pick = entries.FirstOrDefault(t => t.Kind == CustomToolKind.Drill);
            // "countersink" deliberately NOT handled here - SmallestCountersinkBitFor (below) picks by the
            // operation's own target diameter, a strictly better answer than a generic operation-name guess.
            else if (string.Equals(material, "Aluminum", StringComparison.OrdinalIgnoreCase))
                pick = entries.FirstOrDefault(t => t.Kind == CustomToolKind.OFlute);
            else if (operation == "roughing")
                pick = entries.FirstOrDefault(t => t.Kind == CustomToolKind.EndMill && t.Flutes >= 3);

            if (pick == null)
                pick = entries.FirstOrDefault(t => t.Kind == CustomToolKind.EndMill && t.Flutes == 2)
                    ?? entries.FirstOrDefault(t => t.Kind == CustomToolKind.EndMill)
                    ?? entries[0];
            return pick.Id;
        }

        // The smallest countersink-kind tool that can actually reach the given target diameter (a bit's own
        // diameter is the widest it can cut - see BuildCountersink's own comment, and CustomTools.IsCountersink
        // above). Falls back to the largest one if even that is too small - ParameterWarnings still flags
        // that case once a real bit is picked; this just gives the closest starting point rather than
        // silently understating what's needed. Used both for a new Countersink operation's initial tool and
        // whenever the operator edits the target diameter afterward (see WorkOrderView.CaptureFields) -
        // confirmed on real hardware 2026-07-30: the operator wants the diameter to drive the bit choice, not
        // the other way around.
        public static int SmallestCountersinkBitFor(double targetDiameterMm)
        {
            var candidates = (CustomTools.SectionConfig?.Entries ?? new List<CustomTool>())
                .Where(t => t.Kind == CustomToolKind.Countersink).OrderBy(t => t.DiameterMm).ToList();
            var pick = candidates.FirstOrDefault(t => t.DiameterMm >= targetDiameterMm) ?? candidates.LastOrDefault();
            return pick?.Id ?? 0;
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
            bool isSurface = kind == WorkOrderOpKind.Surface;
            bool isMill = !isDrill && !isCountersink && !isChamfer && !isSurface;

            foreach (var item in toolItems)
            {
                var ct = (CustomTool)item.Tag;
                bool visible;
                switch (ct.Kind)
                {
                    case CustomToolKind.Drill: visible = isDrill; break;
                    case CustomToolKind.VBitOrChamfer: visible = isChamfer; break;
                    case CustomToolKind.Countersink: visible = isCountersink; break;
                    // The surfacing bit is otherwise a normal mill-class tool (isMill's own bucket), but a
                    // Surface operation only ever wants IT - a facing pass with an ordinary endmill/ball end
                    // doesn't make sense.
                    case CustomToolKind.Surfacing: visible = isMill || isSurface; break;
                    default: visible = isMill; break;   // EndMill, OFlute, BallEnd
                }
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // docLabel: "Depth of cut:" for milling operations, "Peck depth:" for Drill/Bore's drill mode -
        // same field either way, just labeled for what it means to the caller.
        // showDoc: false for a single-lap finishing pass (Pocket's side/bottom finish) - those don't step
        // down at all (side retraces at the roughing's own Z levels, bottom cuts one lap at true depth), so
        // there is no axial-step value for this dialog to read OR write; showing the advisor's generic
        // "Depth of cut" recommendation there was confusing (a number that looks like it should relate to
        // wall/floor stock-to-leave, but doesn't - that's a different concept entirely).
        // showDirection: false for Drill/Countersink - both are a straight on-center plunge with no path to
        // reverse, so climb/conventional isn't a meaningful choice there.
        // preferredToolValue: an op.Tool-compatible value (a CustomTool.Id - see SelectedToolValue/
        // SelectToolValue), resolved via SelectToolValue rather than a bare SelectedIndex/SelectedItem set.
        public OddJobsFeedsSpeedsDialog(int preferredToolValue, string docLabel = "Depth of cut:", bool showDoc = true, bool showDirection = true)
        {
            InitializeComponent();
            fldDoc.Label = docLabel;
            if (!showDoc)
                fldDoc.Visibility = Visibility.Collapsed;
            this.showDoc = showDoc;
            cbxDirection.SelectedIndex = 0;
            if (!showDirection)
                pnlDirection.Visibility = Visibility.Collapsed;
            PopulateToolItems();

            // Live highlight refresh: recompute whenever anything that affects the comparison changes - the
            // 4 value fields themselves (typing toward/away from the recommendation) and the tool geometry
            // fields (diameter/flutes change what gets recommended in the first place).
            var valueProp = NumericField.ValueProperty;
            foreach (var field in new[] { fldDiameter, fldFlutes, fldRpm, fldFeed, fldPlunge, fldDoc })
                DependencyPropertyDescriptor.FromProperty(valueProp, typeof(NumericField)).AddValueChanged(field, (s, e) => ComputeRecommendation());

            SelectToolValue(preferredToolValue);
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
            if (SelectedTool != null)
            {
                var remembered = OddJobsToolMemory.Find(SelectedToolValue, BitDiameter, Material);
                if (remembered != null)
                {
                    SpindleRPM = remembered.Rpm;
                    Feed = remembered.Feed;
                    PlungeFeed = remembered.PlungeFeed;
                    if (showDoc)
                        DepthOfCut = remembered.DepthOfCut;
                }
            }
        }

        // Read-only from here - Material lives on the Setup tab (StartJobConfig.Section.Material).
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
            var ct = SelectedTool;
            if (ct == null)
                return "mill";
            switch (ct.Kind)
            {
                case CustomToolKind.Drill: return IsHssDrill ? "drill-hss" : "drill";
                // The countersink bits and any V-bit/chamfer-kind tool are the same 45-deg-per-side conical
                // geometry - route into the same EvaluateVBit advisor path.
                case CustomToolKind.VBitOrChamfer:
                case CustomToolKind.Countersink: return "chamfer";
                default: return "mill";
            }
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
            var ct = SelectedTool;
            if (ct == null)
                return;

            bool isDrill = ct.Kind == CustomToolKind.Drill;
            // Diameter deliberately left alone for a drill: a drill's size IS the hole being cut (see
            // WorkOrderCompiler.EffectiveBitDiameter), so the caller sets it from the geometry rather than
            // this dropdown supplying a nominal - applies to any Drill-kind tool, not just the factory default.
            if (!isDrill)
                BitDiameter = ct.DiameterMm;
            Flutes = ct.Flutes;
            // A one-time conservative STARTING RPM for tools whose real bit rating isn't in the advisor's
            // material tables (currently just the seeded countersink defaults - see CustomTool.DefaultRpm's
            // own comment), not a verified safe number.
            if (ct.DefaultRpm > 0d)
                SpindleRPM = ct.DefaultRpm;

            pnlDrillStyle.Visibility = isDrill ? Visibility.Visible : Visibility.Collapsed;
            if (isDrill && cbxDrillStyle.SelectedIndex < 0)
                cbxDrillStyle.SelectedIndex = 0;   // default brad point/twist - preserves old behavior

            // A drill only ever moves sideways as a rapid between pecks (see WorkOrderCompiler.BuildDrill) -
            // every actual cutting move is the peck plunge itself, fed at Plunge feed. "Feed rate" has nothing
            // to drive for this tool, so showing it invites setting a number that's silently ignored.
            fldFeed.Visibility = isDrill ? Visibility.Collapsed : Visibility.Visible;
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
                Strategy = SelectedTool?.Kind == CustomToolKind.Drill ? "drill" : "adaptive",
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
