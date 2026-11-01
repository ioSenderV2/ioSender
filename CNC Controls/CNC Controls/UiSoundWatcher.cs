/*
 * UiSoundWatcher.cs - part of CNC Controls library for Grbl
 *
 * Turns machine state into the three events Settings > User Interface > Sound can attach a sound to:
 * a limit switch asserting, the probe (or tool setter) asserting, and the machine going into ALARM.
 *
 * It watches the VIEW MODEL, not the run strip. The events are described in terms of lamps lighting
 * up because that is where the operator sees them, but the lamps are a view of model.Signals - and a
 * sound that only happens while the strip is on screen would be a sound that stops working when you
 * switch tabs. Same reason it is started from MainWindow and never torn down: the point is to hear
 * the machine while looking at something else, or at nothing.
 *
 * TRANSITIONS ONLY, and the first state seen is swallowed. Connecting to a machine that is already
 * sitting on a limit switch is not the switch being hit, and chiming about it on every connect would
 * teach the operator to ignore the sound - which is the one thing a warning sound must not become.
 *
 * The re-trigger gap is for real switches. A limit switch that is marginally closed chatters, and
 * grblHAL reports status several times a second, so without a floor on the interval a bouncing
 * switch turns into a machine-gun. The gap is per event, so a limit bouncing cannot also swallow the
 * alarm that follows it.
 */

using System;
using System.ComponentModel;
using CNC.Core;

namespace CNC.Controls
{
    public class UiSoundWatcher
    {
        // Every limit input, not just the three the run strip shows. The strip has lamps for X, Y and
        // Z because that is this machine; a 4th-axis limit is still a limit being hit, and silence
        // there would be the wrong kind of surprise.
        private const Signals LimitMask = Signals.LimitX | Signals.LimitY | Signals.LimitZ |
                                          Signals.LimitA | Signals.LimitB | Signals.LimitC |
                                          Signals.LimitU | Signals.LimitV | Signals.LimitW;

        private static readonly TimeSpan RetriggerGap = TimeSpan.FromSeconds(1.5);

        private readonly GrblViewModel model;

        // Null until the first report, which is what makes the first state a baseline rather than an
        // event. bool? rather than bool so "not seen yet" cannot be confused with "was off".
        private bool? lastLimit;
        private bool? lastProbe;
        private bool? lastAlarm;

        private DateTime limitSounded = DateTime.MinValue;
        private DateTime probeSounded = DateTime.MinValue;
        private DateTime alarmSounded = DateTime.MinValue;

        private UiSoundWatcher(GrblViewModel model)
        {
            this.model = model;
            model.PropertyChanged += OnModelPropertyChanged;
        }

        /// <summary>
        /// Start watching. Called once, from MainWindow, as soon as the shared view model exists.
        /// </summary>
        public static UiSoundWatcher Attach(GrblViewModel model)
        {
            return model == null ? null : new UiSoundWatcher(model);
        }

        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Signals is republished on every status report that carries a Pn field, and cleared on
            // every one that does not - grblHAL omits Pn entirely when nothing is asserted - so both
            // edges of every input arrive here as a Signals change.
            if (e.PropertyName == nameof(GrblViewModel.Signals))
            {
                Signals asserted = model.Signals.Value;
                Edge(ref lastLimit, ref limitSounded, (asserted & LimitMask) != 0,
                     AppConfig.Settings.Base.SoundOnLimit);
                // One event for both probe lamps. There is a single Probe input bit; which of the P
                // and T letters lights for it is a matter of which input is currently routed, not of
                // two separate signals - so "the probe light or the tool setter light came on" is one
                // thing happening, and one sound.
                Edge(ref lastProbe, ref probeSounded, (asserted & Signals.Probe) != 0,
                     AppConfig.Settings.Base.SoundOnProbe);
            }
            else if (e.PropertyName == nameof(GrblViewModel.GrblState))
            {
                Edge(ref lastAlarm, ref alarmSounded, model.GrblState.State == GrblStates.Alarm,
                     AppConfig.Settings.Base.SoundOnAlarm);
            }
        }

        /// <summary>
        /// Sound <paramref name="setting"/> when <paramref name="now"/> has just become true. Updates
        /// the remembered state either way, and leaves the timestamp alone unless it actually played,
        /// so a suppressed chatter burst cannot keep pushing the next real event out of reach.
        /// </summary>
        private static void Edge(ref bool? last, ref DateTime sounded, bool now, string setting)
        {
            bool? was = last;
            last = now;

            if (was == null || !now || was == true)      // baseline, released, or still asserted
                return;

            if (DateTime.UtcNow - sounded < RetriggerGap)
                return;

            // Resolve AFTER the edge checks: the common case is no sound configured, and the cost of
            // deciding that should not be paid on every status report.
            if (string.IsNullOrEmpty(setting) || setting == UiSounds.None)
                return;

            sounded = DateTime.UtcNow;
            UiSounds.Play(setting);
        }
    }
}
