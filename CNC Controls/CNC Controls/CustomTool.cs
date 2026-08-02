/*
 * CustomTool.cs - part of CNC Controls library
 *
 * User-defined tools for Work Order, added from Settings:App > Work Order (OddJobsSettingsControl) - lets
 * an operator add a bit ioSender doesn't ship a preset for (e.g. a 1/16" 1-flute O-flute) without a code
 * change. Kind selects which of the existing feed/speed formulas and operation-kind restrictions applies -
 * mirrors the buckets the built-in OddJobsTool enum already falls into (see
 * OddJobsFeedsSpeedsDialog.ToolType() and RestrictToolsFor's isMill/isDrill/isChamfer/isCountersink groups).
 * Persisted as an App.config section via AppConfig.RegisterFolded, same idiom as OddJobsToolMemory.
 */

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
    }

    public class CustomToolList
    {
        public List<CustomTool> Entries = new List<CustomTool>();
    }

    public static class CustomTools
    {
        // WorkOrderOperation.Tool (int) space: 0..N-1 are OddJobsTool enum values (unchanged, still
        // append-only/persisted - see that enum's own comment). A custom tool's op.Tool = IdBase + tool.Id,
        // so every existing persisted work order/enum value is completely unaffected - this is purely
        // additive new range, never colliding with a real (or future) OddJobsTool value.
        public const int IdBase = 10000;

        public static CustomToolList SectionConfig;

        public static CustomTool Find(int opTool)
        {
            return opTool >= IdBase ? SectionConfig?.Entries?.FirstOrDefault(t => t.Id == opTool - IdBase) : null;
        }

        public static int NextId()
        {
            return (SectionConfig?.Entries?.Count > 0 ? SectionConfig.Entries.Max(t => t.Id) : 0) + 1;
        }

        public static void Save()
        {
            AppConfig.Settings.Save();
        }

        // "is this op.Tool value (built-in OR custom) a countersink-kind tool" - the one check several call
        // sites need in a form that works for both. Mirrors OddJobsFeedsSpeedsDialog.IsCountersinkBit's
        // built-in-only version.
        public static bool IsCountersink(int opTool)
        {
            var ct = Find(opTool);
            if (ct != null)
                return ct.Kind == CustomToolKind.Countersink;
            return opTool >= 0 && opTool < IdBase && OddJobsFeedsSpeedsDialog.IsCountersinkBit((OddJobsTool)opTool);
        }
    }
}
