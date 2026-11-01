/*
 * ComponentAvailability.cs - part of CNC Controls library
 *
 * The list of capability-gated components that are NOT offered on the currently connected controller, with the
 * reason each is missing, so the Edit Main Page editor can show the user WHY something is absent (e.g. "Lathe
 * Tools - lathe mode not enabled", "Probing - no probe configured"). It is no longer a hand-maintained switch:
 * each gated view owns its own prerequisite + reason (IAvailabilityGated), and the tab bars record what they
 * pruned here (StretchTabControl.PruneUnavailable -> Note) as the controller connects. So the removal decision
 * and this listing come from one source and cannot drift.
 */

using System.Collections.Generic;

namespace CNC.Controls
{
    public sealed class UnavailableComponent
    {
        // The component/view key (the tab's Tag), where the pruner knew it. The Main Page editor needs this
        // to leave an unavailable component OUT of its placement list: offering a placement for something
        // that is pruned on every load is a choice that silently does nothing, and it listed the component
        // twice - once as placeable, once here with the reason it is not.
        public string Key { get; set; }
        public string Label { get; set; }
        public string Reason { get; set; }
    }

    public static class ComponentAvailability
    {
        private static readonly List<UnavailableComponent> _list = new List<UnavailableComponent>();

        // Record the components a tab bar found unavailable (dedup by label so a reconnect / a second bar's prune
        // does not double-list). Fed by StretchTabControl.PruneUnavailable at connect / Tools-tab realization.
        public static void Note(IEnumerable<UnavailableComponent> items)
        {
            if (items == null)
                return;
            foreach (var item in items)
                if (item != null && !_list.Exists(c => c.Label == item.Label))
                    _list.Add(item);
        }

        /// <summary>
        /// Component keys currently unavailable, for callers that must not offer them. Entries noted without
        /// a key (a pruner that had no Tag to read) are simply absent - unknown stays unknown rather than
        /// being guessed from the label.
        /// </summary>
        public static HashSet<string> UnavailableKeys()
        {
            var keys = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var c in _list)
                if (!string.IsNullOrEmpty(c.Key))
                    keys.Add(c.Key);
            return keys;
        }

        // The components not offered on the current controller (a copy, so callers can mutate their own list).
        public static List<UnavailableComponent> Unavailable()
        {
            return new List<UnavailableComponent>(_list);
        }
    }
}
