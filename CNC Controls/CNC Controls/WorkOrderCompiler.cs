/*
 * WorkOrderCompiler.cs - part of CNC Controls library
 *
 * Compiles an Odd Jobs WorkOrder (toolpaths -> operations, see WorkOrderModel.cs) into ONE merged g-code
 * program: a single PREREQ/units/G59 header, one (TOOL ..) declaration per distinct tool used anywhere in the
 * work order, then each operation's own (TOOLPATH ..)-wrapped section in the order the operator composed
 * them, then a single M30. This replaced the five old per-wizard BuildProgram() methods, each of which
 * emitted a complete standalone program of its own.
 *
 * The per-operation codegen is ported from those wizards - notably PocketWizard's hardware-verified behaviour
 * (2026-07-27): interior clearing rings center-outward (an outline-only pass leaves the middle uncut),
 * rapid-to-already-open-Z then a short feed plunge (feeding the whole way from safe-Z is needlessly slow),
 * whole-floor coverage on a bottom-finish pass (not just a wall lap), and tabs emitted by whichever operation
 * is the last one to actually reach true depth.
 *
 * Tool numbers are derived from the tool each operation was left on in the Feeds and Speeds dialog (see
 * ToolNumberFor) - there is no per-operation tool-number field to get out of step with it.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNC.Controls
{
    public static class WorkOrderCompiler
    {
        private const double ThroughOvercutMm = 0.5d;
        private const double SegmentMm = 2d;
        private const int CircleSegments = 72;
        // How close above the target Z a rapid may get before handing off to a feed-rate plunge - small
        // enough to be a negligible extra cut, big enough for the feed move to settle in.
        private const double PlungeClearanceMm = 1d;

        private static string F(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
        private static string N(double v) { return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture); }
        private static string XY(double[] p) { return "X" + F(p[0]) + " Y" + F(p[1]); }

        private static double StockThickness() { return StartJobConfig.Section?.Thickness ?? 0d; }
        private static double SafeZ() { return StartJobConfig.Section?.SafeZ > 0d ? StartJobConfig.Section.SafeZ : 20d; }

        private static double Rpm(WorkOrderOperation op) { return op.SpindleRPM > 0d ? op.SpindleRPM : 0.70d * op.BitMaxRPM; }

        // How deep an operation actually cuts. A through cut goes past the stock's underside by a small overcut
        // so it actually severs; depth otherwise comes from the operation's own field.
        private static double TrueDepth(WorkOrderOperation op)
        {
            return op.Through ? StockThickness() + ThroughOvercutMm : op.TotalDepth;
        }

        // T8 is RESERVED and never assigned to a cutter.
        //
        // tc.macro uses T8 as a sentinel meaning "the 3D mechanical probe is already in the spindle": it skips
        // the tool swap entirely (no park at G30, no operator prompt, no G49) and then probes through the MAIN
        // probe input instead of the toolsetter input, because the probe's stylus triggers on first touch and
        // must not bear down on the puck. Handing that number to a rigid cutter would mean no swap happening at
        // all, followed by a descent onto the puck with nothing wired to stop it - the puck's NC switch is on
        // the toolsetter input, which T8 deselects. So the sequence steps over 8 rather than through it.
        public const int ProbeToolNumber = 8;

        // The tool a T-number stands for comes from the Feeds and Speeds dialog's own tool list, so the number
        // is just that entry's position (past the reserved 8) - operations sharing a tool share a T-number, and
        // therefore one tool change, without any separate bookkeeping to keep in sync. Tying the number to the
        // TOOL rather than to order of first use is deliberate: it means a given number always identifies the
        // same physical cutter, which is the only thing an operator can act on when the change prompt says to
        // fit one.
        public static int ToolNumberFor(WorkOrderOperation op)
        {
            int n = op.Tool + 1;
            return n < ProbeToolNumber ? n : n + 1;
        }

        #region Geometry

        // The nominal outline of a toolpath's geometry centered on (cx,cy) - a pattern instance position, which
        // for an unpatterned toolpath is just its own X/Y - offset inward by `inset` (a tool-center offset).
        // Closed shapes come back closed (last point == first); a Line comes back open.
        private static List<double[]> Outline(WorkOrderToolpath tp, double cx, double cy, double inset)
        {
            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Line:
                    // A line has no interior, so an inset would only shorten it - the tool straddles it.
                    return OddJobsGeometry.LinePoints(cx, cy, tp.Length, tp.Angle, SegmentMm);
                case WorkOrderGeometryKind.Circle:
                    return OddJobsGeometry.CirclePoints(cx, cy, Math.Max(0.1d, tp.Diameter / 2d - inset), CircleSegments);
                case WorkOrderGeometryKind.Oval:
                    return OddJobsGeometry.EllipsePoints(cx, cy, Math.Max(0.1d, tp.Width / 2d - inset), Math.Max(0.1d, tp.Depth / 2d - inset), CircleSegments);
                case WorkOrderGeometryKind.Square:
                    return OddJobsGeometry.RectPoints(cx, cy, Math.Max(0.1d, tp.Size / 2d - inset), Math.Max(0.1d, tp.Size / 2d - inset), SegmentMm);
                default:
                    return OddJobsGeometry.RectPoints(cx, cy, Math.Max(0.1d, tp.Width / 2d - inset), Math.Max(0.1d, tp.Depth / 2d - inset), SegmentMm);
            }
        }

        // Concentric interior clearing rings, innermost (nearest center) first, stopping short of the wall
        // outline itself - that gets traced separately as the final contour at each Z level. The innermost
        // ring's own radius is always below the bit radius by construction, so its cutting swath covers the
        // center; no separate center-spotting move is needed. Closed geometry only.
        private static List<List<double[]>> ClearingRings(WorkOrderToolpath tp, double cx, double cy, double bitDiameter, double wallLeave, double stepoverPercent)
        {
            var rings = new List<List<double[]>>();
            double bitR = bitDiameter / 2d + wallLeave;
            double step = Math.Max(0.5d, bitDiameter * (stepoverPercent / 100d));

            // Half-extents of the final (wall) path, per geometry - the rings step inward from these.
            double finalA, finalB;
            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Circle: finalA = finalB = tp.Diameter / 2d - bitR; break;
                case WorkOrderGeometryKind.Oval: finalA = tp.Width / 2d - bitR; finalB = tp.Depth / 2d - bitR; break;
                case WorkOrderGeometryKind.Square: finalA = finalB = tp.Size / 2d - bitR; break;
                case WorkOrderGeometryKind.Rect: finalA = tp.Width / 2d - bitR; finalB = tp.Depth / 2d - bitR; break;
                default: return rings;   // open geometry has no interior to clear
            }
            finalA = Math.Max(0.1d, finalA);
            finalB = Math.Max(0.1d, finalB);

            int n = (int)Math.Ceiling(Math.Min(finalA, finalB) / step);
            for (int i = n; i >= 1; i--)
            {
                double a = Math.Max(0.05d, finalA - i * step);
                double b = Math.Max(0.05d, finalB - i * step);
                switch (tp.Geometry)
                {
                    case WorkOrderGeometryKind.Circle:
                        rings.Add(OddJobsGeometry.CirclePoints(cx, cy, a, CircleSegments));
                        break;
                    case WorkOrderGeometryKind.Oval:
                        rings.Add(OddJobsGeometry.EllipsePoints(cx, cy, a, b, CircleSegments));
                        break;
                    default:
                        rings.Add(OddJobsGeometry.RectPoints(cx, cy, a, b, SegmentMm));
                        break;
                }
            }
            return rings;
        }

        private static List<double> PassDepths(double total, double depthOfCut)
        {
            var depths = new List<double>();
            if (total <= 0d) { depths.Add(0d); return depths; }
            double doc = depthOfCut > 0d ? depthOfCut : total;
            double z = 0d;
            while (z < total - 1e-6)
            {
                z = Math.Min(z + doc, total);
                depths.Add(-z);
            }
            if (depths.Count == 0)
                depths.Add(-total);
            return depths;
        }

        // Rapid down to whatever's already known open at this XY (previousZ, or 0 = stock surface the first
        // time), then a short feed plunge into new material. Returns the Z reached, to feed back as previousZ.
        private static double AppendPlunge(List<string> lines, double targetZ, double previousZ, double feed)
        {
            double rapidToZ = Math.Max(previousZ, targetZ + PlungeClearanceMm);
            if (rapidToZ > targetZ + 1e-6)
                lines.Add("G0 Z" + F(rapidToZ));
            lines.Add("G1 Z" + F(targetZ) + " F" + F(feed));
            return targetZ;
        }

        private static void AppendSection(List<string> lines, string description, List<string> sectionLines)
        {
            lines.Add(string.Format("(TOOLPATH {0} - {1} lines)", description, sectionLines.Count));
            lines.AddRange(sectionLines);
        }

        // currentTool is the tool the spindle is ALREADY holding (int.MinValue = unknown, i.e. first section).
        // The M6 is emitted only when it actually has to change: tc.macro treats every M6 as a real swap -
        // it rapids to G30, prompts for the tool, cancels TLO with G49 and re-probes the toolsetter - so a
        // redundant M6 for the tool already fitted costs a full probe cycle and an operator prompt for nothing.
        // Confirmed on hardware 2026-07-28 (a grouped run re-prompted 4x for the same T1).
        // spindleOn/currentRpm mirror the spindle's actual state across the tool-grouped operation sequence
        // (see AppendToolEnd) - a tool-grouped run (e.g. drill all holes, then chamfer all of them) otherwise
        // stopped and restarted the spindle between every single operation of the SAME tool for no reason.
        // M3 is only needed when the spindle isn't already spinning (a real tool change, or the very first
        // operation); a same-tool RPM change while already running just needs a bare S word (grblHAL applies
        // a speed override live, no M3 required) - confirmed on real hardware 2026-07-30 (unwanted stop/start
        // between same-tool operations was burning time and cycling the spindle for no reason).
        private static void AppendToolStart(List<string> lines, WorkOrderOperation op, int currentTool, bool spindleOn, double currentRpm)
        {
            bool toolChanged = ToolNumberFor(op) != currentTool;
            if (toolChanged)
                lines.Add("M6 T" + N(ToolNumberFor(op)));
            double rpm = Rpm(op);
            if (rpm > 0d)
            {
                if (!spindleOn || toolChanged)
                    lines.Add("S" + N(rpm) + " M3");
                else if (Math.Abs(rpm - currentRpm) > 0.5d)
                    lines.Add("S" + N(rpm));
            }
        }

        // sameToolNext: the NEXT scheduled operation (if any) uses the same tool as this one - if so, leave
        // the spindle running (AppendToolStart above skips the redundant M3) instead of stopping it here just
        // to restart it a few lines later. Always still retracts to a safe Z between operations regardless.
        // Retracts BEFORE stopping, not after - confirmed as a real gap on real hardware 2026-07-30: stopping
        // the spindle first left it decelerating to a stop while still down near the just-cut material, then
        // retracting with the bit no longer spinning, instead of clearing the material first.
        private static void AppendToolEnd(List<string> lines, WorkOrderOperation op, bool sameToolNext)
        {
            lines.Add("G0 Z" + F(SafeZ()));
            if (!sameToolNext && Rpm(op) > 0d)
                lines.Add("M5");
        }

        // Walks a path at floorZ, emitting tab bridges when this operation is the one that finishes a through
        // cut. Assumes the tool is already at path[0] and plunged. tabSource is the operation that OWNS the
        // tab settings (the through contour), which is not necessarily the operation currently cutting - a
        // finishing pass that reaches full depth has to reproduce the same bridges.
        private static void AppendPath(List<string> lines, List<double[]> path, double floorZ, double feed, WorkOrderOperation tabSource)
        {
            if (tabSource != null && tabSource.NumTabs > 0 && tabSource.TabHeight > 0d)
            {
                foreach (var pt in OddJobsGeometry.ApplyTabs(path, floorZ, tabSource.TabHeight, (int)Math.Round(tabSource.NumTabs), tabSource.TabWidth))
                    lines.Add("G1 X" + F(pt[0]) + " Y" + F(pt[1]) + " Z" + F(pt[2]) + " F" + F(feed));
            }
            else
            {
                for (int i = 1; i < path.Count; i++)
                    lines.Add("G1 " + XY(path[i]) + " F" + F(feed));
            }
        }

        #endregion

        // Indirect toolpaths carry no geometry or operations of their own - they borrow both, LIVE, from
        // another toolpath by name (see WorkOrderRules.ResolveIndirectSource). Every consumer below (Schedule,
        // ToolDeclarations, BuildOperation, Outline, ...) only ever needs an ordinary WorkOrderToolpath
        // (Geometry + Operations + PatternPositions), so resolving once here - substituting each Indirect
        // entry for a shadow toolpath that borrows the source's geometry/operations but keeps the Indirect
        // one's own name/position/pattern - lets everything else in this file stay oblivious to Indirect
        // existing at all. The shadow's Operations is the SAME list instance as the source's, not a copy, so
        // editing the source afterward is picked up the next time this runs. A broken reference (missing
        // source, self-reference, chained Indirect) is dropped entirely rather than resolved - same as any
        // toolpath with no enabled operations always is; WorkOrderRules.Validate is what surfaces that to the
        // operator, not this method.
        private static WorkOrder ResolveIndirect(WorkOrder wo)
        {
            if (!wo.Toolpaths.Any(t => t.IsIndirect))
                return wo;

            var resolved = new WorkOrder { GroupByTool = wo.GroupByTool, SkipFirstToolChange = wo.SkipFirstToolChange };
            foreach (var tp in wo.Toolpaths)
            {
                if (!tp.IsIndirect)
                {
                    resolved.Toolpaths.Add(tp);
                    continue;
                }

                var source = WorkOrderRules.ResolveIndirectSource(wo, tp);
                if (source == null)
                    continue;

                resolved.Toolpaths.Add(new WorkOrderToolpath
                {
                    Name = tp.Name,
                    Geometry = source.Geometry,
                    Enabled = tp.Enabled,
                    X = tp.X, Y = tp.Y,
                    Length = source.Length, Angle = source.Angle, Diameter = source.Diameter,
                    Width = source.Width, Depth = source.Depth, Size = source.Size,
                    Pattern = tp.Pattern,
                    Columns = tp.Columns, RowSpacing = tp.RowSpacing, ColumnSpacing = tp.ColumnSpacing, Rows = tp.Rows,
                    PatternCount = tp.PatternCount, PatternRadius = tp.PatternRadius,
                    PatternStartAngle = tp.PatternStartAngle, PatternArcSpan = tp.PatternArcSpan,
                    Operations = source.Operations
                });
            }
            return resolved;
        }

        #region Cross-operation coordination

        // The through contour whose tab settings apply, if there is one - tabs are defined once, on the
        // operation that cuts the piece free.
        private static WorkOrderOperation TabDefiner(WorkOrderToolpath tp)
        {
            return tp.Operations.FirstOrDefault(o => WorkOrderRules.SupportsTabs(tp, o));
        }

        // Which operation physically finishes the cut, and so has to be the one leaving the bridges standing:
        // the last one to reach true depth. Bottom finish beats side finish beats the contour itself.
        private static WorkOrderOperation TabEmitter(WorkOrderToolpath tp)
        {
            if (TabDefiner(tp) == null)
                return null;
            return tp.Operations.LastOrDefault(o => o.Kind == WorkOrderOpKind.BottomFinish)
                ?? tp.Operations.LastOrDefault(o => o.Kind == WorkOrderOpKind.SideFinish)
                ?? TabDefiner(tp);
        }

        // The tab settings to apply while cutting `op`, or null if this operation shouldn't leave bridges.
        private static WorkOrderOperation TabsFor(WorkOrderToolpath tp, WorkOrderOperation op, bool atFinalDepth)
        {
            return atFinalDepth && ReferenceEquals(op, TabEmitter(tp)) ? TabDefiner(tp) : null;
        }

        // An operation stops short of its own true depth when a bottom-finish pass will remove that last layer.
        private static double RoughDepth(WorkOrderToolpath tp, WorkOrderOperation op)
        {
            var bottom = tp.Operations.FirstOrDefault(o => o.Kind == WorkOrderOpKind.BottomFinish);
            return Math.Max(0.05d, TrueDepth(op) - (bottom?.FloorStockToLeave ?? 0d));
        }

        // Wall skin roughing leaves behind for a side-finish pass to remove.
        private static double WallLeave(WorkOrderToolpath tp)
        {
            return tp.Operations.FirstOrDefault(o => o.Kind == WorkOrderOpKind.SideFinish)?.WallStockToLeave ?? 0d;
        }

        #endregion

        #region Per-operation codegen
        //
        // Each Build* emits ONE pattern instance, centered on (cx,cy), and does NOT emit the tool change or the
        // spindle stop - BuildOperation wraps the whole set of instances in a single M6/S..M3 .. M5, so a
        // patterned toolpath costs one tool change rather than one per hole.

        private static List<string> BuildPocket(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            double wallLeave = WallLeave(tp);
            var wall = Outline(tp, cx, cy, op.BitDiameter / 2d + wallLeave);
            var rings = ClearingRings(tp, cx, cy, op.BitDiameter, wallLeave, op.Stepover);
            var depths = PassDepths(RoughDepth(tp, op), op.DepthOfCut);

            double previousZ = 0d;
            for (int p = 0; p < depths.Count; p++)
            {
                double z = depths[p];
                var tabs = TabsFor(tp, op, p == depths.Count - 1);
                lines.Add(string.Format("(pass {0} of {1} at Z{2}{3})", p + 1, depths.Count, F(z), tabs != null ? " - wall, tabs" : string.Empty));

                lines.Add("G0 X" + F(cx) + " Y" + F(cy));
                previousZ = AppendPlunge(lines, z, previousZ, op.PlungeFeed);
                foreach (var ring in rings)
                {
                    lines.Add("G1 " + XY(ring[0]) + " F" + F(op.Feed));
                    for (int i = 1; i < ring.Count; i++)
                        lines.Add("G1 " + XY(ring[i]) + " F" + F(op.Feed));
                }
                lines.Add("G1 " + XY(wall[0]) + " F" + F(op.Feed));
                AppendPath(lines, wall, z, op.Feed, tabs);
                lines.Add("G0 Z" + F(SafeZ()));
            }
            return lines;
        }

        // Follows the outline only - no interior clearing. That's what makes it a contour rather than a
        // pocket, and it's the only roughing option for an open geometry.
        private static List<string> BuildContour(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            var path = Outline(tp, cx, cy, tp.IsClosed ? op.BitDiameter / 2d + WallLeave(tp) : 0d);
            var depths = PassDepths(RoughDepth(tp, op), op.DepthOfCut);

            double previousZ = 0d;
            for (int p = 0; p < depths.Count; p++)
            {
                double z = depths[p];
                var tabs = TabsFor(tp, op, p == depths.Count - 1);
                lines.Add(string.Format("(pass {0} of {1} at Z{2}{3})", p + 1, depths.Count, F(z), tabs != null ? " - tabs" : string.Empty));
                lines.Add("G0 " + XY(path[0]));
                previousZ = AppendPlunge(lines, z, previousZ, op.PlungeFeed);
                AppendPath(lines, path, z, op.Feed, tabs);
                lines.Add("G0 Z" + F(SafeZ()));
                // An open path ends away from where it started, so the next pass rapids back to the start -
                // where nothing has been opened up below the surface yet.
                if (!tp.IsClosed)
                    previousZ = 0d;
            }
            return lines;
        }

        // Straight peck drilling on-center with a bit that IS the hole diameter, retracting fully between
        // pecks to clear chips (cycle time traded for not packing the flutes). `openDepth` is how far down
        // this centerline a previous operation already cleared a hole at least this wide - the drill rapids
        // through that rather than pecking its way down open air.
        private static List<string> BuildDrill(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy, double openDepth)
        {
            var lines = new List<string>();
            double depth = TrueDepth(op);
            double peck = op.PeckDepth > 0d ? op.PeckDepth : 2d;

            lines.Add("G0 X" + F(cx) + " Y" + F(cy));
            double z = openDepth;
            if (openDepth > 0d)
                lines.Add(string.Format("(rapid through {0:0.0} mm already opened at this centerline)", openDepth));
            while (z < depth - 1e-6)
            {
                z = Math.Min(z + peck, depth);
                if (openDepth > 0d)
                    lines.Add("G0 Z" + F(-openDepth));
                lines.Add("G1 Z" + F(-z) + " F" + F(op.PlungeFeed));
                lines.Add("G0 Z" + F(SafeZ()));
                if (z < depth - 1e-6)
                    lines.Add("G0 X" + F(cx) + " Y" + F(cy));
            }
            return lines;
        }

        // Tool-center radii a bore has to run to clear the WHOLE hole, innermost first.
        //
        // A single helix at the final radius only works when the bit reaches past the middle: it cuts the
        // annulus from r-bitR to r+bitR, so with r = (hole-bit)/2 anything smaller than half the hole leaves an
        // uncut post standing in the centre. So the innermost pass is pinned at exactly bitR (whose swath runs
        // 0 to 2*bitR, covering the centre), and further passes step outward to the final radius, spaced no
        // wider than the radial stepover so consecutive swaths always overlap.
        //
        // When the bit IS big enough to cover the centre on its own, this collapses to the single pass at the
        // final radius - the classic helical bore, unchanged.
        private static List<double> BoreRadii(WorkOrderOperation op)
        {
            var radii = new List<double>();
            double bitR = op.BitDiameter / 2d;
            double finalR = (op.HoleDiameter - op.BitDiameter) / 2d;

            if (finalR <= bitR + 1e-9)
            {
                radii.Add(Math.Max(0d, finalR));
                return radii;
            }

            double step = Math.Max(0.5d, op.BitDiameter * (op.Stepover / 100d));
            int n = (int)Math.Ceiling((finalR - bitR) / step);
            for (int i = 0; i <= n; i++)
                radii.Add(bitR + (finalR - bitR) * i / n);
            return radii;
        }

        // Helical interpolation out to the hole diameter with a smaller end mill - what a hole that isn't a
        // stock drill size needs. Each radius (see BoreRadii) gets its own continuous helix down to depth,
        // stepping BoreStepDown per revolution, then a clean full lap at depth. No plunges anywhere: the tool
        // is always ramping while it cuts.
        private static List<string> BuildBore(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy, double openDepth)
        {
            var lines = new List<string>();
            double depth = TrueDepth(op);
            double step = op.BoreStepDown > 0d ? op.BoreStepDown : 1d;
            var radii = BoreRadii(op);

            for (int i = 0; i < radii.Count; i++)
            {
                double r = radii[i];
                if (radii.Count > 1)
                    lines.Add(string.Format("(helix {0} of {1} at radius {2})", i + 1, radii.Count, F(r)));

                lines.Add("G0 Z" + F(SafeZ()));
                lines.Add("G0 X" + F(cx + r) + " Y" + F(cy));

                // Rapid down through anything a previous operation already opened at this centerline, then
                // ramp from there.
                double z = openDepth;
                lines.Add("G0 Z" + F(openDepth > 0d ? -openDepth : 0d));

                // A zero radius has no arc to interpolate around - that's a straight plunge on centre, which
                // only happens when the bit exactly fills the hole (and validation steers that to a Drill).
                if (r <= 1e-6)
                {
                    lines.Add("G1 Z" + F(-depth) + " F" + F(op.PlungeFeed));
                }
                else
                {
                    while (z < depth - 1e-6)
                    {
                        z = Math.Min(z + step, depth);
                        lines.Add("G2 X" + F(cx + r) + " Y" + F(cy) + " I" + F(-r) + " J0 Z" + F(-z) + " F" + F(op.Feed));
                    }
                    lines.Add("G2 X" + F(cx + r) + " Y" + F(cy) + " I" + F(-r) + " J0 F" + F(op.Feed));
                }
            }
            return lines;
        }

        // Retraces the wall at every one of the roughing operation's Z levels, cutting out to the TRUE
        // outline - roughing left WallStockToLeave behind for exactly this.
        private static List<string> BuildSideFinish(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            var rough = WorkOrderRules.RoughingOp(tp);
            var path = Outline(tp, cx, cy, tp.IsClosed ? op.BitDiameter / 2d : 0d);
            var depths = PassDepths(rough != null ? RoughDepth(tp, rough) : op.TotalDepth, rough?.DepthOfCut ?? 2d);

            double previousZ = 0d;
            for (int p = 0; p < depths.Count; p++)
            {
                double z = depths[p];
                var tabs = TabsFor(tp, op, p == depths.Count - 1);
                lines.Add(string.Format("(side finish pass {0} of {1} at Z{2}{3})", p + 1, depths.Count, F(z), tabs != null ? " - tabs" : string.Empty));
                lines.Add("G0 " + XY(path[0]));
                previousZ = AppendPlunge(lines, z, previousZ, op.PlungeFeed);
                AppendPath(lines, path, z, op.Feed, tabs);
                lines.Add("G0 Z" + F(SafeZ()));
                if (!tp.IsClosed)
                    previousZ = 0d;
            }
            return lines;
        }

        // Clears the WHOLE floor at true depth (rings, then a wall lap) - the skin roughing left is
        // everywhere, not just at the wall. Confirmed on hardware 2026-07-27.
        private static List<string> BuildBottomFinish(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            var rough = WorkOrderRules.RoughingOp(tp);
            double finishZ = -(rough != null ? TrueDepth(rough) : op.TotalDepth);
            var wall = Outline(tp, cx, cy, op.BitDiameter / 2d);

            lines.Add("G0 X" + F(cx) + " Y" + F(cy));
            AppendPlunge(lines, finishZ, 0d, op.PlungeFeed);
            foreach (var ring in ClearingRings(tp, cx, cy, op.BitDiameter, 0d, op.Stepover))
            {
                lines.Add("G1 " + XY(ring[0]) + " F" + F(op.Feed));
                for (int i = 1; i < ring.Count; i++)
                    lines.Add("G1 " + XY(ring[i]) + " F" + F(op.Feed));
            }
            lines.Add("G1 " + XY(wall[0]) + " F" + F(op.Feed));
            AppendPath(lines, wall, finishZ, op.Feed, TabsFor(tp, op, true));
            lines.Add("G0 Z" + F(SafeZ()));
            return lines;
        }

        // A 45 deg V-bit traces the feature's TRUE top edge (no bit-radius offset - the cone's own geometry
        // does the work) to break the sharp corner. Works on any shape - unlike Countersink below, which only
        // makes sense on a round hole.
        private static List<string> BuildChamfer(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            var edge = Outline(tp, cx, cy, 0d);

            lines.Add("G0 " + XY(edge[0]));
            lines.Add("G0 Z" + F(SafeZ()));
            lines.Add("G1 Z" + F(-op.ChamferDepth) + " F" + F(op.Feed));
            for (int i = 1; i < edge.Count; i++)
                lines.Add("G1 " + XY(edge[i]) + " F" + F(op.Feed));
            lines.Add("G0 Z" + F(SafeZ()));
            return lines;
        }

        // A countersink bit plunged straight down a round hole's centerline - the bit's own 90-deg cone does
        // the chamfering as it descends, so there's no outline to trace at all (unlike Chamfer above).
        // op.CountersinkDiameter is the FINISHED diameter the operator wants, not a raw depth - converted here
        // (depth = diameter / 2, same 45-deg-per-side cone math Chamfer's V-bit uses, just specified the other
        // way around). PlungeFeed (not Feed) since this is a genuine axial plunge, not a corner-breaking trace.
        private static List<string> BuildCountersink(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            double depth = op.CountersinkDiameter / 2d;

            lines.Add("G0 X" + F(cx) + " Y" + F(cy));
            lines.Add("G0 Z" + F(SafeZ()));
            lines.Add("G1 Z" + F(-depth) + " F" + F(op.PlungeFeed));
            lines.Add("G0 Z" + F(SafeZ()));
            return lines;
        }

        // One operation across EVERY pattern instance, inside a single tool change. Instance-minor rather than
        // instance-major on purpose: 6 holes drilled then 6 chamfered costs two tool changes, whereas
        // completing each hole in turn would cost twelve.
        private static List<string> BuildOperation(WorkOrderToolpath tp, WorkOrderOperation op, double openDepth, int currentTool, bool spindleOn, double currentRpm, bool sameToolNext)
        {
            var lines = new List<string>();
            AppendToolStart(lines, op, currentTool, spindleOn, currentRpm);

            var positions = tp.PatternPositions().ToList();
            for (int n = 0; n < positions.Count; n++)
            {
                double cx = positions[n][0], cy = positions[n][1];
                if (positions.Count > 1)
                    lines.Add(string.Format("(instance {0} of {1} at X{2} Y{3})", n + 1, positions.Count, F(cx), F(cy)));

                switch (op.Kind)
                {
                    case WorkOrderOpKind.Pocket: lines.AddRange(BuildPocket(tp, op, cx, cy)); break;
                    case WorkOrderOpKind.Contour: lines.AddRange(BuildContour(tp, op, cx, cy)); break;
                    case WorkOrderOpKind.Drill: lines.AddRange(BuildDrill(tp, op, cx, cy, openDepth)); break;
                    case WorkOrderOpKind.Bore: lines.AddRange(BuildBore(tp, op, cx, cy, openDepth)); break;
                    case WorkOrderOpKind.SideFinish: lines.AddRange(BuildSideFinish(tp, op, cx, cy)); break;
                    case WorkOrderOpKind.BottomFinish: lines.AddRange(BuildBottomFinish(tp, op, cx, cy)); break;
                    case WorkOrderOpKind.Chamfer: lines.AddRange(BuildChamfer(tp, op, cx, cy)); break;
                    case WorkOrderOpKind.Countersink: lines.AddRange(BuildCountersink(tp, op, cx, cy)); break;
                }
            }

            AppendToolEnd(lines, op, sameToolNext);
            return lines;
        }

        #endregion

        // (TOOL T=n D=d TYPE=..) declarations in the Fusion ioSenderBatchPost post-processor's own comment
        // format, one per DISTINCT tool used - so it's clear what each T-number is before the first M6 asks
        // for it, and it feeds the same GCodeProgramComments.DiameterFor/For lookups every other loaded
        // program's tool comments do.
        private static IEnumerable<string> ToolDeclarations(WorkOrder wo)
        {
            var seen = new HashSet<int>();
            foreach (var tp in wo.Toolpaths)
                foreach (var op in wo.EnabledOperations(tp))
                {
                    int t = ToolNumberFor(op);
                    if (!seen.Add(t))
                        continue;

                    var tool = (OddJobsTool)op.Tool;
                    string type = (tool == OddJobsTool.VBit45 || OddJobsFeedsSpeedsDialog.IsCountersinkBit(tool)) ? "VBIT A=90"
                                : (tool == OddJobsTool.BallEnd || tool == OddJobsTool.BallEnd18) ? "BALL"
                                : "FLAT";
                    yield return string.Format("(TOOL T={0} D={1:0.0##} TYPE={2} - {3})", t, EffectiveBitDiameter(tp, op), type, ToolDescription(tool));
                }
        }

        // A drill's diameter IS the hole it cuts, so the bit follows the operation's own hole size; everything
        // else carries a bit independent of what it's cutting.
        public static double EffectiveBitDiameter(WorkOrderToolpath tp, WorkOrderOperation op)
        {
            return op.Kind == WorkOrderOpKind.Drill ? op.HoleDiameter : op.BitDiameter;
        }

        private static string ToolDescription(OddJobsTool tool)
        {
            switch (tool)
            {
                case OddJobsTool.EndMill2Flute: return "2-flute end mill";
                case OddJobsTool.RoughingEndMill3Flute: return "3-flute roughing end mill";
                case OddJobsTool.OFlute: return "O-flute";
                case OddJobsTool.BallEnd: return "ball end";
                case OddJobsTool.SurfacingBit25mm: return "surfacing bit";
                case OddJobsTool.VBit45: return "45 deg V-bit";
                case OddJobsTool.EndMill2Flute18: return "1/8\" 2-flute end mill";
                case OddJobsTool.BallEnd18: return "1/8\" ball end";
                case OddJobsTool.DrillBit: return "drill";
                case OddJobsTool.CountersinkBit38: return "3/8\" (10mm) countersink bit";
                case OddJobsTool.CountersinkBit916: return "9/16\" (14mm) countersink bit";
                case OddJobsTool.CountersinkBit1316: return "13/16\" (21mm) countersink bit";
                case OddJobsTool.CountersinkBit118: return "1-1/8\" (28mm) countersink bit";
                default: return tool.ToString();
            }
        }

        // The order operations are emitted in.
        //
        // Tree order by default. With GroupByTool, operations are grouped onto as few tool changes as possible
        // WITHOUT ever reordering the operations within a toolpath - that order is a real dependency chain
        // (rough leaves stock for the finishing pass; the chamfer breaks an edge that has to exist first), so a
        // plain sort by tool number would happily put a T1 chamfer ahead of the T2 pocket it's meant to
        // chamfer. Instead this walks a cursor per toolpath and keeps taking whichever operations the CURRENT
        // tool can legally do next, only changing tool when none can - a greedy topological grouping.
        private static List<KeyValuePair<WorkOrderToolpath, WorkOrderOperation>> Schedule(WorkOrder wo)
        {
            var order = new List<KeyValuePair<WorkOrderToolpath, WorkOrderOperation>>();

            // Held-back operations are dropped here, once, so every consumer of the schedule sees the same
            // program. Note the ops that remain keep their relative order, so a subset run of just the
            // finishing passes still runs side-then-bottom as authored.
            var ops = wo.Toolpaths.ToDictionary(t => t, t => wo.EnabledOperations(t).ToList());
            var live = wo.Toolpaths.Where(t => ops[t].Count > 0).ToList();

            if (!wo.GroupByTool)
            {
                foreach (var tp in live)
                    foreach (var op in ops[tp])
                        order.Add(new KeyValuePair<WorkOrderToolpath, WorkOrderOperation>(tp, op));
                return order;
            }

            var cursor = live.ToDictionary(t => t, t => 0);
            int currentTool = int.MinValue;

            while (live.Any(t => cursor[t] < ops[t].Count))
            {
                // Whichever operation each toolpath is up to - the only ones legal to emit right now.
                var ready = live.Where(t => cursor[t] < ops[t].Count)
                                .Select(t => new KeyValuePair<WorkOrderToolpath, WorkOrderOperation>(t, ops[t][cursor[t]]))
                                .ToList();

                // Stay on the tool already in the spindle if anything can use it.
                var pick = ready.FirstOrDefault(kv => ToolNumberFor(kv.Value) == currentTool);
                if (pick.Key == null)
                {
                    currentTool = NextTool(ready, live, ops, cursor);
                    pick = ready.First(kv => ToolNumberFor(kv.Value) == currentTool);
                }

                order.Add(pick);
                cursor[pick.Key]++;
            }
            return order;
        }

        // Which tool to change to, when nothing left can be done with the one in the spindle.
        //
        // Picking whatever came first in tree order (what this used to do) is a trap, because a tool can be
        // needed again LATER in a toolpath that is currently blocked behind a different tool. Real case, from a
        // hardware run: three chamfers (T6) were ready, and so was a drill (T9) sitting ahead of a fourth
        // chamfer in the same toolpath. Tree order chose T6, ran the three chamfers, then had to fit the drill
        // in and come BACK to T6 for the fourth - T6, T9, T6, with the drill stranded in the middle of the
        // chamfers. Choosing T9 first costs nothing and lets all four chamfers group: one tool change fewer,
        // and the drill is no longer interleaved with a group it has nothing to do with.
        //
        // So: prefer a tool that won't have to be revisited. For each candidate, simulate exhausting it (taking
        // an operation can expose another that wants the same tool, hence the repeat loop) and count how many
        // operations would STILL need it afterwards. Lowest wins; ties break by tree order, keeping the result
        // as close to the authored sequence as grouping allows.
        private static int NextTool(List<KeyValuePair<WorkOrderToolpath, WorkOrderOperation>> ready,
                                    List<WorkOrderToolpath> live,
                                    Dictionary<WorkOrderToolpath, List<WorkOrderOperation>> ops,
                                    Dictionary<WorkOrderToolpath, int> cursor)
        {
            int best = ToolNumberFor(ready[0].Value), bestStranded = int.MaxValue, bestIndex = int.MaxValue;

            foreach (int tool in ready.Select(kv => ToolNumberFor(kv.Value)).Distinct())
            {
                var sim = new Dictionary<WorkOrderToolpath, int>(cursor);
                bool progressed = true;
                while (progressed)
                {
                    progressed = false;
                    foreach (var tp in live)
                        while (sim[tp] < ops[tp].Count && ToolNumberFor(ops[tp][sim[tp]]) == tool)
                        {
                            sim[tp]++;
                            progressed = true;
                        }
                }

                int stranded = live.Sum(tp => ops[tp].Skip(sim[tp]).Count(op => ToolNumberFor(op) == tool));
                int treeIndex = ready.Where(kv => ToolNumberFor(kv.Value) == tool).Min(kv => live.IndexOf(kv.Key));

                if (stranded < bestStranded || (stranded == bestStranded && treeIndex < bestIndex))
                {
                    best = tool;
                    bestStranded = stranded;
                    bestIndex = treeIndex;
                }
            }
            return best;
        }

        // How many tool changes the program would contain, with or without grouping - lets the UI say what the
        // setting is actually worth on this particular work order rather than promising a saving in the abstract.
        public static int ToolChangeCount(WorkOrder wo, bool grouped)
        {
            wo = ResolveIndirect(wo);
            bool wasGrouped = wo.GroupByTool;
            wo.GroupByTool = grouped;
            try
            {
                int changes = 0, currentTool = int.MinValue;
                foreach (var scheduled in Schedule(wo))
                {
                    int t = ToolNumberFor(scheduled.Value);
                    if (t != currentTool) { changes++; currentTool = t; }
                }
                // The first change isn't one if the operator says that tool is already fitted - count what the
                // program will actually ask them to do.
                if (wo.SkipFirstToolChange && changes > 0)
                    changes--;
                return changes;
            }
            finally { wo.GroupByTool = wasGrouped; }
        }

        // The tool the program will start on, or int.MinValue if nothing is enabled. Read from the schedule, so
        // it follows grouping rather than tree order.
        public static int FirstToolNumber(WorkOrder wo)
        {
            var first = Schedule(ResolveIndirect(wo)).FirstOrDefault();
            return first.Value != null ? ToolNumberFor(first.Value) : int.MinValue;
        }

        public static List<string> BuildProgram(WorkOrder wo)
        {
            wo = ResolveIndirect(wo);
            var lines = new List<string>();
            int opCount = wo.EnabledOperationCount;
            int tpCount = wo.Toolpaths.Count(t => wo.EnabledOperations(t).Any());

            lines.Add(string.Format("(ioSender Odd Jobs - Work Order - {0} toolpath{1}, {2} operation{3})",
                tpCount, tpCount == 1 ? "" : "s", opCount, opCount == 1 ? "" : "s"));
            // A partial run is stated in the program itself - the one artifact that outlives the UI state that
            // produced it, so a saved .macro can't be mistaken for the whole job later.
            if (wo.AnyHeldBack)
                lines.Add(string.Format("(PARTIAL RUN - {0} of {1} operations enabled, {2} held back)",
                    opCount, wo.TotalOperationCount, wo.TotalOperationCount - opCount));
            // EXPR added alongside the #<_tlo_ref> save/load/restore below - named-parameter assignments in
            // the main streamed program (not just inside a called macro) need grblHAL's NGC expression support.
            lines.Add("(PREREQ, connected, homed, noalarm, tlo, EXPR, G59)");

            // Needed both for the header note and to seed the spindle state below.
            int firstTool = FirstToolNumber(wo);
            bool skipFirst = wo.SkipFirstToolChange && firstTool != int.MinValue;

            if (skipFirst)
                lines.Add(string.Format("(FIRST TOOL CHANGE SKIPPED - T{0} assumed already loaded, with a valid tool length offset)", firstTool));

            lines.AddRange(ToolDeclarations(wo));
            lines.Add("G90 G94 G17 G21");
            lines.Add("G59");
            lines.Add("G0 Z" + F(SafeZ()));

            // Load the machine-wide TLO-reference baseline (AppConfig.Base.TloRefBaseline, set once via
            // Machine Setup's "Reference TLO" step) as an INPUT to every M6 in this run, the same fix
            // StartJobView.BuildProgram applies - see its own comment. Without this, tc.macro's G43.1 for
            // each tool change was computed relative to whatever #<_tlo_ref> happened to already be sitting
            // in controller memory (leftover from an unrelated prior action, or 0 after a reset) instead of
            // a real reference - confirmed on real hardware 2026-07-30: a Work Order's SECOND tool change
            // (a V-bit chamfer after an end mill counterbore/bore) plunged ~21mm past the intended 0.5mm
            // chamfer depth, straight through 19mm of stock and into the spoilboard - the two tools' G43.1
            // offsets were computed against different/stale #<_tlo_ref> values instead of the same baseline.
            lines.Add("#<_tlo_saved> = #<_tlo_ref>");
            lines.Add(string.Format("#<_tlo_ref> = {0}", F(AppConfig.Settings.Base.TloRefBaseline)));

            // Tracks, per toolpath, the clear cylinder previous operations have already cut at its centerline,
            // so a hole op can rapid through it instead of feeding down open air - the
            // counterbore-then-through-hole case, where the drill would otherwise peck its way down a hole
            // that's already there. Per toolpath rather than a local, because tool grouping can interleave
            // operations from different toolpaths (within-toolpath order is still guaranteed - see Schedule).
            var open = new Dictionary<WorkOrderToolpath, double[]>();
            foreach (var tp in wo.Toolpaths)
                open[tp] = new[] { 0d, 0d };   // { depth, radius }

            {
                // What the spindle is holding as the program runs, so only real tool changes emit an M6. Seeding
                // it with the first tool is exactly what suppresses that first M6 - no special case needed.
                int spindleTool = skipFirst ? firstTool : int.MinValue;
                // Actual spindle run state (separate from spindleTool) - starts false regardless of skipFirst,
                // since the spindle itself is off at program start even when the tool is assumed already
                // loaded. See AppendToolStart/AppendToolEnd's own comments.
                bool spindleOn = false;
                double spindleRpm = 0d;

                var schedule = Schedule(wo);
                for (int si = 0; si < schedule.Count; si++)
                {
                    var scheduled = schedule[si];
                    var tp = scheduled.Key;
                    var op = scheduled.Value;
                    double openDepth = open[tp][0], openRadius = open[tp][1];

                    int instances = tp.InstanceCount;
                    string suffix = instances > 1 ? string.Format(" x{0}", instances) : string.Empty;

                    // Only safe to rapid down if what's already open is at least as wide as this cut reaches.
                    double usableOpen = op.HoleDiameter / 2d <= openRadius + 1e-6 ? openDepth : 0d;
                    string desc;

                    switch (op.Kind)
                    {
                        case WorkOrderOpKind.Pocket:
                            desc = string.Format("pocket, {0:0.0} mm deep", RoughDepth(tp, op));
                            break;
                        case WorkOrderOpKind.Contour:
                            desc = string.Format("contour, {0:0.0} mm deep{1}", RoughDepth(tp, op), op.Through ? " (through)" : string.Empty);
                            break;
                        case WorkOrderOpKind.Drill:
                            desc = string.Format("drill Ø{0:0.###} mm, {1:0.0} mm deep{2}", op.HoleDiameter, TrueDepth(op), op.Through ? " (through)" : string.Empty);
                            break;
                        case WorkOrderOpKind.Bore:
                            desc = string.Format("bore Ø{0:0.###} mm, {1:0.0} mm deep{2}", op.HoleDiameter, TrueDepth(op), op.Through ? " (through)" : string.Empty);
                            break;
                        case WorkOrderOpKind.SideFinish:
                            desc = "side finishing pass";
                            break;
                        case WorkOrderOpKind.BottomFinish:
                            desc = "bottom finishing pass";
                            break;
                        case WorkOrderOpKind.Chamfer:
                            desc = string.Format("chamfer, {0:0.0#} mm", op.ChamferDepth);
                            break;
                        case WorkOrderOpKind.Countersink:
                            desc = string.Format("countersink, Ø{0:0.##} mm target", op.CountersinkDiameter);
                            break;
                        default:
                            continue;
                    }

                    // Whether the NEXT scheduled operation (if any) uses the same tool - if so, AppendToolEnd
                    // leaves the spindle running instead of stopping it just to restart a few lines later.
                    bool sameToolNext = si + 1 < schedule.Count && ToolNumberFor(schedule[si + 1].Value) == ToolNumberFor(op);

                    // Tool number up front in the header: it's what the operator has to have in the spindle, and
                    // ProgramView's title-bar tooltip reads these to list what's still to come.
                    AppendSection(lines, string.Format("T{0} {1} - {2}{3}", ToolNumberFor(op), tp.Name, desc, suffix),
                        BuildOperation(tp, op, usableOpen, spindleTool, spindleOn, spindleRpm, sameToolNext));
                    spindleTool = ToolNumberFor(op);
                    double rpmThisOp = Rpm(op);
                    if (rpmThisOp > 0d) { spindleRpm = rpmThisOp; spindleOn = sameToolNext; }
                    else spindleOn = false;

                    // Record what this operation leaves open at each centerline, for the hole operations after
                    // it - every instance is identical, so one figure covers them all.
                    switch (op.Kind)
                    {
                        case WorkOrderOpKind.Pocket:
                            if (RoughDepth(tp, op) > openDepth)
                            {
                                openDepth = RoughDepth(tp, op);
                                openRadius = tp.MinSpan == double.MaxValue ? 0d : tp.MinSpan / 2d;
                            }
                            break;
                        case WorkOrderOpKind.Drill:
                        case WorkOrderOpKind.Bore:
                            if (TrueDepth(op) > openDepth) { openDepth = TrueDepth(op); openRadius = op.HoleDiameter / 2d; }
                            break;
                    }

                    open[tp][0] = openDepth;
                    open[tp][1] = openRadius;
                }
            }

            lines.Add("G0 Z" + F(SafeZ()));
            // Park at G30 instead of leaving the machine sitting wherever the last operation finished - same
            // raw-machine-coordinate convention tc.macro/StartJobView already use for this (reading the stored
            // G30 position directly via #5181-3, not the bare G30 word) so a G92/WCS offset in effect at the
            // end of the job can't send this move somewhere unexpected. Confirmed as a real gap on real
            // hardware 2026-07-30 - the machine (and spindle, which AppendToolEnd already stopped on the last
            // operation, however that call came out) was left sitting directly over the just-cut material.
            lines.Add("G53 G0 X#5181 Y#5182");
            lines.Add("G53 G0 Z#5183");
            // Restore whatever #<_tlo_ref> held before this program touched it - same save/restore idiom
            // StartJobView.BuildProgram uses. Only covers a CLEAN finish; an aborted/alarmed run never
            // reaches this line, so #<_tlo_ref> is left at the baseline this run loaded rather than the true
            // prior value - safe (the baseline is itself a trusted reference), just not a perfect restore.
            lines.Add("#<_tlo_ref> = #<_tlo_saved>");
            lines.Add("M30");
            return lines;
        }
    }
}
