namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>
    /// Coarse Rhino geometry kinds the classifier reasons about. The Rhino adapter inspects a
    /// RhinoObject and reports one of these (it owns the RhinoCommon-specific detection); the
    /// core maps it to a <see cref="GeometryTarget"/> so classification stays unit-testable.
    /// </summary>
    public enum RhinoGeometryKind
    {
        Unknown = 0,
        Point,
        BlockInstance,
        OpenCurve,
        ClosedPlanarCurve,
        ClosedNonPlanarCurve,
        PlanarSurface,
        Extrusion,
        ClosedBrep,
        OpenBrep,
        Mesh,
        SubD,
        Hatch
    }
}
