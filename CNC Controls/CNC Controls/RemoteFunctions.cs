/*
 * RemoteFunctions.cs - part of CNC Controls library
 *
 * What a remote button can be set to do, for the three rows whose meaning is the operator's to choose:
 * machine RUNNING, machine HOLDING, machine IDLE.
 *
 * ---- Why this list and not the keyboard's ----
 *
 * The obvious move was to offer whatever Keyboard & Controller offers, and it is the wrong list. That
 * catalogue is thirty-odd entries dominated by Help menu commands and OBS camera controls, and it does
 * not even contain Cycle Start, Feed Hold or Stop - the remote presses those buttons directly. A drop-
 * down of "Help > About" and "OBS: Front Left camera - Stop recording" is not something anyone picks
 * from one-handed standing at a machine.
 *
 * So this is a deliberately short list of things worth doing with a pendant in your hand, chosen with
 * the operator 2026-09-21. Nine entries, every one of which does something useful while the machine is
 * in one of those states.
 *
 * The rows that are NOT here - a prompt on screen, the height map waiting - keep fixed meanings, because
 * there the button is answering a question that is already on the screen. They get an on/off tick and
 * nothing else.
 *
 * ---- Availability ----
 *
 * Resolving a function can return null, and that is an ordinary outcome: Cycle Start when the Start
 * button is disabled, Peek when there is no run strip yet. Null means the press does nothing, which for
 * a bound remote means it beeps and is discarded - the operator hears that it was heard and refused,
 * rather than the machine doing something unexpected. "The app would not let you click this" and "the
 * remote does nothing" stay the same thing, which is the rule the run-strip buttons already follow.
 */

using System.Collections.Generic;

namespace CNC.Controls
{
    public static class RemoteFunctions
    {
        public class Function
        {
            // PROPERTIES, NOT FIELDS. WPF data binding resolves against properties only - a ComboBox with
            // DisplayMemberPath="Label" over a type whose Label is a field binds to nothing and renders a
            // popup of the right height full of blank rows, which is exactly what shipped on 2026-09-21.
            // It fails silently: no exception, no binding error loud enough to notice, just empty rows.
            public string Id { get; set; }
            public string Label { get; set; }
            public string Description { get; set; }

            // So a ComboBox bound straight to the catalogue still reads properly if DisplayMemberPath is
            // ever dropped.
            public override string ToString() { return Label; }
        }

        public const string None = "None";
        public const string CycleStart = "CycleStart";
        public const string FeedHold = "FeedHold";
        public const string Stop = "Stop";
        public const string Peek = "Peek";
        public const string Mdi = "Mdi";
        public const string Status = "Status";
        public const string Reset = "Reset";
        public const string Unlock = "Unlock";

        public static readonly Function[] Catalog = new Function[]
        {
            new Function { Id = None,       Label = "Nothing",        Description = "The button does nothing in this state. It still beeps, so you know it was heard." },
            new Function { Id = CycleStart, Label = "Cycle Start",    Description = "Press the run strip's Cycle Start button - start a program, or resume from a hold." },
            new Function { Id = FeedHold,   Label = "Feed Hold",      Description = "Press the run strip's Feed Hold button - decelerate and stop, keeping position." },
            new Function { Id = Stop,       Label = "Stop",           Description = "Press the run strip's Stop button - end the run." },
            new Function { Id = Peek,       Label = "Peek / Resume",  Description = "Pause at the end of the current block and park at G30 with the spindle off; press again to go back and carry on." },
            new Function { Id = Mdi,        Label = "MDI (open console)", Description = "Open the console with the caret in its input box." },
            new Function { Id = Status,     Label = "Status",         Description = "Show the status message history." },
            new Function { Id = Reset,      Label = "Reset",          Description = "Soft-reset the controller (Ctrl-X). Stops motion immediately and clears the planner." },
            new Function { Id = Unlock,     Label = "Unlock",         Description = "Clear an alarm ($X) so the machine will take commands again." },
        };

        /// <summary>The catalogue entry for an id, or the "Nothing" entry when the id is unknown - an
        /// unrecognised setting must read as harmless, never as something that moves a machine.</summary>
        public static Function Find(string id)
        {
            foreach (var f in Catalog)
                if (f.Id == id)
                    return f;
            return Catalog[0];
        }

        /// <summary>The catalogue as a list, for binding a ComboBox's ItemsSource.</summary>
        public static IList<Function> Items { get { return Catalog; } }
    }
}
