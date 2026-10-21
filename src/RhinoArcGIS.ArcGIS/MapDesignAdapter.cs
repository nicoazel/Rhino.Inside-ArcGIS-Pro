using System;
using System.Collections.Generic;
using System.Linq;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Grasshopper;

namespace RhinoArcGIS.ArcGIS
{
    public sealed partial class ArcGISAdapter
    {
        static string projectSession = Guid.NewGuid().ToString("N");
        public static void ResetMapDesignSession() => projectSession = Guid.NewGuid().ToString("N");
        public MapLayerReference BoundLayer { get; set; }
        public string CrsMapUri { get; set; }
        public static IReadOnlyList<MapDescriptor> DesignMaps() => QueuedTask.Run(() =>
            (IReadOnlyList<MapDescriptor>)Project.Current.GetItems<MapProjectItem>()
                .Select(item => item.GetMap()).Select(m => new MapDescriptor { Uri = m.URI, Name = m.Name }).ToList()).Result;

        internal static Map DesignMap(string uri) => Project.Current?.GetItems<MapProjectItem>()
            .Select(item => item.GetMap()).SingleOrDefault(m => m.URI == uri)
            ?? throw new InvalidOperationException("The chosen map is no longer available.");

        static MapLayerReference Reference(Map map, Layer layer)
        {
            string source = null, crs = null;
            if (layer is FeatureLayer feature)
                try { using var fc = feature.GetFeatureClass(); source = SourceOf(fc); crs = feature.GetSpatialReference()?.ToJson(); } catch { }
            return new MapLayerReference { Project = projectSession, MapUri = map.URI,
                LayerUri = layer.URI, Source = source, Name = layer.Name,
                CrsDefinition = crs };
        }

        FeatureLayer ResolveBoundLayer()
        {
            var map = DesignMap(BoundLayer.MapUri);
            var layer = map.GetLayersAsFlattenedList().SingleOrDefault(l => l.URI == BoundLayer.LayerUri) as FeatureLayer
                ?? throw new InvalidOperationException("The bound feature layer was removed.");
            if (!BoundLayer.Matches(Reference(map, layer)))
                throw new InvalidOperationException("The project or layer data source changed. Choose the layer again.");
            return layer;
        }

        public static IReadOnlyList<MapLayerDescriptor> DesignLayers(string mapUri) => QueuedTask.Run(() => {
            var map = DesignMap(mapUri);
            return (IReadOnlyList<MapLayerDescriptor>)map.GetLayersAsFlattenedList().Select(layer => {
                var reference = Reference(map, layer);
                var entry = new MapLayerDescriptor { Reference = reference, Kind = layer.GetType().Name };
                if (layer is FeatureLayer && reference.Source != null)
                {
                    try { entry.Schema = new ArcGISAdapter { BoundLayer = reference }.GetSchema(layer.Name); }
                    catch (Exception ex) { entry.Status = ex.Message; }
                }
                entry.Status ??= entry.Schema == null ? "Feature reads unavailable" :
                    entry.Schema.Editable ? "Read and edit" : "Read only";
                return entry;
            }).ToList();
        }).Result;

        public FeatureReadResult ReadDesignFeatures(bool selected, int limit)
        {
            if (limit < 1 || limit > 100000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be 1–100,000.");
            return QueuedTask.Run(() => {
                var layer = ResolveBoundLayer();
                var result = new FeatureReadResult { Schema = GetSchema(layer.Name) };
                var filter = new QueryFilter();
                if (selected)
                {
                    using var selection = layer.GetSelection();
                    filter.ObjectIDs = selection.GetObjectIDs();
                    if (filter.ObjectIDs.Count == 0) return result;
                }
                using var cursor = layer.Search(filter);
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    if (result.Features.Count == limit) { result.Truncated = true; break; }
                    var record = ReadFeature(feature);
                    result.Features.Add(new FeatureDraft { Source = BoundLayer, ObjectId = record.Identity.ArcGisObjectId,
                        GlobalId = record.Identity.ArcGisGlobalId, Baseline = FeaturePlanning.Version(record),
                        Geometry = record.Geometry, Attributes = record.Attributes,
                        FieldTypes = result.Schema.Fields.ToDictionary(f => f.Name, f => f.Type, StringComparer.OrdinalIgnoreCase) });
                }
                return result;
            }).Result;
        }

        public MapUpdatePlan PlanDesignUpdates(IReadOnlyList<FeatureDraft> drafts) => QueuedTask.Run(() => {
            var layer = ResolveBoundLayer();
            return FeaturePlanning.Plan(BoundLayer, GetSchema(layer.Name), drafts, ReadRelevant(layer, drafts));
        }).Result;

