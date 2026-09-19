/*
 * RemoteActions.cs - part of CNC Controls library
 *
 * What a press of the shutter remote MEANS, given what the machine and the app are doing at that moment.
 * ShutterRemote is the ear (a low-level keyboard hook - see that file for why one is needed at all); this
 * is the judgement.
 *
 * ---- The policy ----
 *
 *   a prompt is on screen   vol up = OK / Yes        vol down = Cancel / No
 *   the machine is RUNning  either key = Feed Hold
 *   the machine is HOLDing  vol up = Start / resume  vol down = Stop
 *   anything else           nothing - the key goes to Windows and the volume changes as usual
 *
 * Both keys mean Feed Hold while running on purpose. The remote has two modes that send different keys
 * (the PICO sends VOLUME UP in iOS mode and VOLUME DOWN in Android mode), the operator reaching for it
 * mid-cut wants the machine to STOP CUTTING, and a press that does nothing because the remote is in the
 * other mode is the worst possible outcome of that reach. Only once the machine is already held - nothing
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
        /// <param name="cancel">Press Cancel/No, or null when the prompt has no such button - the down key
        /// then does nothing, rather than quietly meaning OK.</param>
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
        public static System.Action Resolve(bool volumeUp)
        {
            if (prompts.Count > 0)
            {
                var prompt = prompts[prompts.Count - 1];
                return volumeUp ? prompt.Ok : prompt.Cancel;
            }

            // A view waiting for the operator (the Height Map's per-point hold) takes BOTH keys as "carry
            // on", not just the up one. This is the case the remote was bought for, the operator has a hand
            // on a touch plate, and which key their remote sends depends on the mode it happens to be in -
            // so having one of the two mean STOP here would abort a half-probed map on a mode setting. The
            // view's own Stop button is a deliberate act at the keyboard, which is the right ceremony for
            // throwing away twenty minutes of probing.
            if (HoldContinue != null)
                return HoldContinue;

            var state = Grbl.GrblViewModel == null ? GrblStates.Unknown : Grbl.GrblViewModel.GrblState.State;

            switch (state)
            {
                case GrblStates.Run:
                    return Press(HoldButton);

                case GrblStates.Hold:
                    return volumeUp ? Press(StartButton) : Press(StopButton);
            }

            return null;
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

            if (on)
            {
                ShutterRemote.Resolve = Resolve;
                ShutterRemote.Start();
            }
            else
                ShutterRemote.Stop();
        }
    }
}
