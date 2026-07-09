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

        // The components not offered on the current controller (a copy, so callers can mutate their own list).
        public static List<UnavailableComponent> Unavailable()
        {
            return new List<UnavailableComponent>(_list);
        }
    }
}
