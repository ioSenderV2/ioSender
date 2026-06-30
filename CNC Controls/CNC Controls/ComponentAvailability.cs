/*
 * ComponentAvailability.cs - part of CNC Controls library
 *
 * Lists the components that are gated by the controller's reported capability / configuration, with the
 * reason each is not offered, so the Edit Main Page editor can show the user WHY something is missing
 * (e.g. "Lathe Wizards - lathe mode not enabled", "Auto Square - no firmware support"). Evaluated from
 * the live GrblInfo, so it reflects the currently connected controller.
 */

using System.Collections.Generic;
using CNC.Core;

namespace CNC.Controls
{
    public sealed class UnavailableComponent
    {
        public string Label { get; set; }
        public string Reason { get; set; }
    }

    public static class ComponentAvailability
    {
        public static List<UnavailableComponent> Unavailable()
        {
            var list = new List<UnavailableComponent>();
            void Add(string label, string reason) => list.Add(new UnavailableComponent { Label = label, Reason = reason });

            if (GrblInfo.NumTools == 0)
                Add("Tool table", "The controller reports no tool table.");

            if (string.IsNullOrEmpty(GrblInfo.TrinamicDrivers))
                Add("Trinamic tuner", "No Trinamic stepper drivers detected.");

            if (!GrblInfo.HasPIDLog)
                Add("PID tuner", "The firmware has no PID-tuning log.");

            if (!AutoSquareWizard.SquaringSettingExists())
                Add("Auto Square - apply offset", "No auto-square offset setting ($170-$172) in this firmware - drill + measure only.");

            if (!GrblInfo.LatheModeEnabled)
                Add("Lathe Wizards", "Lathe mode is not enabled (Settings > App).");

            if (!GrblInfo.HasSDCard)
                Add("SD Card", "No SD card / file system on the controller.");

            if (!GrblInfo.HasProbe)
                Add("Probing", "No probe is configured.");
            else if (!GrblSettings.ReportProbeCoordinates)
                Add("Probing", "Probe coordinate reporting is off ($10 - enable it to use probing).");

            return list;
        }
    }
}
