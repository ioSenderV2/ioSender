/*
 * StartJobConfig.cs - part of CNC Controls library
 *
 * Persisted Load Stock inputs, folded into App.config as the "StartJob" section (was the standalone
 * StartJob.xml). The DTO and its static holder live here (rather than with StartJobView in the top
 * assembly) so AppConfig - which registers the section and reads/writes the holder - can reference the type.
 * StartJobView is event-driven (not dependency-property based), so it doesn't derive from ConfigPanel<T>;
 * it reads/writes StartJobConfig.Section directly and calls AppConfig.Settings.Save().
 */

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
        // material (metal) - now derived from the selected Material (FeedsSpeedsAdvisor.MaterialRef.Conductive),
        // not a separate saved field.
        public string Probe = "ThreeDProbe";   // "ThreeDProbe" or "TouchPlate" (UI selection only - not yet wired into BuildProgram)
        public string Fixture = string.Empty;   // selected fixture's Name (Machine Setup > Fixture definitions)
        // Dynamic fixture's Geometry panel (External/Internal + Is Circle + picked corner/edge) and the
        // "Probe height map" run options - saved so the picker doesn't reset to the front-left/External
        // default every time the Dynamic fixture is reselected.
        public bool GeomInternal = false;
        public bool GeomIsCircle = false;
        public string GeomProbeEdge = "None";   // CNC.Controls.Probing.Edge name - "None" = front-left default
        public int GeomCenterPasses = 1;
        public bool HeightMap = false;
        public double HeightMapGridX = 25d;
        public double HeightMapGridY = 25d;
        // Entered on both Start Job instances now (it's a Setup-level fact - what am I cutting - same as
        // stock location), but only actually CONSUMED by the Odd Jobs job wizards' Feeds and Speeds
        // recommendation (FeedsSpeedsAdvisor.MaterialRefs key); a loaded file has no equivalent consumer yet.
        public string Material = string.Empty;
        // The safe-Z retract height every Odd Jobs job wizard's own generated program uses between
        // passes/tool changes - NOT the same thing as CornerTravelMarginMm ("Safe Z delta" above), which is
        // specific to Start Job's own corner-probing macro. A loaded file has no equivalent consumer.
        public double SafeZ = 20d;
        // General to both Start Job instances (the real tab AND Odd Jobs' Setup sub-tab, now sharing this one
        // section): perimeter clearance to stay clear of - typically clamps/screws holding the stock down
        // around its outer edge. Both instances draw it as a dotted red inset on the stock outline; the Odd
        // Jobs job wizards' own toolpaths (e.g. Surface Stock's raster) additionally stay inside it, so it
        // never has to be re-entered per job. The real Start Job tab has no toolpath of its own to keep clear
        // of anything - there it's purely a visual reference while jogging/placing clamps.
        public double KeepOutInset = 15d;
        // Display-only preference: Width/Height/Thickness/SpacerThickness above are ALWAYS persisted in mm
        // (everything downstream - BuildProgram, the drawing, warnings - assumes mm) regardless of this flag;
        // it only controls which unit the Stock size fields show/accept on screen (StartJobView's mm/in toggle).
        public bool IsImperial = false;
    }

    // Static holder backing the "StartJob" App.config section (read/written by AppConfig.RegisterFolded and
    // by StartJobView's LoadInputs/SaveInputs). Shared by BOTH StartJobView instances now - the real Start Job
    // tab and Odd Jobs' "Setup" sub-tab (job-flow unification, 2026-07-31): Setup is one persistent fact
    // regardless of what program you're about to run, not something duplicated per program source. It used to
    // be two independent sections (a separate OddJobsSetupConfig, deliberately pinned to G59 so it could never
    // touch this one) - that isolation solved a problem that doesn't actually exist: running G-code against a
    // WCS only READS it, and the only thing that ever WRITES to it is the explicit Setup action itself, which
    // was already its own deliberate, rare step. See StartJobView's suppressRotationForOddJobs for the one
    // remaining (temporary) difference between the two instances.
    //
    // There is deliberately NO completion gate here.
    //
    // Odd Jobs used to hide the job tabs until Setup had provably run: an in-memory "completed" flag armed by
    // Setup's own Run and torn down live by a rehome, a G59 move or the TLO reference clearing, plus a
    // -trustme launch flag to bypass it. It cost far more than it bought - it kept getting in the way of
    // simply composing a job, tabs vanished out from under the operator, and the invalidation watches produced
    // their own false positives (a plain Reset re-reading G59 0.011mm off, a soft reset blipping HomedState)
    // that closed the gate for no real reason.
    //
    // What replaced it: Generate asks, once, whether to go ahead on the cached origin and tool length
    // reference (see WorkOrderView.Generate). The operator is the one who knows whether those are still good,
    // and can now build and inspect a work order freely without any of it being gated on the machine.
    public static class StartJobConfig
    {
        public static StartJobSettings Section;
    }
}
