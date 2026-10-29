/*
 * Resources.cs - part of CNC Common
 *
 * Host-environment paths and locale, moved verbatim from CNC Core\Grbl.cs (namespace kept:
 * CNC.Core). Config dir, ini name, logs/backups/generated/work-order folders - both the server
 * and any client host need these, and none of it is machine state. The one machine fact that
 * used to live here (IsLegacyController) moved to GrblInfo where its readers are.
 */

using System;

namespace CNC.Core
{
    public class Resources
    {
        public static string Path { get; set; }
        public static string Locale { get; set; }
        public static string IniName { get; set; }
        public static string IniFile { get { return (System.IO.Path.IsPathRooted(IniName) ? "" : ConfigPath) + IniName; } }
        public static string DebugFile { get; set; } = string.Empty;
        public static string ConfigPath { get; set; }

        public static string BackupsFolder { get { return System.IO.Path.Combine(ConfigPath, "Backups"); } }

        // UI presets - named screen arrangements the operator switches between (the selector in the menu
        // row; see UiPresets.cs). One file per preset, in the .ioconfig document format ConfigOverlay
        // already defines, so a preset IS shareable as an overlay and "Export" is a straight file copy.
        //
        // A preset differs from an overlay in lifetime, not in format: an overlay is a patch applied once
        // and then indistinguishable from the profile, whereas the file under here stays the live copy of
        // that named arrangement and is written back whenever the operator switches away from it.
        public static string PresetsFolder { get { return System.IO.Path.Combine(ConfigPath, "Presets"); } }

        // Presets shipped with the build, offered read-only alongside the operator's own. Editing one and
        // switching away writes the edited copy into PresetsFolder under the same name, which SHADOWS the
        // shipped file - the install folder is never written to (it may not even be writable), and the
        // original stays recoverable by deleting the shadow.
        //
        // Deliberately not copied into the per-user folder at startup: a "copy it in if absent" step is
        // exactly how a file the operator deleted comes back on the next launch.
        public static string ShippedPresetsFolder { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets"); } }

        // Shared by the crash log, debug log and console log: resolve (and create) the "logs"
        // subfolder under the config dir, falling back to %AppData%\ioSender\logs (ConfigPath may
        // still be unresolved this early in startup, e.g. during a crash before settings load), and
        // as a last resort the app's own base directory. Never throws.
        public static string ResolveLogsDirectory()
        {
            string dir;
            try
            {
                dir = ConfigPath;
                if (string.IsNullOrEmpty(dir) || dir == "./" || !System.IO.Path.IsPathRooted(dir))
                    dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ioSender");
                dir = System.IO.Path.Combine(dir, "logs");
                System.IO.Directory.CreateDirectory(dir);
            }
            catch
            {
                try
                {
                    dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    System.IO.Directory.CreateDirectory(dir);
                }
                catch { dir = AppDomain.CurrentDomain.BaseDirectory; }
            }
            return dir;
        }

        // Every MacroProcessor.Run() call writes its g-code here first (see MacroProcessor.cs) - a
        // persistent, inspectable copy of the LAST thing each Generate button actually built, named after
        // the run (e.g. "Start Job" -> "start_job.macro"). Overwritten each run - this is a debugging aid,
        // not a history; the streamed program itself is never saved to disk otherwise.
        public static string GeneratedFolder { get { return System.IO.Path.Combine(ConfigPath, "Generated"); } }

        // Odd Jobs work orders saved by name (Save.../Load... on the Work Order tab). Distinct from the single
        // live work order kept in App.config, which is just "what the tab was left showing".
        public static string WorkOrdersFolder { get { return System.IO.Path.Combine(ConfigPath, "WorkOrders"); } }

        static Resources()
        {
            ConfigPath = Path = @"./";
            Locale = "en-US";
            IniName = "App.config";
        }
    }
}
