/*
 * KeypressHandler.cs - part of CNC Controls library
 *
 * v0.47 / 2026-03-23 / Io Engineering (Terje Io)
 *
 * Lives in CNC.Controls, not CNC.Core: everything here is WPF keyboard input (Key, ModifierKeys,
 * KeyEventArgs, UserControl contexts, Keyboard.FocusedElement). It derives from the portable
 * CNC.Core.JogController, which owns the jog state and execution this class used to duplicate
 * alongside it - so there is now one JogDistances/JogFeedrates/jog-mode, not two.
 *
 * The class NAME is load-bearing: saved key mappings persist their action identity as
 * "<ReflectedType.Name>.<MethodName>" (e.g. "KeypressHandler.FeedOverrideFinePlus") in the App.config
 * "KeyMap" section, so renaming this class - or moving the override functions below off it - silently
 * orphans every existing user's overrides. The namespace is not part of that string and is free to move.
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Serialization;
using CNC.Core;
using CNC.GCode;

namespace CNC.Controls
{
    public class KeypressHandler : JogController
    {
        private int N_AXIS = 3;
        private bool preCancel = false;
        private List<KeypressHandlerFn> handlers = new List<KeypressHandlerFn>();
        private List<HandlerFn> functions = new List<HandlerFn>();
        private AxisJog[] axisjog = new AxisJog[9];
        private JogKey[] jogKeys = new JogKey[] {
            new JogKey(0, Key.Right),
            new JogKey(0, Key.Left),
            new JogKey(1, Key.Up),
            new JogKey(1, Key.Down),
            new JogKey(2, Key.PageUp),
            new JogKey(2, Key.PageDown),
            new JogKey(3, Key.Home),
            new JogKey(3, Key.End),
            new JogKey(4),
            new JogKey(4),
            new JogKey(5),
            new JogKey(5),
            new JogKey(6),
            new JogKey(6),
            new JogKey(7),
            new JogKey(7),
            new JogKey(8),
            new JogKey(8)
        };

        /// <summary>
        /// Install this class as the keyboard-capable jog controller for every GrblViewModel.
        /// Called from App.OnStartup, alongside AppMessageBox.Register(), before the first model is built.
        /// </summary>
        public static void Register()
        {
            GrblViewModel.KeyboardFactory = model => new KeypressHandler(model);
        }

        public KeypressHandler(GrblViewModel model) : base(model)
        {
            for (int i = 0; i < axisjog.Length; i++)
                axisjog[i] = new AxisJog();

            AddFunction(FeedOverrideFinePlus, null);
            AddFunction(FeedOverrideFineMinus, null);
            AddFunction(FeedOverrideCoarseMinus, null);
            AddFunction(FeedOverrideCoarsePlus, null);
            AddFunction(FeedOverrideReset, null);
            AddFunction(FeedOverrideRapidsMedium, null);
            AddFunction(FeedOverrideRapidsLow, null);
            AddFunction(FeedOverrideRapidsReset, null);
            AddFunction(FloodOverrideToggle, null);
            AddFunction(MistOverrideToggle, null);
            AddFunction(Fan0Toggle, null);
            AddFunction(SpindleOverrideFinePlus, null);
            AddFunction(SpindleOverrideFineMinus, null);
            AddFunction(SpindleOverrideCoarseMinus, null);
            AddFunction(SpindleOverrideCoarsePlus, null);
            AddFunction(SpindleOverrideStop, null);
            AddFunction(ProbeConnectedToggle, null);
            AddFunction(OptionalStopToggle, null);
            AddFunction(SingleBlockToggle, null);
        }

        public override void Configure(int numAxes, string axisLetters, bool lathe)
        {
            base.Configure(numAxes, axisLetters, lathe);   // axis letters / lathe orientation are machine config

            N_AXIS = numAxes;
            axisLetters = axisLetters.Replace("-", "");
            for (int i = 0; i < jogKeys.Length; i++)
            {
                jogKeys[i].Command = string.Empty;
            }
            for (int i = 0; i < numAxes; i++)
            {
                var k = lathe ? (i == 0 ? 2 : 0) : i;
                jogKeys[i * 2].Command = axisLetters.Substring(k, 1) + (lathe && i != 0 ? "-{0}" : "{0}");
                jogKeys[i * 2 + 1].Command = axisLetters.Substring(k, 1) + (lathe && i != 0 ? "{0}" : "-{0}");
            }
        }

        // JogMode moved to CNC.Core.JogMode (JogController.cs) - it is jog state, not a keyboard concern,
        // and JogCommand is typed in it. Callers now use the namespace-level JogMode.

        [XmlType(TypeName = "KeyMapping")]
        public class KeypressHandlerFn
        {
            [XmlIgnore]
            internal string method, dummy;

            public Key Key;
            public ModifierKeys Modifiers;
            public bool OnUp;
            [XmlIgnore]
            public UserControl context;
            [XmlIgnore]
            public Func<Key, bool> Call;
            public string Context { get { return context == null ? "null" : context.Name; } set { dummy = value; } }
            public string Method { get { return Call == null ? method : Call.Method.ReflectedType.Name + "." +  Call.Method.Name; } set { method = value; } }
        }

        private class HandlerFn
        {
            public UserControl context;
            public Func<Key, bool> Call;
            public Key DefaultKey = Key.None;                 // factory default (first AddHandler key)
            public ModifierKeys DefaultModifiers = ModifierKeys.None;
            public string Context { get { return context == null ? "null" : context.Name; } }
            public string Method { get { return Call.Method.ReflectedType.Name + "." + Call.Method.Name; } }
        }

        private class JogKey
        {
            public JogKey (int axisIndex, Key key)
            {
                Key = key;
                DefaultKey = key;
                Command = string.Empty;
                AxisIndex = axisIndex;
            }
            public JogKey(int axisIndex)
            {
                Key = Key.None;
                DefaultKey = Key.None;
                Command = string.Empty;
                AxisIndex = axisIndex;
            }

            public Key Key { get; set; }
            public Key DefaultKey { get; private set; }
            public string Command { get; set; }
            public int AxisIndex { get; private set; }
            public bool Remapped { get; set; } = false;
        }

        private class AxisJog
        {
            public AxisJog ()
            {
                Key = Key.None;
                Command = String.Empty;
                Distance = 0d;
            }

            public Key Key { get; set; }
            public string Command { get; set; }
            public double Distance { get; set; }
        }

        public void AddFunction(Func<Key, bool> call, UserControl context)
        {
            var function = functions.Where(k => k.Call == call && k.context == context).FirstOrDefault();
            if (function == null)
                functions.Add(new HandlerFn() { Call = call, context = context });
        }

        public void AddHandler(Key key, ModifierKeys modifiers, Func<Key, bool> handler, UserControl context = null, bool onUp = true)
        {
            AddFunction(handler, context);
            SetDefault(handler, context, key, modifiers);
            handlers.Add(new KeypressHandlerFn(){ Key = key, Modifiers = modifiers, Call = handler, context = context, OnUp = onUp });
        }
        public void AddHandler(Key key, ModifierKeys modifiers, Func<Key, bool> handler, bool onUp)
        {
            AddFunction(handler, null);
            SetDefault(handler, null, key, modifiers);
            handlers.Add(new KeypressHandlerFn() { Key = key, Modifiers = modifiers, Call = handler, context = null, OnUp = onUp });
        }

        private void SetDefault(Func<Key, bool> handler, UserControl context, Key key, ModifierKeys modifiers)
        {
            var fn = functions.Where(k => k.Call == handler && k.context == context).FirstOrDefault();
            if (fn != null && fn.DefaultKey == Key.None)   // first registration wins
            {
                fn.DefaultKey = key;
                fn.DefaultModifiers = modifiers;
            }
        }

        // Jog configuration and state (JogDistances, JogFeedrates, JogStepDistance, SoftLimits,
        // LimitSwitchesClearance, IsJoggingEnabled, IsContinuousJoggingEnabled, DefaultSpeedFast,
        // CanJog/CanJog2/IsJogging, CurrentJogMode, JogModeChanged) all come from the portable
        // JogController base - this class used to keep a second, parallel copy of them.

        /// <summary>True while the current dispatch is a key autorepeat.</summary>
        public bool IsRepeating { get; private set; } = false;

        // ---- persistence -----------------------------------------------------------------------
        //
        // Key mappings are stored as the "KeyMap" section of App.config (folded in from the old standalone
        // KeyMap0.xml). CNC.Core can't reference the config store, so AppConfig keeps SectionConfig in sync with
        // the section payload and provides PersistHook to save App.config; with no hook (e.g. helper tools) the
        // code falls back to the legacy KeyMap0.xml file.
        public static List<KeypressHandlerFn> SectionConfig;
        public static System.Action PersistHook;
        private static bool UseSection { get { return PersistHook != null; } }
        private static string KeyMapPath { get { return Resources.ConfigPath + "KeyMap0.xml"; } }

        // Serializable snapshot of the current mappings: the live handlers (minus the F-key macro handler) plus a
        // synthetic entry for each remapped jog key.
        public List<KeypressHandlerFn> ExportMappings()
        {
            var list = handlers.Where(x => x.Method != "JobControl.FnKeyHandler").ToList();
            for (var i = 0; i < jogKeys.Length; i++)
                if (jogKeys[i].Remapped)
                    list.Add(new KeypressHandlerFn() { Key = jogKeys[i].Key, Modifiers = ModifierKeys.None, OnUp = false, Method = "Jogkey." + GrblInfo.AxisIndexToLetter(i >> 1) + ((i & 1) == 1 ? "minus" : "plus") });
            return list;
        }

        public bool SaveMappings(string filename = null)
        {
            if (handlers.Count == 0)
                return false;

            if (UseSection)
            {
                SectionConfig = ExportMappings();
                PersistHook();
                return true;
            }

            try
            {
                var xs = new XmlSerializer(typeof(List<KeypressHandlerFn>), new XmlRootAttribute("KeyMappings"));
                using (var fsout = new FileStream(filename ?? KeyMapPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    xs.Serialize(fsout, ExportMappings());
                return true;
            }
            catch (Exception e)
            {
                UserPrompt.Show(e.Message, "ioSender", PromptButtons.OK, PromptIcon.Warning);
                return false;
            }
        }

        // One-time importer for the "KeyMap" section: read the legacy KeyMap0.xml if present.
        public static List<KeypressHandlerFn> ReadLegacyFile()
        {
            try
            {
                if (!File.Exists(KeyMapPath))
                    return null;
                var xs = new XmlSerializer(typeof(List<KeypressHandlerFn>), new XmlRootAttribute("KeyMappings"));
                using (var reader = new StreamReader(KeyMapPath))
                    return (List<KeypressHandlerFn>)xs.Deserialize(reader);
            }
            catch
            {
                return null;
            }
        }

        // Load the saved mappings from the App.config "KeyMap" section (populated at config-load). Call once the
        // handlers have been registered (JobView.OnBooted); a null section leaves the default bindings in place.
        public bool LoadMappings()
        {
            if (!UseSection || SectionConfig == null || handlers.Count == 0)
                return false;
            ImportMappings(SectionConfig);
            return true;
        }

        public bool LoadMappings(string filename)
        {
            if (handlers.Count == 0)
                return false;

            try
            {
                var xs = new XmlSerializer(typeof(List<KeypressHandlerFn>), new XmlRootAttribute("KeyMappings"));
                List<KeypressHandlerFn> keymappings;
                using (var reader = new StreamReader(filename))
                    keymappings = (List<KeypressHandlerFn>)xs.Deserialize(reader);
                ImportMappings(keymappings);
                return true;
            }
            catch
            {
                UserPrompt.Show("keymap file is corrupt!", "ioSender", PromptButtons.OK, PromptIcon.Error);
                return false;
            }
        }

        // Apply a deserialized mapping list onto the live handlers / jog keys.
        private void ImportMappings(List<KeypressHandlerFn> keymappings)
        {
            if (keymappings == null)
                return;

            foreach (var newmap in keymappings)
            {
                if (newmap?.method == null)
                    continue;

                if (newmap.method.StartsWith("Jogkey.")) {
                    int k = GrblInfo.AxisLetterToIndex(newmap.method.Substring(7, 1));
                    if(k >= 0 && newmap.method.Substring(8) == "plus" || newmap.method.Substring(8) == "minus")
                    {
                        k = k * 2 + (newmap.method.Substring(8) == "minus" ? 1 : 0);
                        jogKeys[k].Key = newmap.Key;
                        jogKeys[k].Remapped = true;
                    }
                } else {

                    var handler = functions.Where(x => x.Method == newmap.method && x.Context == newmap.Context).FirstOrDefault();
                    var keymap = handlers.Where(k => k.Modifiers == newmap.Modifiers && k.Key == newmap.Key && k.OnUp == newmap.OnUp && k.Context == newmap.Context).FirstOrDefault();

                    if (handler != null)
                    {
                        if (keymap != null)
                        {
                            if (keymap.Method != newmap.method)
                            {
                                keymap.OnUp = newmap.OnUp;
                                keymap.Call = handler.Call;
                            }
                        }
                        else
                            handlers.Add(new KeypressHandlerFn() { Key = newmap.Key, Modifiers = newmap.Modifiers, Call = handler.Call, context = handler.context, OnUp = newmap.OnUp });
                    }
                    else if (keymap != null && newmap.method == "None")
                        handlers.Remove(keymap);
                }
            }
        }

        // ---- Editor API (used by the Key Mappings editor) -------------------------------------

        /// <summary>A single editable binding row surfaced to the Key Mappings editor.</summary>
        public class KeyBinding
        {
            public string Method;           // catalog identity for action bindings, "Jogkey.X.plus" for jog
            public string Context;          // owning control name or "null"
            public Key Key;
            public ModifierKeys Modifiers;
            public Key DefaultKey;          // factory default binding (for change detection / reset)
            public ModifierKeys DefaultModifiers;
            public bool OnUp = true;
            public bool IsJog;
            public int JogIndex = -1;       // index into jogKeys for jog bindings
            public string AxisLabel;        // e.g. "X +" for jog bindings
        }

        /// <summary>All bindable actions (bound and unbound), excluding the macro Fn-key dispatcher.</summary>
        public List<KeyBinding> GetActionBindings()
        {
            var list = new List<KeyBinding>();

            foreach (var fn in functions)
            {
                if (fn.Method == "JobControl.FnKeyHandler")
                    continue;

                var h = handlers.FirstOrDefault(k => k.Call == fn.Call && k.context == fn.context);

                list.Add(new KeyBinding {
                    Method = fn.Method,
                    Context = fn.Context,
                    Key = h == null ? Key.None : h.Key,
                    Modifiers = h == null ? ModifierKeys.None : h.Modifiers,
                    DefaultKey = fn.DefaultKey,
                    DefaultModifiers = fn.DefaultModifiers,
                    OnUp = h == null || h.OnUp
                });
            }

            return list;
        }

        /// <summary>Per-axis jog keys for the active machine (active axes only).</summary>
        public List<KeyBinding> GetJogBindings()
        {
            var list = new List<KeyBinding>();

            for (int i = 0; i < jogKeys.Length; i++)
            {
                if (string.IsNullOrEmpty(jogKeys[i].Command))
                    continue;

                bool plus = (i & 1) == 0;
                string letter = GrblInfo.AxisIndexToLetter(jogKeys[i].AxisIndex);

                list.Add(new KeyBinding {
                    IsJog = true,
                    JogIndex = i,
                    Key = jogKeys[i].Key,
                    Modifiers = ModifierKeys.None,
                    DefaultKey = jogKeys[i].DefaultKey,
                    DefaultModifiers = ModifierKeys.None,
                    AxisLabel = letter + (plus ? " +" : " −"),
                    Method = "Jogkey." + letter + (plus ? ".plus" : ".minus")
                });
            }

            return list;
        }

        /// <summary>Apply edited action bindings back onto the live handler list.</summary>
        public void ApplyActionBindings(IEnumerable<KeyBinding> bindings)
        {
            foreach (var b in bindings)
            {
                if (b.IsJog)
                    continue;

                var fn = functions.FirstOrDefault(f => f.Method == b.Method && f.Context == b.Context);
                if (fn == null)
                    continue;

                var existing = handlers.FirstOrDefault(k => k.Call == fn.Call && k.context == fn.context);

                if (b.Key == Key.None)
                {
                    if (existing != null)
                        handlers.Remove(existing);
                }
                else if (existing != null)
                {
                    existing.Key = b.Key;
                    existing.Modifiers = b.Modifiers;
                    existing.OnUp = b.OnUp;
                }
                else
                    handlers.Add(new KeypressHandlerFn { Key = b.Key, Modifiers = b.Modifiers, Call = fn.Call, context = fn.context, OnUp = b.OnUp });
            }
        }

        /// <summary>Apply edited jog keys back onto the live jog key table.</summary>
        public void ApplyJogBindings(IEnumerable<KeyBinding> bindings)
        {
            foreach (var b in bindings)
            {
                if (!b.IsJog || b.JogIndex < 0 || b.JogIndex >= jogKeys.Length)
                    continue;

                if (jogKeys[b.JogIndex].Key != b.Key)
                {
                    jogKeys[b.JogIndex].Key = b.Key;
                    jogKeys[b.JogIndex].Remapped = true;
                }
            }
        }

        public bool ProcessKeypress(KeyEventArgs e, bool allowJog, UserControl context = null)
        {
            bool isJogging = IsJogging, jogkeyPressed = false;
            JogKey jogKey = null;

            // Focus in an MDI-tagged text box (the MDI strip's edit box, the Console command
            // prompt) must never jog - let its arrow keys drive command history/caret instead.
            // Keys mapped to jog (e.g. Up/Down) otherwise jog before the per-key TextBox check
            // below ever runs, so gate the whole pass on it here.
            if (allowJog && Keyboard.FocusedElement is System.Windows.Controls.TextBox mdiBox && (mdiBox.Tag as string) == "MDI")
                allowJog = false;

            if (e.IsUp && isJogging)
            {
                bool cancel = !allowJog;

                isJogging = false;

                for (int i = 0; i < N_AXIS; i++)
                {
                    if (axisjog[i].Key == e.Key)
                    {
                        axisjog[i].Key = Key.None;
                        axisjog[i].Distance = 0d;
                        cancel = true;
                    }
                    else
                        isJogging = isJogging || (axisjog[i].Key != Key.None);
                }

                isJogging &= allowJog;

                if (cancel && !isJogging && CurrentJogMode != JogMode.Step)
                    JogCancel();
            }

            if (!isJogging && allowJog && Comms.com.OutCount != 0)
                return true;

            AllowJog = allowJog;

            // Ctrl+Shift is allowed through here now (Ctrl+Shift+<jog key> = Slow tier below). It is no longer
            // excluded: the Ctrl+Shift letter jogs (J/H/K/L...) are not jog keys, so they still fall through to
            // the handler dispatch and are unaffected.
            if(IsJoggingEnabled && e.IsDown && CanJog && !(Keyboard.Modifiers == ModifierKeys.Alt || Keyboard.Modifiers == ModifierKeys.Windows))
                jogKey = jogKeys.Where(p => p.Key == e.Key && p.Command != string.Empty).FirstOrDefault();

            if (jogKey != null)
            {
                // Do not respond to autorepeats!
                if (e.IsRepeat)
                    return true;

                if (grbl.GrblState.State == GrblStates.Alarm)   // 'grbl' is this handler's model (== context.DataContext when a context is supplied); using it directly keeps a null-context caller (e.g. a modeless hold prompt forwarding jog keys) from NRE'ing here
                    return true;

                N_AXIS = GrblInfo.AxisFlags.HasFlag(AxisFlags.A) ? 4 : 3;

                isJogging = axisjog[jogKey.AxisIndex].Key != e.Key;
                axisjog[jogKey.AxisIndex].Key = e.Key;
                axisjog[jogKey.AxisIndex].Command = jogKey.Command;
            }
            else
                jogkeyPressed = !(Keyboard.FocusedElement is System.Windows.Controls.TextBox) && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.PageUp || e.Key == Key.PageDown);

            if (isJogging)
            {
                string command = string.Empty;

                if (grbl.GrblState.State == GrblStates.Alarm)   // 'grbl' is this handler's model (== context.DataContext when a context is supplied); using it directly keeps a null-context caller (e.g. a modeless hold prompt forwarding jog keys) from NRE'ing here
                    return true;

                isJogging = false;

                for (int i = 0; i < N_AXIS; i++)
                {
                    if (axisjog[i].Key != Key.None)
                    {
                       isJogging = true;
                        axisjog[i].Distance = axisjog[i].Command.Contains('-') ? -1d : 1d;
                    }
                    else
                        axisjog[i].Distance = 0d;
                }

                if (isJogging)
                {
                    // Tier selection is the keyboard's business; the mode itself lives on the base, so
                    // decide it locally and publish once via SetJogMode (which raises JogModeChanged).
                    JogMode mode;
                    ModifierKeys jogmods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
                    if (jogmods == ModifierKeys.Control)   // Ctrl (alone) -> single step
                    {
                        for (int i = 0; i < N_AXIS; i++)
                            axisjog[i].Key = Key.None;
                        preCancel = !(CurrentJogMode == JogMode.Step || CurrentJogMode == JogMode.None);
                        mode = JogMode.Step;
                        JogDistances[(int)mode] = grbl.JogStep;
                    }
                    else if (IsContinuousJoggingEnabled)
                    {
                        preCancel = true;
                        // Absolute tiers, consistent with the UI jog panel / buttons: Shift = Fast, Ctrl+Shift =
                        // Slow, no modifier = the DefaultSpeedFast default speed.
                        if (jogmods == (ModifierKeys.Control | ModifierKeys.Shift))
                            mode = JogMode.Slow;
                        else if (jogmods == ModifierKeys.Shift)
                            mode = JogMode.Fast;
                        else
                            mode = DefaultSpeedFast ? JogMode.Fast : JogMode.Slow;
                    }
                    else
                    {
                        for (int i = 0; i < N_AXIS; i++)
                            axisjog[i].Key = Key.None;
                        mode = JogMode.None;
                    }

                    SetJogMode(mode);

                    if (mode != JogMode.None)
                    {
                        // Intent only: which axes, which way, how far, how fast. The JogController does
                        // the soft-limit clamping, G91/G53 selection and "$J=" rendering - machine safety
                        // stays server-side rather than in a key handler.
                        var jog = new JogCommand(N_AXIS)
                        {
                            Mode = mode,
                            Distance = JogDistances[(int)mode],
                            Feedrate = JogFeedrates[(int)mode],
                            CancelFirst = preCancel
                        };

                        for (int i = 0; i < N_AXIS; i++)
                            jog.Directions[i] = axisjog[i].Distance;

                        Execute(jog);
                    }

                    return mode != JogMode.None;
                } 
            }

            IsRepeating = e.IsRepeat;

            if (Keyboard.Modifiers == ModifierKeys.Alt)
            {
                var handler = handlers.Where(k => k.Modifiers == Keyboard.Modifiers && k.Key == e.SystemKey && k.OnUp == e.IsUp && k.context == context).FirstOrDefault();
                if (handler != null)
                    return handler.Call(e.SystemKey);
                else
                {
                    handler = handlers.Where(k => k.Modifiers == Keyboard.Modifiers && k.Key == e.SystemKey && k.OnUp == e.IsUp && k.context == null).FirstOrDefault();
                    if (handler != null)
                        return handler.Call(e.SystemKey);
                }
            }
            else
            {
                // Shift/Ctrl/Ctrl+Shift on a jog key are only speed/step modifiers (Shift=fast, Ctrl=step,
                // Ctrl+Shift=slow - applied in JogCommand), never distinct key bindings. In UI jog mode the
                // continuous-jog path above is disabled and the cursor keys dispatch here (registered with
                // ModifierKeys.None), so without this a modified arrow matches nothing and is silently dropped
                // (which is why Shift+arrow only jogged when the arrow was pressed first). Strip those modifiers
                // for jog keys so every combo routes to the unmodified handler and JogCommand applies the tier.
                // The Ctrl+Shift letter jogs (J/H/K/L etc.) are unaffected - those keys are not jog keys.
                ModifierKeys mods = Keyboard.Modifiers;
                if (jogKeys.Any(j => j.Key == e.Key))
                    mods &= ~(ModifierKeys.Shift | ModifierKeys.Control);

                if (mods == ModifierKeys.None || mods == ModifierKeys.Control || mods == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    var handler = handlers.Where(k => k.Modifiers == mods && k.Key == e.Key && k.OnUp == e.IsUp && k.context == context).FirstOrDefault();
                    if (handler != null)
                        return handler.Call(e.Key);
                    else
                    {
                        handler = handlers.Where(k => k.Modifiers == mods && k.Key == e.Key && k.OnUp == e.IsUp && k.context == null).FirstOrDefault();
                        if (handler != null)
                            return handler.Call(e.Key);
                    }
                }
            }

            return jogkeyPressed;
        }

        // Kept as named entry points for existing callers; the base does the work (Cancel already
        // resets the jog mode and raises JogModeChanged).
        public void JogCancel()
        {
            Cancel();
        }

        // Retained for callers that render their own jog block (ControllerMapper's gamepad jogging).
        public void SendJogCommand(string command)
        {
            Send(command, preCancel);
        }

        private bool FeedOverrideFinePlus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_FEED_OVR_FINE_PLUS);

            return true;
        }
        private bool FeedOverrideFineMinus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_FEED_OVR_FINE_MINUS);

            return true;
        }
        private bool FeedOverrideCoarseMinus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_FEED_OVR_COARSE_MINUS);

            return true;
        }
        private bool FeedOverrideCoarsePlus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_FEED_OVR_COARSE_PLUS);

            return true;
        }
        private bool FeedOverrideReset(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_FEED_OVR_RESET);

            return true;
        }
        private bool FeedOverrideRapidsMedium(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_RAPID_OVR_MEDIUM);

            return true;
        }
        private bool FeedOverrideRapidsLow(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_RAPID_OVR_LOW);

            return true;
        }
        private bool FeedOverrideRapidsReset(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_RAPID_OVR_RESET);

            return true;
        }
        private bool FloodOverrideToggle(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_COOLANT_FLOOD_OVR_TOGGLE);

            return true;
        }
        private bool MistOverrideToggle(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_COOLANT_MIST_OVR_TOGGLE);

            return true;
        }
        private bool Fan0Toggle(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_OVERRIDE_FAN0_TOGGLE);

            return true;
        }
        private bool SpindleOverrideFinePlus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_SPINDLE_OVR_FINE_PLUS);

            return true;
        }
        private bool SpindleOverrideFineMinus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_SPINDLE_OVR_FINE_MINUS);

            return true;
        }
        private bool SpindleOverrideCoarseMinus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_SPINDLE_OVR_COARSE_MINUS);

            return true;
        }
        private bool SpindleOverrideCoarsePlus(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_SPINDLE_OVR_COARSE_PLUS);

            return true;
        }
        private bool SpindleOverrideStop(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_SPINDLE_OVR_STOP);

            return true;
        }
        private bool ProbeConnectedToggle(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_PROBE_CONNECTED_TOGGLE);

            return true;
        }
        private bool OptionalStopToggle(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_OPTIONAL_STOP_TOGGLE);

            return true;
        }
        private bool SingleBlockToggle(Key key)
        {
            Comms.com.WriteByte(GrblConstants.CMD_SINGLE_BLOCK_TOGGLE);

            return true;
        }
    }
}
