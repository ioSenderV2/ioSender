/*
 * WorkOrderModel.cs - part of CNC Controls library
 *
 * Odd Jobs "Work Order" data model. The unit of work is a TOOLPATH: a named piece of geometry (one of the
 * open/closed loops this tab can handle) with an ordered list of OPERATIONS underneath describing how to cut
 * it - pocket the enclosed area, follow the outline, drill or bore a hole, finish the wall or floor, break the
 * top edge.
 *
 * The split: a toolpath says WHAT and WHERE (shape, position, size). An operation says HOW - which includes
 * how deep, since depth is a property of the cut rather than of the shape: one circle can be contoured through
 * the stock while another is pocketed 3 mm deep. So depth, the through-cut flag and the tabs that go with it
 * all live on the operations that actually pay attention to them (Contour, Drill, Bore for through; Pocket for
 * depth), not on the geometry.
 *
 * Note there is no tool NUMBER anywhere in this model: the tool lives in the Feeds and Speeds dialog, each
 * operation records which tool was picked there, and WorkOrderCompiler.ToolNumberFor derives the T-number.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CNC.Core;

namespace CNC.Controls
{
    // Every geometry this tab can handle. Line is the only OPEN one - the rest are closed loops. Indirect is
    // not really a shape at all - it carries no dimensions of its own and no operations; it borrows both,
    // live, from another toolpath named in IndirectSource (see WorkOrderToolpath), and only supplies a
    // different X/Y (and optionally its own Pattern) to run them at. Editing the source's geometry or
    // operations is reflected the next time this one generates - that's the whole point versus Duplicate,
    // which forks an independent copy instead. Surface is a facing pass over a Width x Depth area (reuses
    // those two fields, same as Oval/Rect) - it isn't cut relative to any enclosed shape, so it only ever
    // carries the one Surface operation (see WorkOrderRules.AvailableOperations).
    public enum WorkOrderGeometryKind { Line, Circle, Oval, Square, Rect, Surface, Indirect, Text }

    // Which point of a toolpath's own bounding box its X/Y names. Front is -Y and back is +Y, matching the
    // corner order OddJobsGeometry.RectPoints already walks and the front/back sense pcorner.macro uses for
    // its corner ids - so "front left" means the same thing here as it does when probing one.
    public enum WorkOrderAnchor { Center, FrontLeft, FrontRight, BackLeft, BackRight }

    // Repeats a whole toolpath - geometry AND every operation on it - at a set of offsets.
    public enum WorkOrderPatternKind { None, Grid, Circular }

    public enum WorkOrderOpKind { Pocket, Contour, Drill, Bore, SideFinish, BottomFinish, Chamfer, Countersink, Surface, Engrave }

    // Conventional cuts against the cutter's rotation (chip thins to nothing at the end of the cut) - more
    // forgiving of a machine with backlash/flex, since the cutter is always being pushed away from new
    // material rather than pulled into it. Climb cuts with the rotation (chip starts thick, thins to zero) -
    // generally a cleaner finish and less tearout in wood, but a machine with any backlash can let the
    // cutter grab and self-feed into the material. See WorkOrderCompiler.OrderForDirection/BoreArcCommand
    // for how this translates into actual G-code direction (which raw CW/CCW orbit means "climb" flips
    // between an internal feature like a pocket/bore and an external one like a Contour cut).
    public enum WorkOrderCutDirection { Conventional, Climb }

    // A machine-level fact, not a per-work-order one - lives in AppConfig.Config.SpindleDirectionCapability
    // (Settings:App > Work Order), read by WorkOrderCompiler to get Climb/Conventional's raw CW/CCW orbit
    // math right for hardware that can't actually run bidirectional. Some VFD/spindle setups only have a
    // single relay/direction wired, and - as with a real one found 2026-08-01 - the one direction they DO
    // run isn't necessarily the one the M3/"CW" label implies; ioSender's Work Order g-code always sends M3
    // (AppendToolStart never emits M4), so what matters here is which way the spindle ACTUALLY turns when
    // that M3 lands, not the label. FixedCW: M3 physically spins CW as expected (same as Bidirectional for
    // this purpose - the difference only matters to code that would otherwise offer M4 as a live option, see
    // SpindleControl). FixedCCW: M3 physically spins CCW - flips which raw orbit direction is climb.
    public enum SpindleDirectionCapability { Bidirectional, FixedCW, FixedCCW }

    // How to cut the parent toolpath's geometry. Carries no geometry of its own and no tool number.
    public class WorkOrderOperation
    {
        public WorkOrderOpKind Kind = WorkOrderOpKind.Contour;

        // Unchecked in the tree = authored but held back from Generate, so a subset can be run on its own
        // (the "I forgot the finishing passes" case - re-run just those without recutting the pocket).
        // Deliberately persisted: a held-back operation stays held back until it's checked again, rather
        // than quietly coming back on the next launch.
        public bool Enabled = true;

        // Which tool the Feeds and Speeds dialog was left on - the only place a tool is chosen. A CustomTool.Id
        // (see CustomTool.cs) - 0 is the factory-default "1/4" 2-flute endmill" seeded in Default-App.config.
        // Tool is the real identity (stable even across a rename - see CustomTool.Id's own comment); ToolName
        // is a save-time snapshot of that tool's Name, kept purely so the saved file is self-describing and
        // so WorkOrder.ReconcileToolIds can tell "Id still means the same tool" apart from "Id now means a
        // DIFFERENT tool" on load - see that method's own comment for why that distinction matters.
        public int Tool = 0;
        public string ToolName = string.Empty;

        public double BitDiameter = 6.35d;

        // Drill/Bore only: the hole this operation makes, concentric with the toolpath's center but NOT tied
        // to the geometry's own diameter - that's what lets one centerline carry a wide shallow counterbore
        // and a narrow through hole as two operations instead of two toolpaths whose X/Y have to be kept in
        // step by hand. Seeded from the geometry diameter when the operation is added.
        public double HoleDiameter = 6d;

        // How deep this operation cuts. Through takes its depth from the stock thickness instead, and is only
        // offered on the operations it means something for (Contour/Drill/Bore - see WorkOrderRules.SupportsThrough).
        public double TotalDepth = 10d;
        public bool Through = false;

        // Tabs hold a through CONTOUR's cutoff piece in place. Nothing else releases a piece that needs it.
        public double NumTabs = 4d, TabWidth = 6d, TabHeight = 1.5d;

        public double DepthOfCut = 2d;      // axial step per pass - milling operations
        public double Stepover = 40d;       // radial engagement, % of bit diameter - area clearing
        public double PeckDepth = 2d;       // Drill
        public bool DrillHss = false;       // Drill - false = brad point/twist (default), true = HSS
        public double BoreStepDown = 1d;    // Bore - depth gained per helical revolution

        public double WallStockToLeave = 0.3d;    // SideFinish
        public double FloorStockToLeave = 0.3d;   // BottomFinish
        public double ChamferDepth = 0.5d;        // Chamfer

        // Engrave - the WIDTH of the cut stroke in mm, which is what an operator can see and measure. The
        // plunge depth is derived from it and the tool's own included angle (see
        // WorkOrderCompiler.BuildEngrave), because depth is the number that produces the width rather than
        // the one anybody wants to specify: the same 0.8 mm line needs 0.40 mm of depth on a 90-degree bit
        // and 0.69 mm on a 60. Getting that backwards is how the same job cuts differently after a bit
        // change.
        public double EngraveWidth = 0.8d;        // Engrave
        // Countersink - the FINISHED diameter the operator wants (e.g. to seat a specific screw head), not a
        // raw plunge depth - WorkOrderCompiler.BuildCountersink converts it (depth = diameter / 2, same
        // 45-deg-per-side cone math as Chamfer's V-bit, just specified the other way around since a
        // countersink bit's whole point is being sized to a target diameter).
        public double CountersinkDiameter = 12.5d;   // Countersink

        public double Feed = 800d, PlungeFeed = 200d, SpindleRPM = 15000d, BitMaxRPM = 18000d;

        // Meaningless for Drill/Countersink (a straight on-center plunge has no path to reverse) - defaults
        // to Conventional, the safer choice on a machine with any backlash/flex (see the enum's own comment).
        public WorkOrderCutDirection Direction = WorkOrderCutDirection.Conventional;
    }

    // A named piece of geometry plus the operations that cut it.
    public class WorkOrderToolpath
    {
        public string Name = "Toolpath";
        public WorkOrderGeometryKind Geometry = WorkOrderGeometryKind.Circle;

        // Surface only: face the whole in-bounds machine travel envelope (spoilboard resurfacing) instead of
        // this toolpath's own Width/Depth/X/Y - which have no meaning here, since there's no fixture/WCS that
        // covers the whole envelope. Machine-referenced (G53) with its own fresh Z0 touch-off, borrowing G54
        // as scratch space and restoring the Work Order's own WCS afterward - see WorkOrderCompiler.BuildSurface.
        // WorkOrderRules.Validate warns if this isn't the work order's only enabled operation.
        public bool EntireSpoilboard = false;

        // Unchecked = the whole toolpath sits out of Generate, whatever its operations are set to. Their own
        // Enabled flags are left alone so re-checking the toolpath restores exactly what was set before.
        public bool Enabled = true;

        // Position: the point X/Y names is chosen by Anchor below - the shape's center by default (and for
        // Line, its midpoint, which has no corners to name).
        public double X = 0d, Y = 0d;

        // Which point of the shape X/Y actually refers to. Center is the default, so every .workorder saved
        // before this existed - where the element is simply absent and deserializes to the default - keeps
        // its exact previous meaning, and nothing needs migrating.
        //
        // Changing this RE-INTERPRETS X/Y rather than recomputing it: the shape moves, the numbers you typed
        // stay put. That is the behaviour asked for, and it is the one that makes the field useful for what
        // it is for - typing a corner coordinate read off the previous operation so a follow-on pass lines up
        // against a known edge, instead of doing the half-width arithmetic in your head every time.
        public WorkOrderAnchor Anchor = WorkOrderAnchor.Center;

        // Dimensions - which of these matter depends on Geometry.
        public double Length = 50d;      // Line
        public double Angle = 0d;        // Line - degrees from +X; a line without a direction is no line at all
        public double Diameter = 30d;    // Circle
        public double Width = 40d;       // Oval, Rect
        public double Depth = 25d;       // Oval, Rect (the Y dimension - "D" in the picker)
        public double Size = 30d;        // Square

        // Text only. CapHeight is the height of a capital letter in mm - the size an operator actually
        // means by "10 mm lettering" - and the baseline direction reuses Angle above, same meaning as it
        // has for a Line (degrees from +X). Multi-line text is a plain newline in Text.
        public string Text = "TEXT";
        public double CapHeight = 10d;

        // Pattern: the whole toolpath repeats at each instance position. The X/Y above is instance one, and
        // stays the anchor - a Grid grows from it, a Circular pattern orbits it.
        public WorkOrderPatternKind Pattern = WorkOrderPatternKind.None;
        public double Columns = 2d, RowSpacing = 50d, ColumnSpacing = 32d, Rows = 1d;   // Grid
        public double PatternCount = 6d, PatternRadius = 40d, PatternStartAngle = 0d, PatternArcSpan = 360d;   // Circular

        // Indirect only: the Name of another toolpath whose geometry and operations run here instead. A plain
        // name rather than an object reference or id because the model is otherwise entirely value-based (see
        // .workorder save/load) - the cost is that renaming the source breaks the link, which
        // WorkOrderRules.Validate flags rather than something the compiler can silently paper over.
        public string IndirectSource = null;

        public List<WorkOrderOperation> Operations = new List<WorkOrderOperation>();

        // Text is OPEN, like a Line. This matters more than it looks: IsClosed gates the operations a
        // toolpath is offered (Pocket and Bottom finish need an enclosed area) and whether a Contour gets
        // tabs. A stroke font's glyphs are pen strokes, not loops - there is no interior to clear - so
        // letting Text read as closed would have offered to pocket the inside of the letter "S".
        public bool IsClosed { get { return Geometry != WorkOrderGeometryKind.Line && Geometry != WorkOrderGeometryKind.Text; } }
        public bool IsIndirect { get { return Geometry == WorkOrderGeometryKind.Indirect; } }

        // X/Y resolved to the shape's CENTER, whatever Anchor names. Everything downstream - the compiler's
        // geometry builders, the pattern expansion, the stock-canvas preview - is written in terms of a
        // center, so the anchor is converted away exactly once, here. Adding a new consumer of the position
        // that reads X/Y directly would silently ignore the anchor; read these instead.
        public double CenterX { get { return X + HalfWidth * AnchorSignX; } }
        public double CenterY { get { return Y + HalfDepth * AnchorSignY; } }

        // Half-extents of the bounding box, per geometry. A Line has none (it is positioned by its midpoint
        // and has no corners), and Indirect carries no geometry of its own, so both stay put under any
        // anchor rather than being nudged by a dimension that means nothing to them.
        private double HalfWidth
        {
            get
            {
                switch (Geometry)
                {
                    case WorkOrderGeometryKind.Circle: return Diameter / 2d;
                    case WorkOrderGeometryKind.Square: return Size / 2d;
                    case WorkOrderGeometryKind.Oval:
                    case WorkOrderGeometryKind.Rect:
                    case WorkOrderGeometryKind.Surface: return Width / 2d;
                    // Text has no Width/Depth of its own - its extent is whatever the string renders to at
                    // this cap height, so ask the font. Measured on the UNROTATED text deliberately: the
                    // baseline angle rotates the result about the anchor, and taking extents after rotation
                    // would make the anchor drift sideways every time the text was edited.
                    case WorkOrderGeometryKind.Text: return CNC.Core.StrokeFont.Measure(Text, CapHeight).X / 2d;
                    default: return 0d;
                }
            }
        }

        private double HalfDepth
        {
            get
            {
                switch (Geometry)
                {
                    case WorkOrderGeometryKind.Circle: return Diameter / 2d;
                    case WorkOrderGeometryKind.Square: return Size / 2d;
                    case WorkOrderGeometryKind.Oval:
                    case WorkOrderGeometryKind.Rect:
                    case WorkOrderGeometryKind.Surface: return Depth / 2d;
                    case WorkOrderGeometryKind.Text: return CNC.Core.StrokeFont.Measure(Text, CapHeight).Y / 2d;
                    default: return 0d;
                }
            }
        }

        // Which way the center lies FROM the named corner: a front-left anchor has its center up and to the
        // right (+X, +Y), and so on round. Center yields 0 and so is exactly a no-op.
        private double AnchorSignX
        {
            get
            {
                switch (Anchor)
                {
                    case WorkOrderAnchor.FrontLeft:
                    case WorkOrderAnchor.BackLeft: return 1d;
                    case WorkOrderAnchor.FrontRight:
                    case WorkOrderAnchor.BackRight: return -1d;
                    default: return 0d;
                }
            }
        }

        private double AnchorSignY
        {
            get
            {
                switch (Anchor)
                {
                    case WorkOrderAnchor.FrontLeft:
                    case WorkOrderAnchor.FrontRight: return 1d;
                    case WorkOrderAnchor.BackLeft:
                    case WorkOrderAnchor.BackRight: return -1d;
                    default: return 0d;
                }
            }
        }

        // Every position this toolpath's geometry is cut at, instance one first. A None pattern yields exactly
        // the anchor point, so callers never need to special-case the unpatterned toolpath.
        public IEnumerable<double[]> PatternPositions()
        {
            switch (Pattern)
            {
                case WorkOrderPatternKind.Grid:
                {
                    int cols = Math.Max(1, (int)Math.Round(Columns));
                    int rows = Math.Max(1, (int)Math.Round(Rows));
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                            yield return new[] { CenterX + c * ColumnSpacing, CenterY + r * RowSpacing };
                    break;
                }
                case WorkOrderPatternKind.Circular:
                {
                    int n = Math.Max(1, (int)Math.Round(PatternCount));
                    // A full turn divides evenly by count (the last instance would otherwise land back on the
                    // first); a partial arc spreads across it inclusive of both ends, which is what "from here
                    // to there" means for a 3-hole 90-degree arc.
                    bool fullCircle = Math.Abs(Math.Abs(PatternArcSpan) - 360d) < 1e-9;
                    double stepDeg = n <= 1 ? 0d : (fullCircle ? PatternArcSpan / n : PatternArcSpan / (n - 1));
                    for (int i = 0; i < n; i++)
                    {
                        double a = (PatternStartAngle + i * stepDeg) * Math.PI / 180d;
                        yield return new[] { CenterX + PatternRadius * Math.Cos(a), CenterY + PatternRadius * Math.Sin(a) };
                    }
                    break;
                }
                default:
                    yield return new[] { CenterX, CenterY };
                    break;
            }
        }

        public int InstanceCount { get { return PatternPositions().Count(); } }

        // The smallest across-dimension the shape has - bounds what bit can cut it. A line is only ever as
        // wide as the bit itself, so nothing constrains it.
        public double MinSpan
        {
            get
            {
                switch (Geometry)
                {
                    case WorkOrderGeometryKind.Line: return double.MaxValue;
                    // Nothing constrains the cutter: a V-bit engraves with its TIP, and the stroke width is
                    // chosen by depth rather than limited by the bit's nominal diameter.
                    case WorkOrderGeometryKind.Text: return double.MaxValue;
                    case WorkOrderGeometryKind.Circle: return Diameter;
                    case WorkOrderGeometryKind.Square: return Size;
                    default: return Math.Min(Width, Depth);
                }
            }
        }
    }

    public class WorkOrder
    {
        public List<WorkOrderToolpath> Toolpaths = new List<WorkOrderToolpath>();

        // Which WCS slot this work order's origin/TLO reference lives in - Setup is the only vehicle that
        // ever writes a real origin into a WCS slot (StartJobConfig.Section.Wcs, same 1-6 = G54-G59 range -
        // see StartJobView.WcsCode). 0 = FOLLOW Setup's current selection live, resolved fresh every time a
        // program is built (WorkOrderCompiler.ResolveWcs) rather than cached - the default, and what an
        // already-saved work order that predates this field gets for free (XmlSerializer leaves an absent
        // element at its C# default). 1-6 = PINNED to that specific slot regardless of what Setup is set to
        // right now - the operator's explicit override (cbxWcs on the Work Order tab) for the case Setup gets
        // reused for a different job on a different WCS after this one's origin was actually established.
        public int Wcs = 0;

        // Emit operations grouped by tool rather than strictly in tree order, to cut down tool changes. Off by
        // default: the default program order is exactly what the tree shows, which is easier to reason about
        // when something cuts wrong. Grouping NEVER reorders operations within a toolpath - see
        // WorkOrderCompiler.Schedule.
        public bool GroupByTool = false;

        // Assume the program's FIRST tool is already in the spindle, so its M6 is left out. The usual case:
        // that tool was just used to establish the Setup reference, so making tc.macro rapid to G30, prompt for
        // a swap and re-probe it is pure ceremony.
        //
        // The cost of being wrong is real though, and it isn't just the wrong cutter: the M6 is also what
        // re-probes the toolsetter and applies the tool length offset, so a skipped first change runs on
        // whatever TLO is currently in force. Hence off by default and stated in the program header.
        public bool SkipFirstToolChange = false;

        // The single definition of "what Generate will actually cut" - a toolpath contributes only if it is
        // itself enabled, and only the operations under it that are enabled. Everything downstream (the
        // scheduler, the tool declarations, validation, the tool-change count, the summary line) goes through
        // here, so a held-back operation can't leak into one of them and not the others.
        public IEnumerable<WorkOrderOperation> EnabledOperations(WorkOrderToolpath tp)
        {
            return tp.Enabled ? tp.Operations.Where(o => o.Enabled) : Enumerable.Empty<WorkOrderOperation>();
        }

        public int EnabledOperationCount { get { return Toolpaths.Sum(t => EnabledOperations(t).Count()); } }
        public int TotalOperationCount { get { return Toolpaths.Sum(t => t.Operations.Count); } }
        public bool AnyHeldBack { get { return EnabledOperationCount != TotalOperationCount; } }

        private IEnumerable<WorkOrderOperation> AllOperations => Toolpaths.SelectMany(t => t.Operations);

        // Refreshes every operation's ToolName snapshot from its CURRENT tool - called right before every
        // save (see AppConfig.RegisterSections' OddJobsWorkOrder registration) so the saved file always
        // reflects whatever the tool was actually named at save time, regardless of which UI path set
        // op.Tool (NewOperation, the Feeds and Speeds dialog, a countersink diameter edit, ...). A tool that
        // no longer resolves (already-invalidated by ReconcileToolIds, or deleted since this operation was
        // last touched) keeps its last-known name rather than being blanked - that's the name ReconcileToolIds
        // needs to have any chance of finding it again on a future load.
        public void SyncToolNames()
        {
            foreach (var op in AllOperations)
            {
                var name = CustomTools.Find(op.Tool)?.Name;
                if (name != null)
                    op.ToolName = name;
            }
        }

        // Runs once right after load (AppConfig.RegisterSections' OddJobsWorkOrder registration) to catch
        // the case a bare numeric Tool can't: the Id on disk now resolves to a DIFFERENT tool than what was
        // actually saved (e.g. this file was authored against a different install's tool list, or a tool was
        // deleted and a new one happened to land on the same Id later). Trusting the number alone there would
        // silently run the wrong bit; this compares it against the saved ToolName and repairs or invalidates
        // as needed. Returns whether anything changed (the caller resaves so the fix - and the ToolName
        // backfill for a pre-existing file that predates this field - becomes durable, same idiom as
        // AppConfig._migratedFormat).
        public bool ReconcileToolIds()
        {
            bool changed = false;
            var entries = CustomTools.SectionConfig?.Entries ?? new List<CustomTool>();

            foreach (var op in AllOperations)
            {
                var current = CustomTools.Find(op.Tool);

                if (string.IsNullOrEmpty(op.ToolName))
                {
                    // Pre-existing file from before ToolName existed (or a never-yet-saved operation) - no
                    // saved name to compare the Id against, so there's nothing to reconcile. Just backfill
                    // the name if resolvable, so the file upgrades to the self-describing format next save.
                    if (current != null)
                    {
                        op.ToolName = current.Name;
                        changed = true;
                    }
                    continue;
                }

                if (current != null && current.Name == op.ToolName)
                    continue;   // Id still means the same tool it did when this was saved - all good.

                // Id is stale (deleted) or now means something ELSE - look the tool up by its saved name
                // instead. Self-heal only on an unambiguous single match.
                var byName = entries.Where(t => t.Name == op.ToolName).ToList();
                if (byName.Count == 1)
                {
                    op.Tool = byName[0].Id;
                    changed = true;
                }
                else if (op.Tool != -1)
                {
                    // No (or ambiguous) match by name - do NOT silently keep using `current` (a tool with a
                    // DIFFERENT name than what was actually saved). Invalidate instead, so
                    // WorkOrderView.ParameterWarnings flags it and the operator picks a substitute, the same
                    // path already used for a tool deleted outright.
                    op.Tool = -1;
                    changed = true;
                }
            }

            return changed;
        }
    }

    public static class WorkOrderRules
    {
        public static readonly WorkOrderGeometryKind[] AllGeometries =
            { WorkOrderGeometryKind.Line, WorkOrderGeometryKind.Circle, WorkOrderGeometryKind.Oval,
              WorkOrderGeometryKind.Square, WorkOrderGeometryKind.Rect, WorkOrderGeometryKind.Surface,
              WorkOrderGeometryKind.Indirect };

        public static readonly WorkOrderAnchor[] AllAnchors =
            { WorkOrderAnchor.Center, WorkOrderAnchor.FrontLeft, WorkOrderAnchor.FrontRight,
              WorkOrderAnchor.BackLeft, WorkOrderAnchor.BackRight };

        #region Standard drill sizes

        // The sizes a shop drill index actually holds: metric 1-13 mm in 0.5 mm steps, plus the common
        // imperial fractions. A hole matching one of these can be DRILLED with a bit that size; anything else
        // has to be BORED helically with a smaller end mill (see AvailableOperations).
        private static readonly List<KeyValuePair<double, string>> StandardDrills = BuildDrillList();

        private static List<KeyValuePair<double, string>> BuildDrillList()
        {
            var list = new List<KeyValuePair<double, string>>();
            for (double d = 1.0d; d <= 13.0d + 1e-9; d += 0.5d)
                list.Add(new KeyValuePair<double, string>(d, string.Format(CultureInfo.InvariantCulture, "{0:0.#} mm", d)));

            list.Add(new KeyValuePair<double, string>(1.588d, "1/16\""));
            list.Add(new KeyValuePair<double, string>(3.175d, "1/8\""));
            list.Add(new KeyValuePair<double, string>(4.763d, "3/16\""));
            list.Add(new KeyValuePair<double, string>(6.35d, "1/4\""));
            list.Add(new KeyValuePair<double, string>(7.938d, "5/16\""));
            list.Add(new KeyValuePair<double, string>(9.525d, "3/8\""));
            list.Add(new KeyValuePair<double, string>(12.7d, "1/2\""));

            return list.OrderBy(e => e.Key).ToList();
        }

        // Tolerance for calling a diameter "a standard size" - tight enough that 6.4 doesn't silently become a
        // 1/4" bit, loose enough to absorb the rounding in an imperial-to-metric conversion.
        private const double DrillMatchToleranceMm = 0.05d;

        public static bool TryMatchDrill(double diameterMm, out string name)
        {
            foreach (var entry in StandardDrills)
                if (Math.Abs(entry.Key - diameterMm) <= DrillMatchToleranceMm)
                {
                    name = entry.Value;
                    return true;
                }
            name = null;
            return false;
        }

        // The hole a toolpath's geometry describes - only a circle has one.
        public static bool TryHoleDiameter(WorkOrderToolpath tp, out double diameter)
        {
            diameter = tp.Diameter;
            return tp.Geometry == WorkOrderGeometryKind.Circle;
        }

        #endregion

        // The toolpath an Indirect one actually borrows geometry and operations from, or null if the reference
        // is broken (missing, renamed, or itself Indirect - see Validate). Everything else resolves to itself,
        // so a caller that always goes through this method never needs to special-case "not Indirect".
        public static WorkOrderToolpath ResolveIndirectSource(WorkOrder wo, WorkOrderToolpath tp)
        {
            if (!tp.IsIndirect)
                return tp;
            var source = wo.Toolpaths.FirstOrDefault(t => string.Equals(t.Name, tp.IndirectSource, StringComparison.OrdinalIgnoreCase));
            return source != null && !source.IsIndirect ? source : null;
        }

        public static readonly WorkOrderPatternKind[] AllPatterns =
            { WorkOrderPatternKind.None, WorkOrderPatternKind.Grid, WorkOrderPatternKind.Circular };

        public static string PatternLabel(WorkOrderPatternKind kind)
        {
            switch (kind)
            {
                case WorkOrderPatternKind.Grid: return "Grid (columns x rows)";
                case WorkOrderPatternKind.Circular: return "Circular (bolt circle)";
                default: return "None (single)";
            }
        }

        public static string GeometryLabel(WorkOrderGeometryKind kind)
        {
            switch (kind)
            {
                case WorkOrderGeometryKind.Line: return "Line (length)";
                case WorkOrderGeometryKind.Circle: return "Circle (diameter)";
                case WorkOrderGeometryKind.Oval: return "Oval (width, depth)";
                case WorkOrderGeometryKind.Square: return "Square (size)";
                case WorkOrderGeometryKind.Rect: return "Rectangle (width, depth)";
                case WorkOrderGeometryKind.Surface: return "Surface (width, depth) - face the whole area";
                case WorkOrderGeometryKind.Indirect: return "Indirect (repeat another toolpath here)";
                case WorkOrderGeometryKind.Text: return "Text (engrave with a V-bit)";
                default: return kind.ToString();
            }
        }

        // Front is -Y, back is +Y - stated in the label rather than left to be worked out, because "front"
        // is only obvious once you know which way the machine's Y runs.
        public static string AnchorLabel(WorkOrderAnchor a)
        {
            switch (a)
            {
                case WorkOrderAnchor.Center: return "Center";
                case WorkOrderAnchor.FrontLeft: return "Front left corner (-X, -Y)";
                case WorkOrderAnchor.FrontRight: return "Front right corner (+X, -Y)";
                case WorkOrderAnchor.BackLeft: return "Back left corner (-X, +Y)";
                case WorkOrderAnchor.BackRight: return "Back right corner (+X, +Y)";
                default: return a.ToString();
            }
        }

        public static string OpLabel(WorkOrderOpKind kind)
        {
            switch (kind)
            {
                case WorkOrderOpKind.Engrave: return "Engrave (V-bit, single pass along the strokes)";
                case WorkOrderOpKind.Pocket: return "Pocket (clear the enclosed area)";
                case WorkOrderOpKind.Contour: return "Contour (follow the outline)";
                case WorkOrderOpKind.Drill: return "Drill (straight peck)";
                case WorkOrderOpKind.Bore: return "Bore (helical)";
                case WorkOrderOpKind.SideFinish: return "Side finishing pass";
                case WorkOrderOpKind.BottomFinish: return "Bottom finishing pass";
                case WorkOrderOpKind.Chamfer: return "Chamfer the top edge";
                case WorkOrderOpKind.Countersink: return "Countersink (plunge to a target diameter)";
                case WorkOrderOpKind.Surface: return "Surface (face the whole area)";
                default: return kind.ToString();
            }
        }

        // Through only means something where the point is to get to the other side: cutting a piece out
        // (Contour) or making a hole (Drill/Bore). A pocket that went through would have no floor left to
        // clear, and a finishing pass or chamfer just follows whatever the roughing operation did.
        public static bool SupportsThrough(WorkOrderOpKind kind)
        {
            return kind == WorkOrderOpKind.Contour || kind == WorkOrderOpKind.Drill || kind == WorkOrderOpKind.Bore;
        }

        // Only a through cutout releases a piece that has to be held in place.
        public static bool SupportsTabs(WorkOrderToolpath tp, WorkOrderOperation op)
        {
            return op.Kind == WorkOrderOpKind.Contour && op.Through && tp.IsClosed;
        }

        // The operation whose depth the finishing passes follow.
        public static WorkOrderOperation RoughingOp(WorkOrderToolpath tp)
        {
            return tp.Operations.FirstOrDefault(o => o.Kind == WorkOrderOpKind.Pocket
                                                  || o.Kind == WorkOrderOpKind.Contour
                                                  || o.Kind == WorkOrderOpKind.Bore);
        }

        // Drill and Bore are diameter-specific, so several of them on one centerline is a normal thing to want
        // (a counterbore plus a through hole, a pilot then a finish size). Every other kind would be
        // meaningless twice over on the same geometry.
        public static bool IsRepeatable(WorkOrderOpKind kind)
        {
            return kind == WorkOrderOpKind.Drill || kind == WorkOrderOpKind.Bore;
        }

        // Pocket and Contour are two answers to the same question - clear the area, or just follow its
        // outline - so having both on one geometry is contradictory, not additive.
        private static bool HasRoughing(WorkOrderToolpath tp)
        {
            return tp.Operations.Any(o => o.Kind == WorkOrderOpKind.Pocket || o.Kind == WorkOrderOpKind.Contour);
        }

        // Which operations make sense for a toolpath's geometry:
        //  - an OPEN loop has no enclosed area to clear and no floor to finish, so Pocket/BottomFinish are out;
        //  - Pocket/Contour are mutually exclusive, and neither is offered once either is present;
        //  - Drill and Bore need a round hole, so they're Circle-only, and each carries its own diameter.
        public static IEnumerable<WorkOrderOpKind> AvailableOperations(WorkOrderToolpath tp)
        {
            // Indirect carries no operations of its own - it runs whatever the source toolpath currently has.
            if (tp.IsIndirect)
                yield break;

            // Surface faces the whole Width x Depth area - it isn't cut relative to an enclosed shape, so none
            // of the outline-based operations (pocket, contour, drill, finishing, chamfer...) apply to it.
            if (tp.Geometry == WorkOrderGeometryKind.Surface)
            {
                yield return WorkOrderOpKind.Surface;
                yield break;
            }

            // Text is engraved and nothing else. Its glyphs are pen strokes along a centreline, so there is
            // no outline to contour, no interior to pocket and no edge to chamfer - every other operation
            // here would be tracing a path that does not describe the letter's shape.
            if (tp.Geometry == WorkOrderGeometryKind.Text)
            {
                yield return WorkOrderOpKind.Engrave;
                yield break;
            }

            if (!HasRoughing(tp))
            {
                if (tp.IsClosed)
                    yield return WorkOrderOpKind.Pocket;
                yield return WorkOrderOpKind.Contour;
            }

            if (tp.Geometry == WorkOrderGeometryKind.Circle)
            {
                yield return WorkOrderOpKind.Drill;
                yield return WorkOrderOpKind.Bore;
                // Plunges a countersink bit straight down the hole's centerline - only makes sense on a
                // round hole, unlike Chamfer's outline trace which works on any shape.
                yield return WorkOrderOpKind.Countersink;
            }

            yield return WorkOrderOpKind.SideFinish;
            if (tp.IsClosed)
                yield return WorkOrderOpKind.BottomFinish;
            yield return WorkOrderOpKind.Chamfer;
        }

        // What the picker offers for an existing toolpath: everything its geometry allows, minus the
        // once-only kinds it already has.
        public static IEnumerable<WorkOrderOpKind> OfferableOperations(WorkOrderToolpath tp)
        {
            var present = tp.Operations.Select(o => o.Kind).ToList();
            return AvailableOperations(tp).Where(k => IsRepeatable(k) || !present.Contains(k));
        }

        // Whether boring this hole with this bit needs more than one helical pass. A single helix at the final
        // radius only reaches the centre when the bit is at least half the hole; below that the bore steps
        // outward through several radii instead (see WorkOrderCompiler.BoreRadii) - not a limitation, just
        // worth surfacing since it changes how long the operation takes.
        public static bool NeedsSteppedBore(double holeDiameter, double bitDiameter)
        {
            return (holeDiameter - bitDiameter) / 2d > bitDiameter / 2d + 1e-9;
        }

        // Bogus combinations - flagged on Generate, not merely filtered out of the picker.
        public static List<string> Validate(WorkOrder wo)
        {
            var warnings = new List<string>();

            // A rotated WCS (G10 L2 R, grblHAL ROTATION_ENABLE) is a real hazard here: WorkOrderCompiler
            // computes every toolpath's X/Y assuming the active WCS's axes are aligned with the machine's
            // physical axes - it has no rotation compensation anywhere in it. This used to be prevented
            // structurally (Odd Jobs' Setup was pinned to its own G59, always left unrotated) - now that Setup
            // is one shared thing with a free WCS choice (job-flow unification, 2026-07-31), this check is the
            // ONLY thing standing between a rotation set via ordinary Setup use and a Work Order silently
            // cutting skewed. Checked once for the whole work order, not per-toolpath. Resolves THIS work
            // order's own effective WCS (WorkOrderCompiler.ResolveWcs - follows Setup live if wo.Wcs is 0,
            // otherwise the pinned slot) rather than whatever happens to be active on the DRO right now -
            // see WorkOrder.Wcs's own comment on why those can differ.
            string wcs = WorkOrderCompiler.WcsCode(wo);
            var wcsData = GrblWorkParameters.GetCoordinateSystem(wcs);
            if (wcsData != null && Math.Abs(wcsData.Rotation) > 1e-6)
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} has a {1:0.###} deg rotation set - Work Order toolpaths don't account for WCS rotation and would cut skewed. Clear the rotation (or switch to an unrotated WCS) before generating.",
                    wcs, wcsData.Rotation));

            foreach (var tp in wo.Toolpaths)
            {
                string label = tp.Name + ": ";

                if (tp.IsIndirect)
                {
                    var source = wo.Toolpaths.FirstOrDefault(t => string.Equals(t.Name, tp.IndirectSource, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrEmpty(tp.IndirectSource))
                        warnings.Add(label + "no source toolpath selected.");
                    else if (string.Equals(tp.IndirectSource, tp.Name, StringComparison.OrdinalIgnoreCase))
                        warnings.Add(label + "can't reference itself.");
                    else if (source == null)
                        warnings.Add(string.Format("{0}source toolpath \"{1}\" no longer exists - it was renamed or removed.", label, tp.IndirectSource));
                    else if (source.IsIndirect)
                        warnings.Add(label + "an Indirect toolpath can't point at another Indirect toolpath.");
                    else if (source.Operations.Count == 0)
                        warnings.Add(string.Format("{0}source toolpath \"{1}\" has no operations of its own yet.", label, tp.IndirectSource));
                    continue;
                }

                if (tp.Operations.Count == 0)
                {
                    warnings.Add(label + "no operations - add at least one.");
                    continue;
                }

                var allowed = AvailableOperations(tp).ToList();
                foreach (var op in tp.Operations)
                {
                    if (!allowed.Contains(op.Kind) && !(op.Kind == WorkOrderOpKind.Pocket || op.Kind == WorkOrderOpKind.Contour))
                        warnings.Add(label + OpLabel(op.Kind) + " is not possible on this geometry.");

                    // A drill only exists in stock sizes - anything else has to be bored out.
                    if (op.Kind == WorkOrderOpKind.Drill && !TryMatchDrill(op.HoleDiameter, out _))
                        warnings.Add(string.Format("{0}Ø{1:0.###} mm is not a standard drill size - use a Bore operation instead.", label, op.HoleDiameter));
                }

                if (tp.Operations.Count(o => o.Kind == WorkOrderOpKind.Pocket || o.Kind == WorkOrderOpKind.Contour) > 1)
                    warnings.Add(label + "Pocket and Contour are alternatives - keep only one.");

                // A finishing pass only has meaning alongside the roughing operation that left stock for it.
                if (RoughingOp(tp) == null && tp.Operations.Any(o => o.Kind == WorkOrderOpKind.SideFinish || o.Kind == WorkOrderOpKind.BottomFinish))
                    warnings.Add(label + "a finishing pass needs a Pocket, Contour or Bore operation to leave stock for it.");

                foreach (var kind in tp.Operations.GroupBy(o => o.Kind).Where(g => g.Count() > 1 && !IsRepeatable(g.Key)).Select(g => g.Key))
                    warnings.Add(label + OpLabel(kind) + " appears more than once.");

                // Entire-spoilboard touches off its own Z0 and temporarily borrows G54, machine-referenced -
                // it resets the work origin, which would corrupt any operation that runs after it in the same
                // program. Safe only as the work order's one and only enabled operation.
                if (tp.EntireSpoilboard && wo.EnabledOperations(tp).Any() && wo.EnabledOperationCount > 1)
                    warnings.Add(label + "Entire spoilboard resets the work origin - it must be the only enabled operation in this work order.");
            }

            // Everything above deliberately checks what's AUTHORED, not what's enabled: holding an operation
            // back shouldn't hide a mistake in it, and a subset run of just the finishing passes still wants
            // the "needs a roughing op" rule satisfied by the roughing op sitting there unchecked.
            if (wo.TotalOperationCount > 0 && wo.EnabledOperationCount == 0)
                warnings.Add("Every operation is unchecked - nothing to generate.");

            return warnings;
        }

        public static string DescribeGeometry(WorkOrderToolpath tp)
        {
            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Line:
                    return string.Format("line {0:0.#} mm @ {1:0}°", tp.Length, tp.Angle);
                case WorkOrderGeometryKind.Circle:
                    return string.Format("circle Ø{0:0.###}", tp.Diameter);
                case WorkOrderGeometryKind.Oval:
                    return string.Format("oval {0:0.#}x{1:0.#}", tp.Width, tp.Depth);
                case WorkOrderGeometryKind.Square:
                    return string.Format("square {0:0.#}", tp.Size);
                case WorkOrderGeometryKind.Surface:
                    return string.Format("surface {0:0.#}x{1:0.#}", tp.Width, tp.Depth);
                case WorkOrderGeometryKind.Indirect:
                    return string.IsNullOrEmpty(tp.IndirectSource)
                        ? "indirect (no source selected)"
                        : string.Format("indirect -> {0}", tp.IndirectSource);
                default:
                    return string.Format("rect {0:0.#}x{1:0.#}", tp.Width, tp.Depth);
            }
        }

        public static string Summarize(WorkOrderToolpath tp)
        {
            int n = tp.InstanceCount;
            string pattern = n > 1
                ? string.Format(", {0} {1}", n, tp.Pattern == WorkOrderPatternKind.Grid ? "in a grid" : "on a bolt circle")
                : string.Empty;
            return string.Format("{0}  ({1} @ X{2:0.0} Y{3:0.0}{4})", tp.Name, DescribeGeometry(tp), tp.X, tp.Y, pattern);
        }

        public static string Summarize(WorkOrderOperation op)
        {
            string depth = op.Through ? "through" : string.Format("{0:0.#} mm deep", op.TotalDepth);
            switch (op.Kind)
            {
                case WorkOrderOpKind.Pocket:
                    return string.Format("Pocket - {0}, Ø{1:0.##} bit, {2:0}% stepover", depth, op.BitDiameter, op.Stepover);
                case WorkOrderOpKind.Contour:
                    return string.Format("Contour - {0}, Ø{1:0.##} bit{2}", depth, op.BitDiameter,
                        op.Through ? string.Format(", {0:0} tabs", op.NumTabs) : string.Empty);
                case WorkOrderOpKind.Drill:
                    return string.Format("Drill Ø{0:0.###} - {1}, {2:0.#} mm peck", op.HoleDiameter, depth, op.PeckDepth);
                case WorkOrderOpKind.Bore:
                    return string.Format("Bore Ø{0:0.###} - {1}, Ø{2:0.##} bit", op.HoleDiameter, depth, op.BitDiameter);
                case WorkOrderOpKind.SideFinish:
                    return string.Format("Side finish - Ø{0:0.##}, leaves {1:0.0##} mm", op.BitDiameter, op.WallStockToLeave);
                case WorkOrderOpKind.BottomFinish:
                    return string.Format("Bottom finish - Ø{0:0.##}, leaves {1:0.0##} mm", op.BitDiameter, op.FloorStockToLeave);
                case WorkOrderOpKind.Chamfer:
                    return string.Format("Chamfer - {0:0.0#} mm deep", op.ChamferDepth);
                case WorkOrderOpKind.Countersink:
                    return string.Format("Countersink - Ø{0:0.##} mm target", op.CountersinkDiameter);
                case WorkOrderOpKind.Surface:
                    return string.Format("Surface - {0}, Ø{1:0.##} bit, {2:0}% stepover", depth, op.BitDiameter, op.Stepover);
                default:
                    return op.Kind.ToString();
            }
        }
    }
}
