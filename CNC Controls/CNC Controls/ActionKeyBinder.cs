/*
 * ActionKeyBinder.cs - part of CNC Controls library
 *
 * UI-zoom keyboard shortcuts (Settings:App's UI scale). These are dispatched at the MAIN-WINDOW level
 * (like the console toggle and tab-switch shortcuts) rather than through KeypressHandler.ProcessKeypress,
 * because ProcessKeypress is only ever called from specific views' own PreviewKeyDown (Job/Probing/Jog
 * flyout) - never at the window level - so a handler registered there only fires while that view has
 * focus. Zoom needs to work regardless of which tab is showing.
 *
 * Storage/editing lives in KeyMapEditor ("Keyboard & Controller" > UI zoom group), reusing the same
 * TabShortcut {Id, Key} shape and Config.ActionShortcuts list as tab-switch shortcuts, so all keyboard
 * bindings - jog, action, console, tab-switch, and this - are assignable from that one tab.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public static class ActionKeyBinder
    {
        public class ActionInfo
        {
            public string Id;
            public string Label;      // shown in the Keyboard & Controller row and capture prompt
            public Key DefaultKey;
            public ModifierKeys DefaultModifiers;
            public string Group;      // outline group in the editor; null = "UI zoom" (see KeyMapEditor.Categorize)
            public string Description; // row tooltip; null falls back to the label
        }


        // Declared BEFORE Catalog on purpose: static field initializers run in declaration order, so
        // filtering an array that has not been assigned yet throws at type initialization.
        private static readonly ActionInfo[] catalog = new ActionInfo[]
        {
            new ActionInfo { Id = "UiScaleUp",   Label = "Zoom in (UI scale)",  DefaultKey = Key.OemPlus,  DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "UiScaleDown", Label = "Zoom out (UI scale)", DefaultKey = Key.OemMinus, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
#if DEBUG
            // Debug-only diagnostic (MainWindow.Screenshot_Action) - renders the main window to a PNG and
            // prompts where to save it. Not something a released build should expose as a bindable action,
            // so the whole row (catalog entry, handler registration, and thus the Keyboard & Controller UI
            // for it) only exists in a Debug build.
            new ActionInfo { Id = "Screenshot", Label = "Debug: Screenshot main window", DefaultKey = Key.F6, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
#endif
            // Demo-shoot RTSP camera control (RtspCamerasControl / ObsBridge.SetCameraRecording) - only
            // does anything with -demomarker's OBS bridge armed and that camera's hotkey names configured.
            new ActionInfo { Id = "ObsCamAStart", Label = "OBS: Front Left camera - Start recording", DefaultKey = Key.F9,  DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "ObsCamAStop",  Label = "OBS: Front Left camera - Stop recording",  DefaultKey = Key.F10, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "ObsCamBStart", Label = "OBS: Front Right camera - Start recording", DefaultKey = Key.F11, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "ObsCamBStop",  Label = "OBS: Front Right camera - Stop recording",  DefaultKey = Key.F12, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "ObsAppStart",  Label = "OBS: App/screen capture - Start recording", DefaultKey = Key.F7,  DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "ObsAppStop",   Label = "OBS: App/screen capture - Stop recording",  DefaultKey = Key.F8,  DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },

            // Run-strip buttons as bindable actions ("Program" group - KeyMapEditor.Categorize routes an
            // ActionKeyBinder row by the Group named here). Unbound by default for the same reason as the
            // menu commands below. The handlers live in MainWindow and press the very button they name, so
            // there is one implementation of each and the key cannot drift from the button.
            // F12 by default - the key the retired "Toggle console window" action used, pointed at the
            // console's replacement. An upgrading profile has no Program.Mdi row yet, so SeedDefaults gives it
            // F12 and the key keeps reaching the console; it now OPENS it (Esc dismisses, as everywhere else)
            // instead of toggling. A profile that has already bound this action keeps whatever it chose -
            // SeedDefaults only seeds an Id that is entirely absent.
            new ActionInfo { Id = "Program.Mdi",    Label = "MDI (open console for input)", DefaultKey = Key.F12, Group = "Program", Description = "Press the run strip's MDI button: open the console with the caret in its input box, ready to type. Never hides it - Esc closes it." },
            new ActionInfo { Id = "Program.Status", Label = "Status (message history)",     Group = "Program", Description = "Press the run strip's Status button: show the status message history since launch." },
            // Peek belongs in this group for the reason the group exists: like MDI and Status it must stay
            // live DURING a run, unlike the bindable menu commands, which the menu bar disables wholesale
            // while a job streams. That is also why Peek is not a menu item at all - it would be unreachable
            // exactly when it is wanted.
            new ActionInfo { Id = "Program.Peek",   Label = "Peek / Resume (park and look at the work)", Group = "Program", Description = "Press the run strip's Peek button: pause at the end of the current block, park at G30 with the spindle off, then press again to go back and carry on." },

            // Main-menu commands. All unbound by default (DefaultKey = None) - these are conveniences, and
            // grabbing keys for them uninvited would collide with whatever the operator already uses. The
            // handlers live in MainWindow, which registers each one against the SAME menu item it drives and
            // refuses to act while that item is disabled, so a shortcut can never do what the menu won't.
            // The views that used to be tabs are NOT here - they keep their "Tab.*" ids (KeyMapEditor.TabTargets)
            // so a binding made while they were on the bar still works now that they are menu items.
            new ActionInfo { Id = "Menu.Connect",        Label = "Connect...",                Group = KeyMapEditor.MenuGroup, Description = "Open the connection dialog." },
            new ActionInfo { Id = "Menu.LoadProgram",    Label = "File > Load Program...",    Group = KeyMapEditor.MenuGroup, Description = "Open a g-code file." },
            new ActionInfo { Id = "Menu.LoadWorkOrder",  Label = "File > Load Work Order...", Group = KeyMapEditor.MenuGroup, Description = "Open a saved work order." },
            new ActionInfo { Id = "Menu.NewWorkOrder",   Label = "File > New Work Order...",  Group = KeyMapEditor.MenuGroup, Description = "Start a new work order." },
            new ActionInfo { Id = "Menu.LoadSvgLaser",   Label = "File > Load SVG Laser Job...", Group = KeyMapEditor.MenuGroup, Description = "Burn an SVG with the laser." },
            new ActionInfo { Id = "Menu.Camera",         Label = "Tools > Camera",            Group = KeyMapEditor.MenuGroup, Description = "Open the camera window." },
            // The ONE menu command that ships bound, and the exception the rule above is worth stating for.
            // Context help is F1 everywhere else in Windows, but ioSender's F1 is not free: original
            // ioSender reserved F1-F9 for the first nine macros, JobControl still registers them that way,
            // and the macro handler runs BEFORE the shortcut dispatcher - so on a machine that uses that
            // convention, F1 help simply never fires and there was no other way in. Shift+F1 sits outside
            // the macro convention (macros bind unmodified F-keys only), so it collides with nothing, and
            // unlike the hard-coded F1 branch in MainWindow this row can be rebound or cleared.
            new ActionInfo { Id = "Menu.Manual",         Label = "Help > User manual",        DefaultKey = Key.F1, DefaultModifiers = ModifierKeys.Shift, Group = KeyMapEditor.MenuGroup, Description = "Open the user manual at the page for the current view - the same thing F1 does, on a key a macro cannot take." },
            new ActionInfo { Id = "Menu.Wiki",           Label = "Help > Wiki",               Group = KeyMapEditor.MenuGroup, Description = "Open the online wiki in a browser." },
            new ActionInfo { Id = "Menu.UsageTips",      Label = "Help > Usage tips",         Group = KeyMapEditor.MenuGroup, Description = "Open the usage tips page in a browser." },
            new ActionInfo { Id = "Menu.BriefTour",      Label = "Help > A brief tour",       Group = KeyMapEditor.MenuGroup, Description = "Open the brief tour." },
            new ActionInfo { Id = "Menu.VideoTutorials", Label = "Help > Video tutorials",    Group = KeyMapEditor.MenuGroup, Description = "Open the video tutorials." },
            new ActionInfo { Id = "Menu.ErrorCodes",     Label = "Help > Error and alarm codes", Group = KeyMapEditor.MenuGroup, Description = "Open the error and alarm code reference." },
            new ActionInfo { Id = "Menu.RestartIoSender", Label = "Help > Restart ioSender...", Group = KeyMapEditor.MenuGroup, Description = "Close and reopen ioSender, saving settings first. Asks before restarting." },
            new ActionInfo { Id = "Menu.CheckForUpdates", Label = "Help > Check for updates...", Group = KeyMapEditor.MenuGroup, Description = "Check GitHub for a newer ioSender release." },
            new ActionInfo { Id = "Menu.RollBack",       Label = "Help > Roll back to previous version...", Group = KeyMapEditor.MenuGroup, Description = "Swap back to the build installed before the last update." },
            new ActionInfo { Id = "Menu.OpenDataFolder", Label = "Help > Open Application data folder", Group = KeyMapEditor.MenuGroup, Description = "Open the per-user folder holding App.config, key mappings and backups." },
            new ActionInfo { Id = "Menu.About",          Label = "Help > About",              Group = KeyMapEditor.MenuGroup, Description = "Show the About window." },
        };

        /// <summary>
        /// The bindable actions, minus any belonging to a feature that is currently held back.
        ///
        /// The filter is the point: THIS catalogue - not the menu, and not registerMenuActions - is what
        /// Settings &gt; Keyboard lists. Hiding a menu item without hiding its row here leaves the command
        /// visible and bindable in the editor, which is exactly what e5fe6542 did to "Load SVG Laser Job":
        /// it collapsed the menu entry and skipped the action registration, and the row stayed on screen.
        /// </summary>
        /// <remarks>
        /// Computed on every read, NOT a static readonly array. Features.SvgLaserJob is set from the
        /// command line during startup, and a static field initializer would freeze whatever the flag
        /// held the first time this type was touched - which is a race decided by unrelated code, and
        /// exactly the "right at startup, stale forever after" shape. Two callers, neither hot.
        /// </remarks>
        public static ActionInfo[] Catalog
        {
            get { return catalog.Where(a => a.Id != "Menu.LoadSvgLaser" || Features.SvgLaserJob).ToArray(); }
        }

        private static readonly Dictionary<string, Func<Key, bool>> handlers = new Dictionary<string, Func<Key, bool>>();

        // Ensure every catalog entry has a row in Config.ActionShortcuts. Only adds rows for an Id that
        // is ENTIRELY ABSENT - clearing a binding in Keyboard & Controller leaves an empty-Key row behind
        // (see KeyMapEditor.Commit) so this doesn't silently reinstate the default on a later run.
        public static void SeedDefaults()
        {
            var list = AppConfig.Settings.Base.ActionShortcuts ??
                       (AppConfig.Settings.Base.ActionShortcuts = new List<TabShortcut>());

            bool changed = false;
            foreach (var a in Catalog)
            {
                if (list.Any(x => x.Id == a.Id))
                    continue;
                list.Add(new TabShortcut { Id = a.Id, Key = ShortcutKey.ToStorageString(a.DefaultKey, a.DefaultModifiers) });
                changed = true;
            }
            if (changed)
                AppConfig.Settings.Save();
        }

        // A control that performs the action registers its handler here (idempotent - a later call for
        // the same Id replaces the earlier one).
        public static void Register(string id, Func<Key, bool> handler)
        {
            handlers[id] = handler;
        }

        /// <summary>
        /// Run an action WITHOUT a keypress. Added for the shutter remote, whose buttons can be assigned
        /// the same things (MDI, Status, Peek) but arrive through a keyboard hook rather than the shortcut
        /// dispatcher - so there is no KeyEventArgs to hand Dispatch, and inventing one would be a lie.
        ///
        /// False when nothing is registered for the id, or the handler declines: the handlers refuse while
        /// the thing they drive is disabled, and a caller must be able to tell "did nothing" from "did it".
        /// </summary>
        public static bool Invoke(string id)
        {
            Func<Key, bool> handler;
            if (string.IsNullOrEmpty(id) || !handlers.TryGetValue(id, out handler) || handler == null)
                return false;

            try { return handler(Key.None); }
            catch { return false; }   // a remote button must not be able to take the app down
        }

        /// <summary>Whether an action has a handler at all - so a caller can grey out what cannot run.</summary>
        public static bool CanInvoke(string id)
        {
            return !string.IsNullOrEmpty(id) && handlers.ContainsKey(id);
        }

        /// <summary>The action's current shortcut as a display string ("Ctrl+S"), or null when unbound.
        /// The ActionShortcuts counterpart of TabKeyBinder.CurrentDisplay, so a caller showing a binding
        /// (a menu item's gesture text, say) does not care which of the two stores it came from.</summary>
        public static string CurrentDisplay(string id)
        {
            var row = AppConfig.Settings.Base.ActionShortcuts?.FirstOrDefault(x => x.Id == id);
            Key k;
            ModifierKeys m;
            if (row != null && !string.IsNullOrEmpty(row.Key) && ShortcutKey.TryParse(row.Key, out k, out m) && k != Key.None)
                return ShortcutKey.ToDisplayString(k, m);
            return null;
        }

        // Resolve the pressed key/modifiers against Config.ActionShortcuts and invoke the matching
        // registered handler, if any. Reads the list fresh each call (small, rarely-pressed, no need to
        // cache) so it always reflects whatever Keyboard & Controller last saved. Returns true (and the
        // caller should set e.Handled) when dispatched.
        public static bool Dispatch(KeyEventArgs e)
        {
            var list = AppConfig.Settings.Base.ActionShortcuts;
            if (list == null || list.Count == 0)
                return false;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys mods = Keyboard.Modifiers;

            // A text-producing combo (no modifier, or Shift only for a capital/symbol) must not be stolen from
            // a focused text box - bind "L" to Load Program and you would otherwise never type an L into the
            // MDI again. Same guard, same reason, as MainWindow.dispatchTabShortcut. It never bit the original
            // zoom/OBS entries because those all ship on Ctrl+Alt, but the menu commands are bound by hand.
            // A FUNCTION key is exempt: F1-F24 produce no text, so nothing is stolen from the box by
            // dispatching one. Without the exemption Shift+F1 (Help > User manual's default) would do
            // nothing whenever the caret sat in the MDI or a settings field - which is a fair share of the
            // moments someone reaches for help.
            bool textProducing = mods == ModifierKeys.None || mods == ModifierKeys.Shift;
            if (key >= Key.F1 && key <= Key.F24)
                textProducing = false;

            if (textProducing && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                return false;

            foreach (var row in list)
            {
                Key k;
                ModifierKeys m;
                if (string.IsNullOrEmpty(row.Key) || !ShortcutKey.TryParse(row.Key, out k, out m) || k != key || m != mods)
                    continue;
                if (handlers.TryGetValue(row.Id, out var fn) && fn(key))
                    return true;
            }
            return false;
        }
    }
}
