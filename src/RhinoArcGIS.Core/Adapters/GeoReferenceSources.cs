using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>
    /// Optional Rhino-side capability: expose the document's Earth Anchor Point and its built-in
    /// model→ENU-metres transform. Implemented by the real Rhino adapter; kept separate from
    /// <see cref="IRhinoAdapter"/> so existing fakes/tests need not implement it.
    /// </summary>
    public interface IEarthAnchorSource
    {
        EarthAnchor GetEarthAnchor();

        /// <summary>RhinoCommon's <c>EarthAnchorPoint.GetModelToEarthTransform(units)</c> as a neutral matrix.</summary>
        AffineTransform GetModelToEarthMetres();
    }

    /// <summary>
    /// Optional ArcGIS-side capability: project WGS84 lat/long into the active map CRS and report
    /// the CRS linear unit. Implemented by the real ArcGIS adapter.
    /// </summary>
    public interface ICrsProjector
    {
        Xyz ProjectFromWgs84(double latitude, double longitude, double elevation);
        UnitSystem GetCrsLinearUnit();
    }

    /// <summary>
    /// Optional: the CRS's linear unit as exact metres per unit, for units that
    /// <see cref="UnitSystem"/> does not name (Clarke's foot, links, Indian yards ...).
    /// </summary>
    public interface ICrsUnitScale
    {
        double GetCrsMetresPerUnit();
    }

    /// <summary>
    /// Optional ArcGIS-side capability: the target CRS's exact local frame at a point, measured by
    /// the GIS's own geodesy -- see <see cref="LocalFrame"/>. With it, the georeference honours
    /// projection scale, grid convergence and units without the core doing any geodesy itself.
    /// </summary>
    public interface ILocalFrameProjector
    {
        /// <summary>The frame at a WGS84 point, or null when the CRS is unavailable.</summary>
        LocalFrame GetLocalFrame(double latitude, double longitude, double elevation);
    }

    /// <summary>
    /// A CRS's local frame at one point: the point's own CRS coordinates, and the CRS vectors of one
    /// metre of ground toward true east and true north. Scale factor, convergence and linear units
    /// are all folded into the two vectors.
    /// </summary>
    public sealed class LocalFrame
    {
        public Xyz Origin { get; set; }
        public Xyz EastPerMetre { get; set; }
        public Xyz NorthPerMetre { get; set; }

        /// <summary>CRS vertical units per metre.</summary>
        public double VerticalPerMetre { get; set; } = 1.0;

        /// <summary>
        /// The datum transformation used from the anchor's WGS84 to the CRS, as the GIS names it;
        /// "none (same datum)" when there is nothing to transform, "none available ..." when the
        /// datums differ and no transformation exists (placement is then off by the datum shift).
        /// </summary>
        public string DatumTransformation { get; set; }

        /// <summary>True when the datums differ and nothing could transform between them.</summary>
        public bool DatumUnresolved =>
            DatumTransformation != null && DatumTransformation.StartsWith("none available", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Optional ArcGIS-side capability: a model/GIS map that places every coordinate through the
    /// GIS's own geodesy and datum transformations (no linearisation), for the anchor's layer.
    /// Preferred over <see cref="ILocalFrameProjector"/> when available.
    /// </summary>
    public interface IGeodeticMapProvider
    {
        /// <summary>The map for this anchor, or null when the layer's CRS is unavailable.</summary>
        ICoordinateMap CreateGeodeticMap(EarthAnchor anchor);
    }
}
