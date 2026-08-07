/*
 * MachineState.cs - part of CNC Core library
 *
 * What the MACHINE is doing, separated from how a client chooses to show it. Step 5 of the client/server
 * split: GrblViewModel is simultaneously the protocol state machine, the WPF binding surface and the
 * command sender, and 95 files bind to it. A server owns only the first of those three.
 *
 * ---- Why composition rather than inheritance ----
 *
 * Two earlier splits in this project used inheritance - KeypressHandler : JogController, and
 * CNC.Controls.GCode : GCodeProgram - because in both the client class ADDS desktop behaviour to a
 * portable base, and a server would legitimately use the base alone. That is not the shape here.
 *
 * A server holds THE machine state; a client holds a MIRROR of it, fed by a delta stream and living in a
 * different process (eventually a different language). Those are two objects, not one type with two
 * users. Inheritance would model them as one and let the WPF client keep passing C# objects around - the
 * exact shortcut that a browser client cannot take, and the reason it is worth refusing now while it is
 * still cheap. So GrblViewModel HOLDS one of these and forwards.
 *
 * ---- Why the object-typed members moved first ----
 *
 * Position, EnumFlags<T> and AxisLetter are created once and mutated in place - nothing in the repo ever
 * reassigns them (checked, not assumed). So ownership can move here while GrblViewModel's properties
 * become getters returning the SAME instances: every binding in the app still points at the object it
 * already pointed at, and the notification behaviour cannot change because the notifying objects
 * themselves are untouched. Scalars are a separate slice - those need real setters, and a setter is where
 * behaviour hides.
 *
 * ---- What does NOT belong here, and why it is worth saying ----
 *
 * Roughly a third of GrblViewModel is client policy that no server should ever hold: the ResponseLog /
 * CommandLog / MDIHistory buffers and their four display filters, the ICommand bindings, IsCameraVisible,
 * the keyboard/jog provider delegates. Those stay where they are. The test that has held up all through
 * this split is the one that keeps holding: what talks to the machine stays, what talks to the operator
 * goes.
 *
 * NO INotifyPropertyChanged on this class deliberately. Change notification is a client concern; the
 * server's job is to produce deltas. The members it currently holds notify on their own behalf because
 * they are shared instances during the transition - that is a transitional property, not the design.
 */

using CNC.GCode;   // AxisFlags - the axis bitmask is a g-code-level concept, not a Core-local one

namespace CNC.Core
{
    /// <summary>
    /// The controller's own state - position, homing, signals and offsets. Owned by whatever is talking to
    /// the machine; mirrored by clients.
    /// </summary>
    public class MachineState
    {
        // Machine coordinates, as last reported. IsSet() per axis distinguishes "not homed / never
        // reported" from a genuine zero, which is why this is a Position and not three doubles.
        public Position MachinePosition { get; private set; } = new Position();

        // Work coordinates - machine position less the active offsets (WCS + G92 + tool length).
        public Position WorkPosition { get; private set; } = new Position();

        // Whichever of the two above is currently the authoritative reading for display and for the jog
        // controller's soft-limit arithmetic.
        public Position Position { get; private set; } = new Position();

        // The offsets that separate the two. WorkPositionOffset is the controller's own WCO report; the
        // other two are read back from $# and are what a probe result has to be interpreted against.
        public Position WorkPositionOffset { get; private set; } = new Position();
        public Position ToolOffset { get; private set; } = new Position();
        public Position HomePosition { get; private set; } = new Position();

        // Where the last probe triggered, in machine coordinates. Meaningless unless the probe succeeded -
        // the success flag lives with the scalars for now.
        public Position ProbePosition { get; private set; } = new Position();

        // Axis naming for this machine (letters, and the lathe remapping). A MACHINE property, not a
        // display preference - it decides what a jog command is even called.
        public AxisLetter AxisLetter { get; private set; } = new AxisLetter();

        // Which axes have been homed this session. Gates soft limits and every absolute move.
        public EnumFlags<AxisFlags> AxisHomed { get; private set; } = new EnumFlags<AxisFlags>(AxisFlags.None);

        // Limit/probe/door/hold inputs as last reported, and the subset this build actually has wired.
        public EnumFlags<Signals> Signals { get; private set; } = new EnumFlags<Signals>(Core.Signals.Off);
        public EnumFlags<Signals> OptionalSignals { get; set; } = new EnumFlags<Signals>(Core.Signals.Off);

        // Axes currently under a scaling factor (G51).
        public EnumFlags<AxisFlags> AxisScaled { get; private set; } = new EnumFlags<AxisFlags>(AxisFlags.None);
    }
}
