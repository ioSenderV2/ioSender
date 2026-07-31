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

namespace CNC.Controls
{
    // Every geometry this tab can handle. Line is the only OPEN one - the rest are closed loops.
    public enum WorkOrderGeometryKind { Line, Circle, Oval, Square, Rect }

    // Repeats a whole toolpath - geometry AND every operation on it - at a set of offsets.
    public enum WorkOrderPatternKind { None, Grid, Circular }

    public enum WorkOrderOpKind { Pocket, Contour, Drill, Bore, SideFinish, BottomFinish, Chamfer, Countersink }

    // How to cut the parent toolpath's geometry. Carries no geometry of its own and no tool number.
    public class WorkOrderOperation
    {
        public WorkOrderOpKind Kind = WorkOrderOpKind.Contour;

        // Unchecked in the tree = authored but held back from Generate, so a subset can be run on its own
        // (the "I forgot the finishing passes" case - re-run just those without recutting the pocket).
        // Deliberately persisted: a held-back operation stays held back until it's checked again, rather
        // than quietly coming back on the next launch.
        public bool Enabled = true;

        // Which tool the Feeds and Speeds dialog was left on - the only place a tool is chosen.
        public int Tool = (int)OddJobsTool.EndMill2Flute;

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
        // Countersink - the FINISHED diameter the operator wants (e.g. to seat a specific screw head), not a
        // raw plunge depth - WorkOrderCompiler.BuildCountersink converts it (depth = diameter / 2, same
        // 45-deg-per-side cone math as Chamfer's V-bit, just specified the other way around since a
        // countersink bit's whole point is being sized to a target diameter).
        public double CountersinkDiameter = 12.5d;   // Countersink

        public double Feed = 800d, PlungeFeed = 200d, SpindleRPM = 15000d, BitMaxRPM = 18000d;
    }

    // A named piece of geometry plus the operations that cut it.
    public class WorkOrderToolpath
    {
        public string Name = "Toolpath";
        public WorkOrderGeometryKind Geometry = WorkOrderGeometryKind.Circle;

        // Unchecked = the whole toolpath sits out of Generate, whatever its operations are set to. Their own
        // Enabled flags are left alone so re-checking the toolpath restores exactly what was set before.
        public bool Enabled = true;

        // Position: the shape's center (for Line, its midpoint).
        public double X = 0d, Y = 0d;

        // Dimensions - which of these matter depends on Geometry.
        public double Length = 50d;      // Line
        public double Angle = 0d;        // Line - degrees from +X; a line without a direction is no line at all
        public double Diameter = 30d;    // Circle
        public double Width = 40d;       // Oval, Rect
        public double Depth = 25d;       // Oval, Rect (the Y dimension - "D" in the picker)
        public double Size = 30d;        // Square

        // Pattern: the whole toolpath repeats at each instance position. The X/Y above is instance one, and
        // stays the anchor - a Grid grows from it, a Circular pattern orbits it.
        public WorkOrderPatternKind Pattern = WorkOrderPatternKind.None;
        public double Columns = 2d, RowSpacing = 50d, ColumnSpacing = 32d, Rows = 1d;   // Grid
        public double PatternCount = 6d, PatternRadius = 40d, PatternStartAngle = 0d, PatternArcSpan = 360d;   // Circular

        public List<WorkOrderOperation> Operations = new List<WorkOrderOperation>();

        public bool IsClosed { get { return Geometry != WorkOrderGeometryKind.Line; } }

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
                            yield return new[] { X + c * ColumnSpacing, Y + r * RowSpacing };
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
                        yield return new[] { X + PatternRadius * Math.Cos(a), Y + PatternRadius * Math.Sin(a) };
                    }
                    break;
                }
                default:
                    yield return new[] { X, Y };
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
    }

    public static class WorkOrderRules
    {
        public static readonly WorkOrderGeometryKind[] AllGeometries =
            { WorkOrderGeometryKind.Line, WorkOrderGeometryKind.Circle, WorkOrderGeometryKind.Oval,
              WorkOrderGeometryKind.Square, WorkOrderGeometryKind.Rect };

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
                default: return kind.ToString();
            }
        }

        public static string OpLabel(WorkOrderOpKind kind)
        {
            switch (kind)
            {
                case WorkOrderOpKind.Pocket: return "Pocket (clear the enclosed area)";
                case WorkOrderOpKind.Contour: return "Contour (follow the outline)";
                case WorkOrderOpKind.Drill: return "Drill (straight peck)";
                case WorkOrderOpKind.Bore: return "Bore (helical)";
                case WorkOrderOpKind.SideFinish: return "Side finishing pass";
                case WorkOrderOpKind.BottomFinish: return "Bottom finishing pass";
                case WorkOrderOpKind.Chamfer: return "Chamfer the top edge";
                case WorkOrderOpKind.Countersink: return "Countersink (plunge to a target diameter)";
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
            foreach (var tp in wo.Toolpaths)
            {
                string label = tp.Name + ": ";

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
                default:
                    return op.Kind.ToString();
            }
        }
    }
}
