/*
 * TabRegistry.cs - part of CNC Controls library for Grbl
 *
 * Registry of the main-window tabs (Grbl, Probing, SD Card, ...) that can be shown,
 * reordered or hidden via the "Edit Main Page" dialog's Tabs tab (ioSender XL).
 *
 * The host window (which owns the actual TabItems) publishes the set of tabs that are
 * present after capability filtering by calling Publish(); the editor reads Available
 * to build its lists and the host applies Config.Tabs (ordered ViewType names) on startup.
 *
 */

using System.Collections.Generic;

namespace CNC.Controls
{
    public class TabInfo
    {
        public string Name { get; }    // ViewType name - stored in Config.Tabs, stable across sessions
        public string Label { get; }   // tab header, shown in the editor

        public TabInfo(string name, string label)
        {
            Name = name;
            Label = label;
        }
    }

    public static class TabRegistry
    {
        // All tabs present in the host window (in their built-in order), published by the host at startup.
        public static List<TabInfo> Available = new List<TabInfo>();

        // True once a host that supports tab configuration has published its tabs (ioSender XL).
        public static bool Enabled { get { return Available.Count > 0; } }

        public static void Publish(IEnumerable<TabInfo> tabs)
        {
            Available = new List<TabInfo>(tabs);
        }
    }
}