        static IReadOnlyList<FeatureRecord> ReadRelevant(FeatureLayer layer, IReadOnlyList<FeatureDraft> drafts)
        {
            // GlobalIDs remain authoritative. The first wave limits planning reads to 100k rows.
            var ids = new HashSet<long>(drafts.Where(d => d.ObjectId.HasValue).Select(d => d.ObjectId.Value));
            var gids = new HashSet<Guid>(drafts.Where(d => d.GlobalId.HasValue).Select(d => d.GlobalId.Value));
            var records = new List<FeatureRecord>();
            using var cursor = layer.Search();
            int count = 0;
            while (cursor.MoveNext())
            {
                using var f = (Feature)cursor.Current;
                if (++count > 100000) throw new InvalidOperationException("Update planning is limited to layers with 100,000 rows.");
                var record = ReadFeature(f);
                if (ids.Contains(f.GetObjectID()) || (record.Identity.ArcGisGlobalId.HasValue && gids.Contains(record.Identity.ArcGisGlobalId.Value)))
                    records.Add(record);
            }
            return records;
        }

        public string CommitDesignUpdates(MapUpdatePlan plan) => QueuedTask.Run(() => {
            if (!BoundLayer.Matches(plan.Layer) || !plan.CanCommit) throw new InvalidOperationException("No valid reviewed updates.");
            var layer = ResolveBoundLayer();
            var schema = GetSchema(layer.Name);
            if (plan.SchemaVersion != FeaturePlanning.SchemaVersion(schema))
                throw new InvalidOperationException("Layer schema changed since review. Prepare a new plan.");
            var patches = plan.Patches;
            var drafts = patches.Select(p => new FeatureDraft { Source = BoundLayer, ObjectId = p.ObjectId,
                GlobalId = p.GlobalId, Baseline = p.Baseline, Geometry = p.Values.Geometry,
                ReplaceGeometry = p.Values.Geometry != null, Changes = p.Values.Attributes }).ToList();
            var verified = FeaturePlanning.Plan(BoundLayer, schema, drafts, ReadRelevant(layer, drafts));
            if (!verified.CanCommit) throw new InvalidOperationException(string.Join("\n", verified.Issues));
            var op = new global::ArcGIS.Desktop.Editing.EditOperation { Name = "Grasshopper update features" };
            foreach (var patch in verified.Patches)
                op.Modify(layer, patch.ObjectId, BuildAttributeDictionary(patch.Values, layer.GetSpatialReference(), FieldTypeMap(layer), ShapeTypeOf(layer)));
            if (!op.Execute()) throw new InvalidOperationException(op.ErrorMessage);
            return $"Updated {patches.Count} features. ArcGIS edits remain available for Undo / Save Edits.";
        }).Result;

        public IReadOnlyList<FeatureDraft> AppendDesignFeatures(IReadOnlyList<FeatureDraft> drafts) => QueuedTask.Run(() => {
            var layer = ResolveBoundLayer();
            var schema = GetSchema(layer.Name);
            var errors = new List<string>();
            if (!schema.Editable) errors.Add("Layer is read-only.");
            var records = new List<FeatureRecord>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var draft in drafts)
            {
                if (string.IsNullOrWhiteSpace(draft.Key) || !keys.Add(draft.Key)) errors.Add("Each appended feature needs a unique explicit key.");
                FeaturePlanning.ValidateGeometry(draft.Geometry, schema.GeometryType, errors);
                FeaturePlanning.ValidateFields(schema, draft.Changes, errors);
                records.Add(new FeatureRecord { Geometry = draft.Geometry, Attributes = draft.Changes });
            }
            if (records.Count == 0 || records.Count > 10000) errors.Add("Append requires 1–10,000 features.");
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors.Distinct()));
            var ids = CreateFeatures(layer.Name, records);
            if (ids.Count != drafts.Count) throw new InvalidOperationException("ArcGIS did not return all created identities. Inspect edits before retrying.");
            var readback = ReadFeatures(layer.Name, ids).ToDictionary(r => r.Identity.ArcGisObjectId.Value);
            return (IReadOnlyList<FeatureDraft>)ids.Select((id, i) => {
                var record = readback[id];
                return new FeatureDraft { Source = BoundLayer, Key = drafts[i].Key, ObjectId = id,
                    GlobalId = record.Identity.ArcGisGlobalId, Baseline = FeaturePlanning.Version(record),
                    Geometry = record.Geometry, Attributes = record.Attributes,
                    FieldTypes = schema.Fields.ToDictionary(f => f.Name, f => f.Type, StringComparer.OrdinalIgnoreCase) };
            }).ToList();
        }).Result;
    }
}
