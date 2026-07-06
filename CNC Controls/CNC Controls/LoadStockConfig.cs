/*
 * LoadStockConfig.cs - part of CNC Controls library
 *
 * Persisted Load Stock inputs, folded into App.config as the "LoadStock" section (was the standalone
 * LoadStock.xml). The DTO and its static holder live here (rather than with LoadStockView in the top
 * assembly) so AppConfig - which registers the section and reads/writes the holder - can reference the type.
 * LoadStockView is event-driven (not dependency-property based), so it doesn't derive from ConfigPanel<T>;
 * it reads/writes LoadStockConfig.Section directly and calls AppConfig.Settings.Save().
 */

namespace CNC.Controls
{
    // Persisted Load Stock inputs so the estimate/corner/options survive restarts.
    public class LoadStockSettings
    {
        public double Width = 100d;
        public double Height = 100d;
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
    }

    // Static holder backing the "LoadStock" App.config section (read/written by AppConfig.RegisterFolded and
    // by LoadStockView's LoadInputs/SaveInputs).
    public static class LoadStockConfig
    {
        public static LoadStockSettings Section;
    }
}
