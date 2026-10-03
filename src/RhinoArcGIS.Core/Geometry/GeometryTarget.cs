namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>The ArcGIS-side geometry class a Rhino object maps to (spec 01, 03).</summary>
    public enum GeometryTarget
    {
        Unsupported = 0,
        PointZ,
        PolylineZ,
        PolygonZ,
        PolygonExtrusion,
        Multipatch
    }
}
