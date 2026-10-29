/*
 * UiPresets.cs - part of CNC Controls library
 *
 * Named screen arrangements the operator switches between - the selector in the menu row.
 *
 * A PRESET IS NOT A NEW FORMAT. It is the same .ioconfig document ConfigOverlay already defines, so
 * "export this preset" is a file copy and a preset can be handed to someone else as an overlay. What
 * differs is lifetime, and that is the whole reason presets exist as a separate idea:
 *
 *   an overlay is a patch - applied once, merged in, thereafter indistinguishable from the profile;
 *   a preset is a live copy - the file stays the record of that named arrangement, and is written
 *   back whenever the operator switches AWAY from it.
 *
 * That write-back is what makes switching lossless, which is the property an overlay cannot offer:
 * ConfigOverlay's replace mode supplants a section wholesale, so with bare overlays, picking a name
 * would silently discard whatever the operator had changed since.
 *
 * Switching reuses the proven staging path rather than inventing a second one. A running instance must
 * never write App.config (dozens of call sites save the whole in-memory state during a session and
 * would clobber it moments later), so the merge happens in AppConfig.TryLoad where the document on
 * disk is authoritative: Switch() writes the outgoing preset out, stages the incoming one exactly as
 * Help > Support > Apply does, and the caller restarts.
 *
 * The MODIFIED check compares documents, not objects, and never touches the disk: both sides go
 * through ConfigOverlay.ExtractUiLayout, so the live config and the preset file are reduced to the
 * same canonical XML by the same code. Comparing field-by-field against a snapshot would have been
 * the other option and is the one that rots - it drifts the moment UiLayoutCoreElements gains an
 * element, with nothing reporting the difference.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CNC.Core;

namespace CNC.Controls
{
    // One named arrangement: where its file is, and whether it came with the build.
    public sealed class UiPreset
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Shipped { get; set; }
        public List<ConfigOverlayEntry> Sections { get; set; }
        public string Error { get; set; }

        public override string ToString() { return Name; }
    }

    public static class UiPresets
    {
        // Shown for a profile that is not tracking any named preset - a fresh install, or an operator who
        // has only ever rearranged things themselves. It is a REAL preset name, not a placeholder: on any
        // switch away, the current arrangement is written out under it, so "I was happy with how I had it"
        // survives trying Classic out of curiosity. Reserving the name is what makes every state nameable,
        // which in turn is what makes every switch lossless.
        public const string CustomName = "Custom";

        public static string ActiveName
        {
            get
            {
                var n = AppConfig.Settings?.Base?.ActivePreset;
                return string.IsNullOrWhiteSpace(n) ? CustomName : n.Trim();
            }
        }

        #region The library

        /// <summary>
        /// Every preset, the operator's own first. A user file SHADOWS a shipped one of the same name -
        /// that is how editing a shipped preset works, since the install folder is never written to.
        /// </summary>
        public static List<UiPreset> All()
        {
            var list = new List<UiPreset>();

            foreach (var p in Read(Resources.PresetsFolder, false))
                list.Add(p);

            foreach (var p in Read(Resources.ShippedPresetsFolder, true))
                if (!list.Any(u => string.Equals(u.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(p);

            // The profile's own arrangement is always offerable, even with no file behind it yet - the file
            // appears when something is switched away from. Without this the selector could not name what
            // the operator is currently looking at on a fresh install.
            if (!list.Any(p => string.Equals(p.Name, ActiveName, StringComparison.OrdinalIgnoreCase)))
                list.Insert(0, new UiPreset { Name = ActiveName, Path = PathFor(ActiveName), Sections = new List<ConfigOverlayEntry>() });

            return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static UiPreset Find(string name)
        {
            return All().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Where a preset of this name is written. Always the per-user folder - see ShippedPresetsFolder.
        public static string PathFor(string name)
        {
            return System.IO.Path.Combine(Resources.PresetsFolder, Sanitize(name) + ConfigOverlay.FileExtension);
        }

        // A preset name becomes a file name, so the characters a file name cannot hold are dropped rather
        // than being allowed to fail the write later with something unhelpful.
        public static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return CustomName;

            var bad = System.IO.Path.GetInvalidFileNameChars();
            var clean = new string(name.Trim().Where(c => !bad.Contains(c)).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(clean) ? CustomName : clean;
        }

        private static IEnumerable<UiPreset> Read(string folder, bool shipped)
        {
            var list = new List<UiPreset>();

            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    return list;

                foreach (var file in Directory.GetFiles(folder, "*" + ConfigOverlay.FileExtension).OrderBy(f => f))
                {
                    var preset = new UiPreset
                    {
                        Path = file,
                        Shipped = shipped,
                        Sections = new List<ConfigOverlayEntry>()
                    };

                    try
                    {
                        var doc = XDocument.Load(file);
                        preset.Sections = ConfigOverlay.Describe(doc);
                        // The name INSIDE the document wins over the file name: that is what travels when
                        // the file is renamed on the way to someone else.
                        preset.Name = ConfigOverlay.NameOf(doc);
                        if (preset.Sections.Count == 0)
                            preset.Error = "contains no configuration sections";
                    }
                    catch (Exception ex)
                    {
                        preset.Error = ex.Message;
                    }

                    if (string.IsNullOrWhiteSpace(preset.Name))
                        preset.Name = System.IO.Path.GetFileNameWithoutExtension(file);

                    list.Add(preset);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("config", "preset folder listing failed (" + folder + ") - " + ex.Message);
            }

            return list;
        }

        #endregion

        #region Modified check

        /// <summary>
        /// The live UI arrangement, reduced to canonical XML. ConfigStore.WriteDocument() serialises the
        /// whole config in memory, so this costs no disk access and - crucially - no Save(), which would
        /// write App.config from a running instance.
        /// </summary>
        public static string LiveState()
        {
            try
            {
                return Canonical(ConfigOverlay.ExtractUiLayout(ConfigStore.WriteDocument()));
            }
            catch (Exception ex)
            {
                DebugLog.Write("config", "preset LiveState failed - " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A preset file reduced to the same canonical XML. Running a preset document back through
        /// ExtractUiLayout is idempotent and is what makes the two sides comparable as strings.
        /// </summary>
        public static string FileState(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;
                return Canonical(ConfigOverlay.ExtractUiLayout(XDocument.Load(path)));
            }
            catch (Exception ex)
            {
                DebugLog.Write("config", "preset FileState failed (" + path + ") - " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// True when the live arrangement no longer matches the active preset's file. Unknown is NOT
        /// reported as modified: a preset with no file yet (a fresh "Custom"), or one that could not be
        /// read, returns false rather than labelling the UI from a comparison that did not happen.
        /// </summary>
        public static bool IsModified()
        {
            var active = Find(ActiveName);
            var saved = FileState(active?.Path);
            var live = LiveState();
            return saved != null && live != null && saved != live;
        }

        // Order and the friendly name must not count as a difference: the name is metadata, and section
        // order follows whichever document the sections were read out of.
        private static string Canonical(XDocument doc)
        {
            if (doc?.Root == null)
                return null;

            var root = new XElement(doc.Root.Name);
            foreach (var sec in doc.Root.Elements()
                                        .OrderBy(s => (string)s.Attribute("key"), StringComparer.Ordinal))
                root.Add(new XElement(sec));

            return root.ToString(SaveOptions.DisableFormatting);
        }

        #endregion

        #region Save and switch

        /// <summary>
        /// Write the live arrangement out as <paramref name="name"/>. Always into the per-user folder, so
        /// saving over a shipped preset shadows it instead of writing to the install directory.
        /// </summary>
        public static bool Save(string name, out string error)
        {
            error = null;

            try
            {
                string clean = Sanitize(name);
                Directory.CreateDirectory(Resources.PresetsFolder);

                // Built from the in-memory document, not from App.config on disk: the file may be older
                // than this session (auto-save is optional), and writing it here is exactly what must not
                // happen from a running instance.
                var doc = ConfigOverlay.ExtractUiLayout(ConfigStore.WriteDocument(), clean);
                if (ConfigOverlay.Describe(doc).Count == 0)
                {
                    error = "this configuration has no screen-layout sections to save";
                    return false;
                }

                doc.Save(PathFor(clean));
                DebugLog.Write("config", "preset saved - " + clean);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                DebugLog.Write("config", "preset save failed - " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Stage a switch to <paramref name="name"/>: write the outgoing preset out so nothing is lost,
        /// record the new active name, save the profile, and stage the incoming document for the next
        /// load. The CALLER restarts (GrblConfigView.DoRestart) - staging and restarting are kept apart so
        /// a failure here changes nothing.
        /// </summary>
        public static bool Switch(string name, out string error)
        {
            error = null;

            var target = Find(name);
            if (target == null || target.Error != null || target.Sections == null || target.Sections.Count == 0)
            {
                error = "that preset has nothing to apply";
                return false;
            }

            // Outgoing first, and unconditionally. This is the losslessness: the operator's edits to the
            // arrangement they are leaving go back into ITS file, not into whatever they switch to.
            string saveError;
            if (!Save(ActiveName, out saveError))
            {
                error = "the current arrangement could not be saved as \"" + ActiveName + "\" - " + saveError;
                return false;
            }

            // Recorded BEFORE the save: the restart reads this back to know which preset it is on, and
            // ActivePreset is deliberately not one of UiLayoutCoreElements, so the incoming preset's own
            // Core merge cannot overwrite it.
            AppConfig.Settings.Base.ActivePreset = Sanitize(name);

            // Save before staging: StageOverlay's pre-switch backup is a copy of the file on DISK, so
            // anything changed this session and not yet written would be missing from what Undo restores.
            AppConfig.Settings.Save();

            if (!AppConfig.StageOverlay(target.Path))
            {
                error = "the preset could not be staged - see the debug log";
                return false;
            }

            DebugLog.Write("config", "preset switch staged - " + target.Name);
            return true;
        }

        /// <summary>
        /// Delete a preset. Only ever the per-user file: deleting a shadowed name uncovers the shipped
        /// original rather than removing it.
        /// </summary>
        public static bool Delete(string name, out string error)
        {
            error = null;

            try
            {
                string path = PathFor(name);
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        #endregion
    }
}
