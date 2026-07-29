/*
 * OddJobsToolMemory.cs - part of CNC Controls library
 *
 * Remembers the feeds/speeds the operator actually SETTLED ON for a given (tool kind, diameter, material)
 * combination, so a value dialed in once - e.g. what a 1/4" end mill really likes in MDF on this machine -
 * doesn't have to be re-derived every time an operation using that same bit and material is added.
 *
 * This is deliberately distinct from FeedsSpeedsAdvisor: the advisor supplies published chip-load/surface-
 * speed REFERENCE values (a starting point, still shown as the recommendation highlight in
 * OddJobsFeedsSpeedsDialog), whereas this records the operator's own measured preference, which wins for
 * prefill. Persisted as an App.config section via AppConfig.RegisterFolded.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNC.Controls
{
    public class ToolMemoryEntry
    {
        public int Tool;                  // OddJobsTool
        public double DiameterMm;
        public string Material = string.Empty;
        public double Rpm, Feed, PlungeFeed, DepthOfCut;
    }

    public class ToolMemoryList
    {
        public List<ToolMemoryEntry> Entries = new List<ToolMemoryEntry>();
    }

    public static class OddJobsToolMemory
    {
        public static ToolMemoryList SectionConfig;

        // Diameters are matched to 0.01 mm - a bit's nominal size is a fixed physical property, so anything
        // finer than that is float noise from a units conversion, not a different tool.
        private static bool Matches(ToolMemoryEntry e, OddJobsTool tool, double diameterMm, string material)
        {
            return e.Tool == (int)tool
                && Math.Abs(e.DiameterMm - diameterMm) < 0.005d
                && string.Equals(e.Material ?? string.Empty, material ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        public static ToolMemoryEntry Find(OddJobsTool tool, double diameterMm, string material)
        {
            if (string.IsNullOrEmpty(material))
                return null;   // material is part of the key - without one there's nothing meaningful to recall
            return SectionConfig?.Entries?.FirstOrDefault(e => Matches(e, tool, diameterMm, material));
        }

        public static void Remember(OddJobsTool tool, double diameterMm, string material, double rpm, double feed, double plungeFeed, double depthOfCut)
        {
            if (string.IsNullOrEmpty(material) || diameterMm <= 0d)
                return;

            if (SectionConfig == null)
                SectionConfig = new ToolMemoryList();

            var entry = SectionConfig.Entries.FirstOrDefault(e => Matches(e, tool, diameterMm, material));
            if (entry == null)
            {
                entry = new ToolMemoryEntry { Tool = (int)tool, DiameterMm = Math.Round(diameterMm, 2), Material = material };
                SectionConfig.Entries.Add(entry);
            }
            entry.Rpm = rpm;
            entry.Feed = feed;
            entry.PlungeFeed = plungeFeed;
            entry.DepthOfCut = depthOfCut;
            AppConfig.Settings.Save();
        }

        public static string Describe(ToolMemoryEntry e)
        {
            return e == null ? string.Empty : string.Format(CultureInfo.InvariantCulture,
                "Recalled your last {0:0.##} mm settings for this material: {1:0} rpm, {2:0}/{3:0} mm/min feed/plunge.",
                e.DiameterMm, e.Rpm, e.Feed, e.PlungeFeed);
        }
    }
}
