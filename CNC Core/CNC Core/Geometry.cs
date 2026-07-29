/*
 * Geometry.cs - part of CNC Core library
 *
 * Portable geometry types, replacing System.Windows.Media.Media3D.Point3D and System.Windows.Point
 * in CNC.Core (both live in WPF assemblies, so they blocked a .NET 8 target).
 *
 * DOUBLE precision, deliberately. The obvious framework replacement, System.Numerics.Vector3, is
 * single precision - about 7 significant digits. These types carry g-code coordinates through arc and
 * spline interpolation, the emulator and height maps, where accumulating in float would quietly lose
 * resolution on a large work area (at 1000 mm, a float step is already ~0.0001 mm, and interpolation
 * accumulates). Machine geometry stays in double; only the renderer converts, and it converts at the
 * point of drawing.
 *
 * X/Y/Z are fields, not properties: existing code mutates points in place (e.g.
 * GCodeEmulator's "action.End.X = ..." on a field of a class), which a property-based struct would
 * not allow. The WPF Point3D behaved the same way, so this keeps those call sites unchanged.
 *
 * Conversion to the WPF types lives at the WPF boundary (CNC.Controls.GeometryInterop), not here -
 * CNC.Core must not know that Media3D exists.
 */

using System;

namespace CNC.Core
{
    /// <summary>A point in 3D space, double precision. Portable replacement for Media3D.Point3D.</summary>
    public struct Point3D
    {
        public double X, Y, Z;

        public Point3D(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Point3D operator +(Point3D a, Point3D b)
        {
            return new Point3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Point3D operator -(Point3D a, Point3D b)
        {
            return new Point3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public double[] ToArray()
        {
            return new double[] { X, Y, Z };
        }

        /// <summary>
        /// Rotate about the Z axis through the pivot (xOff, yOff). Angle in RADIANS, counter-clockwise.
        /// Z is unchanged.
        ///
        /// Replaces RP.Math.Vector3.RotateZ(xOff, yOff, rad) - same signature and convention, verified
        /// numerically against that assembly before it was dropped. RP.Math was an external net462-only
        /// DLL whose source is no longer present on this machine (the build only still resolved it from a
        /// stale copy in bin\), used for exactly these three lines of WCS-rotation maths.
        /// </summary>
        public Point3D RotateZ(double xOff, double yOff, double rad)
        {
            double dx = X - xOff, dy = Y - yOff;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            return new Point3D(xOff + dx * cos - dy * sin,
                               yOff + dx * sin + dy * cos,
                               Z);
        }

        // Equality must compare with double ==, exactly as the WPF Point3D did. The compiler-generated
        // ValueType.Equals for an all-double struct compares BITWISE, which differs in two ways that
        // matter to callers like CarveView's "!a.End.Equals(a.Start)" zero-length-move test:
        // -0.0 would not equal 0.0 (spurious segment), and NaN would equal NaN.
        public bool Equals(Point3D other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is Point3D && Equals((Point3D)obj);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode();
        }

        public static bool operator ==(Point3D a, Point3D b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Point3D a, Point3D b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return string.Format("{0},{1},{2}", X, Y, Z);
        }
    }

    /// <summary>A point in 2D space, double precision. Portable replacement for System.Windows.Point.</summary>
    public struct Point2D
    {
        public double X, Y;

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(Point2D other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is Point2D && Equals((Point2D)obj);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() ^ Y.GetHashCode();
        }

        public static bool operator ==(Point2D a, Point2D b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Point2D a, Point2D b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return string.Format("{0},{1}", X, Y);
        }
    }
}
