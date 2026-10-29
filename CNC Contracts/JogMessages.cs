/*
 * JogMessages.cs - part of CNC Contracts
 *
 * JogMode and JogCommand, moved verbatim from CNC Core's JogController.cs - already pure data (a
 * client builds one, the server's JogController clamps/renders/sends it), so this is the wire shape
 * of a jog request with no changes needed. Namespace stays CNC.Core for the same zero-churn reason
 * the other moved types do - see MachineEnums.cs's header.
 */

namespace CNC.Core
{
    public enum JogMode
    {
        Step = 0,
        Slow,
        Fast,
        None // must be last!
    }

    /// <summary>
    /// A jog request: which axes move, in which direction, how far and how fast.
    /// The client supplies Distance/Feedrate (normally from the controller's configured tier);
    /// the controller still owns clamping and rendering, so machine safety does not move client-side.
    /// </summary>
    public class JogCommand
    {
        /// <summary>Per-axis direction: -1, 0 (not moving) or +1. Indexed by axis.</summary>
        public double[] Directions;

        /// <summary>Requested distance for this jog, in current units.</summary>
        public double Distance;

        /// <summary>Requested feedrate for this jog.</summary>
        public double Feedrate;

        /// <summary>Speed/step tier this request came from - reported as the active jog mode.</summary>
        public JogMode Mode = JogMode.None;

        /// <summary>Cancel any in-flight jog before issuing this one (the old preCancel flag).</summary>
        public bool CancelFirst;

        public JogCommand(int axes)
        {
            Directions = new double[axes];
        }

        public bool IsMoving
        {
            get
            {
                for (int i = 0; i < Directions.Length; i++)
                    if (Directions[i] != 0d)
                        return true;
                return false;
            }
        }
    }
}
