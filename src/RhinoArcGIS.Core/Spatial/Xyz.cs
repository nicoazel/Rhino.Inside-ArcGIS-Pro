using System.Globalization;

namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// Neutral 3D coordinate used across the interop core so that core logic never depends
    /// on RhinoCommon's Point3d or the ArcGIS MapPoint. Adapters convert to/from this type.
    /// </summary>
    public readonly struct Xyz
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public Xyz(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Xyz(double x, double y) : this(x, y, 0.0) { }

        /// <summary>Euclidean distance to another coordinate.</summary>
        public double DistanceTo(Xyz other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            double dz = Z - other.Z;
            return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture, "({0}, {1}, {2})", X, Y, Z);
        }
    }
}
