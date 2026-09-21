/*
 * RemoteActions.cs - part of CNC Controls library
 *
 * What a press of the shutter remote MEANS, given what the machine and the app are doing at that moment.
 * ShutterRemote is the ear (a low-level keyboard hook - see that file for why one is needed at all); this
 * is the judgement.
 *
 * ---- The policy ----
 *
 *   a prompt is on screen   primary = OK / Yes       secondary = Cancel / No
 *                           ...and when the prompt has NO Cancel button, both buttons mean OK
 *   the machine is RUNning  either button = Feed Hold
 *   the machine is HOLDing  primary = Start / resume secondary = Stop
 *   anything else           nothing - the key goes to Windows and the volume changes as usual
 *
 * Both keys mean Feed Hold while running on purpose. The remote's two BUTTONS send different keys (on
 * the PICO: the Android one VOLUME UP, the iOS one VOLUME DOWN), a hand reaching for it mid-cut grabs
 * whichever button it finds first, and that hand wants the machine to STOP CUTTING. Asking an operator to
 * pick the correct button of two while a cutter is in the work is the wrong thing to ask. Only once the machine is already held - nothing
 * moving, nothing burning - does the difference between the two keys start to carry meaning.
 *
 * ---- Why it presses the real buttons ----
 *
 * Start, Feed Hold and Stop are the Job control's own buttons, registered here by JobControl itself. The
 * remote raises their Click, and only when the button is enabled. So the remote inherits every gate the
 * app already applies - it cannot start a job the app would refuse to start, and "Start" means exactly
 * what the Start button means at that moment, including "resume from a hold" (JobRunner.CanRun). One
 * implementation, no second opinion about when a run may begin.
 *
 * ---- Why prompts register themselves ----
 *
 * "Is a dialog open?" is answered by the dialogs, not by walking Application.Current.Windows looking for
 * something with a default button. A sweep like that would eventually press OK on a settings window or a
 * file dialog that happened to be up, which is not a thing a camera remote should be able to do. Only the
 * two prompts that stand between the operator and the work register: the app's message box and
 * MacroProcessor's hold prompt (the (MBOX ...) directive - "jog to the corner, then click OK").
 */

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CNC.Core;

namespace CNC.Controls
{
    public static class RemoteActions
    {
        // ---- seams the app fills in ------------------------------------------------------------------

        /// <summary>The Job control's own run buttons, registered by JobControl. See the header.</summary>
        public static Button StartButton, HoldButton, StopButton;

        /// <summary>
        /// A view that is itself waiting for the operator, and knows better than the generic Start what
        /// "carry on" means - the Height Map's per-point hold. Null whenever nothing is waiting; asserted
        /// from the view's own state rather than toggled, so no path out of a run can leave it set.
        /// </summary>
        public static System.Action HoldContinue;

        // ---- prompts -----------------------------------------------------------------------------------

        private class PromptScope : IDisposable
        {
            public System.Action Ok, Cancel;
            public void Dispose() { prompts.Remove(this); }
        }

        private static readonly List<PromptScope> prompts = new List<PromptScope>();

        /// <summary>
        /// Declare that a prompt is on screen and how the remote should answer it. Dispose when it closes -
        /// the scope removes itself, so a using() block cannot leak one.
        /// </summary>
        /// <param name="ok">Press OK/Yes. Never null.</param>
        /// <param name="cancel">Press Cancel/No, or null when the prompt has no such button - in which
        /// case BOTH keys mean OK, because on a prompt whose only button is OK there is nothing else the
        /// down key could mean. See Resolve.</param>
        public static IDisposable ShowingPrompt(System.Action ok, System.Action cancel)
        {
            var scope = new PromptScope { Ok = ok, Cancel = cancel };
            prompts.Add(scope);   // innermost last - a prompt raised over a prompt answers first
            return scope;
        }

        // ---- the policy ---------------------------------------------------------------------------------

