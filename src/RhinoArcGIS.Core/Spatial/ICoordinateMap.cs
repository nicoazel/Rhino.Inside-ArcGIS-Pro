namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// Maps coordinates and lengths between Rhino model space and GIS (projected) space. Implemented
    /// by <see cref="CoordinateTransform"/> (simple anchor) and <see cref="GeoReference"/> (Earth
    /// Anchor Point). The interop services depend on this abstraction, not a concrete transform.
    /// </summary>
    public interface ICoordinateMap
    {
        Xyz ModelToGis(Xyz p);
        Xyz GisToModel(Xyz p);

        /// <summary>Scale a scalar length (e.g. an extrusion height) from model units to GIS units.</summary>
        double ModelLengthToGis(double length);

        /// <summary>Scale a scalar length from GIS units to model units.</summary>
        double GisLengthToModel(double length);
    }
}
