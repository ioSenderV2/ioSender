/*
 * WorkSurface.cs - part of CNC Controls library
 *
 * How much of the table is actually board.
 *
 * Two features used to answer that question by asking the MACHINE how far it can travel: the Height Map
 * tab's "Full work surface" and the Work Order's Entire Spoilboard toolpath both derived their extent from
 * $130-$132. That is only right when the spoilboard fills the table, and on a machine with a toolsetter
 * mounted off the front of the board it is actively wrong: the gantry can reach the toolsetter, so Y travel
 * runs well past the board's front edge. Confirmed on this machine 2026-08-19 - G59.3 sits at Y-838 while
 * the board stops around Y-780, so the last ~60mm of Y travel is air with the toolsetter standing in it.
 *
 * For the height map that wasted probes over nothing. For surfacing it drove a spinning cutter across the
 * toolsetter's neighbourhood, which is the same mistake with a much worse ending.
 *
 * So the board's extent is its own machine fact, kept here, and NOT expressed by shrinking the travel
 * limits: the machine really can reach Y-838 and must keep being allowed to, or tc.macro can never drive to
 * the toolsetter at all. Travel describes the gantry; this describes the board.
 *
 * Undefined (the default) means "the board fills the table", which is what both callers assumed before this
 * existed - so a machine where that is true needs no setup and behaves exactly as it did.
 */

using System;
using System.Xml.Serialization;
using CNC.Core;

namespace CNC.Controls
{
    public class WorkSurface
    {
        /// <summary>
        /// False until the operator states the board's size, in which case the full travel envelope is used
        /// - the previous behaviour, and correct wherever the board really does cover the table.
        /// </summary>
        public bool Defined { get; set; } = false;

        /// <summary>Board extent in MACHINE coordinates. Only meaningful when <see cref="Defined"/>.</summary>
        public double MinX { get; set; } = 0d;
        public double MaxX { get; set; } = 0d;
        public double MinY { get; set; } = 0d;
        public double MaxY { get; set; } = 0d;

        /// <summary>The live instance from the config store; never null so callers need no guard.</summary>
        [XmlIgnore]
        public static WorkSurface Current
        {
            get { return ConfigStore.Get<WorkSurface>() ?? fallback; }
        }

        private static readonly WorkSurface fallback = new WorkSurface();

        // ---- the travel envelope, the fallback and the safety clamp ----
        //
        // These duplicated themselves into HeightMapView and WorkOrderCompiler, which is part of how the two
        // came to disagree about what they were measuring. One copy, here, next to the thing that overrides it.

        public static double AxisTravel(int axis)
        {
            double t = GrblSettings.GetDouble(GrblSetting.MaxTravelBase + axis);
            return double.IsNaN(t) ? 0d : Math.Abs(t);
        }

        private static double AxisDir(int axis)
        {
            if (GrblInfo.ForceSetOrigin)
                return GrblInfo.HomingDirection.HasFlag(GrblInfo.AxisIndexToFlag(axis)) ? 1d : -1d;
            return -1d;
        }

        /// <summary>Machine-coordinate minimum of the travel envelope for an axis.</summary>
        public static double TravelMin(int axis)
        {
            return AxisDir(axis) > 0d ? 0d : -AxisTravel(axis);
        }

        /// <summary>
        /// How far to stay off the travel limits. Never less than 5mm, and always clear of the homing
        /// pull-off, so a move to the computed edge cannot trip a limit switch.
        /// </summary>
        public static double Inset()
        {
            double pulloff = GrblSettings.GetDouble(GrblSetting.HomingPulloff);
            if (double.IsNaN(pulloff)) pulloff = 0d;
            return Math.Max(5d, pulloff + 1d);
        }

        /// <summary>
        /// Usable minimum for an axis: the board's edge when one is defined, the travel envelope otherwise,
        /// and in both cases held clear of the limits by <see cref="Inset"/>.
        ///
        /// Clamped rather than trusted - a board extent typed larger than the machine, or left over from a
        /// different machine profile, must not become permission to drive into a limit switch.
        /// </summary>
        public double UsableMin(int axis)
        {
            double travelMin = TravelMin(axis) + Inset();
            if (!Defined)
                return travelMin;
            double boardMin = axis == 0 ? MinX : MinY;
            return Math.Max(boardMin, travelMin);
        }

        /// <summary>Usable maximum for an axis. See <see cref="UsableMin"/>.</summary>
        public double UsableMax(int axis)
        {
            double travelMax = TravelMin(axis) + AxisTravel(axis) - Inset();
            if (!Defined)
                return travelMax;
            double boardMax = axis == 0 ? MaxX : MaxY;
            return Math.Min(boardMax, travelMax);
        }

        /// <summary>Usable span for an axis; 0 when the numbers describe nothing workable.</summary>
        public double UsableSpan(int axis)
        {
            return Math.Max(0d, UsableMax(axis) - UsableMin(axis));
        }

        /// <summary>One line for the operator: what the board is, against what the machine can reach.</summary>
        public string Summary
        {
            get
            {
                if (!Defined)
                    return string.Format("Not set - the whole table is treated as board ({0:0} x {1:0} mm of travel).",
                                         AxisTravel(0), AxisTravel(1));

                return string.Format("Board {0:0} x {1:0} mm, of {2:0} x {3:0} mm of travel.",
                                     UsableSpan(0), UsableSpan(1), AxisTravel(0), AxisTravel(1));
            }
        }
    }
}
