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
