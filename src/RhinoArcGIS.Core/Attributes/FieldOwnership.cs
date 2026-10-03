namespace RhinoArcGIS.Core.Attributes
{
    /// <summary>
    /// Field-level ownership (spec 04 §5). Determines push/pull behaviour and conflict handling.
    /// </summary>
    public enum FieldOwnership
    {
        /// <summary>Rhino / Grasshopper is authoritative (e.g. height_m, panel_count).</summary>
        RhinoOwned = 0,

        /// <summary>ArcGIS is authoritative (e.g. parcel_id, review_status).</summary>
        ArcGisOwned,

        /// <summary>Both sides may edit; both-changed requires conflict review (e.g. status, material).</summary>
        Shared,

        /// <summary>Calculated during sync; never hand-edited (e.g. area_m2, centroid_x).</summary>
        Derived,

        /// <summary>Synced for context only; never overwritten (e.g. source_globalid).</summary>
        Locked,

        /// <summary>Stays in Rhino; not pushed (e.g. temporary GH params).</summary>
        LocalOnlyRhino,

        /// <summary>Stays in GIS; not pulled (e.g. enterprise audit fields).</summary>
        LocalOnlyArcGis
    }
}
