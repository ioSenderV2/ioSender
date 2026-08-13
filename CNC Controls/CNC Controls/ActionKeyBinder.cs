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


        public static readonly ActionInfo[] Catalog = new ActionInfo[]
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

            // Main-menu commands. All unbound by default (DefaultKey = None) - these are conveniences, and
            // grabbing keys for them uninvited would collide with whatever the operator already uses. The
            // handlers live in MainWindow, which registers each one against the SAME menu item it drives and
            // refuses to act while that item is disabled, so a shortcut can never do what the menu won't.
            // The views that used to be tabs are NOT here - they keep their "Tab.*" ids (KeyMapEditor.TabTargets)
            // so a binding made while they were on the bar still works now that they are menu items.
            new ActionInfo { Id = "Menu.Connect",        Label = "Connect...",                Group = KeyMapEditor.TopLevelGroup, Description = "Open the connection dialog." },
            new ActionInfo { Id = "Menu.LoadProgram",    Label = "File > Load Program...",    Group = KeyMapEditor.TopLevelGroup, Description = "Open a g-code file." },
            new ActionInfo { Id = "Menu.LoadWorkOrder",  Label = "File > Load Work Order...", Group = KeyMapEditor.TopLevelGroup, Description = "Open a saved work order." },
            new ActionInfo { Id = "Menu.NewWorkOrder",   Label = "File > New Work Order...",  Group = KeyMapEditor.TopLevelGroup, Description = "Start a new work order." },
            new ActionInfo { Id = "Menu.Camera",         Label = "Tools > Camera",            Group = KeyMapEditor.TopLevelGroup, Description = "Open the camera window." },
            new ActionInfo { Id = "Menu.Wiki",           Label = "Help > Wiki",               Group = KeyMapEditor.TopLevelGroup, Description = "Open the online wiki in a browser." },
            new ActionInfo { Id = "Menu.UsageTips",      Label = "Help > Usage tips",         Group = KeyMapEditor.TopLevelGroup, Description = "Open the usage tips page in a browser." },
            new ActionInfo { Id = "Menu.BriefTour",      Label = "Help > A brief tour",       Group = KeyMapEditor.TopLevelGroup, Description = "Open the brief tour." },
            new ActionInfo { Id = "Menu.VideoTutorials", Label = "Help > Video tutorials",    Group = KeyMapEditor.TopLevelGroup, Description = "Open the video tutorials." },
            new ActionInfo { Id = "Menu.ErrorCodes",     Label = "Help > Error and alarm codes", Group = KeyMapEditor.TopLevelGroup, Description = "Open the error and alarm code reference." },
            new ActionInfo { Id = "Menu.CheckForUpdates", Label = "Help > Check for updates...", Group = KeyMapEditor.TopLevelGroup, Description = "Check GitHub for a newer ioSender release." },
            new ActionInfo { Id = "Menu.RollBack",       Label = "Help > Roll back to previous version...", Group = KeyMapEditor.TopLevelGroup, Description = "Swap back to the build installed before the last update." },
            new ActionInfo { Id = "Menu.OpenDataFolder", Label = "Help > Open Application data folder", Group = KeyMapEditor.TopLevelGroup, Description = "Open the per-user folder holding App.config, key mappings and backups." },
            new ActionInfo { Id = "Menu.About",          Label = "Help > About",              Group = KeyMapEditor.TopLevelGroup, Description = "Show the About window." },
        };

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
            if ((mods == ModifierKeys.None || mods == ModifierKeys.Shift)
                 && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
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
