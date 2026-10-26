/*
 * UiSounds.cs - part of CNC Controls library for Grbl
 *
 * The catalogue of sounds Settings > User Interface > Sound can attach to a machine event, and the
 * player that sounds them.
 *
 * Modelled on RemoteFunctions: a static catalogue of Id/Label entries bound straight to a ComboBox,
 * with "None" first and an unknown id resolving to it. Same reason, too - a setting that cannot be
 * understood must read as "do nothing", never as something that happens.
 *
 * ONE SETTING PER EVENT, holding one of three things: "None", a catalogue id, or a full path to the
 * operator's own .wav. A path is recognised by not being in the catalogue, which keeps a custom
 * choice from needing a second setting beside each of the three (and a second to keep in step).
 *
 * .wav only, through System.Media.SoundPlayer. That is the format every sound Windows ships is in,
 * it needs no reference beyond System, and it cannot pull in a media stack that might block the UI
 * thread or need a codec the box does not have. An mp3 picked from the Browse dialog is refused
 * where it is chosen, in the settings panel, rather than failing silently later when a limit trips.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Media;

namespace CNC.Controls
{
    public static class UiSounds
    {
        public class Sound
        {
            // PROPERTIES, NOT FIELDS - WPF binds only to properties. RemoteFunctions.cs carries the
            // same note and the incident behind it: a ComboBox over a type whose Label is a field
            // renders a popup of the right height full of blank rows, silently.
            public string Id { get; set; }
            public string Label { get; set; }
            public string File { get; set; }        // null for None; a file name under the Windows media folder
            public override string ToString() { return Label; }
        }

        public const string None = "None";

        /// <summary>Marker id the panel's ComboBox uses for "Browse...", never stored in a setting.</summary>
        public const string Browse = "Browse";

        private static readonly string MediaDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

        // Candidates, in the order they are offered. Every one of these ships with Windows 10 and 11,
        // but the list is FILTERED BY EXISTENCE when the catalogue is built - a box with a trimmed
        // install, or a future Windows that drops one, must not offer a sound that cannot play. The
        // labels say what the sound is like, not what Microsoft called the file, because "Windows
        // Notify System Generic" tells an operator nothing about what they are about to hear.
        private static readonly Sound[] Candidates = new Sound[]
        {
            new Sound { Id = "Ding",      Label = "Ding - short and soft",        File = "ding.wav" },
            new Sound { Id = "Notify",    Label = "Notify - two soft tones",      File = "Windows Notify System Generic.wav" },
            new Sound { Id = "Chime",     Label = "Chime - three rising notes",   File = "chimes.wav" },
            new Sound { Id = "Chord",     Label = "Chord - mellow, descending",   File = "chord.wav" },
            new Sound { Id = "Exclaim",   Label = "Exclaim - attention",          File = "Windows Exclamation.wav" },
            new Sound { Id = "Critical",  Label = "Critical - urgent, insistent", File = "Windows Critical Stop.wav" },
            new Sound { Id = "Error",     Label = "Error - hard, two tones",      File = "Windows Error.wav" },
            new Sound { Id = "Hardware",  Label = "Hardware - low thud",          File = "Windows Hardware Fail.wav" },
            new Sound { Id = "Tada",      Label = "Tada - cheerful flourish",     File = "tada.wav" },
        };

        private static ObservableCollection<Sound> _items;

        /// <summary>
        /// The catalogue for binding a ComboBox's ItemsSource: None, then every candidate whose file
        /// is actually present, then any file the operator has chosen, then Browse. Built once - the
        /// Windows media folder does not change while the app runs, and a settings page should not
        /// stat ten files every time it is shown.
        ///
        /// OBSERVABLE, and shared by all three rows, so that a file chosen for one event appears
        /// immediately in the other two - wanting the same custom sound on two events is likely, and
        /// browsing to the same file twice to get it would be silly. A plain list would need every
        /// ComboBox rebound by hand after each Browse.
        /// </summary>
        public static IList<Sound> Items
        {
            get
            {
                if (_items == null)
                {
                    var list = new ObservableCollection<Sound> { new Sound { Id = None, Label = "None", File = null } };
                    foreach (var s in Candidates)
                    {
                        try
                        {
                            if (File.Exists(Path.Combine(MediaDir, s.File)))
                                list.Add(s);
                        }
                        catch { }   // an unreadable media folder means no built-in sounds, not a crash
                    }
                    list.Add(new Sound { Id = Browse, Label = "Browse for a .wav file...", File = null });
                    _items = list;
                }
                return _items;
            }
        }

        /// <summary>
        /// Make sure a chosen file path is IN the catalogue, so a ComboBox bound by SelectedValuePath
        /// can actually select it - a SelectedValue with no matching item silently selects nothing,
        /// which would read as "None" and lose the operator's choice. Returns the entry, existing or
        /// new. Inserted before Browse, which stays last because it is an action, not a sound.
        /// </summary>
        public static Sound EnsureCustom(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            var existing = Find(path);
            if (existing != null)
                return existing;

            string label;
            try { label = Path.GetFileName(path); }
            catch { label = path; }

            var entry = new Sound { Id = path, Label = label, File = null };
            var items = (ObservableCollection<Sound>)Items;
            items.Insert(items.Count > 0 && items[items.Count - 1].Id == Browse ? items.Count - 1 : items.Count, entry);
            return entry;
        }

        /// <summary>The catalogue entry for an id, or null when the id is not in the catalogue
        /// (which is how a stored custom file path is told apart from a built-in choice).</summary>
        public static Sound Find(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (var s in Items)
                if (s.Id == id)
                    return s;
            return null;
        }

        /// <summary>
        /// What a stored setting means: the file to play, or null for "say nothing". Null covers
        /// None, an empty setting, Browse (never a stored value, but harmless if one appears), a
        /// catalogue entry whose file has since gone, and a custom path that no longer exists.
        /// </summary>
        public static string ResolveFile(string setting)
        {
            if (string.IsNullOrEmpty(setting) || setting == None || setting == Browse)
                return null;

            // A BUILT-IN is the only kind with a File, and it names a file in the Windows media
            // folder. Everything else here - a custom entry added by EnsureCustom, or a path from a
            // config written on another machine that was never added at all - IS the path, so the
            // test is "has a File", not "is in the catalogue". Those two came apart the moment
            // custom picks started being inserted into the catalogue so they could be selected.
            var known = Find(setting);
            if (known != null && known.File != null)
            {
                string path = Path.Combine(MediaDir, known.File);
                return File.Exists(path) ? path : null;
            }

            // A path the operator chose. It may have been renamed, deleted, or live on a drive that
            // is not mounted today - all of which mean silence, not an exception on a status report.
            try { return File.Exists(setting) ? setting : null; }
            catch { return null; }
        }

        /// <summary>How a stored setting should READ in the UI - the catalogue label, or the file's
        /// own name for a custom pick, with a marker when it cannot be found.</summary>
        public static string Describe(string setting)
        {
            if (string.IsNullOrEmpty(setting) || setting == None)
                return "None";

            var known = Find(setting);
            if (known != null && known.File != null)
                return known.Label;         // a built-in: its label is the whole story

            string name;
            try { name = Path.GetFileName(setting); }
            catch { name = setting; }

            return ResolveFile(setting) == null ? name + " (missing)" : name;
        }

        /// <summary>True if the file is something SoundPlayer can actually play.</summary>
        public static bool IsPlayable(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            try
            {
                return File.Exists(path) &&
                       string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Sound what this setting names. Never throws and never blocks: a bad file, a file that
        /// vanished, or a header SoundPlayer cannot parse all end as silence. This is called from a
        /// status-report handler, so an exception here would surface as the app falling over while
        /// the machine is moving.
        /// </summary>
        public static void Play(string setting)
        {
            string path = ResolveFile(setting);
            if (path == null)
                return;

            try
            {
                // Play, not PlaySync: PlaySync holds the calling thread until the sound finishes,
                // and the caller is the UI thread handling a status report.
                var player = new SoundPlayer(path);
                player.Play();
            }
            catch { }
        }
    }
}
