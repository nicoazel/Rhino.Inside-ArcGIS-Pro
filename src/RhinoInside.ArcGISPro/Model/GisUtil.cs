using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Reads features out of the active map and reduces them to <see cref="FeatureRecord"/>.
    /// </summary>
    /// <remarks>
    /// Geometry is projected to WGS84 here, so what leaves this class is geographic
    /// (latitude, longitude, elevation) rather than map coordinates. That is deliberate: it is the
    /// coordinate space Rhino's EarthAnchorPoint transform consumes, so the Rhino half never has to
    /// know anything about the map's projection, and Rhino handles model units itself.
    ///
    /// Everything runs on the ArcGIS main CIM thread via QueuedTask, which is required for any
    /// access to layers, cursors and geometry. Nothing in this class references RhinoCommon.
    /// </remarks>
    internal static class GisUtil
    {
        /// <summary>
        /// Reads the current selection from every feature layer in the active map. When nothing is
        /// selected anywhere, falls back to reading all features so a pull still does something
        /// useful on a freshly added layer.
        /// </summary>
        internal static Task<List<FeatureRecord>> ReadSelectedFeaturesAsync(int maxFeatures = 5000)
        {
            return QueuedTask.Run(() => ReadFeatures(maxFeatures));
        }

        /// <summary>
        /// Centre of the active map view as a WGS84 location, for anchoring the Rhino document to
        /// wherever the user has navigated to in Pro.
        /// </summary>
        /// <summary>Selects one feature in a map layer and zooms the active map view to it.</summary>
        internal static Task SelectFeatureAsync(string layerName, long objectId)
        {
            return QueuedTask.Run(() =>
            {
                var map = RhinoArcGIS.ArcGIS.ActiveMap.Current;
                var layer = map?.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) return;
                map.SetSelection(null);
                layer.Select(new QueryFilter { ObjectIDs = new[] { objectId } }, SelectionCombinationMethod.New);
                MapView.Active?.ZoomToSelected(TimeSpan.FromMilliseconds(300));
            });
        }

        internal static Task<GeoPoint> GetMapCentreAsync()
        {
            return QueuedTask.Run(() => GetMapCentre());
        }

        static GeoPoint GetMapCentre()
        {
            var extent = MapView.Active?.Extent;
            if (extent == null) return null;

            var centre = extent.Center;
            if (centre == null) return null;

            var projection = GetProjection(centre.SpatialReference ?? extent.SpatialReference,
                                           new Dictionary<int, SourceProjection>());

            if (!(ToWgs84(centre, projection) is MapPoint wgs84)) return null;

            return new GeoPoint { Latitude = wgs84.Y, Longitude = wgs84.X };
        }

        static List<FeatureRecord> ReadFeatures(int maxFeatures)
        {
            var records = new List<FeatureRecord>();

            var map = MapView.Active?.Map;
            if (map == null) return records;

            var layers = map.GetLayersAsFlattenedList().OfType<FeatureLayer>().ToList();
            var anySelected = layers.Any(l => l.GetSelection().GetCount() > 0);

            // One projection per source spatial reference, reused across features.
            var projections = new Dictionary<int, SourceProjection>();

            foreach (var layer in layers)
            {
                if (records.Count >= maxFeatures) break;

                var colorArgb = GetLayerColorArgb(layer);

                QueryFilter filter = null;
                if (anySelected)
                {
                    var oids = layer.GetSelection().GetObjectIDs();
                    if (oids.Count == 0) continue;
                    filter = new QueryFilter { ObjectIDs = oids.ToList() };
                }

                using (var cursor = layer.Search(filter))
                {
                    while (cursor.MoveNext())
                    {
                        if (records.Count >= maxFeatures) break;
                        if (!(cursor.Current is Feature feature)) continue;

                        var record = ToRecord(feature, layer.Name, colorArgb, projections);
                        if (record != null) records.Add(record);
                    }
                }
            }

            return records;
        }

        static FeatureRecord ToRecord(Feature feature, string layerName, int colorArgb,
                                      Dictionary<int, SourceProjection> projections)
        {
            var shape = feature.GetShape();
            if (shape == null) return null;

            var projection = GetProjection(shape.SpatialReference, projections);
            shape = ToWgs84(shape, projection);
            if (shape == null) return null;

            var zScale = projection.ZToMetres;

            var record = new FeatureRecord
            {
                LayerName = layerName,
                ColorArgb = colorArgb
            };

            switch (shape)
            {
                case MapPoint point:
                    record.Kind = FeatureGeometryKind.Point;
                    record.Parts.Add(new[] { point.Y, point.X, ZOf(point, shape.HasZ, zScale) });
                    break;

                case Multipoint multipoint:
                    // Each vertex becomes its own part so it can be added as an individual point.
                    record.Kind = FeatureGeometryKind.Point;
                    foreach (var p in multipoint.Points)
                        record.Parts.Add(new[] { p.Y, p.X, ZOf(p, shape.HasZ, zScale) });
                    break;

                case Polyline polyline:
                    record.Kind = FeatureGeometryKind.Polyline;
                    AddParts(record, polyline, zScale);
                    break;

                case Polygon polygon:
                    record.Kind = FeatureGeometryKind.Polygon;
                    AddParts(record, polygon, zScale);
                    break;

                default:
                    // Multipatch and anything else is not handled yet.
                    return null;
            }

            if (record.Parts.Count == 0) return null;

            ReadAttributes(feature, record);
            return record;
        }

        /// <summary>
        /// How to get one source spatial reference into WGS84, worked out once and reused.
        /// </summary>
        sealed class SourceProjection
        {
            internal ProjectionTransformation Transformation;

            /// <summary>
            /// Metres per source Z unit. Only relevant when the source has no vertical coordinate
            /// system: in that case projecting passes Z through untouched, so data in a foot-based
            /// spatial reference would otherwise arrive at Rhino as if those numbers were metres.
            /// </summary>
            internal double ZToMetres = 1.0;

            internal bool PassThrough;
        }

        static SourceProjection GetProjection(SpatialReference sourceSR,
                                              Dictionary<int, SourceProjection> cache)
        {
            if (sourceSR == null) return new SourceProjection { PassThrough = true };

            if (cache.TryGetValue(sourceSR.Wkid, out var cached)) return cached;

            var projection = new SourceProjection();

            if (sourceSR.Wkid == SpatialReferences.WGS84.Wkid)
            {
                projection.PassThrough = true;
            }
            else
            {
                try
                {
                    // A source carrying a vertical coordinate system gets its heights converted as
                    // part of the projection; otherwise the environment's preferred datum
                    // transformation is used, which is what applies the NAD83 -> WGS84 shift a bare
                    // project would skip.
                    // Heights too when the source carries a vertical system; otherwise ArcGIS's own
                    // datum choice (environment first, then the best for the area), the same policy
                    // the sync's local frame uses -- see RhinoArcGIS.ArcGIS.DatumTransforms.
                    if (sourceSR.HasVcs)
                        projection.Transformation = ProjectionTransformation.CreateWithVertical(sourceSR, SpatialReferences.WGS84, null);
                    else
                    {
                        // No single location here (features can be anywhere), so ArcGIS chooses for
                        // the whole extent of the systems when the environment has no preference.
                        projection.Transformation = RhinoArcGIS.ArcGIS.DatumTransforms
                            .Between(sourceSR, SpatialReferences.WGS84, double.NaN, double.NaN).Transformation;
                    }
                }
                catch
                {
                    projection.Transformation = null;
                }

                if (!sourceSR.HasVcs)
                {
                    try
                    {
                        var factor = sourceSR.ZUnit?.ConversionFactor ?? 1.0;
                        if (factor > 0.0) projection.ZToMetres = factor;
                    }
                    catch
                    {
                        projection.ZToMetres = 1.0;
                    }
                }
            }

            cache[sourceSR.Wkid] = projection;
            return projection;
        }

        static Geometry ToWgs84(Geometry shape, SourceProjection projection)
        {
            if (projection.PassThrough) return shape;

            try
            {
                return projection.Transformation != null
                    ? GeometryEngine.Instance.ProjectEx(shape, projection.Transformation)
                    : GeometryEngine.Instance.Project(shape, SpatialReferences.WGS84);
            }
            catch
            {
                try { return GeometryEngine.Instance.Project(shape, SpatialReferences.WGS84); }
                catch { return null; }
            }
        }

        /// <summary>
        /// Copies each part of a multipart geometry across separately. Flattening them into one run
        /// of points is what makes multipart parcels and streets come through as a single shape
        /// zig-zagging between rings.
        /// </summary>
        static void AddParts(FeatureRecord record, Multipart multipart, double zScale)
        {
            var hasZ = multipart.HasZ;

            foreach (var part in multipart.Parts)
            {
                if (part.Count == 0) continue;

                // A part is a run of segments; its points are each segment's start, plus the final
                // segment's end to close out the run.
                var coords = new double[(part.Count + 1) * 3];
                var i = 0;
                foreach (var segment in part)
                {
                    var sp = segment.StartPoint;
                    coords[i++] = sp.Y;
                    coords[i++] = sp.X;
                    coords[i++] = ZOf(sp, hasZ, zScale);
                }

                var end = part[part.Count - 1].EndPoint;
                coords[i++] = end.Y;
                coords[i++] = end.X;
                coords[i] = ZOf(end, hasZ, zScale);

                record.Parts.Add(coords);
            }
        }

        /// <summary>Elevation in metres, which is what the Rhino earth transform expects.</summary>
        static double ZOf(MapPoint point, bool hasZ, double zToMetres) =>
            hasZ && !double.IsNaN(point.Z) ? point.Z * zToMetres : 0.0;

        static void ReadAttributes(Feature feature, FeatureRecord record)
        {
            var fields = feature.GetFields();
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field.FieldType == FieldType.Geometry) continue;

                try
                {
                    var value = feature.GetOriginalValue(i);
                    record.Attributes[field.Name] = value?.ToString() ?? string.Empty;
                }
                catch
                {
                    // Blobs and rasters cannot be read as text; skip rather than fail the feature.
                }
            }
        }

        /// <summary>
        /// Best-effort read of a layer's symbol colour, so Rhino layers come through looking like
        /// the map. Falls back to black.
        /// </summary>
        static int GetLayerColorArgb(FeatureLayer layer)
        {
            const int black = unchecked((int)0xFF000000);

            try
            {
                var renderer = layer.GetRenderer();
                if (renderer == null) return black;

                var json = Newtonsoft.Json.Linq.JObject.Parse(renderer.ToJson());
                var values = (Newtonsoft.Json.Linq.JArray)json["symbol"]?["symbol"]?["symbolLayers"]?[0]?["color"]?["values"];
                if (values == null || values.Count < 3) return black;

                var c = values.Select(v => (int)Math.Round((double)v)).ToList();
                return (255 << 24) | (Clamp(c[0]) << 16) | (Clamp(c[1]) << 8) | Clamp(c[2]);
            }
            catch
            {
                return black;
            }
        }

        static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
    }
}
