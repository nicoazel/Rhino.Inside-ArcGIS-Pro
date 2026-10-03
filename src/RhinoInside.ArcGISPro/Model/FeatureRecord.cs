using System.Collections.Generic;

namespace RhinoInside.ArcGISPro
{
    internal enum FeatureGeometryKind
    {
        Point,
        Polyline,
        Polygon
    }

    /// <summary>
    /// A GIS feature reduced to plain numbers and strings.
    /// </summary>
    /// <remarks>
    /// This is the hand-off type between the two halves of a pull: <see cref="GisUtil"/> fills these
    /// in on the ArcGIS main CIM thread, and <see cref="RhinoHost"/> turns them into geometry on the
    /// UI thread. Keeping it free of both RhinoCommon and ArcGIS types is deliberate -- it means the
    /// GIS half never forces RhinoCommon to load, and the Rhino half never has to touch a
    /// thread-affine ArcGIS object.
    /// </remarks>
    internal sealed class FeatureRecord
    {
        internal string LayerName { get; set; }

        /// <summary>Layer colour as ARGB, used for the corresponding Rhino layer.</summary>
        internal int ColorArgb { get; set; }

        internal FeatureGeometryKind Kind { get; set; }

        /// <summary>
        /// Geometry parts, each a flat run of triples ordered
        /// (latitude degrees, longitude degrees, elevation metres) -- the coordinate convention
        /// Rhino's <c>EarthAnchorPoint.GetModelToEarthTransform</c> works in, so these feed straight
        /// through its inverse. Multipart features keep one entry per part rather than being
        /// flattened into a single run.
        /// </summary>
        internal List<double[]> Parts { get; } = new List<double[]>();

        internal Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Outcome of a pull, for reporting back to the dockpane.
    /// </summary>
    internal sealed class PullResult
    {
        internal int FeatureCount { get; set; }
        internal int ObjectCount { get; set; }
        internal int LayerCount { get; set; }
        internal int SkippedCount { get; set; }

        /// <summary>Earth anchor the model is georeferenced against.</summary>
        internal double AnchorLatitude { get; set; }
        internal double AnchorLongitude { get; set; }

        /// <summary>Rhino model unit system the geometry landed in.</summary>
        internal string ModelUnits { get; set; }

        /// <summary>Which slot latitude occupies in this Rhino's earth vector; see RhinoHost.</summary>
        internal bool LatitudeIsX { get; set; }

        /// <summary>Extents of the created geometry in model units, for sanity-checking placement.</summary>
        internal double ModelWidth { get; set; }
        internal double ModelHeight { get; set; }

        internal List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>A WGS84 location, free of both ArcGIS and Rhino types.</summary>
    internal sealed class GeoPoint
    {
        internal double Latitude { get; set; }
        internal double Longitude { get; set; }
    }

    /// <summary>Current state of the Rhino document's earth anchor, for display.</summary>
    internal sealed class EarthAnchorInfo
    {
        internal bool IsSet { get; set; }
        internal double Latitude { get; set; }
        internal double Longitude { get; set; }
        internal double ModelBaseX { get; set; }
        internal double ModelBaseY { get; set; }
    }
}
