/*
 * StartJobConfig.cs - part of CNC Controls library
 *
 * Persisted Load Stock inputs, folded into App.config as the "StartJob" section (was the standalone
 * StartJob.xml). The DTO and its static holder live here (rather than with StartJobView in the top
 * assembly) so AppConfig - which registers the section and reads/writes the holder - can reference the type.
 * StartJobView is event-driven (not dependency-property based), so it doesn't derive from ConfigPanel<T>;
 * it reads/writes StartJobConfig.Section directly and calls AppConfig.Settings.Save().
 */

using System;

namespace CNC.Controls
{
    // Persisted Load Stock inputs so the estimate/corner/options survive restarts.
    public class StartJobSettings
    {
        public double Width = 100d;
        public double Height = 100d;
        public double Thickness = 19d;   // estimated stock thickness (Z), mm; only used for the <= 1 in probe check
        // Sacrificial spacer/backer board thickness (mm) UNDER the stock, same footprint (e.g. 1/4" MDF that a
        // thin aluminium sheet is taped to). The corner probe finds the real spoilboard, so the effective "floor"
        // for the face-probe start height is spoilboard + spacer - without this a thin sheet gets probed down in
        // the spacer/tape band instead of on the metal. 0 = no spacer (bare fence). Passed to pcorner as _ls_spacer.
        public double SpacerThickness = 0d;
        // Corner Fence Measure only: corners 2-4 travel at corner 1's own measured stock top plus this margin
        // (see pcorner.macro's #<_ls_maxz>) instead of retracting fully to machine top between corners. Must
        // clear any fence/clamp hardware between corners on this fixture - not a universal default, hence
        // adjustable rather than hardcoded.
        public double CornerTravelMarginMm = 10d;
        // Corner Fence Measure only: off (default) treats width/height as a conservative estimate (padded);
        // on, they're trusted exact and corners 2-4 probe close to their computed true position (corner 2
        // assumes near-zero skew; corners 3/4 use the skew measured from corners 1-2) instead of the padded
        // reference. See BuildProgram - only changes the REFERENCE fed to pcorner.macro, not the macro itself.
        public bool ExactSize = false;
        public string Corner = "FrontLeft";
        public int Wcs = 1;            // 1 = G54
        public bool Measure = true;
        // Default OFF: setting a WCS rotation (G10 L2 R) arms a grblHAL rotation-transform bug on affected
        // firmware - a garbage runtime rotation is applied to far-from-origin G54 moves, throwing the first
        // cut rapid off the table (false Alarm:2 soft limit) on the NEXT program run, even for a ~0.1 deg
        // skew. Load Stock itself stays clean (it scrubs R0 before probing); the rotation only bites the
        // program that runs after. Opt in only on firmware with the rotation transform fixed.
        public bool ApplyRotation = false;   // set the WCS rotation from the measured skew (G10 L2 R)
        public bool SetTloRef = false;        // reference the puck TLO after corner 1 (Load Stock == start_job)
        // Off: the generated program probes/measures as usual but skips the G10 L2 X/Y/Z origin commit (and,
        // in BuildProgram, the WCS rotation write too - rotating an origin this run doesn't touch would be
        // meaningless) - lets Measure/Verify-style runs report numbers without moving the selected WCS.
        public bool SetOrigin = true;
        // A touch plate probes by electrical continuity with the stock, so it only works on conductive
        // material (metal). Gates the Probe selection below - unchecked forces "ThreeDProbe".
        public bool StockConductive = false;
        public string Probe = "ThreeDProbe";   // "ThreeDProbe" or "TouchPlate" (UI selection only - not yet wired into BuildProgram)
        public string Fixture = string.Empty;   // selected fixture's Name (Machine Setup > Fixture definitions)
        // Odd Jobs' constrained Setup instance only: stock material (FeedsSpeedsAdvisor.MaterialRefs key),
        // used by the job wizards' Feeds and Speeds recommendation. Unused/blank on the real Start Job tab.
        public string Material = string.Empty;
        // Odd Jobs' constrained Setup instance only: the shared safe-Z retract height every job wizard's
        // own generated program uses between passes/tool changes - NOT the same thing as
        // CornerTravelMarginMm ("Safe Z delta" above), which is specific to Start Job's own corner-probing
        // macro. Unused on the real Start Job tab.
        public double SafeZ = 20d;
        // General to both Start Job instances (the real tab AND Odd Jobs' constrained Setup): perimeter
        // clearance to stay clear of - typically clamps/screws holding the stock down around its outer edge.
        // Both instances draw it as a dotted red inset on the stock outline; the Odd Jobs job wizards' own
        // toolpaths (e.g. Surface Stock's raster) additionally stay inside it via OddJobsSetupConfig, so it
        // never has to be re-entered per job. The real Start Job tab has no toolpath of its own to keep clear
        // of anything - there it's purely a visual reference while jogging/placing clamps.
        public double KeepOutInset = 15d;
        // Display-only preference: Width/Height/Thickness/SpacerThickness above are ALWAYS persisted in mm
        // (everything downstream - BuildProgram, the drawing, warnings - assumes mm) regardless of this flag;
        // it only controls which unit the Stock size fields show/accept on screen (StartJobView's mm/in toggle).
        public bool IsImperial = false;
    }

    // Static holder backing the "StartJob" App.config section (read/written by AppConfig.RegisterFolded and
    // by StartJobView's LoadInputs/SaveInputs).
    public static class StartJobConfig
    {
        public static StartJobSettings Section;
    }

    // Separate persisted holder for the Odd Jobs tab's own "Setup" sub-tab - a second, independent instance
    // of StartJobView (constrainedToOddJobs: true) that always targets G59 with Measure/TLO ref forced on,
    // so it never overwrites the real Start Job tab's own StartJobConfig.Section (which may be aimed at a
    // different WCS for the operator's loaded G-code file). Same DTO shape, own "OddJobsSetup" App.config
    // section (see AppConfig.RegisterFolded).
    public static class OddJobsSetupConfig
    {
        private static StartJobSettings _section;

        // A property (not a plain field) so OddJobsView can react immediately when Setup's Generate commits
        // new settings (SetOrigin becoming true), instead of only re-checking on some later tab-switch.
        public static StartJobSettings Section
        {
            get { return _section; }
            set { _section = value; Changed?.Invoke(); }
        }

        // Raised whenever Section is (re)assigned - i.e. whenever Setup's Generate_Click saves its inputs
        // (see StartJobView.SaveInputs via its Section property) - or whenever IsCompleted below changes.
        // OddJobsView subscribes to re-enable/disable the other 4 job tabs without waiting for a tab switch.
        public static event Action Changed;

        // There is deliberately NO completion gate here any more.
        //
        // Odd Jobs used to hide the job tabs until Setup had provably run: an in-memory "completed" flag armed
        // by Setup's own Run and torn down live by a rehome, a G59 move or the TLO reference clearing, plus a
        // -trustme launch flag to bypass it. It cost far more than it bought - it kept getting in the way of
        // simply composing a job, tabs vanished out from under the operator, and the invalidation watches
        // produced their own false positives (a plain Reset re-reading G59 0.011mm off, a soft reset blipping
        // HomedState) that closed the gate for no real reason.
        //
        // What replaced it: Generate asks, once, whether to go ahead on the cached G59 origin and tool length
        // reference (see WorkOrderView.Generate). The operator is the one who knows whether those are still
        // good, and can now build and inspect a work order freely without any of it being gated on the machine.
    }
}
