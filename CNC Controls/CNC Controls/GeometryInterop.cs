/*
 * GeometryInterop.cs - part of CNC Controls library
 *
 * Bridges CNC.Core's portable geometry (CNC.Core.Point3D, double precision) to the WPF renderer types
 * (System.Windows.Media.Media3D.Point3D). This lives in the WPF layer on purpose: CNC.Core must not
 * know Media3D exists, which is what allows it to target .NET 8.
 *
 * The conversion is lossless - Media3D.Point3D is double precision too, so this is a plain copy, not
 * a narrowing. Call it at the point of drawing.
 */

using Media3D = System.Windows.Media.Media3D;

namespace CNC.Controls
{
    public static class GeometryInterop
    {
        /// <summary>Machine geometry -> renderer geometry.</summary>
        public static Media3D.Point3D ToMedia3D(this CNC.Core.Point3D p)
        {
            return new Media3D.Point3D(p.X, p.Y, p.Z);
        }

        /// <summary>Renderer geometry -> machine geometry.</summary>
        public static CNC.Core.Point3D ToCore(this Media3D.Point3D p)
        {
            return new CNC.Core.Point3D(p.X, p.Y, p.Z);
        }
    }
}
