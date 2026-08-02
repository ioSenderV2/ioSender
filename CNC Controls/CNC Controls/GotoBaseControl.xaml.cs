/*
 * GotoControl.xaml.cs - part of CNC Controls library
 *
 * v0.47 / 2026-02-16 / Io Engineering (Terje Io)
 *
 */

/*

Copyright (c) 2020-2026, Io Engineering (Terje Io)
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

· Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

· Redistributions in binary form must reproduce the above copyright notice, this
list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

· Neither the name of the copyright holder nor the names of its contributors may
be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

*/

using System.Windows;
using System.Windows.Controls;
using CNC.Core;
using CNC.GCode;
using System.Collections.ObjectModel;

namespace CNC.Controls
{
    public partial class GotoBaseControl : UserControl
    {
        private string _gcs = "G54";

        public GotoBaseControl()
        {
            InitializeComponent();
            GrblWorkParameters.CoordinateSystems.CollectionChanged += CoordinateSystems_CollectionChanged;
        }

        public string CoordinateSystem { get { return _gcs; } set { _gcs = value; } }
        public ObservableCollection<CoordinateSystem> CoordinateSystems { get; set; } = new ObservableCollection<CoordinateSystem>();

        private void button_Click(object sender, RoutedEventArgs e)
        {
            var model = DataContext as GrblViewModel;
            if (model == null)
                return;

            string tag = (string)(sender as Button).Tag;
            SafeGoto(model, tag == "G5x" ? CoordinateSystem : tag);
        }

        // EVERY Go To below is a machine-coordinate move (G53 rapids, or a bare G28/G30 whose stored target is
        // machine coordinates) - so all of them are only as trustworthy as the machine-coordinate reference
        // itself. Refuse when the controller is not homed and homing is configured: MPos is then a fiction and
        // the "go to" turns into a blind displacement from a false origin, at rapid.
        //
        // Confirmed on real hardware 2026-08-02, from the console log: an unexpected controller reboot (WDOG1
        // hang-watchdog reset mid-job) brought grblHAL back up IDLE - not Alarm - with MPos:0,0,0 and H:0, at
        // whatever physical spot the tool was sitting. A G30 sent ~55 min later chased the stored G30 machine
        // coordinates as a displacement from that false zero and rapided (F22206) into the front-right hard
        // stops, stalling X and both Y steppers. Soft limits are no backstop here - grblHAL only enforces them
        // when homed. Note "Idle, not Alarm": nothing in the machine state forced a re-home, and ioSender's own
        // HomedState only maps to NotHomed for Alarm substate 11 (GrblViewModel), landing on Unknown instead -
        // so a check for "== Homed" is the correct polarity, NOT "!= NotHomed".
        //
        // The guard lives in these two shared routines rather than on the buttons because the button-level
        // IsEnabled binding was exactly what was missing: GotoBaseControl.xaml's own button has it, the
        // per-offset flyout's Go button (OffsetFlyout.xaml) never did.
        private static bool MachinePositionTrusted (GrblViewModel model)
        {
            if (!model.IsHomingEnabled || model.HomedState == HomedState.Homed)
                return true;

            AppDialogs.Show("Machine is not homed - machine coordinates are not valid.\n\n" +
                             "Go To moves are machine-coordinate moves, so this would rapid to a position " +
                             "measured from an unknown origin. Home the machine ($H) first.",
                              "ioSender", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // THE single "Go To" routine - used by every go-to-offset button (this panel, the per-offset flyout)
        // so Safe Z is applied uniformly. <code> is a work-coordinate origin (G54-G59.3, G92) or the G28/G30
        // secondary home; its target machine position is read from the $#-populated coordinate table.
        //
        // Safe Z (when enabled): lift Z to machine top first, traverse X/Y (and any rotaries), then descend Z -
        // so the move clears any fixtures or stock standing up in Z instead of cutting a diagonal through them.
        // Requires homing (machine coordinates valid), soft limits ($20, so the moves are envelope-checked) and
        // a known target; otherwise falls back to the original single (coordinated) move.
        public static void SafeGoto(GrblViewModel model, string code)
        {
            if (model == null || string.IsNullOrEmpty(code))
                return;

            if (!MachinePositionTrusted(model))
                return;

            CoordinateSystem cs = GrblWorkParameters.GetCoordinateSystem(code);

            if (AppConfig.Settings.Base.SafeGotoZ && cs != null && model.HomedState == HomedState.Homed
                 && GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) == 1
                 && GrblInfo.AxisFlags.HasFlag(AxisFlags.Z))
            {
                // Machine Z0 is the homed top (already top-minus-pull-off with force-set-origin), the highest
                // point reachable without re-tripping the Z limit.
                string planar = cs.ToString(GrblInfo.AxisFlags & ~AxisFlags.Z);
                model.ExecuteCommand("G53G0Z0");
                if (!string.IsNullOrEmpty(planar))
                    model.ExecuteCommand("G53G0" + planar);
                model.ExecuteCommand("G53G0" + cs.ToString(AxisFlags.Z));
                return;
            }

            // Fallback: original single (coordinated) move.
            if (code == "G28" || code == "G30")
                model.ExecuteCommand(code);
            else if (cs != null)
                model.ExecuteCommand("G53G0" + cs.ToString(GrblInfo.AxisFlags));
        }

        // Go-to for an EXPLICIT machine target - the named-fixture presets (OffsetFlyout) supply their stored
        // machine coordinates directly (an app-side library) rather than reading a firmware coordinate slot.
        // Same Safe Z discipline as SafeGoto; the fallback is a plain coordinated G53 rapid (there is no
        // firmware code to defer to).
        public static void SafeGotoMachine(GrblViewModel model, Position target)
        {
            if (model == null || target == null)
                return;

            if (!MachinePositionTrusted(model))
                return;

            if (AppConfig.Settings.Base.SafeGotoZ && model.HomedState == HomedState.Homed
                 && GrblSettings.GetInteger(GrblSetting.SoftLimitsEnable) == 1
                 && GrblInfo.AxisFlags.HasFlag(AxisFlags.Z))
            {
                string planar = target.ToString(GrblInfo.AxisFlags & ~AxisFlags.Z);
                model.ExecuteCommand("G53G0Z0");
                if (!string.IsNullOrEmpty(planar))
                    model.ExecuteCommand("G53G0" + planar);
                model.ExecuteCommand("G53G0" + target.ToString(AxisFlags.Z));
                return;
            }

            model.ExecuteCommand("G53G0" + target.ToString(GrblInfo.AxisFlags));
        }

        private void CoordinateSystems_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if(e.NewItems.Count == 1)
            {
                if(((CoordinateSystem)e.NewItems[0]).Code.StartsWith("G5")) {
                    CoordinateSystems.Add((CoordinateSystem)e.NewItems[0]);
                }
            }
        }
    }
}
