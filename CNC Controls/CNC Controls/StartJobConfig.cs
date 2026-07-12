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
        public string Probe = string.Empty;
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
}
