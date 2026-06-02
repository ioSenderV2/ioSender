/*
 * ShortcutKey.cs - part of CNC Core library for Grbl
 *
 * Shared parsing / formatting of keyboard shortcut strings, so the configurable
 * console shortcut (App.config) and the in-app Key Mappings editor agree on one
 * canonical text form.
 *
 */

using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace CNC.Core
{
    public static class ShortcutKey
    {
        // Modifier combinations the keypress dispatcher actually honours (see KeypressHandler.ProcessKeypress).
        public static readonly ModifierKeys[] SupportedModifiers = new ModifierKeys[]
        {
            ModifierKeys.None,
            ModifierKeys.Control,
            ModifierKeys.Control | ModifierKeys.Shift,
            ModifierKeys.Alt
        };

        /// <summary>
        /// Parse a shortcut string such as "Ctrl+Shift+X", "Esc" or "Alt+R" into a key + modifiers.
        /// Parsed manually (not via KeyGesture) so a modifier-less key such as Esc is allowed.
        /// </summary>
        public static bool TryParse(string shortcut, out Key key, out ModifierKeys modifiers)
        {
            key = Key.None;
            modifiers = ModifierKeys.None;

            if (string.IsNullOrWhiteSpace(shortcut))
                return false;

            try
            {
                string[] parts = shortcut.Split('+');
                string keyName = parts[parts.Length - 1].Trim();

                if (keyName.Equals("Esc", StringComparison.OrdinalIgnoreCase))
                    keyName = "Escape";

                key = (Key)Enum.Parse(typeof(Key), keyName, true);

                for (int i = 0; i < parts.Length - 1; i++) switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= ModifierKeys.Control; break;
                    case "alt": modifiers |= ModifierKeys.Alt; break;
                    case "shift": modifiers |= ModifierKeys.Shift; break;
                    case "win":
                    case "windows": modifiers |= ModifierKeys.Windows; break;
                }
            }
            catch
            {
                key = Key.None;
                modifiers = ModifierKeys.None;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Canonical storage form (modifier enum names + key enum name joined by '+'), round-trips through TryParse.
        /// </summary>
        public static string ToStorageString(Key key, ModifierKeys modifiers)
        {
            if (key == Key.None)
                return string.Empty;

            var parts = new List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Control");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Windows");
            parts.Add(key.ToString());

            return string.Join("+", parts);
        }

        /// <summary>
        /// Human-friendly form for display (Ctrl, Esc, symbol keys), not guaranteed to round-trip.
        /// </summary>
        public static string ToDisplayString(Key key, ModifierKeys modifiers)
        {
            if (key == Key.None)
                return string.Empty;

            string mods = modifiers == ModifierKeys.None
                ? string.Empty
                : modifiers.ToString().Replace(", ", "+").Replace("Control", "Ctrl") + "+";

            return mods + KeyName(key);
        }

        /// <summary>Friendly display name for a single key (e.g. D0 -> 0, NumPad5 -> Num 5, Escape -> Esc).</summary>
        private static string KeyName(Key key)
        {
            string k = key.ToString();

            // D0-D9 are the top-row number keys; show the bare digit.
            if (k.Length == 2 && k[0] == 'D' && k[1] >= '0' && k[1] <= '9')
                return k.Substring(1);

            if (k.Length == 7 && k.StartsWith("NumPad") && k[6] >= '0' && k[6] <= '9')
                return "Num " + k.Substring(6);

            switch (k)
            {
                case "Oem3":
                case "OemTilde": return "`";
                case "OemPlus": return "+";
                case "OemMinus": return "-";
                case "OemQuestion": return "/";
                case "OemPeriod": return ".";
                case "OemComma": return ",";
                case "Escape": return "Esc";
                case "Prior": return "PageUp";
                case "Next": return "PageDown";
                default: return k;
            }
        }

        /// <summary>True if the modifier combination is one the keypress dispatcher will act on.</summary>
        public static bool IsSupportedModifier(ModifierKeys modifiers)
        {
            return Array.IndexOf(SupportedModifiers, modifiers) >= 0;
        }
    }
}
