using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Profiles;

namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// Builds the model↔GIS <see cref="ICoordinateMap"/> for a sync run. Prefers Rhino's Earth
    /// Anchor Point (when the Rhino adapter exposes it and the ArcGIS adapter can project the
    /// anchor into the map CRS); otherwise falls back to the profile's <see cref="ProjectAnchor"/>.
    /// </summary>
    public static class GeoReferenceFactory
    {
        public static ICoordinateMap Create(IRhinoAdapter rhino, IArcGISAdapter arcgis, LayerMappingProfile profile)
        {
            if (rhino is IEarthAnchorSource eas && arcgis is ICrsProjector projector)
            {
                EarthAnchor anchor = eas.GetEarthAnchor();
                if (anchor != null && anchor.IsValid)
                {
                    Xyz origin = projector.ProjectFromWgs84(anchor.Latitude, anchor.Longitude, anchor.Elevation);
                    UnitSystem crsUnits = projector.GetCrsLinearUnit();

                    if (UsesLocalFrame(rhino) && arcgis is IGeodeticMapProvider geodetic)
                    {
                        ICoordinateMap exact = geodetic.CreateGeodeticMap(anchor);
                        if (exact != null) return exact;
                    }
                    if (UsesLocalFrame(rhino) && arcgis is ILocalFrameProjector framer)
                    {
                        LocalFrame frame = framer.GetLocalFrame(anchor.Latitude, anchor.Longitude, anchor.Elevation);
                        if (frame != null)
                            return GeoReference.FromLocalFrame(anchor, frame.Origin, frame.EastPerMetre,
                                frame.NorthPerMetre, frame.VerticalPerMetre);
                    }

                    // Built from the anchor's own parameters rather than from Rhino's
                    // GetModelToEarthTransform. FromModelToEarth expects model -> ENU metres relative
                    // to the anchor, but that Rhino transform yields absolute degrees of latitude and
                    // longitude plus metres of elevation. Feeding it in is wrong three ways: degrees
                    // where metres are expected, absolute where anchor-relative is expected (so the
                    // projected origin is applied twice), and its axis order is the reverse of what
                    // RhinoCommon documents -- latitude comes back on Y, not X, which is what made
                    // pulled geometry appear rotated and squashed until it was measured directly.
                    // The anchor parameters carry no such ambiguity and are the unit-tested path.
                    // A named unit keeps its nominal length (documents synced this way stay put; the
                    // US survey foot is read as the international foot, 2 ppm apart). Any other
                    // unit comes from the GIS exactly, never silently as metres.
                    double metresPerCrsUnit = crsUnits.IsKnown() ? crsUnits.MetersPerUnit()
                        : projector is ICrsUnitScale scale ? scale.GetCrsMetresPerUnit() : double.NaN;
                    return GeoReference.FromAnchorParameters(anchor, origin, metresPerCrsUnit);
                }
            }

            ProjectAnchor pa = profile != null && profile.ProjectAnchor != null
                ? profile.ProjectAnchor.ToProjectAnchor()
                : ProjectAnchor.Identity;
            return GeoReference.FromProjectAnchor(pa);
        }

        /// <summary>Document key naming how the document's georeference is computed.</summary>
        public const string ModeKey = "gis.georef.mode";

        /// <summary>The exact local-frame georeference (see <see cref="GeoReference.FromLocalFrame"/>).</summary>
        public const string LocalFrameMode = "local-frame";

        /// <summary>The original planar georeference, kept for documents already synced with it.</summary>
        public const string PlanarMode = "planar";

        /// <summary>
        /// Documents record which georeference they were synced with. Switching an existing
        /// document to the exact frame would move every object's computed GIS position -- a whole
        /// layer of false edits, and an apply that pushes them -- so only documents that say
        /// "local-frame" (new ones; see the add-in's coordinator) get it.
        /// </summary>
        private static bool UsesLocalFrame(IRhinoAdapter rhino)
        {
            if (!(rhino is IDocumentStringStore store)) return false;
            try { return store.GetDocumentString(ModeKey) == LocalFrameMode; }
            catch { return false; }
        }

    }
}