        /// <summary>
        /// What this press means right now, or null when it means nothing - in which case ShutterRemote
        /// lets the key through to Windows and the volume changes as it always would.
        /// </summary>
        /// <param name="isPrimary">Was this the PRIMARY button - the one pressed when the remote was
        /// bound? The remote's buttons carry no up/down marking, so which key each sends is settled at
        /// binding and translated by ShutterRemote before it gets here (RemoteDevices.IsPrimary).</param>
        public static System.Action Resolve(bool isPrimary)
        {
            var cfg = AppConfig.Settings?.Base;

            if (prompts.Count > 0)
            {
                if (cfg != null && !cfg.RemoteOnPrompt)
                    return null;

                var prompt = prompts[prompts.Count - 1];

                // No Cancel button => BOTH keys mean OK. This used to return the null Cancel, so on the
                // commonest prompt of all - "move the plate to the next corner, then click OK", which has
                // no Cancel - the iOS button did nothing whatsoever and the press went to the
                // volume control instead. Confirmed on real hardware 2026-09-21: MBOX armed at 12:02:34,
                // the operator's press logged at 12:02:55 as "nothing was waiting on it", and the prompt
                // finally answered with the mouse at 12:02:59.
                //
                // It is the same argument this file already makes for the Run state, and it was wrong to
                // stop short of it here: the remote has TWO BUTTONS and the operator may press either, so
                // a button that did nothing would just be one they have to remember not to press. Where a
                // prompt has two real answers the two buttons still differ; where it has only one, both
                // give it.
                return isPrimary || prompt.Cancel == null ? prompt.Ok : prompt.Cancel;
            }

            // A view waiting for the operator (the Height Map's per-point hold) takes BOTH keys as "carry
            // on", not just the up one. This is the case the remote was bought for, the operator has a hand
            // on a touch plate, and the remote has two buttons under their thumb - so having one of the two
            // mean STOP here would throw away a half-probed map on a mis-press. The
            // view's own Stop button is a deliberate act at the keyboard, which is the right ceremony for
            // throwing away twenty minutes of probing.
            if (HoldContinue != null)
                return cfg != null && !cfg.RemoteOnHeightMap ? null : HoldContinue;

            var state = Grbl.GrblViewModel == null ? GrblStates.Unknown : Grbl.GrblViewModel.GrblState.State;

            // The three state rows are the operator's to assign (RemoteFunctions). Each can be switched
            // off on its own - a behaviour someone dislikes should be turnable off, not a reason to stop
            // using the remote - and the defaults reproduce exactly what this switch used to hard-code.
            switch (state)
            {
                case GrblStates.Run:
                    if (cfg == null || !cfg.RemoteOnRun)
                        return null;
                    return Perform(isPrimary ? cfg.RemoteRunPrimary : cfg.RemoteRunSecondary);

                case GrblStates.Hold:
                    if (cfg == null || !cfg.RemoteOnHold)
                        return null;
                    return Perform(isPrimary ? cfg.RemoteHoldPrimary : cfg.RemoteHoldSecondary);

                case GrblStates.Idle:
                    if (cfg == null || !cfg.RemoteOnIdle)
                        return null;
                    return Perform(isPrimary ? cfg.RemoteIdlePrimary : cfg.RemoteIdleSecondary);
            }

            return null;
        }

