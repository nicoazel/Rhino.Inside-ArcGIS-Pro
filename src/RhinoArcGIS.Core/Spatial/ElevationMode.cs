namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// How Z values are interpreted when transforming between Rhino and GIS.
    /// See spec 06 §8 (geometry.elevation_mode).
    /// </summary>
    public enum ElevationMode
    {
        /// <summary>Z is an absolute elevation in the GIS vertical datum.</summary>
        Absolute = 0,

        /// <summary>Z is relative to the project anchor's GIS elevation.</summary>
        Relative
    }
}
