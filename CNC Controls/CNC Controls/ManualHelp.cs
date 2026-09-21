/*
 * ManualHelp.cs - deep-links the running app into the ioSender V2 user manual.
 *
 * The manual is a single self-contained HTML file (docs/manual/index.html) whose topic
 * sections have stable anchors that match this ViewType -> anchor map. Pressing F1 (or a
 * per-view help button) opens the manual at the anchor for whatever the user is looking at.
 *
 * Resolution order for the manual location:
 *   1. a "Manual\index.html" bundled next to the executable (release install layout),
 *   2. a "docs\manual\index.html" next to the executable,
 *   3. a "docs\manual\index.html" found by walking up from the executable (dev checkouts),
 *   4. the published online copy (OnlineBase) as a last resort.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CNC.Controls
{
    public static class ManualHelp
    {
        // Published copy - used only when no local manual file is found. Served from the
        // gh-pages branch of ioSenderV2/ioSender (manual content at the branch root).
        public const string OnlineBase = "https://iosenderv2.github.io/ioSender/index.html";

        // ViewType -> manual anchor. Keep in sync with the section ids in docs/manual/index.html
        // (see docs/manual/README.md "Anchor map"). A ViewType absent here opens the manual home.
        //
        // An anchor that no longer exists is WORSE than being absent: the browser opens the manual and
        // silently does not scroll, so F1 looks like it worked and landed on the wrong page. Two of these
        // sat here for weeks - "start-job" (the topic was renamed #setup on 2026-07-31) and "probing" (the
        // topic was deleted on 2026-08-01) - because nothing checks a string in this file against the
        // manual. If you rename or remove a topic, grep this map in the same edit.
        static readonly Dictionary<ViewType, string> Topics = new Dictionary<ViewType, string>
        {
            { ViewType.StartJob,       "setup" },
            { ViewType.GRBL,           "job" },
            { ViewType.MachineSetup,   "machine-setup" },
            { ViewType.Tools,          "tools" },
            // The Probing tab has had no topic of its own since it left the recommended workflow; what an
            // operator wants from it now lives under Setup (its Dynamic fixture does ad-hoc probing, its
            // Actions cover the height map). Pointing there beats the manual's front page.
            { ViewType.Probing,        "setup" },
            { ViewType.Offsets,        "offsets" },
            { ViewType.GRBLConfig,     "settings" },
            { ViewType.SDCard,         "sdcard" },
            { ViewType.GCodeViewer,    "gcode-viewer" },
            { ViewType.HeightMap,      "heightmap" },
            { ViewType.LatheWizards,   "lathe" },
            { ViewType.FeedsAndSpeeds, "feeds-and-speeds" },
            { ViewType.WorkOrder,      "work-order" },
            { ViewType.Calibration,    "calibration" },
            { ViewType.TrinamicTuner,  "tools" },
            { ViewType.PIDTuner,       "tools" }
        };

        /// <summary>
        /// Every anchor this map can hand out, for a test or a build-time check against the manual.
        /// Exposed rather than left implicit because the failure it guards - an anchor that quietly
        /// stopped existing - is invisible at runtime.
        /// </summary>
        public static IEnumerable<string> AllTopics { get { return Topics.Values; } }

        public static string TopicFor(ViewType view)
        {
            string anchor;
            return Topics.TryGetValue(view, out anchor) ? anchor : string.Empty;
        }

        public static void Open(ViewType view)
        {
            string anchor = TopicFor(view);

            // Logged because the two ways this goes wrong are indistinguishable on screen: an anchor that
            // does not exist and no anchor at all both land the reader on the front page, looking exactly
            // like help that "just opens the manual". Both have happened. This says which.
            CNC.Core.DebugLog.Write("help", string.Format("F1/manual: view={0} anchor={1}",
                view, string.IsNullOrEmpty(anchor) ? "(none - view not in the map)" : anchor));

            Open(anchor);
        }

        public static void Open(string anchor)
        {
            string url = ResolveBase();
            if (!string.IsNullOrEmpty(anchor))
                url += "#" + anchor;

            try
            {
                // Process.Start on a URL is ShellExecute, and ShellExecute DISCARDS THE FRAGMENT of a
                // file:// URL: it resolves the URL to a local path and opens the file, so everything after
                // the '#' is gone before any browser sees it. Confirmed on this machine 2026-09-18 - the
                // address bar showed plain index.html with no anchor, for a URL that definitely had one.
                //
                // That is the whole reason context help "just opened the manual". The anchor map, the topic
                // resolution and the page's own hash handling were all correct and all innocent; the app
                // never had a way to tell, because a stripped anchor and a missing one look identical.
                //
                // So a local, anchored URL is handed to the BROWSER EXECUTABLE as an argument - a browser
                // keeps the fragment of a URL on its command line. Anything else (the https fallback, or a
                // plain URL with no anchor) goes through the shell exactly as before: https keeps its
                // fragment through ShellExecute, and with no anchor there is nothing to lose.
                bool localAnchored = !string.IsNullOrEmpty(anchor) &&
                                     url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

                if (!localAnchored || !OpenInDefaultBrowser(url))
                    Process.Start(url);
            }
            catch
            {
                // Opening help must never take down the app - swallow (no browser, blocked, etc.).
            }
        }

        /// <summary>
        /// Launch the user's default browser with <paramref name="url"/> as a command-line argument.
        /// Returns false if the default browser cannot be resolved or will not start, so the caller can
        /// fall back to the shell - a manual opened at the wrong place still beats one that does not open.
        /// </summary>
        static bool OpenInDefaultBrowser(string url)
        {
            try
            {
                string exe = DefaultBrowserExe();
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    return false;

                Process.Start(new ProcessStartInfo(exe, "\"" + url + "\"") { UseShellExecute = false });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The executable Windows would use for an http link: the user's UrlAssociations choice, resolved
        /// through its ProgId's shell open command. Read rather than assumed - "the default browser" is a
        /// per-user setting and hard-coding one would send half the readers somewhere they do not use.
        /// </summary>
        static string DefaultBrowserExe()
        {
            string progId;
            using (var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
                progId = choice?.GetValue("ProgId") as string;

            if (string.IsNullOrEmpty(progId))
                return null;

            string command;
            using (var cmd = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                command = cmd?.GetValue(null) as string;

            if (string.IsNullOrEmpty(command))
                return null;

            // Typically: "C:\...\msedge.exe" --single-argument %1
            // Only the executable is wanted; the URL is passed as an ordinary argument, which is what keeps
            // the fragment intact. A switch like --single-argument is that browser's way of taking an
            // unescaped URL and is not needed - nor safe to pass on blindly to a different browser.
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : null;
            }

            int space = command.IndexOf(' ');
            return space > 0 ? command.Substring(0, space) : command;
        }

        static string ResolveBase()
        {
            foreach (string candidate in Candidates())
            {
                if (File.Exists(candidate))
                    return new Uri(candidate).AbsoluteUri;   // file:///... form
            }
            return OnlineBase;
        }

        static IEnumerable<string> Candidates()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            yield return Path.Combine(baseDir, "Manual", "index.html");
            yield return Path.Combine(baseDir, "docs", "manual", "index.html");

            // Dev convenience: walk up a few levels to find a repo checkout's docs\manual.
            DirectoryInfo dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                yield return Path.Combine(dir.FullName, "docs", "manual", "index.html");
                dir = dir.Parent;
            }
        }
    }
}
