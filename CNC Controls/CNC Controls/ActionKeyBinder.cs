/*
 * ActionKeyBinder.cs - part of CNC Controls library
 *
 * Assignable (rebindable) keyboard shortcuts for actions that previously used a fixed, hardcoded
 * key - UI zoom (Settings:App) and the job feed-rate override (JobControl), specifically. Mirrors
 * TabKeyBinder's storage/UX (reuses the TabShortcut {Id, Key} shape, ShortcutKey parsing, and the
 * same BindKeyWindow capture dialog) but for arbitrary invoked actions rather than tab switches,
 * and - unlike tab shortcuts - ships with real defaults out of the box (SeedDefaults).
 *
 * A control that owns the actual behaviour calls Register(id, handler) once (e.g. from its Loaded
 * handler); MainWindow's PreviewKeyDown chain calls Dispatch(e) once, which resolves the pressed
 * key/modifiers against Config.ActionShortcuts and invokes the matching registered handler.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CNC.Core;

namespace CNC.Controls
{
    public static class ActionKeyBinder
    {
        public class ActionInfo
        {
            public string Id;
            public string Label;      // shown in the rebind list and the capture dialog
            public Key DefaultKey;
            public ModifierKeys DefaultModifiers;
        }

        // The catalog of assignable actions. Add an entry here + a Register() call at the site that
        // performs the action to make something else rebindable the same way.
        public static readonly ActionInfo[] Catalog = new ActionInfo[]
        {
            new ActionInfo { Id = "UiScaleUp",   Label = "Zoom in (UI scale)",  DefaultKey = Key.OemPlus,  DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },
            new ActionInfo { Id = "UiScaleDown", Label = "Zoom out (UI scale)", DefaultKey = Key.OemMinus, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Alt },

            // Preserves the app's long-standing defaults - only the ability to change them is new.
            new ActionInfo { Id = "JobFeedRateUp",       Label = "Job feed rate +",          DefaultKey = Key.OemPlus,  DefaultModifiers = ModifierKeys.Control },
            new ActionInfo { Id = "JobFeedRateDown",     Label = "Job feed rate −",     DefaultKey = Key.OemMinus, DefaultModifiers = ModifierKeys.Control },
            new ActionInfo { Id = "JobFeedRateUpFine",   Label = "Job feed rate + (fine)",   DefaultKey = Key.OemPlus,  DefaultModifiers = ModifierKeys.Control | ModifierKeys.Shift },
            new ActionInfo { Id = "JobFeedRateDownFine", Label = "Job feed rate − (fine)", DefaultKey = Key.OemMinus, DefaultModifiers = ModifierKeys.Control | ModifierKeys.Shift },
        };

        private static readonly Dictionary<string, Func<Key, bool>> handlers = new Dictionary<string, Func<Key, bool>>();

        public static event System.Action ActionShortcutsChanged;

        // Ensure every catalog entry has a row in Config.ActionShortcuts. Only adds rows for an Id that
        // is ENTIRELY ABSENT - an explicit Clear() leaves an empty-Key row behind specifically so this
        // doesn't silently reinstate the default the next time the app starts.
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
        // the same Id replaces the earlier one, so a control can safely re-register on re-Loaded).
        public static void Register(string id, Func<Key, bool> handler)
        {
            handlers[id] = handler;
        }

        // Resolve the pressed key/modifiers against Config.ActionShortcuts and invoke the matching
        // registered handler, if any. Returns true (and the caller should set e.Handled) when dispatched.
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

        public static ActionInfo Find(string id)
        {
            return Catalog.FirstOrDefault(a => a.Id == id);
        }

        // The action's current shortcut as a display string ("Ctrl+Alt+="), or null when unbound.
        public static string CurrentDisplay(string id)
        {
            var s = AppConfig.Settings.Base.ActionShortcuts?.FirstOrDefault(x => x.Id == id);
            Key k;
            ModifierKeys m;
            if (s != null && !string.IsNullOrEmpty(s.Key) && ShortcutKey.TryParse(s.Key, out k, out m) && k != Key.None)
                return ShortcutKey.ToDisplayString(k, m);
            return null;
        }

        public static bool PromptAndBind(Window owner, string id)
        {
            var info = Find(id);
            if (info == null)
                return false;

            var dlg = new BindKeyWindow(info.Label, CurrentDisplay(id)) { Owner = owner };
            if (dlg.ShowDialog() != true)
                return false;

            SetBinding(id, dlg.CapturedKey, dlg.CapturedModifiers);
            return true;
        }

        // Clears to an explicit "unbound" row (Key = "") rather than removing it, so SeedDefaults()
        // never reinstates the default behind the operator's back on a later run.
        public static void Clear(string id)
        {
            var list = AppConfig.Settings.Base.ActionShortcuts ??
                       (AppConfig.Settings.Base.ActionShortcuts = new List<TabShortcut>());
            var row = list.FirstOrDefault(x => x.Id == id);
            if (row == null)
                list.Add(new TabShortcut { Id = id, Key = string.Empty });
            else
                row.Key = string.Empty;
            Persist();
        }

        // Assign a key to an action, dropping any prior binding for this action and any OTHER action
        // already using the same combo (one key -> one action, so dispatch is never ambiguous).
        private static void SetBinding(string id, Key key, ModifierKeys mods)
        {
            var list = AppConfig.Settings.Base.ActionShortcuts ??
                       (AppConfig.Settings.Base.ActionShortcuts = new List<TabShortcut>());

            string stored = ShortcutKey.ToStorageString(key, mods);
            list.RemoveAll(x => x.Id != id && x.Key == stored);
            var row = list.FirstOrDefault(x => x.Id == id);
            if (row == null)
                list.Add(new TabShortcut { Id = id, Key = stored });
            else
                row.Key = stored;
            Persist();
        }

        private static void Persist()
        {
            AppConfig.Settings.Save();
            ActionShortcutsChanged?.Invoke();
        }
    }
}
