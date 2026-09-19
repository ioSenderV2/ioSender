/*
 * HeightMapCompensation.cs - part of CNC Controls library
 *
 * The seam between a Work Order that wants its program height-compensated and the code that can actually
 * do it.
 *
 * ---- Why a seam and not a call ----
 *
 * The height map, its store and the transform all live in CNC Controls Probing, and Probing REFERENCES
 * CNC Controls, not the other way round. Work Order lives here. So this assembly cannot call that one, and
 * inverting the reference to suit one feature would be the wrong trade by a distance.
 *
 * Instead the application wires the two together at startup, exactly as it already does for the other
 * cross-layer behaviours in this codebase (MacroProcessor.ActiveGenerate, UserPrompt.Handler). Unwired,
 * every member here answers "no map" and Work Order's option disables itself with a reason - which is also
 * what a build without the Probing assembly should do.
 */

using System;

namespace CNC.Controls
{
    public static class HeightMapCompensation
    {
        /// <summary>True when a map has been probed and kept. Wired to SetupHeightMap.HasMap.</summary>
        public static Func<bool> HasMap;

        /// <summary>One line describing the stored map and its age, for the Work Order's summary.</summary>
        public static Func<string> Describe;

        /// <summary>
        /// Why the stored map must NOT be applied to the setup as it stands, or null when it may be.
        /// Returns operator-facing text - see SetupHeightMap.WhyNotApplicable. The argument asks for the
        /// machine's offsets to be RE-READ first rather than taken from the last report; see Refusal.
        /// </summary>
        public static Func<bool, string> WhyNotApplicable;

        /// <summary>
        /// Apply the stored map to the loaded program. Returns null on success, or operator-facing text
        /// explaining why nothing was applied.
        /// </summary>
        public static Func<string> ApplyToLoadedProgram;

        /// <summary>
        /// Adopt a .map file as the current map. Returns null on success, or operator-facing text.
        /// Used for the sidecar a work order carries beside it.
        /// </summary>
        public static Func<string, string> LoadFromFile;

        public static bool Available { get { return HasMap != null && HasMap(); } }

        public static string DescribeMap()
        {
            return Describe != null ? Describe() : "height map compensation is not available in this build";
        }

        /// <summary>
        /// The refusal text for applying the stored map right now, or null when it can be applied.
        /// Unwired counts as a refusal rather than as permission: silently skipping compensation a work
        /// order explicitly asked for would leave the operator cutting an uncompensated job believing
        /// otherwise, which is the one outcome worse than refusing.
        /// </summary>
        /// <param name="fresh">
        /// True when this answer DECIDES something - the check Generate makes before applying the map - so
        /// the machine's own offsets are re-read first instead of trusted from whenever they last happened
        /// to arrive. False for a summary line on a UI refresh, which must not make a blocking round trip
        /// to the controller. See SetupHeightMap.WorkOrigin for what went wrong without the distinction.
        /// </param>
        public static string Refusal(bool fresh = false)
        {
            if (WhyNotApplicable == null)
                return "Height map compensation is not available in this build.";
            return WhyNotApplicable(fresh);
        }
    }
}
