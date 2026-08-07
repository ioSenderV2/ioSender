/*
 * CustomTool.cs - part of CNC Controls library
 *
 * Every Work Order tool - both the factory-default set (seeded into Default-App.config at Id 0-13, promoted
 * into an existing App.config the same way any other new default-config key is - see
 * ConfigStore.ReadDocument's template-fallback path) and anything the operator adds from Settings:App > Work
 * Order (OddJobsSettingsControl, "+" button / CustomToolEditDialog). There is no separate hardcoded tool
 * list any more - Kind selects which of the feed/speed formulas and operation-kind restrictions applies (see
 * OddJobsFeedsSpeedsDialog.ToolType() and RestrictToolsFor's isMill/isDrill/isChamfer/isCountersink groups).
 * Persisted as an App.config section via AppConfig.RegisterFolded, same idiom as OddJobsToolMemory.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace CNC.Controls
{
    // EndMill/OFlute/BallEnd/Surfacing all use the exact same "mill" feed formula (chip-load x RPM x
    // flutes) - kept as separate named values (rather than one generic "Mill") only because BallEnd and
    // Surfacing each carry one real behavioral difference the others don't: BallEnd declares TYPE=BALL
    // (not FLAT) in the (TOOL ...) g-code comment, read by the simulator/Fusion post for cutter-shape
    // material removal; Surfacing is additionally offered on a Surface toolpath's tool dropdown, not just
    // ordinary milling operations (see OddJobsFeedsSpeedsDialog.RestrictToolsFor's isMill||isSurface).
    // EndMill and OFlute are otherwise identical to each other in every switch in this codebase - the
    // distinction is purely for the operator's own clarity in the dropdown.
    public enum CustomToolKind { EndMill, OFlute, BallEnd, Surfacing, Drill, VBitOrChamfer, Countersink }

    public class CustomTool
    {
        // Stable, assigned once at creation (CustomTools.NextId) - never the list index, since deleting an
        // earlier entry would shift every later one's index but must not repoint WorkOrderOperation.Tool
        // values already saved against it.
        public int Id;
        public string Name = string.Empty;
        public CustomToolKind Kind = CustomToolKind.EndMill;
        public double DiameterMm = 6.35d;
        // Ignored for Drill/Countersink (their advisor formulas don't use flute count) - harmless to leave
        // set, just not shown/editable for those kinds in CustomToolEditDialog.
        public int Flutes = 2;
        // 0 = no override, let FeedsSpeedsAdvisor's material table drive the RPM (the normal case). Set only
        // on the seeded countersink defaults, whose real bit rating isn't in the advisor's tables - see
        // OddJobsFeedsSpeedsDialog.cbxTool_SelectionChanged's own comment on where the 8000 rpm came from.
        // Not operator-editable via CustomToolEditDialog (retune the RPM field itself instead) - this is
        // just the one-time starting value shown when the tool is first selected.
        public double DefaultRpm = 0d;

        // Full included angle of a V-shaped tool, in degrees - the angle BETWEEN the two flanks, which is
        // how bits are sold ("60 degree V-bit"), not the half-angle the trigonometry actually wants.
        // Meaningful only for VBitOrChamfer and Countersink; ignored (and hidden in the edit dialog) for
        // every other kind, the same way Flutes is for Drill/Countersink.
        //
        // It matters because the tool's angle is what converts between depth and width. A V-bit plunged
        // 1 mm cuts 2 mm wide at 90 degrees but only 1.15 mm at 60 - so engraving and countersinking both
        // give the wrong size if the angle is assumed. BuildCountersink assumed 90 outright.
        //
        // Defaults to 90 rather than 0 deliberately: an existing tool list has no such element, so every
        // saved tool deserializes to exactly the value that was previously hardcoded, and no installed
        // work order changes behaviour. 0 would have been a silent divide-into-nonsense.
        public double IncludedAngleDeg = 90d;

        /// <summary>
        /// Half the included angle in RADIANS - what the depth/width trigonometry actually takes. Clamped
        /// well away from 0 and 180 so a nonsense value cannot produce an infinite or negative depth;
        /// tan() at those limits is where a bad tool definition would otherwise turn into a plunge.
        /// </summary>
        public double HalfAngleRad
        {
            get
            {
                double deg = IncludedAngleDeg;
                if (double.IsNaN(deg) || deg < 1d || deg > 179d)
                    deg = 90d;
                return deg * Math.PI / 360d;   // /2 for half-angle, then degrees->radians
            }
        }

        /// <summary>
        /// What this tool will actually cut when asked for a stroke <paramref name="requestedWidth"/> mm
        /// wide: how deep to plunge, the width really achieved, and whether the request had to be limited.
        /// </summary>
        /// <remarks>
        /// The limit is real geometry, not a policy choice. A V-bit's quoted diameter is its MAXIMUM
        /// cutting diameter - the width of the cone where the flutes end and the shank begins - so it is
        /// also the widest stroke the bit can engrave. Ask for more and the arithmetic happily returns a
        /// depth as though the cone continued forever, and the machine drives the SHANK into the work.
        ///
        ///     max usable depth = (D/2) / tan(halfAngle)
        ///     1/4" 90 deg -> 3.18 mm deep, 1/4" 60 deg -> 5.50 mm deep; both cap at a 6.35 mm stroke
        ///
        /// Note the maximum width is the diameter whatever the angle - the angle only decides how far down
        /// you travel to reach it.
        ///
        /// Lives here, on the tool, so the compiler and the UI readout share one answer. They were already
        /// computing depth separately from the same formula, which is exactly the arrangement that drifts.
        /// </remarks>
        public EngraveCut EngraveCutFor(double requestedWidth)
        {
            var cut = new EngraveCut();

            // A tool with no diameter recorded cannot be checked against one - don't invent a limit that
            // would silently narrow a stroke the operator asked for.
            cut.MaxWidth = DiameterMm > 0d ? DiameterMm : double.MaxValue;

            double want = Math.Max(0.01d, requestedWidth);
            cut.Clamped = want > cut.MaxWidth;
            cut.Width = cut.Clamped ? cut.MaxWidth : want;
            cut.Depth = (cut.Width / 2d) / Math.Tan(HalfAngleRad);

            return cut;
        }
    }

    /// <summary>The result of asking a tool for a given engraved stroke width - see CustomTool.EngraveCutFor.</summary>
    public struct EngraveCut
    {
        public double Depth;      // mm to plunge
        public double Width;      // the width actually cut - equals what was asked for unless Clamped
        public double MaxWidth;   // the widest stroke this tool can cut at all
        public bool Clamped;      // the request exceeded MaxWidth and was limited to it
    }

    public class CustomToolList
    {
        public List<CustomTool> Entries = new List<CustomTool>();
    }

    public static class CustomTools
    {
        // WorkOrderOperation.Tool IS the tool's Id directly - no offset. The factory-default tools are
        // seeded at Id 0-13 (matching the retired OddJobsTool enum's own ordinals exactly, so a work order
        // saved before this file existed still resolves to the same tool); NextId() naturally continues
        // from there for anything the operator adds afterward.
        public static CustomToolList SectionConfig;

        public static CustomTool Find(int opTool)
        {
            return SectionConfig?.Entries?.FirstOrDefault(t => t.Id == opTool);
        }

        public static int NextId()
        {
            return (SectionConfig?.Entries?.Count > 0 ? SectionConfig.Entries.Max(t => t.Id) : 0) + 1;
        }

        public static void Save()
        {
            AppConfig.Settings.Save();
        }

        // "is this op.Tool value a countersink-kind tool" - the one check several call sites need.
        public static bool IsCountersink(int opTool)
        {
            return Find(opTool)?.Kind == CustomToolKind.Countersink;
        }
    }
}
