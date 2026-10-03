using System.Collections.Generic;

namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>
    /// The ArcGIS side of the interop (spec 06 §4). Implemented by <c>RhinoArcGIS.ArcGIS</c> against
    /// the ArcGIS Pro SDK. The core depends only on this abstraction.
    /// </summary>
    public interface IArcGISAdapter
    {
        /// <summary>Feature layer names available in the active project/map.</summary>
        IReadOnlyList<string> GetLayerNames();

        /// <summary>Read the schema (fields, domains, geometry type) of a layer.</summary>
        LayerSchema GetSchema(string layerName);

        /// <summary>Read all features from a layer as neutral records (coordinates in GIS space).</summary>
        IReadOnlyList<FeatureRecord> ReadFeatures(string layerName);

        /// <summary>Read only the currently selected features of a layer.</summary>
        IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName);

        /// <summary>Create new features in a layer within an edit operation; returns assigned ObjectIDs aligned to input order.</summary>
        IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features);

        /// <summary>Update existing features (matched by ArcGIS ObjectID on the record identity) within an edit operation.</summary>
        void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features);

        /// <summary>Refresh/rebuild scene caches after edits (spec 06 §4).</summary>
        void RefreshScene();
    }

    /// <summary>
    /// Reads chosen features by ObjectID. Optional: without it, a caller that needs a few features
    /// back -- the ones a push just wrote -- reads the whole layer and picks them out.
    /// </summary>
    public interface IFeatureLookup
    {
        IReadOnlyList<FeatureRecord> ReadFeatures(string layerName, IReadOnlyCollection<long> objectIds);
    }
}
