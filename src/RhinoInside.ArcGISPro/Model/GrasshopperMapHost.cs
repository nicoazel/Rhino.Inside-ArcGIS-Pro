using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using RhinoArcGIS.ArcGIS;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Grasshopper;
using RhinoArcGIS.Core.Spatial;

namespace RhinoInside.ArcGISPro
{
    internal sealed class GrasshopperMapHost : IMapDesignHost
    {
        readonly Dispatcher ui;
        readonly MapDesignPreview preview = new();
        public GrasshopperMapHost(Dispatcher dispatcher) { ui = dispatcher; }
        T OnUi<T>(Func<T> action) => ui.CheckAccess() ? action() : ui.Invoke(action);
        ArcGISAdapter Adapter(MapLayerReference layer) => new() { BoundLayer = layer, CrsLayer = layer.Name };

        [MethodImpl(MethodImplOptions.NoInlining)]
        (ICoordinateMap Map, string Stamp) Coordinates(MapLayerReference layer = null, string mapUri = null)
        {
            SyncCoordinator.EnsureEarthAnchor();
            return OnUi(() => {
                var adapter = layer == null ? new ArcGISAdapter { CrsMapUri = mapUri } : Adapter(layer);
                var map = GeoReferenceFactory.Create(new RhinoArcGIS.Rhino.RhinoAdapter(), adapter, null);
                if (adapter.LastGeodeticMap?.DatumTransformation?.StartsWith("none available", StringComparison.Ordinal) == true)
                    throw new InvalidOperationException("A datum transformation is unavailable for this map.");
                return (map, global::Rhino.RhinoDoc.ActiveDoc.RuntimeSerialNumber + ":" + RhinoArcGIS.Core.Change.ModelStamp.Fingerprint(map));
            });
        }
        public IReadOnlyList<MapDescriptor> Maps() => ArcGISAdapter.DesignMaps();
        public IReadOnlyList<MapLayerDescriptor> Layers(string uri) => ArcGISAdapter.DesignLayers(uri);
        public FeatureReadResult Read(MapLayerReference layer, bool selected, int limit) => SyncCoordinator.RunMapOperation(() =>
        {
            var context = Coordinates(layer);
            var result = Adapter(layer).ReadDesignFeatures(selected, limit);
            foreach (var f in result.Features)
            { f.Geometry = NeutralGeometryTransform.GisToRhino(f.Geometry, context.Map); f.ModelContext = context.Stamp; }
            return result;
        });
        List<FeatureDraft> ToGis(MapLayerReference layer, IReadOnlyList<FeatureDraft> features)
        {
            var context = Coordinates(layer);
            return features.Select(f => {
                if (f.Source != null && f.ModelContext != context.Stamp)
                    throw new InvalidOperationException("Rhino document, units or earth anchor changed. Read features again.");
                var copy = f.Copy();
                copy.Geometry = NeutralGeometryTransform.RhinoToGis(copy.Geometry, context.Map);
                return copy;
            }).ToList();
        }
        public MapUpdatePlan Plan(MapLayerReference layer, IReadOnlyList<FeatureDraft> features) =>
            SyncCoordinator.RunMapOperation(() => Adapter(layer).PlanDesignUpdates(ToGis(layer, features)));
        public string Commit(MapUpdatePlan plan) => SyncCoordinator.RunMapOperation(() =>
        {
                return Adapter(plan.Layer).CommitDesignUpdates(plan);
        });
        public MapLayerReference Create(string mapUri, string name, string geodatabase, GeometryTarget target, IReadOnlyList<FieldDefinition> fields)
            => SyncCoordinator.RunMapOperation(() =>
            {
                if (global::ArcGIS.Desktop.Core.Project.Current.HasEdits)
                    throw new InvalidOperationException("Save or discard ArcGIS edits before creating a feature class.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    if (field == null || !System.Text.RegularExpressions.Regex.IsMatch(field.Name ?? "", "^[A-Za-z][A-Za-z0-9_]{0,63}$") ||
                        !names.Add(field.Name) || new[] { "OBJECTID", "FID", "GlobalID", "Shape", "Shape_Length", "Shape_Area" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Use unique field names of 1–64 letters, digits or underscores; system fields are reserved.");
                    if (field.Type == RhinoArcGIS.Core.Attributes.FieldType.Text && (field.Length < 1 || field.Length > 4000))
                        throw new InvalidOperationException("Text field length must be 1–4,000.");
                }
                var path = string.IsNullOrWhiteSpace(geodatabase) ? global::ArcGIS.Desktop.Core.Project.Current.DefaultGeodatabasePath : geodatabase;
                var actual = new ArcGISAdapter().CreateFeatureClass(path, name, target, fields, mapUri, absoluteElevation: true);
                return Layers(mapUri).Single(l => l.Reference.Name == actual).Reference;
            });
        public IReadOnlyList<FeatureDraft> Append(MapLayerReference layer, IReadOnlyList<FeatureDraft> features)
            => SyncCoordinator.RunMapOperation(() =>
            {
                var result = Adapter(layer).AppendDesignFeatures(ToGis(layer, features));
                var context = Coordinates(layer);
                foreach (var f in result)
                { f.Geometry = NeutralGeometryTransform.GisToRhino(f.Geometry, context.Map); f.ModelContext = context.Stamp; }
                return result;
            });
        public void Preview(Guid owner, string mapUri, IReadOnlyList<NeutralGeometry> geometry, int argb, double width)
        {
            var context = Coordinates(mapUri: mapUri);
            preview.Show(owner, mapUri, geometry.Select(g => NeutralGeometryTransform.RhinoToGis(g, context.Map)).ToList(), argb, width);
        }
        public void ClearPreview(Guid owner) => preview.Clear(owner);
        public void CloseProject() { preview.ClearAll(); ArcGISAdapter.ResetMapDesignSession(); }
    }
}
