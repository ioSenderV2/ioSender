/*
 * MachineClient.cs - part of CNC Client
 *
 * The client-side ambient: "the machine this client is looking at". A desktop client has exactly
 * one machine connection, so a static accessor is true here in the way it never was in Core (the
 * same reasoning that kept the GCode.File singleton client-side). The host wires it once at
 * startup, after constructing its state stream; views reach it instead of Grbl.GrblViewModel.
 */

using CNC.Contracts;

namespace CNC.Client
{
    public static class MachineClient
    {
        /// <summary>The machine view model views bind against. Null until Attach runs.</summary>
        public static MachineViewModel Model { get; private set; }

        /// <summary>Wire the ambient client to a state stream. Call once at host startup.</summary>
        public static MachineViewModel Attach(IMachineStateStream stream)
        {
            Model = new MachineViewModel(new MachineMirror(stream));
            return Model;
        }
    }
}
