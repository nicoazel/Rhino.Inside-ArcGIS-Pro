using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;

namespace RhinoArcGIS.Core.Grasshopper
{
    public static class FeaturePlanning
    {
        public static string SchemaVersion(LayerSchema schema) => JsonConvert.SerializeObject(schema);
        public static NeutralGeometry Combine(IReadOnlyList<NeutralGeometry> pieces)
        {
            if (pieces.Count == 0 || pieces.Any(g => g == null) || pieces.Any(g => g.Kind != pieces[0].Kind))
                throw new ArgumentException("A feature requires one or more pieces of the same geometry kind.");
            var result = new NeutralGeometry { Kind = pieces[0].Kind };
            foreach (var piece in pieces)
                switch (piece.Kind)
                {
                    case NeutralGeometryKind.Point: result.Points.AddRange(piece.Points); break;
                    case NeutralGeometryKind.Polyline:
                        result.Parts.AddRange(piece.Parts.Count > 0 ? piece.Parts : new List<List<Spatial.Xyz>> { piece.Points }); break;
                    case NeutralGeometryKind.Polygon: result.Rings.AddRange(piece.Rings); break;
                    case NeutralGeometryKind.Multipatch:
                        if (piece.Mesh == null) throw new ArgumentException("Mesh payload is missing.");
                        result.Mesh ??= new NeutralMesh { IsClosed = true };
                        int offset = result.Mesh.Vertices.Count;
                        result.Mesh.Vertices.AddRange(piece.Mesh.Vertices);
                        result.Mesh.Faces.AddRange(piece.Mesh.Faces.Select(f => f.Select(i => i + offset).ToArray()));
                        result.Mesh.IsClosed &= piece.Mesh.IsClosed;
                        break;
                }
            return JsonConvert.DeserializeObject<NeutralGeometry>(JsonConvert.SerializeObject(result));
        }
        // Full payload, including nulls and mesh faces. A topology edit must invalidate a baseline.
        public static string Version(FeatureRecord record)
        {
            var content = JsonConvert.SerializeObject(new {
                record.Identity.ArcGisObjectId, record.Identity.ArcGisGlobalId, record.Geometry,
                Attributes = record.Attributes.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
            });
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(content)));
        }

        public static MapUpdatePlan Plan(MapLayerReference layer, LayerSchema schema,
            IEnumerable<FeatureDraft> drafts, IReadOnlyList<FeatureRecord> current)
        {
            var patches = new List<FeaturePatch>();
            var errors = new List<string>();
            var seen = new HashSet<long>();
            if (schema == null || !schema.Editable || schema.Source != layer.Source)
                errors.Add("Target is unavailable, read-only, or its dataset changed.");
            if (schema == null) return new MapUpdatePlan(layer, patches, errors);
            foreach (var draft in drafts)
            {
                if (draft.Source == null || !layer.Matches(draft.Source))
                { errors.Add("Feature belongs to a different layer or project. Use Append for new output."); continue; }
                // A GlobalID never falls back to a possibly renumbered ObjectID.
                var matches = current.Where(f => draft.GlobalId.HasValue
                    ? f.Identity.ArcGisGlobalId == draft.GlobalId
                    : f.Identity.ArcGisObjectId == draft.ObjectId).ToList();
                if (matches.Count != 1 || !matches[0].Identity.ArcGisObjectId.HasValue)
                { errors.Add($"{draft}: target feature missing or ambiguous."); continue; }
                var live = matches[0];
                long oid = live.Identity.ArcGisObjectId.Value;
                if (!seen.Add(oid)) { errors.Add($"{draft}: duplicate update target."); continue; }
                if (draft.Baseline == null || draft.Baseline != Version(live))
                { errors.Add($"{draft}: ArcGIS changed since read. Refresh and review again."); continue; }
                var attrs = new Dictionary<string, string>(draft.Changes, StringComparer.OrdinalIgnoreCase);
                ValidateFields(schema, attrs, errors);
                if (draft.ReplaceGeometry) ValidateGeometry(draft.Geometry, schema.GeometryType, errors);
                if (!draft.ReplaceGeometry && attrs.Count == 0) continue;
                patches.Add(new FeaturePatch {
                    ObjectId = oid, GlobalId = live.Identity.ArcGisGlobalId, Baseline = draft.Baseline,
                    Values = new FeatureRecord {
                        Identity = new SyncIdentity { ArcGisObjectId = oid, ArcGisGlobalId = live.Identity.ArcGisGlobalId },
                        Geometry = draft.ReplaceGeometry ? draft.Geometry : null, Attributes = attrs
                    }
                });
            }
            return new MapUpdatePlan(layer, patches, errors, SchemaVersion(schema));
        }

        public static void ValidateFields(LayerSchema schema, IReadOnlyDictionary<string, string> fields, IList<string> errors)
        {
            foreach (var pair in fields)
            {
                var field = schema?.FindField(pair.Key);
                if (field == null || !field.Editable || pair.Key.StartsWith("gis.", StringComparison.OrdinalIgnoreCase))
                { errors.Add($"Field '{pair.Key}' is unknown or protected."); continue; }
                if (pair.Value == null && !field.Nullable)
                    errors.Add($"Field '{pair.Key}' cannot be null.");
                else if (field.Type == FieldType.Text && field.Length > 0 && pair.Value?.Length > field.Length)
                    errors.Add($"Field '{pair.Key}' exceeds {field.Length} characters.");
                else if (!FieldValueValidator.TryValidate(pair.Value, field.Type, field.Domain, out var error))
                    errors.Add($"Field '{pair.Key}': {error}");
                else if (field.Type == FieldType.Double && pair.Value != null &&
                    double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    (double.IsNaN(number) || double.IsInfinity(number)))
                    errors.Add($"Field '{pair.Key}' must be finite.");
            }
        }

        public static GeometryTarget Target(NeutralGeometry geometry) => geometry == null ? GeometryTarget.Unsupported :
            geometry.Kind == NeutralGeometryKind.Point ? GeometryTarget.PointZ :
            geometry.Kind == NeutralGeometryKind.Polyline ? GeometryTarget.PolylineZ :
            geometry.Kind == NeutralGeometryKind.Polygon ? GeometryTarget.PolygonZ : GeometryTarget.Multipatch;

        public static void ValidateGeometry(NeutralGeometry geometry, GeometryTarget target, IList<string> errors)
        {
            if (geometry == null || geometry.IsEmpty() || Target(geometry) != target)
            { errors.Add("Geometry is empty or incompatible with the layer shape type."); return; }
            if (geometry.AllPoints().Any(p => double.IsNaN(p.X) || double.IsInfinity(p.X) ||
                double.IsNaN(p.Y) || double.IsInfinity(p.Y) || double.IsNaN(p.Z) || double.IsInfinity(p.Z)))
                errors.Add("Geometry contains non-finite coordinates.");
            if (geometry.Kind == NeutralGeometryKind.Point && geometry.Points.Count != 1)
                errors.Add("Point output requires exactly one point per feature.");
            if (geometry.Kind == NeutralGeometryKind.Polyline && (geometry.Parts.Count > 0 ? geometry.Parts :
                new List<List<Spatial.Xyz>> { geometry.Points }).Any(p => p == null || p.Count < 2))
                errors.Add("Each line part requires at least two vertices.");
            if (geometry.Kind == NeutralGeometryKind.Polygon && (geometry.Rings.Count == 0 || geometry.Rings.Any(r => r == null || r.Count < 3)))
                errors.Add("Each polygon ring requires at least three vertices.");
            if (geometry.Kind == NeutralGeometryKind.Multipatch && (geometry.Mesh == null || geometry.Mesh.Faces.Count == 0 ||
                geometry.Mesh.Faces.Any(f => f == null || f.Length < 3 || f.Length > 4 || f.Any(i => i < 0 || i >= geometry.Mesh.Vertices.Count))))
                errors.Add("Mesh faces or vertex indices are invalid.");
        }

        public static object TypedValue(string value, FieldType type)
        {
            if (value == null) return null;
            switch (type)
            {
                case FieldType.Integer: return long.Parse(value, CultureInfo.InvariantCulture);
                case FieldType.Double: return double.Parse(value, CultureInfo.InvariantCulture);
                case FieldType.Boolean: return value == "1" || bool.Parse(value == "0" ? "false" : value);
                case FieldType.Date: return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                case FieldType.Guid: return Guid.Parse(value);
                default: return value;
            }
        }
    }
}
