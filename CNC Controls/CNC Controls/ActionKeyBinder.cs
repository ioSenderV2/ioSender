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
        }

        public static readonly ActionInfo[] Catalog = new ActionInfo[]
        {
            new ActionInfo { Id = "UiScaleUp",   Label = "Zoom in (UI scale)",  DefaultKey = Key.OemPlus,  DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "UiScaleDown", Label = "Zoom out (UI scale)", DefaultKey = Key.OemMinus, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
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
