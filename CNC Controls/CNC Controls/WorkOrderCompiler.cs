/*
 * WorkOrderCompiler.cs - part of CNC Controls library
 *
 * Compiles an Odd Jobs WorkOrder (toolpaths -> operations, see WorkOrderModel.cs) into ONE merged g-code
 * program: a single PREREQ/units/WCS-select header (WorkOrder.Wcs - whichever G54-G59 slot Setup actually
 * established this work order's origin in, see WorkOrderWcs()), one (TOOL ..) declaration per distinct tool
 * used anywhere in the work order, then each operation's own (TOOLPATH ..)-wrapped section in the order the
 * operator composed them, then a single M30. This replaced the five old per-wizard BuildProgram() methods,
 * each of which emitted a complete standalone program of its own.
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
using CNC.Core;

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

        // WorkOrder.Wcs: 0 = follow Setup's CURRENT selection live, 1-6 = pinned to that specific G54-G59
        // slot regardless of Setup - see that field's own comment for why "follow" has to be resolved fresh
        // rather than cached (Setup is a SHARED screen, also used by the plain Start Job tab, so its value can
        // move on to a different job's WCS after this work order's own origin was actually established - a
        // "pinned" work order deliberately survives that, a "follow" one deliberately doesn't). Clamped to
        // 1-6 either way. Public so WorkOrderRules.Validate (WorkOrderModel.cs) and WorkOrderView's own
        // Generate confirm dialog can resolve the SAME effective WCS a build would actually use.
        public static int ResolveWcs(WorkOrder wo)
        {
            int wcs = wo?.Wcs ?? 0;
            if (wcs == 0)
                wcs = StartJobConfig.Section?.Wcs ?? 6;
            return Math.Min(Math.Max(wcs, 1), 6);
        }

        public static string WcsCode(WorkOrder wo)
        {
            return "G" + (53 + ResolveWcs(wo)).ToString(CultureInfo.InvariantCulture);
        }

        // Resolved once per BuildProgram() call (ambient rather than threaded through every Build* method -
        // this compiler already leans on StartJobConfig.Section the same way for SafeZ/StockThickness) so
        // BuildSurfaceEntireSpoilboard, several calls deep, can still read it via WorkOrderWcs()/ScratchWcs()
        // below without a signature change everywhere. Always the RESOLVED 1-6 value (ResolveWcs already
        // applied) - "follow" is settled once, at the moment the program is actually built, not re-resolved
        // mid-build.
        private static int _wcs = 6;

        private static string WorkOrderWcs()
        {
            return "G" + (53 + _wcs).ToString(CultureInfo.InvariantCulture);
        }

        // BuildSurfaceEntireSpoilboard borrows a WCS slot as scratch space for its own machine-referenced
        // touch-off, on the assumption that Work Order's REAL WCS (WorkOrderWcs, above) is left untouched by
        // it. Picks G55 in the one case that assumption would otherwise break (the operator set Work Order
        // up on G54 itself) and G54 otherwise - either way, always a slot distinct from WorkOrderWcs().
        private static string ScratchWcs()
        {
            return WorkOrderWcs() == "G54" ? "G55" : "G54";
        }

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
        // reliefToolRadius > 0 asks for corner reliefs on a Square/Rect that has them ticked - pass the
        // cutter radius from an INTERNAL wall pass (Pocket, Side finish) and 0 everywhere else. It is a
        // parameter rather than something read off `tp` because the relief is sized by the CUTTER, which
        // belongs to the operation: the same rectangle roughed with a 1/4" and finished with a 1/8" wants
        // two different reliefs. Contour passes 0 - its corners are convex (see IsInternalFeature) and a
        // round cutter already reproduces them exactly, so there is nothing there to relieve.
        private static List<double[]> Outline(WorkOrderToolpath tp, double cx, double cy, double inset, double reliefToolRadius = 0d)
        {
            double reach = tp.CornerReliefs && reliefToolRadius > 0d
                            ? OddJobsGeometry.DogboneReachFor(reliefToolRadius, inset)
                            : 0d;

            switch (tp.Geometry)
            {
                case WorkOrderGeometryKind.Line:
                    // A line has no interior, so an inset would only shorten it - the tool straddles it.
                    return OddJobsGeometry.LinePoints(cx, cy, tp.Length, tp.Angle, SegmentMm);
                case WorkOrderGeometryKind.Text:
                    // Text is many disjoint strokes, not one outline, so it has no meaningful single path -
                    // BuildEngrave goes to StrokeFont directly. Returning empty rather than falling through
                    // to the Rect default below, which would have quietly traced a rectangle around the
                    // words for any caller that asked.
                    return new List<double[]>();
                case WorkOrderGeometryKind.Circle:
                    return OddJobsGeometry.CirclePoints(cx, cy, Math.Max(0.1d, tp.Diameter / 2d - inset), CircleSegments);
                case WorkOrderGeometryKind.Oval:
                    return OddJobsGeometry.EllipsePoints(cx, cy, Math.Max(0.1d, tp.Width / 2d - inset), Math.Max(0.1d, tp.Depth / 2d - inset), CircleSegments);
                case WorkOrderGeometryKind.Square:
                    return OddJobsGeometry.RectPoints(cx, cy, Math.Max(0.1d, tp.Size / 2d - inset), Math.Max(0.1d, tp.Size / 2d - inset), SegmentMm, reach);
                default:
                    return OddJobsGeometry.RectPoints(cx, cy, Math.Max(0.1d, tp.Width / 2d - inset), Math.Max(0.1d, tp.Depth / 2d - inset), SegmentMm, reach);
            }
        }

        // Climb vs conventional isn't a fixed CW-or-CCW answer - it flips depending on which SIDE of the cut
        // path the material being removed is on. On an INTERNAL feature (a bore/pocket/hole, where the wall
        // material is OUTSIDE the tool's path) climb means orbiting the SAME direction as the spindle; on an
        // EXTERNAL feature (a Contour cutting a part free, where the kept material is INSIDE the tool's path)
        // climb means orbiting OPPOSITE the spindle. Conventional is the other one in each case.
        // Chamfer is geometrically ambiguous - it just traces whatever outline it's given (BuildChamfer),
        // which could be a hole's rim or a part's outer edge - treated as internal here since breaking a
        // hole/counterbore's top edge is Odd Jobs' actual motivating use case, not a profile's; an operator
        // chamfering an external edge can compensate by picking the opposite Direction setting.
        private static bool IsInternalFeature(WorkOrderOpKind kind)
        {
            return kind != WorkOrderOpKind.Contour;
        }

        // Which way the spindle ACTUALLY turns when this compiler's g-code runs it - AppendToolStart always
        // emits M3, never M4, so the only way that's not physically CW is a machine whose spindle/VFD is
        // documented as only running backwards from the M3/"CW" label (see SpindleDirectionCapability's own
        // comment - found on real hardware 2026-08-01). Bidirectional and FixedCW both mean M3 spins CW here;
        // only FixedCCW differs.
        private static bool PhysicalSpindleIsCW()
        {
            return AppConfig.Settings.Base?.SpindleDirectionCapability != SpindleDirectionCapability.FixedCCW;
        }

        // Translates the operator's Climb/Conventional choice, IsInternalFeature, and the machine's actual
        // physical spindle direction into a raw CW/CCW orbit: climb means "same direction as the spindle" for
        // an internal feature and "opposite the spindle" for an external one (see IsInternalFeature) - so
        // this is CW only when that relationship, evaluated against PhysicalSpindleIsCW, lands on CW.
        private static bool WantsCWOrbit(WorkOrderOperation op)
        {
            bool climb = op.Direction == WorkOrderCutDirection.Climb;
            bool orbitMatchesSpindle = IsInternalFeature(op.Kind) ? climb : !climb;
            bool spindleCW = PhysicalSpindleIsCW();
            return orbitMatchesSpindle ? spindleCW : !spindleCW;
        }

        // OddJobsGeometry's CirclePoints/EllipsePoints/RectPoints all generate CCW, closed (first == last).
        // On the (default, and by far the most common) case of a standard CW spindle, that raw CCW order is
        // already Conventional for an internal feature and Climb for an external one - so this only needs to
        // reverse the list when WantsCWOrbit calls for the opposite of that. Reversing a closed list (first
        // == last) flips the walked direction without moving the start point, since the old last point (==
        // the old first) is still first after Reverse(). Open geometry (a Line) has no inside/outside to
        // speak of, so Direction is a no-op there.
        private static List<double[]> OrderForDirection(List<double[]> path, WorkOrderToolpath tp, WorkOrderOperation op)
        {
            if (!tp.IsClosed || !WantsCWOrbit(op))
                return path;

            var reversed = new List<double[]>(path);
            reversed.Reverse();
            return reversed;
        }

        // Bore is always an internal feature (a hole). On a standard CW spindle this is G2 for Climb, G3 for
        // Conventional - G2 was also this method's long-standing unconditional behavior before Direction
        // existed. Both use the same I/J center offset; only the arc word changes.
        private static string BoreArcCommand(WorkOrderOperation op)
        {
            return WantsCWOrbit(op) ? "G2" : "G3";
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

        /// <summary>
        /// Make arbitrary text safe to place INSIDE a (...) g-code comment.
        ///
        /// grblHAL-style parsers end a comment at the FIRST ')', so one nested bracket truncates the
        /// block and everything after it parses as g-code. That is not theoretical: on 2026-08-15 a
        /// real work order failed on
        ///     (TOOLPATH T16 Rect 1 - v-carve "..." , 27.3 mm caps (sized to fit)-31387lines)
        /// where the comment ended at "fit)" and the controller rejected "-31387lines)" with "G-code
        /// words consist of a letter and a value" - error:2, at the very start of a 32-minute job.
        ///
        /// Every string that reaches a comment goes through here, rather than each call site
        /// remembering: this file already carried FOUR copies of the same Replace pair, and the one
        /// place that mattered had none. Newlines go too - a comment cannot span lines.
        ///
        /// Sanitise where g-code is GENERATED, never on the way out to the controller. Rewriting
        /// blocks in the send path is what once dropped a G59.3 and drove the machine 128 mm into a
        /// touch plate; what the operator's program says is what the controller must receive.
        /// </summary>
        internal static string CommentText(string text)
        {
            return (text ?? string.Empty)
                .Replace('(', '[').Replace(')', ']')
                .Replace((char)13, ' ').Replace((char)10, '|');
        }

        private static void AppendSection(List<string> lines, string description, List<string> sectionLines)
        {
            // description is assembled from operator-authored names AND from this file's own literals -
            // and one of those literals, " (sized to fit)", is exactly what broke a real job. Sanitising
            // HERE, at the boundary where text becomes a comment, covers both without every contributor
            // to a description having to know it ends up inside brackets.
            lines.Add(string.Format("(TOOLPATH {0} - {1} lines)", CommentText(description), sectionLines.Count));
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

            var resolved = new WorkOrder { GroupByTool = wo.GroupByTool, SkipFirstToolChange = wo.SkipFirstToolChange, Wcs = wo.Wcs };
            foreach (var tp in wo.Toolpaths)
            {
                if (!tp.IsIndirect)
                {
                    resolved.Toolpaths.Add(tp);
                    continue;
                }

                // One shadow per placement: one for an Indirect pointing at a single toolpath, N for one
                // pointing at a GROUP. WorkOrderRules.Expand decides both what supplies each shape and
                // where it lands, and the editor's preview draws from the same call - so a group cannot
                // cut in an arrangement the preview never drew.
                var placements = WorkOrderRules.Expand(wo, tp);
                if (placements.Count == 0)
                    continue;       // broken reference - dropped, exactly as before

                foreach (var placement in placements)
                {
                    var geom = placement.Geometry;

                    // Everything comes from the borrowed toolpath by default - the shadow exists to BE it,
                    // somewhere else. Listing its fields by hand instead is how this silently lost SvgFile,
                    // SvgWidth, the whole text block and CornerReliefs: an Indirect pointing at an SVG
                    // resolved to a toolpath with no artwork, and one pointing at Text resolved back to the
                    // "TEXT" default. Inverting the default means a field added to WorkOrderToolpath is
                    // inherited here without anyone remembering to come back for it.
                    //
                    // The pattern block comes across with everything else, which was a real change when it
                    // landed: it used to be copied from `tp`, where Pattern is forced to None for every
                    // Indirect toolpath, so those eight fields only ever copied defaults and an Indirect
                    // pointing at a 3x2 grid cut ONE instance instead of six.
                    var shadow = WorkOrderRules.CopyFields(geom, new WorkOrderToolpath());

                    // ... except the handful the INDIRECT toolpath supplies, which is what makes this a
                    // different toolpath rather than a second copy. [NoClone] held back Name and Operations,
                    // and both are set explicitly just below.
                    //
                    // A group expands to several shadows, so each is named for the member it came from -
                    // otherwise every (TOOLPATH ...) comment in the program would carry the same name.
                    shadow.Name = placements.Count > 1 ? tp.Name + "/" + geom.Name : tp.Name;

                    // The Indirect toolpath's own tick is the ONLY gate on its output: it cuts what the
                    // source DEFINES, not what the source currently has ticked.
                    //
                    // So the borrowed operations are copied here with Enabled forced on, rather than shared
                    // as the same objects. Sharing them meant the source's ticks gated the copy too, and
                    // unticking a group - which cascades to its members' operations - silently produced a
                    // ticked Indirect toolpath that cut nothing at all. Reported the first time anyone
                    // disabled a group whose copy they still wanted.
                    //
                    // This costs nothing in liveness. ResolveIndirect runs at Generate, so every parameter
                    // is still read fresh from the source each time - feeds, depths, tools, the lot. The one
                    // thing not inherited is the tick, which is a staging control ("what do I want cut on
                    // this run") rather than part of the definition being borrowed.
                    shadow.Enabled = tp.Enabled;
                    shadow.Operations = geom.Operations
                                            .Select(o =>
                                            {
                                                var copy = WorkOrderRules.CopyFields(o, new WorkOrderOperation());
                                                copy.Enabled = true;
                                                return copy;
                                            })
                                            .ToList();

                    // Position comes from the placement, already resolved - absolute, or relative to the
                    // source's own, and for a group offset from its anchor member. It is a CENTER, so the
                    // shadow is pinned to Center/Absolute: inheriting the borrowed toolpath's anchor would
                    // apply its half-width a second time, and inheriting a Relative mode would re-resolve a
                    // position that is already resolved.
                    shadow.X = placement.X;
                    shadow.Y = placement.Y;
                    shadow.Anchor = WorkOrderAnchor.Center;
                    shadow.OffsetMode = WorkOrderOffsetMode.Absolute;

                    // The shadow presents the borrowed geometry, so it is not Indirect and must not look it -
                    // IsIndirect keys off Geometry, but a stale name left here would be a live reference for
                    // anything reading the field rather than the property. Its group goes too: the copy is
                    // not a member of the group it was copied FROM.
                    shadow.IndirectSource = null;
                    shadow.Group = string.Empty;

                    resolved.Toolpaths.Add(shadow);
                }
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
            // Corner reliefs go on the pass that leaves the FINAL wall. When wallLeave > 0 a Side finish
            // follows and owns the wall, so relieving here would cut away the very stock that pass was
            // left to take - the reliefs are emitted there instead.
            var wall = OrderForDirection(Outline(tp, cx, cy, op.BitDiameter / 2d + wallLeave,
                                                wallLeave > 0d ? 0d : op.BitDiameter / 2d), tp, op);
            var rings = ClearingRings(tp, cx, cy, op.BitDiameter, wallLeave, op.Stepover)
                .Select(r => OrderForDirection(r, tp, op)).ToList();
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
            var path = OrderForDirection(Outline(tp, cx, cy, tp.IsClosed ? op.BitDiameter / 2d + WallLeave(tp) : 0d), tp, op);
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

        // Engrave text with a V-bit: follow each glyph stroke once, at a single depth. The stroke font gives
        // centreline paths (see CNC.Core.StrokeFont), so the cut IS the letter - there is no outline to
        // offset and no interior to clear, which is why this shares nothing with BuildContour.
        //
        // DEPTH IS DERIVED, NOT SPECIFIED. The operator asks for a stroke WIDTH, because that is what they
        // can see and measure on the finished part; a V-bit produces that width by plunging until its cone
        // is that wide, which depends entirely on the tool's included angle:
        //
        //     width = 2 * depth * tan(halfAngle)   =>   depth = (width / 2) / tan(halfAngle)
        //
        // The same 0.8 mm line is 0.40 mm deep on a 90-degree bit and 0.69 mm on a 60. Specifying depth
        // instead would mean the same job cutting a different width after a bit change, silently.
        //
        // Single pass on purpose: a V-bit at engraving depths removes very little, and stepping down would
        // just retrace the same groove. DepthOfCut is not consulted, and no tabs - there is nothing being
        // cut free.
        private static List<string> BuildEngrave(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            // Shape text (HasText on a Line/Circle/Oval/Square/Rect): size, placement and baseline angle
            // come from the fit resolver - the SAME one WorkOrderRules.Validate ran to gate Generate, so
            // by the time we are here it fits; the refusal below is defense against a caller that skipped
            // validation, not a second opinion.
            double capHeight = tp.CapHeight, angleDeg = tp.Angle, fitDx = 0d, fitDy = 0d;
            bool shapeText = tp.HasText && tp.Geometry != WorkOrderGeometryKind.Text
                          && WorkOrderRules.SupportsShapeText(tp.Geometry);
            if (shapeText)
            {
                var fit = WorkOrderTextFit.Resolve(tp);
                if (!fit.Fits)
                    return new List<string> { "(ENGRAVE skipped - text does not fit: "
                        + CommentText(fit.Error) + ")" };
                capHeight = fit.CapHeight; angleDeg = fit.Angle; fitDx = fit.OffsetX; fitDy = fit.OffsetY;
            }

            // One op kind, two ways of cutting it. Artwork has no stroke-font equivalent - an SVG IS
            // outlines - so it always carves whatever the engrave width says; text carves when it carries
            // a real font family, chosen on the GEOMETRY where the font itself is (WorkOrderToolpath.
            // FontFamily). Both used to be separate branches here while the editor tested only IsCarved,
            // so the two disagreed about every SVG toolpath - see WorkOrderToolpath.CarvesOutlines.
            if (tp.CarvesOutlines)
                return BuildVCarve(tp, op, cx, cy, capHeight, angleDeg, fitDx, fitDy);

            var lines = new List<string>();

            var strokes = CNC.Core.StrokeFont.Render(tp.Text, capHeight);
            if (strokes.Count == 0)
                return lines;   // empty or wholly unrenderable text - emit nothing rather than a bare plunge

            // One shared answer for depth-from-width, INCLUDING the clamp at the bit's own maximum cutting
            // diameter - see CustomTool.EngraveCutFor. Computing it here as well as in the UI is how the two
            // would drift apart.
            var tool = CustomTools.Find(op.Tool);
            var cut = tool != null ? tool.EngraveCutFor(op.EngraveWidth)
                                   : new EngraveCut { Width = Math.Max(0.01d, op.EngraveWidth), MaxWidth = double.MaxValue,
                                                      Depth = Math.Max(0.01d, op.EngraveWidth) / 2d };
            double halfAngle = tool != null ? tool.HalfAngleRad : Math.PI / 4d;
            double depth = Math.Max(0.01d, cut.Depth);

            // StrokeFont lays the text out with its baseline starting at the origin and running along +X.
            // Move it so the toolpath's anchor lands on (cx,cy), then rotate about that same anchor - so
            // the point the operator pinned stays pinned as the angle changes. Anything else makes lining
            // text up against an edge an exercise in trial and error.
            //
            // Two centering conventions on purpose: the Text KIND centers the cap-height band on the
            // anchor (what "put 10 mm lettering at X,Y" means); shape text centers the MEASURED BLOCK,
            // because the block is what the fit resolver proved fits, then shifts by the alignment
            // offsets - which are in the text frame, so they rotate with a Line's baseline.
            var size = CNC.Core.StrokeFont.Measure(tp.Text, capHeight);
            double originX = -size.X / 2d + fitDx;
            double originY = shapeText ? -size.Y / 2d + fitDy : -size.Y / 2d + capHeight / 2d;
            double rad = angleDeg * Math.PI / 180d;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "(ENGRAVE \"{0}\" cap {1:0.###} mm, stroke {2:0.###} mm wide -> {3:0.###} mm deep at {4:0.#} deg included)",
                // grblHAL ends a comment at the FIRST ')' (see the note on this file's comment helper), so
                // brackets in the operator's own text would truncate the block and let the rest parse as
                // g-code. Newlines are folded to '|' for the same reason - one comment, one line.
                CommentText(tp.Text),
                capHeight, cut.Width, depth, halfAngle * 360d / Math.PI));

            if (cut.Clamped)
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "(ENGRAVE stroke width limited to {0:0.###} mm - the widest this bit can cut; asked for {1:0.###})",
                    cut.Width, op.EngraveWidth));

            foreach (var stroke in strokes)
            {
                for (int i = 0; i < stroke.Count; i++)
                {
                    double lx = stroke[i].X + originX, ly = stroke[i].Y + originY;
                    double x = cx + lx * cos - ly * sin;
                    double y = cy + lx * sin + ly * cos;

                    if (i == 0)
                    {
                        lines.Add("G0 Z" + F(SafeZ()));
                        lines.Add("G0 X" + F(x) + " Y" + F(y));
                        lines.Add("G1 Z" + F(-depth) + " F" + F(op.PlungeFeed));
                    }
                    else
                        lines.Add("G1 X" + F(x) + " Y" + F(y) + " F" + F(op.Feed));
                }
            }
            lines.Add("G0 Z" + F(SafeZ()));

            return lines;
        }

        // Even-odd ray cast along +X against a single closed ring - for assigning carve passes to the
        // glyph that contains them.
        private static bool PointInRing(List<Point2D> ring, Point2D pt)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double yi = ring[i].Y, yj = ring[j].Y;
                if ((yi > pt.Y) == (yj > pt.Y))
                    continue;
                double xInt = ring[i].X + (pt.Y - yi) / (yj - yi) * (ring[j].X - ring[i].X);
                if (pt.X < xInt)
                    inside = !inside;
            }
            return inside;
        }

        // Grid cell for the carve's distance field. 0.1 mm keeps the field's error an order below anything
        // a V-bit leaves in wood, and text-sized fields build in well under a second at it.
        private const double CarveResolution = 0.1d;

        // Default depth between successive carve levels, used when an operation has no depth of cut set.
        // This does NOT scallop the walls - consecutive passes tile the cone exactly, a deeper pass's flank
        // running through the tip of the one above - it only sets how much material a single ring removes,
        // and the in-plan faceting of curved boundaries.
        //
        // Which also means the shallower levels are geometrically REDUNDANT wherever the region reaches
        // full depth: the deepest pass alone recreates the whole V, and the ones above it cut material that
        // pass is about to take. They are depth-of-cut stepping, nothing more - so the operation's own
        // DepthOfCut now drives it (CarveStep), and this is only the fallback for one left at zero.
        private const double CarveDepthStepDefault = 0.5d;

        // The depth step a carve should use: the operation's own, or the default when it has none. Clamped
        // to something a tip can survive being asked for - a step of zero would be an infinite level count,
        // and the engine's own Math.Max would silently turn it into 0.01 mm and build for a very long time.
        private static double CarveStep(WorkOrderOperation op)
        {
            return op != null && op.DepthOfCut > 0.01d ? op.DepthOfCut : CarveDepthStepDefault;
        }

        // The distance-field build inside VCarve.Build is by far the most expensive thing Generate does -
        // minutes for board-sized lettering at CarveResolution - and it depends on NOTHING but the glyph
        // outlines (text/font/style/size) and the cone (half angle, max depth). Position, alignment, WCS,
        // the skip-first-tool-change option, feeds - none of them touch it; they act on the emitted
        // coordinates afterwards. So the passes are memoized on exactly those inputs: clicking Generate
        // again for another identical board - or after toggling "first tool already loaded", or after
        // nudging X/Y - reuses the carve and takes seconds, while any edit to the artwork or the V-bit
        // changes the key and recomputes. The key must BE the full input set, or the cache serves the
        // wrong geometry with no symptom (the resolution/step consts are compile-time fixed, so they are
        // not part of it - expose either as a setting and it MUST join the key). Pass objects are
        // read-only downstream (grouping reorders references, emission reads points), so sharing is safe.
        //
        // THAT CLAIM WAS ONCE FALSE AND IT SHIPPED. The key covered only the TEXT inputs, because text
        // was the only carvable thing when it was written; 7143bc4d added SVG as a second geometry
        // source and did not extend it. Neither SvgFile nor SvgWidth was in the key, and capHeight -
        // which IS in it, is the TEXT cap height, unrelated to artwork. So changing the artwork width and
        // regenerating HIT the cache and reused the old passes (indistinguishable from the width field
        // being inert, which is the bug fbc93787 had just fixed), and pointing a toolpath at a different
        // .svg carved the PREVIOUS logo. Static and process-lifetime, so a restart hid it.
        // Adding a geometry source means adding its inputs to CarveSourceKey. There is no other answer.

        // U+0001 as the field separator - an unambiguous boundary even though Text and file paths are
        // free-form; a bare concat, or any separator that can occur inside a value, lets different field
        // combinations produce one key. Built from (char)1 rather than written as an escape on purpose:
        // an escape in source is something a bulk edit can silently mangle, and this very method spent
        // part of its own fix carrying a raw control byte for exactly that reason.
        private static readonly string KeySep = ((char)1).ToString();

        private static readonly Dictionary<string, List<CNC.Core.VCarvePass>> carvePassCache = new Dictionary<string, List<CNC.Core.VCarvePass>>();
        private const int CarveCacheMax = 8;   // a work order rarely has more distinct carves than this

        /// <summary>
        /// Everything about WHERE THE CONTOURS COME FROM, as a key fragment - the half of the carve's
        /// input set that differs per geometry kind. Leads with the kind, so a text carve and an SVG
        /// carve can never collide however their other fields line up. Returns null when the inputs
        /// cannot be fingerprinted, which means "do not cache this" - never "cache it under a guess".
        /// </summary>
        private static string CarveSourceKey(WorkOrderToolpath tp, double capHeight)
        {
            if (tp.Geometry != WorkOrderGeometryKind.Svg)
                return string.Join(KeySep, "text", tp.Text, tp.FontFamily,
                    tp.FontBold ? "b" : "", tp.FontItalic ? "i" : "",
                    capHeight.ToString("R", CultureInfo.InvariantCulture));

            // A PATH IS NOT ITS CONTENTS, so the key carries a hash of the BYTES. Iterating on a logo
            // means re-exporting over the same filename, which leaves path and width identical while
            // the artwork changes underneath - the cache would go stale in precisely the workflow the
            // SVG feature exists to serve. Write-stamp+length was the cheaper option and was rejected:
            // a restore from backup, or any exporter that preserves timestamps, changes content while
            // both of those hold still. Hashing a few hundred KB is free against a carve measured in
            // MINUTES, so there is no reason to accept a weaker identity here.
            string hash;
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(System.IO.File.ReadAllBytes(tp.SvgFile)));
            }
            catch
            {
                hash = null;    // missing/unreadable/locked - see the null contract above
            }
            if (hash == null)
                return null;

            return string.Join(KeySep, "svg", tp.SvgFile ?? string.Empty,
                tp.SvgWidth.ToString("R", CultureInfo.InvariantCulture), hash);
        }

        // What this carve is OF, for the debug trace - a HIT/MISS line that does not name its source
        // cannot be read against the artwork that produced it.
        private static string CarveSourceLabel(WorkOrderToolpath tp)
        {
            return tp.Geometry == WorkOrderGeometryKind.Svg
                ? "svg=\"" + (string.IsNullOrEmpty(tp.SvgFile) ? "(none)" : System.IO.Path.GetFileName(tp.SvgFile))
                  + "\" w=" + tp.SvgWidth.ToString("0.###", CultureInfo.InvariantCulture) + "mm"
                : "text=\"" + tp.Text + "\"";
        }

        private static List<CNC.Core.VCarvePass> CachedCarvePasses(WorkOrderToolpath tp, double capHeight,
                                                                   double halfAngle, double maxDepth, double step,
                                                                   List<IList<Point2D>> polys)
        {
            // The source fragment (see CarveSourceKey) plus the cone AND the depth step. The step is in the
            // key because it is now a setting rather than a compile-time constant - which the comment above
            // named as the exact condition requiring it. Without it, changing an operation's depth of cut
            // and regenerating would HIT the cache and reuse passes built at the old step: the field would
            // look inert, which is precisely how the SVG width bug presented.
            string source = CarveSourceKey(tp, capHeight);
            string key = source == null ? null : string.Join(KeySep, source,
                halfAngle.ToString("R", CultureInfo.InvariantCulture),
                maxDepth.ToString("R", CultureInfo.InvariantCulture),
                step.ToString("R", CultureInfo.InvariantCulture));

            List<CNC.Core.VCarvePass> passes;
            if (key != null && carvePassCache.TryGetValue(key, out passes))
            {
                // Logged because the whole cost question turns on this: a HIT returns in microseconds and
                // a MISS is the multi-minute build. A timing trace that does not say which it was is
                // unreadable - the second Generate of the same artwork looks like a different engine.
                if (DebugLog.Enabled)
                    DebugLog.Write("workorder", string.Format(CultureInfo.InvariantCulture,
                        "carve cache HIT ({0} passes) {1}", passes.Count, CarveSourceLabel(tp)));
                return passes;
            }

            var swCarve = System.Diagnostics.Stopwatch.StartNew();
            passes = CNC.Core.VCarve.Build(polys, halfAngle, maxDepth, CarveResolution, step);
            swCarve.Stop();
            if (DebugLog.Enabled)
                DebugLog.Write("workorder", string.Format(CultureInfo.InvariantCulture,
                    "carve cache MISS {0:0}ms ({1} passes, {2} contours, res={3}mm, step={4}mm{5}) {6}",
                    swCarve.Elapsed.TotalMilliseconds, passes.Count, polys.Count,
                    CarveResolution, step,
                    key == null ? ", NOT CACHEABLE" : string.Empty, CarveSourceLabel(tp)));

            if (key != null)
            {
                if (carvePassCache.Count >= CarveCacheMax)
                    carvePassCache.Clear();
                carvePassCache[key] = passes;
            }
            return passes;
        }

        // V-carve text set in a real outline font: the bit follows iso-depth contours of the glyph's
        // FILLED shape, so strokes taper into their corners the way carved lettering does. Depth is again
        // a consequence, but of the glyph's own local width rather than a requested stroke width: at every
        // point the cone sinks until it spans the stroke (see CNC.Core.VCarve for the geometry and its
        // error budget). The one ceiling is the bit itself - past DiameterMm the cone runs out and the
        // shank would rub - and where a stroke is wider than that ceiling the bottom flattens off and is
        // cleared, below.
        // capHeight/angleDeg/fitDx/fitDy arrive resolved from BuildEngrave: the toolpath's own values for
        // the Text kind, the fit resolver's for shape text (see there).
        private static List<string> BuildVCarve(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy,
                                                double capHeight, double angleDeg, double fitDx, double fitDy)
        {
            var lines = new List<string>();

            // Where the contours come from is the ONLY difference between carving a word and carving a
            // logo - everything below this point is the same code (see docs/Architecture-SVG-Import.md).
            List<OutlineContour> outline;
            if (tp.Geometry == WorkOrderGeometryKind.Svg)
            {
                var svg = SvgOutlines.Load(tp.SvgFile, tp.SvgWidth);
                if (svg.Error != null)
                    return new List<string> { "(VCARVE skipped - " + CommentText(svg.Error) + ")" };
                // An import that dropped elements must NOT cut the remainder. A partial logo looks
                // plausible in the preview and is wrong in the wood, and the operator has no way to see
                // which half went missing - so refuse, and name what was skipped.
                if (!svg.IsComplete)
                    return new List<string> { "(VCARVE skipped - "
                        + CommentText(System.IO.Path.GetFileName(tp.SvgFile) + " uses features this build cannot import: "
                                      + svg.Describe()) + ")" };
                outline = svg.Contours;
            }
            else
                outline = TrueTypeOutlines.Render(tp.Text, tp.FontFamily, capHeight, tp.FontBold, tp.FontItalic);

            if (outline.Count == 0)
                return lines;   // blank text or an unresolvable family - emit nothing rather than a bare plunge

            var tool = CustomTools.Find(op.Tool);
            double halfAngle = tool != null ? tool.HalfAngleRad : Math.PI / 4d;
            // One shared answer for the carve's depth ceiling, INCLUDING the operation's optional cap and
            // the clamp at the bit's own cutting diameter - see CustomTool.CarveDepthFor. Computing it
            // here as well as in the editor's note is how the two would drift apart (same reasoning, and
            // the same precedent, as EngraveCutFor in BuildEngrave above).
            double maxDepth = tool != null
                ? tool.CarveDepthFor(op.CarveMaxDepth).Depth
                : (op.CarveMaxDepth > 0d ? op.CarveMaxDepth : 3d);

            // The engine carves in the text's own coordinates (baseline flat, Y up) and the transform to
            // the stock happens per emitted point - same shape either way, but the field's grid stays
            // aligned with the glyphs instead of sampling them diagonally.
            var polys = new List<IList<Point2D>>();
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var c in outline)
            {
                polys.Add(c.Points);
                foreach (var p in c.Points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }

            // clearFlats on (the default): where a stroke is wider than the cone can span, the engine
            // appends the maxDepth clearing generations itself - see VCarve.Build.
            var passes = CachedCarvePasses(tp, capHeight, halfAngle, maxDepth, CarveStep(op), polys);
            if (passes.Count == 0)
                return lines;

            // Anchor at the centre of the text's bounding box - the same extents the model reports for
            // placement - then rotate about that anchor, so the pinned point stays pinned as the angle
            // changes, exactly as the stroke branch does. Shape text additionally shifts by the fit's
            // alignment offsets, pre-rotation (the text frame), matching the stroke branch.
            double ox = -(minX + maxX) / 2d + fitDx, oy = -(minY + maxY) / 2d + fitDy;
            double rad = angleDeg * Math.PI / 180d;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            // Cut each letter COMPLETELY before moving to the next. The engine's global shallow-to-deep
            // order keeps the "step down into material already opened" invariant, but that invariant only
            // matters WITHIN a connected region - across separate glyphs it just interleaves, so the
            // machine hopped letter to letter at every depth level, filling in bits and pieces until the
            // very end. Passes are regrouped by the outer glyph contour that contains them, letters
            // ordered left to right along the (unrotated) baseline, keeping the original relative order -
            // regular passes shallow to deep, then that glyph's clearing generations - inside each.
            var outers = new List<List<Point2D>>();
            foreach (var c in outline)
                if (c.IsOuter)
                    outers.Add(c.Points);
            outers.Sort((a, b) =>
            {
                double ax = double.MaxValue, bx = double.MaxValue;
                foreach (var p in a) if (p.X < ax) ax = p.X;
                foreach (var p in b) if (p.X < bx) bx = p.X;
                return ax.CompareTo(bx);
            });
            var grouped = new List<CNC.Core.VCarvePass>(passes.Count);
            var claimed = new bool[passes.Count];
            foreach (var outer in outers)
                for (int i = 0; i < passes.Count; i++)
                    if (!claimed[i] && passes[i].Path.Count > 0 && PointInRing(outer, passes[i].Path[0]))
                    {
                        claimed[i] = true;
                        grouped.Add(passes[i]);
                    }
            // A pass no outer claims (a boundary ring's first point can sit a hair outside) still gets
            // cut - losing the grouping nicety must never lose a cut.
            for (int i = 0; i < passes.Count; i++)
                if (!claimed[i])
                    grouped.Add(passes[i]);
            passes = grouped;

            int cutPasses = 0;
            foreach (var p in passes)
                if (p.Depth >= 0.01d)
                    cutPasses++;

            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "(VCARVE \"{0}\" in {1}{2}{3}, cap {4:0.###} mm, {5} passes to {6:0.###} mm max at {7:0.#} deg included)",
                // Same sanitising as BuildEngrave: grblHAL ends a comment at the FIRST ')'.
                CommentText(tp.Text),
                CommentText(tp.FontFamily),
                tp.FontBold ? " bold" : string.Empty, tp.FontItalic ? " italic" : string.Empty,
                capHeight, cutPasses, maxDepth, halfAngle * 360d / Math.PI));

            // Between rings of the same carve a short hop clears the work - nothing on this operation
            // sticks up past the stock top - where retracting to full SafeZ 193 times would spend more
            // of the job travelling in Z than cutting. First approach and final retract still use SafeZ.
            const double hop = 2d;

            bool approached = false;
            foreach (var pass in passes)   // already shallow to deep, outside to inside - the cut order
            {
                // The depth-0 boundary rings carve NOTHING - a cone at zero depth has zero width, and the
                // groove's top edge is formed by the deeper passes' flanks anyway. Emitting them sent the
                // machine tracing every letter outline while grazing the surface - 800 lines of motion
                // before the first real plunge, which on the machine looked like the whole job "barely
                // kissing the stock".
                if (pass.Depth < 0.01d)
                    continue;

                bool first = true;
                foreach (var p in pass.Path)
                {
                    double lx = p.X + ox, ly = p.Y + oy;
                    double x = cx + lx * cos - ly * sin;
                    double y = cy + lx * sin + ly * cos;

                    if (first)
                    {
                        first = false;
                        lines.Add("G0 Z" + F(approached ? hop : SafeZ()));
                        approached = true;
                        lines.Add("G0 X" + F(x) + " Y" + F(y));
                        lines.Add("G1 Z" + F(-pass.Depth) + " F" + F(op.PlungeFeed));
                    }
                    else
                        lines.Add("G1 X" + F(x) + " Y" + F(y) + " F" + F(op.Feed));
                }
            }
            lines.Add("G0 Z" + F(SafeZ()));

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
            double bitR = op.BitDiameter / 2d;
            var radii = BoreRadii(op);

            for (int i = 0; i < radii.Count; i++)
            {
                double r = radii[i];

                // A feed word on a G2/G3 governs the PROGRAMMED path - the tool CENTRE - but the cutting
                // edge rides out at (r + bitR) and so travels (r + bitR)/r times as far per revolution.
                // On a bore whose diameter approaches the cutter's that ratio is big: a 5mm hole with a
                // 3.175mm cutter is 2.7x. The feed was being emitted uncorrected, so a value entered as
                // "what the cutter should see" arrived at the edge multiplied by it - overloading the tool
                // precisely where it is thinnest, and worst on exactly the small tapping-size bores where a
                // 1/8" cutter is most fragile. Correcting it here makes op.Feed mean the one thing an
                // operator can reason about: the speed of the CUTTING EDGE through the material.
                //
                // ONLY for a wall-following pass. This compensation is the standard one for PERIPHERAL arc
                // milling - following a wall at some radial depth of cut, where the engaged edge genuinely
                // does outrun the axis. It does NOT hold for a FULL-IMMERSION helix, and applying it there
                // is a real mistake that broke a cutter:
                //
                //   When r <= bitR the tool covers the hole centre, so it is cutting a SLOT - 180 degrees of
                //   engagement, centre to wall. In a full-immersion cut the chip thickness is set by how far
                //   the tool AXIS advances per tooth, not by how fast the wall contact point sweeps round.
                //   Halving the programmed feed there halves the actual chip, and a cutter taking a chip
                //   thinner than its own edge radius rubs instead of shearing: fine powdery chips, chatter,
                //   heat, then aluminium welds to the flute and it seizes.
                //
                //   Measured on a 5mm bore with a 2.5mm single flute at 12000 rpm: an entered 550 became a
                //   programmed 275, an axis chip load of 0.023mm against a 0.03-0.05mm target. The workpiece
                //   absorbed roughly ten times the energy the metal removal needed - all of it friction.
                //
                // So: correct only where r > bitR (an annular pass that is genuinely following a wall), and
                // otherwise emit the feed as entered, which is what this did before the correction existed
                // and was never the thing breaking cutters.
                bool wallPass = r > bitR;
                double arcFeed = wallPass && r + bitR > 1e-6 ? op.Feed * r / (r + bitR) : op.Feed;

                if (radii.Count > 1)
                    lines.Add(string.Format("(helix {0} of {1} at radius {2})", i + 1, radii.Count, F(r)));
                if (r > 1e-6)
                    lines.Add(wallPass
                        ? string.Format("(bore helix: wall pass, centre feed {0} so the cutting edge cuts at {1})", F(arcFeed), F(op.Feed))
                        : string.Format("(bore helix: full immersion, feed {0} as entered - chip load is set by axis advance)", F(arcFeed)));

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
                    string arc = BoreArcCommand(op);
                    while (z < depth - 1e-6)
                    {
                        z = Math.Min(z + step, depth);
                        lines.Add(arc + " X" + F(cx + r) + " Y" + F(cy) + " I" + F(-r) + " J0 Z" + F(-z) + " F" + F(arcFeed));
                    }
                    // The finishing lap - same circle, no Z - takes the same correction: it is the same
                    // radius, so its edge outruns its centre by the same ratio.
                    lines.Add(arc + " X" + F(cx + r) + " Y" + F(cy) + " I" + F(-r) + " J0 F" + F(arcFeed));
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
            // This pass leaves the final wall, so it is the one that carries the corner reliefs (see
            // BuildPocket, which stands down whenever a Side finish is present to do it).
            var path = OrderForDirection(Outline(tp, cx, cy, tp.IsClosed ? op.BitDiameter / 2d : 0d,
                                                 tp.IsClosed ? op.BitDiameter / 2d : 0d), tp, op);
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
            var wall = OrderForDirection(Outline(tp, cx, cy, op.BitDiameter / 2d), tp, op);

            lines.Add("G0 X" + F(cx) + " Y" + F(cy));
            AppendPlunge(lines, finishZ, 0d, op.PlungeFeed);
            foreach (var ring in ClearingRings(tp, cx, cy, op.BitDiameter, 0d, op.Stepover).Select(r => OrderForDirection(r, tp, op)))
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
            var edge = OrderForDirection(Outline(tp, cx, cy, 0d), tp, op);

            lines.Add("G0 " + XY(edge[0]));
            lines.Add("G0 Z" + F(SafeZ()));
            lines.Add("G1 Z" + F(-op.ChamferDepth) + " F" + F(op.Feed));
            for (int i = 1; i < edge.Count; i++)
                lines.Add("G1 " + XY(edge[i]) + " F" + F(op.Feed));
            lines.Add("G0 Z" + F(SafeZ()));
            return lines;
        }

        // A countersink bit plunged straight down a round hole's centerline - the bit's own cone does the
        // chamfering as it descends, so there's no outline to trace at all (unlike Chamfer above).
        // op.CountersinkDiameter is the FINISHED diameter the operator wants, not a raw depth - converted
        // here from the TOOL'S OWN included angle: half the diameter is the radius the cone must reach, and
        // it gains radius at tan(halfAngle) per unit of depth.
        //
        // This used to be a flat "depth = diameter / 2", which silently assumed every countersink was 90
        // degrees. It is only right at 90 (tan 45 = 1); on a 60-degree bit the same formula plunges to a
        // hole 15% too small, and on a 120 it goes nearly twice as deep as asked. See
        // CustomTool.IncludedAngleDeg - which defaults to 90, so an already-saved tool list reproduces the
        // old behaviour exactly.
        // PlungeFeed (not Feed) since this is a genuine axial plunge, not a corner-breaking trace.
        private static List<string> BuildCountersink(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            var tool = CustomTools.Find(op.Tool);
            double halfAngle = tool != null ? tool.HalfAngleRad : Math.PI / 4d;   // no tool record: the old 90-deg assumption
            double depth = (op.CountersinkDiameter / 2d) / Math.Tan(halfAngle);

            lines.Add("G0 X" + F(cx) + " Y" + F(cy));
            lines.Add("G0 Z" + F(SafeZ()));
            lines.Add("G1 Z" + F(-depth) + " F" + F(op.PlungeFeed));
            lines.Add("G0 Z" + F(SafeZ()));
            return lines;
        }

        // Serpentine (boustrophedon) raster over a w x h area, stepping across the shorter axis by the
        // stepover and cutting along the longer one, alternating direction - ported from the standalone
        // SurfaceSpoilboardWizard this replaced. Local coordinates, (0,0) at one corner; BuildSurface offsets
        // the result to the toolpath's own position.
        private static List<double[]> RasterPath(double w, double h, double stepover)
        {
            var pts = new List<double[]>();
            bool longIsX = w >= h;
            double longLen = longIsX ? w : h;
            double crossLen = longIsX ? h : w;
            int nRows = Math.Max(2, (int)Math.Ceiling(crossLen / stepover) + 1);
            double step = crossLen / (nRows - 1);   // even spacing, <= requested stepover, last row exactly at the far edge

            void Add(bool lx, double longVal, double crossVal) => pts.Add(lx ? new[] { longVal, crossVal } : new[] { crossVal, longVal });

            Add(longIsX, 0d, 0d);
            double curLong = 0d;
            for (int i = 0; i < nRows; i++)
            {
                double cross = Math.Min(i * step, crossLen);
                if (i > 0)
                    Add(longIsX, curLong, cross);            // step across to this row
                double endLong = curLong == 0d ? longLen : 0d;  // cut along the long axis, alternating direction
                Add(longIsX, endLong, cross);
                curLong = endLong;
            }
            return pts;
        }

        #region Entire-spoilboard machine envelope (ported from the retired standalone SurfaceSpoilboardWizard)

        // Per-axis max travel ($130 = X, $131 = Y), absolute value; 0 if unknown.
        private static double AxisTravel(int axisIndex)
        {
            double t = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + axisIndex);
            return double.IsNaN(t) ? 0d : Math.Abs(t);
        }

        // +1 if the axis travels in the +machine direction away from home, else -1.
        private static double AxisDir(int axis)
        {
            if (GrblInfo.ForceSetOrigin)
                return GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(axis)) ? 1d : -1d;
            return -1d;
        }

        // Machine-coordinate minimum (lower) corner of the travel envelope on one axis.
        private static double EnvMin(int axis)
        {
            return AxisDir(axis) > 0d ? 0d : -AxisTravel(axis);
        }

        // Safe inset from each machine limit: the homing pull-off plus 1 mm, so the raster never grazes a
        // limit switch at the home end.
        private static double EnvelopeInset()
        {
            double pulloff = GrblSettings.GetDouble(GrblSetting.HomingPulloff);
            if (double.IsNaN(pulloff)) pulloff = 0d;
            return pulloff + 1d;
        }

        #endregion

        // Faces the whole Width x Depth area centered on (cx,cy) with a single boustrophedon pass -
        // surfacing is a light skim, not stepped roughing. Ordinary WCS-relative cutting, same as every other
        // Build* - the operator's already-established Setup Z0 is trusted, same as any other operation.
        private static List<string> BuildSurface(WorkOrderToolpath tp, WorkOrderOperation op, double cx, double cy)
        {
            var lines = new List<string>();
            double w = Math.Max(0.1d, tp.Width), h = Math.Max(0.1d, tp.Depth);
            double stepover = Math.Max(0.5d, op.BitDiameter * (op.Stepover / 100d));
            double ox = cx - w / 2d, oy = cy - h / 2d;   // corner of the faced area
            var raster = RasterPath(w, h, stepover).Select(p => new[] { p[0] + ox, p[1] + oy }).ToList();
            double z = -TrueDepth(op);

            lines.Add("G0 " + XY(raster[0]));
            AppendPlunge(lines, z, 0d, op.PlungeFeed);
            for (int i = 1; i < raster.Count; i++)
                lines.Add("G1 " + XY(raster[i]) + " F" + F(op.Feed));
            lines.Add("G0 Z" + F(SafeZ()));
            return lines;
        }

        // Faces the WHOLE in-bounds machine travel envelope - spoilboard resurfacing, not a positioned cut, so
        // this toolpath's Width/Depth/X/Y are ignored. No WCS covers "the whole envelope", so this is entirely
        // machine-referenced (G53): it touches off its own fresh Z0 (the board may not be flat - trusting
        // Setup's Z0 from wherever IT was touched off would be wrong here) by jogging to the surface, same
        // MBOX-prompt idiom the retired standalone wizard used. Borrows a scratch WCS slot (ScratchWcs - never
        // the work order's own real one, WorkOrderWcs) to hold that touch-off, then restores the real one
        // before returning, since WorkOrderRules.Validate only WARNS this must be the sole enabled operation
        // rather than enforcing it structurally.
        private static List<string> BuildSurfaceEntireSpoilboard(WorkOrderOperation op)
        {
            var lines = new List<string>();
            double stepover = Math.Max(0.5d, op.BitDiameter * (op.Stepover / 100d));
            double inset = EnvelopeInset();
            double w = Math.Max(0d, AxisTravel(0) - 2d * inset);
            double h = Math.Max(0d, AxisTravel(1) - 2d * inset);
            double ox = EnvMin(0) + inset, oy = EnvMin(1) + inset;
            double zTop = EnvMin(2) + AxisTravel(2) - inset;
            double z = -TrueDepth(op);
            // G10 L2's P-number addresses a WCS by slot index (P1=G54 ... P6=G59), not by G-code - has to
            // track ScratchWcs()'s own choice of slot.
            int scratchP = ScratchWcs() == "G54" ? 1 : 2;

            lines.Add("(surface - entire spoilboard, machine-referenced)");
            lines.Add("G53 G0 Z" + F(zTop));
            lines.Add(string.Format("G53 G0 X{0} Y{1}", F(ox), F(oy)));
            lines.Add("(WAITIDLE)");
            lines.Add("(MBOX, OKCANCEL, Jog to the HIGHEST point of the board [keyboard/jog moves X, Y and Z] and lower the bit until it just touches, then click OK to set work Z0 to that height. Click Cancel to abort.)");
            lines.Add("(WAITIDLE)");
            // Same scratch slot as the X/Y anchor below - a hardcoded P1 here would write into G54's own Z
            // whenever G55 is actually the scratch slot (the work order's real WCS being G54), corrupting it.
            lines.Add(string.Format("G10 L20 P{0} Z0", scratchP));
            lines.Add("G53 G0 Z" + F(zTop));
            lines.Add("(WAITIDLE)");
            lines.Add("(MBOX, OKCANCEL, Z0 is set and Z is raised to the top. Fit the dust boot / do any final prep, then click OK to start. Click Cancel to abort.)");
            lines.Add("(WAITIDLE)");
            lines.Add(string.Format("G10 L2 P{0} X{1} Y{2}", scratchP, F(ox), F(oy)));
            lines.Add(ScratchWcs());
            lines.Add("G0 Z" + F(SafeZ()));

            // Tool change + spindle start happen HERE - after the machine-referenced park/touch-off is
            // already done, not before it (BuildOperation skips its normal AppendToolStart/End wrapper for
            // this op - see its own comment). G53 ignores the active WCS (G54-G59) but NOT an active G43
            // tool-length offset, so doing a real M6 (which applies one) BEFORE the G53 moves above would let
            // that offset stack onto their machine-coordinate targets and can overshoot a soft limit -
            // confirmed on real hardware 2026-08-01 (ALARM 2 immediately after the M6/spindle-start when tried
            // in the wrong order). The retired standalone SurfaceSpoilboardWizard always did it in this same
            // order for the same reason.
            int toolNum = ToolNumberFor(op);
            lines.Add("M6 T" + N(toolNum));
            double rpm = Rpm(op);
            if (rpm > 0d)
                lines.Add("S" + N(rpm) + " M3");

            // G10 L2 P1 above already anchored G54's origin at (ox,oy), so the raster is walked directly in
            // G54-relative coordinates starting at (0,0) - no further offset needed.
            var raster = RasterPath(w, h, stepover);
            lines.Add("G0 " + XY(raster[0]));
            AppendPlunge(lines, z, 0d, op.PlungeFeed);
            for (int i = 1; i < raster.Count; i++)
                lines.Add("G1 " + XY(raster[i]) + " F" + F(op.Feed));

            lines.Add("G0 Z" + F(SafeZ()));
            if (rpm > 0d)
                lines.Add("M5");
            lines.Add(WorkOrderWcs());   // restore the Work Order's own WCS for anything that follows
            return lines;
        }

        // One operation across EVERY pattern instance, inside a single tool change. Instance-minor rather than
        // instance-major on purpose: 6 holes drilled then 6 chamfered costs two tool changes, whereas
        // completing each hole in turn would cost twelve.
        private static List<string> BuildOperation(WorkOrderToolpath tp, WorkOrderOperation op, double openDepth, int currentTool, bool spindleOn, double currentRpm, bool sameToolNext)
        {
            var lines = new List<string>();

            // Entire Spoilboard is fully self-contained - it emits its OWN tool change/spindle start
            // positioned AFTER its machine-referenced park/touch-off (see BuildSurfaceEntireSpoilboard's own
            // comment for why that order matters), not this generic wrapper's normal before-everything-else
            // placement.
            if (op.Kind == WorkOrderOpKind.Surface && tp.EntireSpoilboard)
                return BuildSurfaceEntireSpoilboard(op);

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
                    // EntireSpoilboard never reaches here - BuildOperation returns early for it, above.
                    case WorkOrderOpKind.Surface: lines.AddRange(BuildSurface(tp, op, cx, cy)); break;
                    case WorkOrderOpKind.Engrave: lines.AddRange(BuildEngrave(tp, op, cx, cy)); break;
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

                    var ct = CustomTools.Find(op.Tool);
                    // ct is null only for a stale op.Tool referencing a tool since deleted from the list -
                    // ParameterWarnings flags that at Generate time, this just keeps the comment generic.
                    // A= was hardcoded to 90, so a 60-degree V-bit declared itself as 90 to the simulator and
                    // the Fusion post that read this - the cutter shape they drew was simply the wrong cone.
                    // It reads the tool's own angle now (CustomTool.IncludedAngleDeg, which defaults to 90,
                    // so nothing changes for a tool whose angle was never set).
                    string type = ct == null ? "FLAT"
                                : (ct.Kind == CustomToolKind.VBitOrChamfer || ct.Kind == CustomToolKind.Countersink)
                                      ? string.Format(CultureInfo.InvariantCulture, "VBIT A={0:0.#}", ct.IncludedAngleDeg)
                                : ct.Kind == CustomToolKind.BallEnd ? "BALL"
                                : "FLAT";
                    string description = ct?.Name ?? ("tool " + op.Tool);
                    // description is the operator's own custom-tool name - a tool called
                    // '1/4" endmill (roughing)' would truncate this comment exactly like the TOOLPATH one did.
                    yield return string.Format("(TOOL T={0} D={1:0.0##} TYPE={2} - {3})", t, EffectiveBitDiameter(tp, op), type, CommentText(description));
                }
        }

        // A drill's diameter IS the hole it cuts, so the bit follows the operation's own hole size; everything
        // else carries a bit independent of what it's cutting.
        public static double EffectiveBitDiameter(WorkOrderToolpath tp, WorkOrderOperation op)
        {
            return op.Kind == WorkOrderOpKind.Drill ? op.HoleDiameter : op.BitDiameter;
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
            _wcs = ResolveWcs(wo);
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
            // The WCS condition names whichever slot THIS work order actually uses (WorkOrderWcs, set from
            // wo.Wcs above) - used to hardcode G59, which failed the "is it set" check for anyone whose Setup
            // established the origin on a different WCS.
            lines.Add(string.Format("(PREREQ, connected, homed, noalarm, tlo, EXPR, {0})", WorkOrderWcs()));

            // Declare the stock, so anything reading the program knows what the material is rather than
            // inferring it. The 3D view in particular was drawing the toolpath's own bounding box plus a
            // margin and calling it the stock, which is why a 368x232 board showed up as a block the size
            // of the lettering on it (reported 2026-08-10).
            //
            // Size comes from the Setup tab's Load Stock inputs - the same values the Work Order lays its
            // toolpaths out against. OX/OY say where the stock's MINIMUM corner sits in work coordinates,
            // which for a Work Order is the origin itself: AddToolpath centres a new toolpath at
            // (Width/2, Height/2), so the stock spans 0..Width, 0..Height by construction. Stating it
            // explicitly rather than letting consumers assume it keeps a Fusion program - whose origin can
            // be anywhere on the block - reading exactly as it did before.
            var stock = StartJobConfig.Section;
            if (stock != null && stock.Width > 0d && stock.Height > 0d && stock.Thickness > 0d)
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "(STOCK X={0:0.000} Y={1:0.000} Z={2:0.000} OX=0.000 OY=0.000)",
                    stock.Width, stock.Height, stock.Thickness));

            // Needed both for the header note and to seed the spindle state below.
            int firstTool = FirstToolNumber(wo);
            bool skipFirst = wo.SkipFirstToolChange && firstTool != int.MinValue;

            if (skipFirst)
                lines.Add(string.Format("(FIRST TOOL CHANGE SKIPPED - T{0} assumed already loaded, with a valid tool length offset)", firstTool));

            lines.AddRange(ToolDeclarations(wo));
            lines.Add("G90 G94 G17 G21");
            lines.Add(WorkOrderWcs());
            // The program's OPENING retract has to be machine-referenced, not "G0 Z{SafeZ}" in the work
            // coordinate system. SafeZ is a work-Z height, meaningful only once the tool is already near the
            // job; as the very first move it is measured from the work Z origin, which on a typical job sits
            // ~100mm BELOW machine top. Starting parked at G30 (machine Z near 0), "G0 Z20" is therefore a
            // ~90mm PLUNGE, not a retract - confirmed on real hardware 2026-08-02, reported as the job
            // "dipsy-doing" downward before the tool change went off to the toolsetter, and over a spoilboard
            // with a low work Z origin it is a genuine collision risk rather than just an ugly move.
            //
            // Lift to machine top instead - unambiguous from any starting position, and the same first line
            // MacroProcessor.EmitGotoG30 uses. X/Y are named explicitly (held at their current machine
            // position) rather than writing a bare "G53 G0 Z0": see EmitGotoG30's comment - a firmware bug
            // sign-flips a homing-direction-inverted ($23) axis's parser base after a G53 move that leaves it
            // "unmoved", producing a false Alarm:2.
            //
            // Entire Spoilboard skips this entirely: it doesn't trust the work order's WCS at all (see
            // BuildSurfaceEntireSpoilboard) and its own first line is already a machine-referenced G53 rapid.
            bool entireSpoilboardOnly = wo.Toolpaths.Any(t => t.EntireSpoilboard && wo.EnabledOperations(t).Any());
            if (!entireSpoilboardOnly)
                lines.Add("G53 G0 X[#<_abs_x>] Y[#<_abs_y>] Z0");

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
                        case WorkOrderOpKind.Surface:
                            desc = tp.EntireSpoilboard
                                ? string.Format("surface - entire spoilboard, {0:0.0} mm deep", TrueDepth(op))
                                : string.Format("surface {0:0.0}x{1:0.0} mm, {2:0.0} mm deep", tp.Width, tp.Depth, TrueDepth(op));
                            break;
                        case WorkOrderOpKind.Engrave:
                            // Artwork describes itself by FILE and size - falling through to the text
                            // wording below would report a logo as v-carve "" in , 0 mm caps.
                            if (tp.Geometry == WorkOrderGeometryKind.Svg)
                            {
                                double svgH = tp.SvgWidth * SvgOutlines.AspectOf(tp.SvgFile);
                                desc = string.Format("v-carve {0}, {1:0.#} x {2:0.#} mm",
                                                     CommentText(string.IsNullOrEmpty(tp.SvgFile)
                                                                 ? "(no file)" : System.IO.Path.GetFileName(tp.SvgFile)),
                                                     tp.SvgWidth, svgH);
                                break;
                            }
                            // The operator's own text goes into a g-code comment, and grblHAL ends a comment
                            // at the FIRST ')' - so a bracket in it would truncate the block and let the rest
                            // parse as g-code. Sanitised exactly as BuildEngrave sanitises its own.
                            string shown = CommentText(tp.Text);
                            // Shape text sized to fit reports the size the fit actually resolved, marked as
                            // such - "0 mm caps" would be the field, not the cut.
                            double descCap = tp.CapHeight;
                            string sized = string.Empty;
                            if (tp.HasText && tp.Geometry != WorkOrderGeometryKind.Text && WorkOrderRules.SupportsShapeText(tp.Geometry) && tp.CapHeight <= 0d)
                            {
                                var descFit = WorkOrderTextFit.Resolve(tp);
                                if (descFit.Fits) { descCap = descFit.CapHeight; sized = " (sized to fit)"; }
                            }
                            desc = tp.IsCarved
                                ? string.Format("v-carve \"{0}\" in {1}, {2:0.#} mm caps{3}", shown, tp.FontFamily, descCap, sized)
                                : string.Format("engrave \"{0}\", {1:0.#} mm caps{2}, {3:0.###} mm stroke", shown, descCap, sized, op.EngraveWidth);
                            break;
                        default:
                            // Was "continue", which SKIPPED THE WHOLE OPERATION - BuildOperation never ran,
                            // so an operation the operator had enabled produced no g-code at all while the
                            // header still counted it and the tool still appeared in the list. That is what
                            // adding Engrave hit: it engraved nothing and the only clue was a 13-line file.
                            //
                            // A description is presentation. Losing one must never lose the CUT, so an
                            // unrecognised kind now falls back to its own label and is emitted normally.
                            // Every kind has a case above; this exists so the next one added cannot vanish.
                            desc = WorkOrderRules.OpLabel(op.Kind);
                            break;
                    }

                    // Whether the NEXT scheduled operation (if any) uses the same tool - if so, AppendToolEnd
                    // leaves the spindle running instead of stopping it just to restart a few lines later.
                    bool sameToolNext = si + 1 < schedule.Count && ToolNumberFor(schedule[si + 1].Value) == ToolNumberFor(op);

                    // Tool number up front in the header: it's what the operator has to have in the spindle, and
                    // ProgramView's title-bar tooltip reads these to list what's still to come.
                    //
                    // Timing instrumentation only. The schedule index, the description and the line count are
                    // all already computed here, so this is also exactly the granularity a progress message
                    // could use - measured first to confirm the time is where it looks like it is, rather than
                    // reporting per-operation progress and discovering one operation is the whole four minutes.
                    var swOp = System.Diagnostics.Stopwatch.StartNew();
                    int linesBefore = lines.Count;
                    AppendSection(lines, string.Format("T{0} {1} - {2}{3}", ToolNumberFor(op), tp.Name, desc, suffix),
                        BuildOperation(tp, op, usableOpen, spindleTool, spindleOn, spindleRpm, sameToolNext));
                    swOp.Stop();
                    if (DebugLog.Enabled)
                        DebugLog.Write("workorder", string.Format(CultureInfo.InvariantCulture,
                            "compile op {0}/{1} {2:0}ms | +{3} lines | {4} - {5}",
                            si + 1, schedule.Count, swOp.Elapsed.TotalMilliseconds,
                            lines.Count - linesBefore, tp.Name, desc));
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

            // Park at G30 instead of leaving the machine sitting wherever the last operation finished - same
            // raw-machine-coordinate convention tc.macro/StartJobView already use for this (reading the stored
            // G30 position directly via #5181-3, not the bare G30 word) so a G92/WCS offset in effect at the
            // end of the job can't send this move somewhere unexpected. Confirmed as a real gap on real
            // hardware 2026-07-30 - the machine (and spindle, which AppendToolEnd already stopped on the last
            // operation, however that call came out) was left sitting directly over the just-cut material.
            //
            // Use the shared EmitGotoG30 rather than hand-rolling it. The hand-rolled version here retracted
            // with a work-coordinate "G0 Z{SafeZ}" and then traversed - which is NOT Safe Z: with a work Z
            // origin ~113mm below machine top, "G0 Z20" left the tool ~93mm below machine top and it crossed
            // the whole table at that height. Confirmed on real hardware 2026-08-02. It also wrote a bare
            // "G53 G0 Z#5183" with X/Y unmoved, the exact shape of the false-Alarm:2 firmware bug EmitGotoG30
            // names both axes to avoid.
            MacroProcessor.EmitGotoG30(lines.Add);
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
