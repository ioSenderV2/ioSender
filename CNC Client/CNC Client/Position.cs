/*
 * Position.cs - part of CNC Client
 *
 * Client-side coordinate holder: X/Y/Z in the machine's reported units, NaN = unset (the same
 * convention CNC Core's Position uses, so values adapted across the client boundary keep their
 * "never reported" semantics). Deliberately NOT the Core type: a contracts-only client cannot see
 * CNC.Core, and this one is a plain value carrier - no INPC, no axis-flags coupling. The scaling
 * constructor mirrors Core's Position(Position, scaleFactor) multiply semantics for X/Y/Z (Core
 * scales only GrblInfo-configured axes; a wire client has no axis config, and every current use is
 * X/Y/Z metric normalization).
 */

namespace CNC.Client
{
    public class Position
    {
        public double X = double.NaN, Y = double.NaN, Z = double.NaN;

        public Position()
        {
        }

        public Position(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Position(Position pos, double scaleFactor)
        {
            X = pos.X * scaleFactor;
            Y = pos.Y * scaleFactor;
            Z = pos.Z * scaleFactor;
        }

        /// <summary>Build from a wire position array (null slots and missing axes stay NaN).</summary>
        public static Position FromWire(double?[] values)
        {
            var p = new Position();
            if (values != null)
            {
                if (values.Length > 0 && values[0].HasValue) p.X = values[0].Value;
                if (values.Length > 1 && values[1].HasValue) p.Y = values[1].Value;
                if (values.Length > 2 && values[2].HasValue) p.Z = values[2].Value;
            }
            return p;
        }
    }
}
