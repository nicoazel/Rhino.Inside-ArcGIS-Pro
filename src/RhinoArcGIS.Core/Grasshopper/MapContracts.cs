using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;

namespace RhinoArcGIS.Core.Grasshopper
{
    /// <summary>A binding is scoped to one open project, map, layer URI and verified dataset.</summary>
    public sealed class MapLayerReference
    {
        public string Project { get; set; }
        public string MapUri { get; set; }
        public string LayerUri { get; set; }
        public string Source { get; set; }
        public string CrsDefinition { get; set; }
        public string Name { get; set; }
        public bool Matches(MapLayerReference other) => other != null &&
            Project == other.Project && MapUri == other.MapUri && LayerUri == other.LayerUri &&
            !string.IsNullOrEmpty(Source) && Source == other.Source && CrsDefinition == other.CrsDefinition;
        public override string ToString() => Name ?? LayerUri;
    }

    public sealed class MapDescriptor
    {
        public string Uri { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    public sealed class MapLayerDescriptor
    {
        public MapLayerReference Reference { get; set; }
        public string Kind { get; set; }
        public string Status { get; set; }
        public LayerSchema Schema { get; set; }
        public override string ToString() => Reference.Name + " (" + Kind + ")";
    }

    /// <summary>Geometry is in model coordinates at the component boundary and GIS coordinates in a plan.</summary>
    public sealed class FeatureDraft
    {
        public MapLayerReference Source { get; set; }
        public long? ObjectId { get; set; }
        public Guid? GlobalId { get; set; }
        public string Baseline { get; set; }
        public string ModelContext { get; set; }
        public string Key { get; set; }
        public NeutralGeometry Geometry { get; set; }
        public bool ReplaceGeometry { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Changes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Attributes.FieldType> FieldTypes { get; set; } = new Dictionary<string, Attributes.FieldType>(StringComparer.OrdinalIgnoreCase);
        public FeatureDraft Copy() => JsonConvert.DeserializeObject<FeatureDraft>(JsonConvert.SerializeObject(this));
        public override string ToString() => GlobalId?.ToString() ?? ObjectId?.ToString() ?? Key ?? "New feature";
    }

    public sealed class FeatureReadResult
    {
        public List<FeatureDraft> Features { get; set; } = new List<FeatureDraft>();
        public bool Truncated { get; set; }
        public LayerSchema Schema { get; set; }
    }

    public sealed class FeaturePatch
    {
        public long ObjectId { get; set; }
        public Guid? GlobalId { get; set; }
        public string Baseline { get; set; }
        public FeatureRecord Values { get; set; }
    }

    /// <summary>Owns a frozen serialization, so later upstream mutations cannot change a reviewed plan.</summary>
    public sealed class MapUpdatePlan
    {
        readonly string payload;
        readonly MapLayerReference layer;
        readonly string[] issues;
        public string SchemaVersion { get; }
        public MapLayerReference Layer => JsonConvert.DeserializeObject<MapLayerReference>(JsonConvert.SerializeObject(layer));
        public IReadOnlyList<string> Issues => Array.AsReadOnly(issues);
        public IReadOnlyList<FeaturePatch> Patches => JsonConvert.DeserializeObject<List<FeaturePatch>>(payload);
        public bool CanCommit => issues.Length == 0 && Patches.Count > 0;
        public MapUpdatePlan(MapLayerReference target, IEnumerable<FeaturePatch> patches, IEnumerable<string> errors, string schemaVersion = null)
        {
            SchemaVersion = schemaVersion;
            layer = JsonConvert.DeserializeObject<MapLayerReference>(JsonConvert.SerializeObject(target));
            payload = JsonConvert.SerializeObject(patches.ToList());
            issues = errors.ToArray();
        }
        public override string ToString() => $"{Patches.Count} updates; {Issues.Count} issues";
    }
}
