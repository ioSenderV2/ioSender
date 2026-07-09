/*
 * IAvailabilityGated.cs - part of CNC Controls library
 *
 * Implemented by a tab / sub-tab content view that is only offered when the connected controller meets a
 * prerequisite (a probe, a tool table, lathe mode, an SD card / file system, Trinamic drivers, a PID log,
 * an auto-square offset setting, ...). The view owns BOTH the capability check and the human-readable reason,
 * evaluated from the live GrblInfo / GrblSettings - so the removal decision (StretchTabControl.PruneUnavailable)
 * and the "Edit Main Page > Unavailable" listing (ComponentAvailability) come from a single place and can no
 * longer drift. A view that does NOT implement this interface is always available (the default).
 */

namespace CNC.Controls
{
    public interface IAvailabilityGated
    {
        // null  => the component is available on the currently connected controller.
        // else  => the reason it is not (fully) offered, shown in Edit Main Page > Unavailable.
        string UnavailableReason { get; }

        // When UnavailableReason != null: true  => the host tab bar REMOVES this tab (it can do nothing here);
        //                                  false => the host KEEPS the tab but still records the reason - a
        //                                           degraded-but-usable component (e.g. Auto Square, which still
        //                                           serves as a squareness gauge when the offset setting is absent).
        bool HideWhenUnavailable { get; }
    }
}
