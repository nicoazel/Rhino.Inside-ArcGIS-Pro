namespace RhinoArcGIS.Core.Identity
{
    /// <summary>
    /// Canonical Rhino user-string keys for system/sync metadata. Per spec 04 §15 these live
    /// under the <c>gis.</c> namespace so they never collide with user-facing design attributes
    /// (height_m, asset_type, …) which are stored un-namespaced.
    /// </summary>
    public static class GisKeys
    {
        public const string Namespace = "gis.";

        public const string SyncGuid = "gis.sync_guid";
        /// <summary>
        /// The Rhino object that owns these tracking tags. Rhino copies user strings during
        /// Alt-gumball, copy and array operations; a different object id means a new object,
        /// even when the original is outside the current sync scope.
        /// </summary>
        public const string RhinoObjectId = "gis.rhino_objectid";
        public const string ArcGisGlobalId = "gis.arcgis_globalid";
        public const string ArcGisObjectId = "gis.arcgis_objectid";
        public const string ArcGisLayer = "gis.arcgis_layer";

        /// <summary>
        /// The data source the object was pulled from (workspace path + feature class), as distinct
        /// from the map label in <see cref="ArcGisLayer"/>. A sync refuses to run against a layer
        /// whose source differs: same label, different data is the classic week-later mistake.
        /// </summary>
        public const string ArcGisSource = "gis.arcgis_source";
        public const string SourceCrs = "gis.source_crs";
        public const string LastPullTime = "gis.last_pull_time";
        public const string LastPushTime = "gis.last_push_time";
        public const string GeometryHash = "gis.geometry_hash";

        /// <summary>
        /// Hash of the geometry as Rhino represents it, recorded at the last sync. Rhino does not
        /// keep every vertex ArcGIS sent -- a planar Brep drops sub-tolerance segments and degenerate
        /// spurs -- so the same feature hashes differently on the two sides even when neither has
        /// been edited. Each side is compared against its own baseline; when this key is absent
        /// <see cref="GeometryHash"/> stands for both.
        /// </summary>
        public const string RhinoGeometryHash = "gis.rhino_geometry_hash";

        /// <summary>
        /// The Rhino geometry's model-space hash and the georeference it was baselined under, written
        /// with the geometry baselines. While it matches, the object is untouched and its baselines
        /// stand without mapping it into the CRS; see <see cref="Change.ModelStamp"/>. Whatever
        /// writes the baselines without it must remove it.
        /// </summary>
        public const string RhinoModelStamp = "gis.rhino_model_stamp";
        public const string AttributeHash = "gis.attribute_hash";
        public const string SyncState = "gis.sync_state";

        /// <summary>
        /// On a Rhino object that is one piece of a multipart feature: the <see cref="SyncGuid"/> of
        /// the object that carries the feature's identity. Such objects hold no identity of their
        /// own; the adapter folds them back into that representative when reading.
        /// </summary>
        public const string PartOf = "gis.part_of";

        /// <summary>Position of a part within its feature, so read-back keeps the original order.</summary>
        public const string PartIndex = "gis.part_index";

        /// <summary>Prefix used for per-field hashes, e.g. <c>gis.field_hash.height_m</c>.</summary>
        public const string FieldHashPrefix = "gis.field_hash.";

        /// <summary>
        /// ArcGIS field that stores extra Rhino metadata without a dedicated schema field
        /// (spec 04 §14). Un-namespaced because it is a real ArcGIS field, not a Rhino system key.
        /// </summary>
        public const string RhinoAttrsJsonField = "rhino_attrs_json";

        /// <summary>True if the given user-string key belongs to the system namespace.</summary>
        public static bool IsSystemKey(string key)
            => !string.IsNullOrEmpty(key) && key.StartsWith(Namespace, System.StringComparison.Ordinal);

        public static string FieldHashKey(string fieldName) => FieldHashPrefix + fieldName;
    }
}
