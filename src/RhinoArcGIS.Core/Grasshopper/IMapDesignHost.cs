using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;

namespace RhinoArcGIS.Core.Grasshopper
{
    /// <summary>Called by worker tasks; implementations marshal SDK work to its owning thread.</summary>
    public interface IMapDesignHost
    {
        IReadOnlyList<MapDescriptor> Maps();
        IReadOnlyList<MapLayerDescriptor> Layers(string mapUri);
        FeatureReadResult Read(MapLayerReference layer, bool selected, int limit);
        MapUpdatePlan Plan(MapLayerReference layer, IReadOnlyList<FeatureDraft> features);
        string Commit(MapUpdatePlan plan);
        MapLayerReference Create(string mapUri, string name, string geodatabase, GeometryTarget target,
            IReadOnlyList<FieldDefinition> fields);
        IReadOnlyList<FeatureDraft> Append(MapLayerReference layer, IReadOnlyList<FeatureDraft> features);
        void Preview(Guid owner, string mapUri, IReadOnlyList<NeutralGeometry> geometry, int argb, double width);
        void ClearPreview(Guid owner);
    }

    public static class MapDesignConnection
    {
        public static IMapDesignHost Host { get; set; }
        public static IMapDesignHost RequireHost() => Host ??
            throw new InvalidOperationException("Open Grasshopper inside Rhino.Inside–ArcGIS Pro to use these components.");
    }
}
