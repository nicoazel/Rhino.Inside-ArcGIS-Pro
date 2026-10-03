namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// Defines how Rhino model space maps onto GIS coordinates. The spec forbids guessing a
    /// CRS automatically (01 non-goals); the anchor must be set explicitly per project
    /// (06 §8 project_anchor).
    /// </summary>
    public sealed class ProjectAnchor
    {
        /// <summary>The Rhino-space point that corresponds to <see cref="GisPoint"/>.</summary>
        public Xyz RhinoPoint { get; }

        /// <summary>The GIS-space point that the Rhino anchor maps onto.</summary>
        public Xyz GisPoint { get; }

        /// <summary>Rotation applied about the vertical (Z) axis, in degrees, Rhino → GIS.</summary>
        public double RotationDegrees { get; }

        /// <summary>
        /// Uniform scale applied Rhino → GIS. Usually the Rhino-units-to-GIS-units factor.
        /// Must be non-zero.
        /// </summary>
        public double Scale { get; }

        public ProjectAnchor(Xyz rhinoPoint, Xyz gisPoint, double rotationDegrees = 0.0, double scale = 1.0)
        {
            if (scale == 0.0)
                throw new System.ArgumentOutOfRangeException(nameof(scale), "Anchor scale must be non-zero.");

            RhinoPoint = rhinoPoint;
            GisPoint = gisPoint;
            RotationDegrees = rotationDegrees;
            Scale = scale;
        }

        /// <summary>An identity anchor (Rhino origin maps to GIS origin, no rotation or scale).</summary>
        public static ProjectAnchor Identity { get; } =
            new ProjectAnchor(new Xyz(0, 0, 0), new Xyz(0, 0, 0), 0.0, 1.0);
    }
}