        /// <summary>
        /// Turn an assigned function id into something to run, or null when it cannot run right now.
        ///
        /// Null is an ordinary answer, not an error: Cycle Start with the Start button disabled, Peek
        /// before the run strip exists. The caller treats it as "this press means nothing", which for a
        /// bound remote is a beep and a discarded press - the operator hears that it was heard and
        /// refused. That keeps "the app would not let you click this" and "the remote does nothing" the
        /// same thing, which is the rule the run-strip buttons already follow.
        /// </summary>
        private static System.Action Perform(string functionId)
        {
            switch (functionId)
            {
                case RemoteFunctions.CycleStart: return Press(StartButton);
                case RemoteFunctions.FeedHold:   return Press(HoldButton);
                case RemoteFunctions.Stop:       return Press(StopButton);

                // Run-strip actions that already exist as bindable keyboard actions. Routed through
                // ActionKeyBinder rather than reached for directly, so there is ONE implementation of
                // each and a remote button cannot drift from what the same action does on a key.
                case RemoteFunctions.Peek:   return Action("Program.Peek");
                case RemoteFunctions.Mdi:    return Action("Program.Mdi");
                case RemoteFunctions.Status: return Action("Program.Status");

                case RemoteFunctions.Reset:
                    return () => Grbl.Reset();

                case RemoteFunctions.Unlock:
                    return Grbl.GrblViewModel == null
                            ? (System.Action)null
                            : () => Grbl.GrblViewModel.ExecuteCommand(GrblConstants.CMD_UNLOCK);
            }

            return null;   // "None", and anything unrecognised - an unknown setting must read as harmless
        }

        /// <summary>A keyboard action, or null when nothing has registered a handler for it yet.</summary>
        private static System.Action Action(string id)
        {
            return ActionKeyBinder.CanInvoke(id) ? (System.Action)(() => ActionKeyBinder.Invoke(id)) : null;
        }

        /// <summary>
        /// Raise the button's Click, or null when there is no such button or it is disabled/hidden - which
        /// makes "the app would not let you click this" and "the remote does nothing" the same thing.
        /// </summary>
        private static System.Action Press(Button button)
        {
            if (button == null || !button.IsEnabled || !button.IsVisible)
                return null;

            return () => button.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
        }

        // ---- the hook's lifetime ------------------------------------------------------------------------

        private static bool wired;
        private static bool? lastLogged;

        /// <summary>
        /// Install or remove the keyboard hook to match the setting. Safe to call repeatedly; called at
        /// startup and whenever the setting changes.
        /// </summary>
        public static void Sync()
        {
            if (!wired && AppConfig.Settings?.Base != null)
            {
                wired = true;
                AppConfig.Settings.Base.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(Config.ShutterRemoteEnabled))
                        Sync();
                };
            }

            bool on = AppConfig.Settings?.Base != null && AppConfig.Settings.Base.ShutterRemoteEnabled;

            // Say which way it went, always. Turned OFF, this used to produce no log line whatsoever -
            // ShutterRemote.Stop returns silently when no hook is installed - so "the remote does nothing"
            // and "the remote is switched off" were the same silence, and the first thing to check was the
            // last thing anyone would think to look at (2026-09-18: the setting had been quietly reset when
            // it was renamed, and the log had not one word to say about it).
            if (on != lastLogged)
            {
                lastLogged = on;
                DebugLog.Write("remote", on
                    ? "shutter remote: ENABLED - the volume keys will act where a press means something"
                    : "shutter remote: disabled - tick 'A shutter remote drives the machine' under Settings > User Interface > Remote");
            }

            if (on)
            {
                ShutterRemote.Resolve = Resolve;
                ShutterRemote.Start();

                // Which DEVICE sent the key. Measurement only for now - it changes nothing about what a
                // press does - but it is the plumbing per-device binding would need, and the question it
                // answers first is whether that binding is possible at all. See RemoteDevices.
                RemoteDevices.Start();

                // Ticking the box arms a one-shot bind when nothing is bound yet. Unticking clears the
                // binding (below), so untick-and-retick is how an operator rebinds after replacing a
                // remote - no dialog, no device list, one button press.
                if (string.IsNullOrEmpty(AppConfig.Settings.Base.ShutterRemoteDevice))
                    RemoteDevices.ArmBinding();
            }
            else
            {
                ShutterRemote.Stop();
                RemoteDevices.Stop();
                RemoteDevices.ClearBinding();
            }
        }
    }
}
