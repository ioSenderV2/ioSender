/*
 * SvgMatrix.cs - part of the CNC.Svg library
 *
 * A 2D affine transform, replacing System.Windows.Media.Matrix so this assembly carries no WPF.
 *
 * ---- Why the row-vector convention is preserved exactly ----
 *
 * WPF matrices are ROW-vector: p' = p * M. The SVG transform parser that uses this was written
 * against that convention - "own.Append(inherited)" means "apply my own transform, then my parent's"
 * and "Prepend" builds a left-to-right transform list applied right-to-left. Both are correct only
 * for row vectors. Switching to the column-vector convention that most maths texts use would invert
 * every one of those calls, and the failure mode is artwork that lands plausibly but wrongly placed -
 * which is the whole class of bug the transform handling exists to avoid.
 *
 * So this is deliberately a faithful copy of WPF's semantics, not a nicer matrix type:
 *
 *     | M11  M12  0 |
 *     | M21  M22  0 |          p' = (x*M11 + y*M21 + OffsetX,  x*M12 + y*M22 + OffsetY)
 *     | OffX OffY 1 |
 *
 * Verified against System.Windows.Media.Matrix by SvgCompare (tools/svg-compare), which runs the
 * same transform strings through both and compares the resulting points.
 */

using System;

namespace CNC.Svg
{
    /// <summary>A 2D affine transform in WPF's row-vector convention: p' = p * M.</summary>
    public struct SvgMatrix
    {
        public double M11, M12, M21, M22, OffsetX, OffsetY;

        public SvgMatrix(double m11, double m12, double m21, double m22, double offsetX, double offsetY)
        {
            M11 = m11; M12 = m12; M21 = m21; M22 = m22; OffsetX = offsetX; OffsetY = offsetY;
        }

        public static SvgMatrix Identity
        {
            get { return new SvgMatrix(1d, 0d, 0d, 1d, 0d, 0d); }
        }

        /// <summary>
        /// Exact comparison against identity, matching WPF. A tolerance would be wrong here: this only
        /// gates whether the transform is applied at all, and a matrix that is nearly identity must
        /// still be applied or the artwork drifts by however much "nearly" was.
        /// </summary>
        public bool IsIdentity
        {
            get
            {
                return M11 == 1d && M12 == 0d && M21 == 0d && M22 == 1d && OffsetX == 0d && OffsetY == 0d;
            }
        }

        /// <summary>a * b, row-vector order: a applies first, then b.</summary>
        public static SvgMatrix Multiply(SvgMatrix a, SvgMatrix b)
        {
            return new SvgMatrix(
                a.M11 * b.M11 + a.M12 * b.M21,
                a.M11 * b.M12 + a.M12 * b.M22,
                a.M21 * b.M11 + a.M22 * b.M21,
                a.M21 * b.M12 + a.M22 * b.M22,
                a.OffsetX * b.M11 + a.OffsetY * b.M21 + b.OffsetX,
                a.OffsetX * b.M12 + a.OffsetY * b.M22 + b.OffsetY);
        }

        /// <summary>this = this * m. The existing transform applies first, then <paramref name="m"/>.</summary>
        public void Append(SvgMatrix m)
        {
            this = Multiply(this, m);
        }

        /// <summary>this = m * this. <paramref name="m"/> applies first, then the existing transform.</summary>
        public void Prepend(SvgMatrix m)
        {
            this = Multiply(m, this);
        }

        /// <summary>Append a rotation about the origin, in DEGREES (SVG and WPF both use degrees).</summary>
        public void Rotate(double angleDegrees)
        {
            Append(RotationMatrix(angleDegrees));
        }

        /// <summary>Append a rotation about (centreX, centreY), in degrees.</summary>
        public void RotateAt(double angleDegrees, double centreX, double centreY)
        {
            // Translate the centre to the origin, rotate, translate back - composed in row-vector order
            // so the whole thing is a single append, exactly as WPF's own RotateAt behaves.
            var m = new SvgMatrix(1d, 0d, 0d, 1d, -centreX, -centreY);
            m.Append(RotationMatrix(angleDegrees));
            m.Append(new SvgMatrix(1d, 0d, 0d, 1d, centreX, centreY));
            Append(m);
        }

        private static SvgMatrix RotationMatrix(double angleDegrees)
        {
            double r = angleDegrees * Math.PI / 180d;
            double c = Math.Cos(r), s = Math.Sin(r);
            return new SvgMatrix(c, s, -s, c, 0d, 0d);
        }

        public void Transform(double x, double y, out double outX, out double outY)
        {
            outX = x * M11 + y * M21 + OffsetX;
            outY = x * M12 + y * M22 + OffsetY;
        }
    }
}
