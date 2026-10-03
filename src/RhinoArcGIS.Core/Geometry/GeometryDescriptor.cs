namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>
    /// A neutral description of a Rhino object's geometry, produced by the Rhino adapter and
    /// consumed by <see cref="GeometryClassifier"/>. Carries only what the classifier needs,
    /// so the core never references RhinoCommon.
    /// </summary>
    public sealed class GeometryDescriptor
    {
        public RhinoGeometryKind Kind { get; set; } = RhinoGeometryKind.Unknown;

        /// <summary>True if the geometry has zero/empty extent.</summary>
        public bool IsEmpty { get; set; }

        /// <summary>For curves: whether the curve is closed.</summary>
        public bool IsClosed { get; set; }

        /// <summary>For curves/surfaces: whether the geometry is planar.</summary>
        public bool IsPlanar { get; set; }

        /// <summary>For Breps/meshes: whether the solid is closed (watertight).</summary>
        public bool IsSolid { get; set; }

        public GeometryDescriptor() { }

        public GeometryDescriptor(RhinoGeometryKind kind)
        {
            Kind = kind;
        }
    }
}
