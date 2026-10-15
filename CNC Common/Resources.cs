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
