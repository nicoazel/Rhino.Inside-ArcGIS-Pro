using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.DDL;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Identity;
using DdlFieldDescription = ArcGIS.Core.Data.DDL.FieldDescription;
using CoreFieldDefinition = RhinoArcGIS.Core.Adapters.FieldDefinition;
using CoreFieldType = RhinoArcGIS.Core.Attributes.FieldType;
using CoreGeometryTarget = RhinoArcGIS.Core.Geometry.GeometryTarget;
using CoreXyz = RhinoArcGIS.Core.Spatial.Xyz;
using CoreUnit = RhinoArcGIS.Core.Spatial.UnitSystem;

namespace RhinoArcGIS.ArcGIS
{
    /// <summary>
    /// ArcGIS adapter (spec 06 §4) over the ArcGIS Pro SDK. All geodatabase access runs on the
    /// MCT via <see cref="QueuedTask"/>. Read, write, schema creation, and cleanup paths are all
    /// implemented here and exercised by the opt-in live ArcGIS Pro harness.
    /// </summary>
    public sealed class ArcGISAdapter : IArcGISAdapter, IFeatureLookup, ICrsProjector, ICrsUnitScale, ILocalFrameProjector, IGeodeticMapProvider
    {
        /// <summary>
        /// The layer whose coordinate system the georeference is built in. Feature geometry is
        /// read and written in each layer's own spatial reference, so the Rhino anchor must be
        /// projected into that same one -- not the active view's, which for a scene or another
        /// map can be a different CRS and would shift every object. Null uses the active map.
        /// </summary>
        public string CrsLayer { get; set; }

        /// <summary>Durable backing data identity captured from the saved document link for this run.</summary>
        public string ExpectedSource { get; set; }

        /// <summary>The frame this adapter last measured, for reporting which datum transformation was used.</summary>
        public LocalFrame LastFrame { get; private set; }

        /// <summary>The per-vertex map this adapter last built, for reporting.</summary>
        public GeodeticCoordinateMap LastGeodeticMap { get; private set; }

        /// <summary>
        /// Per-vertex placement through ArcGIS's geodesy for the synced layer's CRS; see
        /// <see cref="GeodeticCoordinateMap"/>.
        /// </summary>
        public RhinoArcGIS.Core.Spatial.ICoordinateMap CreateGeodeticMap(RhinoArcGIS.Core.Spatial.EarthAnchor anchor)
        {
            if (anchor == null || !anchor.IsValid) return null;
            var sr = QueuedTask.Run(() => GeoreferenceSpatialReference()).Result;
            if (sr == null) return null;
            LastGeodeticMap = new GeodeticCoordinateMap(anchor, sr);
            return LastGeodeticMap;
        }

        /// <summary>Feature layers of the active map, then of the other maps shown this session.</summary>
        public IReadOnlyList<string> GetLayerNames()
        {
            return QueuedTask.Run(() =>
                (IReadOnlyList<string>)LayersInCandidateMaps().Select(l => l.Name).ToList()).Result;
        }

        /// <summary>
        /// Feature layers by distinct name: every layer of the active map, then layers of other
        /// shown maps and scenes whose names the active map does not already have.
        /// </summary>
        private static List<FeatureLayer> LayersInCandidateMaps()
        {
            var layers = new List<FeatureLayer>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in AllLayersInCandidateMaps())
                if (names.Add(layer.Name)) layers.Add(layer);
            return layers;
        }

        private static List<FeatureLayer> AllLayersInCandidateMaps()
        {
            var layers = new List<FeatureLayer>();
            foreach (var map in ActiveMap.Candidates)
            {
                try { layers.AddRange(map.GetLayersAsFlattenedList().OfType<FeatureLayer>()); }
                catch { continue; } // a map removed from the project since it was shown
            }
            return layers;
        }

        /// <summary>
        /// Every feature layer in the map with the data source behind it (workspace path and
        /// feature class; see <see cref="SourceOf"/>), in map order. A layer whose source cannot
        /// be read -- a broken data link -- is listed with a null source.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> GetLayerSources()
        {
            return QueuedTask.Run(() =>
            {
                var result = new List<KeyValuePair<string, string>>();
                foreach (var layer in AllLayersInCandidateMaps())
                {
                    string source = null;
                    try
                    {
                        using (var fc = layer.GetFeatureClass())
                            source = fc == null ? null : SourceOf(fc);
                    }
                    catch { }
                    result.Add(new KeyValuePair<string, string>(layer.Name, source));
                }
                return (IReadOnlyList<KeyValuePair<string, string>>)result;
            }).Result;
        }

        /// <summary>
        /// Creates a Z-aware feature class in a file geodatabase, adds it to the active map, and
        /// returns the actual (uniquified) layer name. The schema operation is cleaned up if adding
        /// the map layer fails, so the command does not leave an invisible partial result behind.
        /// </summary>
        public string CreateFeatureClass(string geodatabasePath, string requestedName,
                                         CoreGeometryTarget target,
                                         IReadOnlyList<CoreFieldDefinition> fields)
        {
            if (string.IsNullOrWhiteSpace(geodatabasePath))
                throw new ArgumentException("A file geodatabase path is required.", nameof(geodatabasePath));
            if (string.IsNullOrWhiteSpace(requestedName))
                throw new ArgumentException("A feature class name is required.", nameof(requestedName));

            return QueuedTask.Run(() =>
            {
                var map = ActiveMap.Current
                    ?? throw new InvalidOperationException("Open a map or scene before creating an ArcGIS layer.");
                if (!Directory.Exists(geodatabasePath))
                    throw new DirectoryNotFoundException($"The project's default geodatabase does not exist: {geodatabasePath}");

                var geometryType = ToArcGisGeometryType(target);
                using var geodatabase = new Geodatabase(
                    new FileGeodatabaseConnectionPath(new Uri(geodatabasePath)));

                var usedNames = new HashSet<string>(
                    map.GetLayersAsFlattenedList().Select(layer => layer.Name),
                    StringComparer.OrdinalIgnoreCase);
                var definitions = geodatabase.GetDefinitions<FeatureClassDefinition>();
                try
                {
                    foreach (var definition in definitions) usedNames.Add(definition.GetName());
                }
                finally
                {
                    foreach (var definition in definitions) definition.Dispose();
                }

                var name = UniqueName(requestedName, usedNames);
                var fieldDescriptions = new List<DdlFieldDescription>
                {
                    DdlFieldDescription.CreateObjectIDField(),
                    DdlFieldDescription.CreateGlobalIDField()
                };
                foreach (var field in fields ?? Array.Empty<CoreFieldDefinition>())
                {
                    if (field == null || string.IsNullOrWhiteSpace(field.Name)) continue;
                    var description = new DdlFieldDescription(field.Name, ToArcGisFieldType(field.Type))
                    {
                        IsNullable = field.Nullable
                    };
                    if (field.Type == CoreFieldType.Text)
                        description.Length = Math.Max(1, Math.Min(4000, field.Length > 0 ? field.Length : 255));
                    fieldDescriptions.Add(description);
                }

                var shape = new ShapeDescription(geometryType, map.SpatialReference) { HasZ = true };
                var featureClass = new FeatureClassDescription(name, fieldDescriptions, shape);
                var schema = new SchemaBuilder(geodatabase);
                schema.Create(featureClass);
                if (!schema.Build())
                    throw new InvalidOperationException("Feature class creation failed: " +
                                                        string.Join("; ", schema.ErrorMessages));

                try
                {
                    var layer = LayerFactory.Instance.CreateLayer(
                        new Uri(Path.Combine(geodatabasePath, name)), map)
                        ?? throw new InvalidOperationException("ArcGIS created the feature class but could not add it to the active map.");
                    // Scene-ground placement is the intended default only for 3D design meshes.
                    // Point/line/polygon classes must retain their authored absolute Z behavior.
                    if (target == CoreGeometryTarget.Multipatch) SetOnGroundElevation(layer);
                    return layer.Name;
                }
                catch
                {
                    DeleteDefinition(geodatabase, name);
                    throw;
                }
            }).Result;
        }

        /// <summary>
        /// Removes and deletes a feature class created in the current project's default
        /// geodatabase. Refuses any other data source; this is used by the live test clean-up path.
        /// </summary>
        public bool DeleteCreatedFeatureClass(string layerName)
        {
            return QueuedTask.Run(() =>
            {
                var map = ActiveMap.Current;
                var layer = FindLayer(layerName);
                if (map == null) return false;

                var project = global::ArcGIS.Desktop.Core.Project.Current;
                var defaultPath = project?.DefaultGeodatabasePath;
                if (string.IsNullOrWhiteSpace(defaultPath)) return false;

                // SchemaBuilder cannot run while an edit session is pending. Check before removing
                // the map layer: a failed cleanup must not make a still-valid result disappear from
                // the map. Saving or discarding is deliberately the caller's choice because either
                // operation applies to every pending edit in the ArcGIS project.
                if (project.HasEdits)
                    throw new InvalidOperationException(
                        "Save or discard pending ArcGIS edits before deleting the created feature class.");

                var datasetName = layerName;
                var geodatabasePath = defaultPath;
                if (layer != null)
                {
                    using (var featureClass = layer.GetFeatureClass())
                    {
                        datasetName = featureClass.GetName();
                        var path = featureClass.GetPath()?.LocalPath;
                        geodatabasePath = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
                    }
                }

                if (string.IsNullOrWhiteSpace(geodatabasePath) || string.IsNullOrWhiteSpace(defaultPath) ||
                    !string.Equals(Path.GetFullPath(geodatabasePath).TrimEnd(Path.DirectorySeparatorChar),
                                   Path.GetFullPath(defaultPath).TrimEnd(Path.DirectorySeparatorChar),
                                   StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Refusing to delete '{layerName}': it is not in the project's default geodatabase.");

                using var geodatabase = new Geodatabase(
                    new FileGeodatabaseConnectionPath(new Uri(defaultPath)));
                var definitions = geodatabase.GetDefinitions<FeatureClassDefinition>();
                var exists = false;
                try
                {
                    exists = definitions.Any(definition =>
                        string.Equals(definition.GetName(), datasetName, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    foreach (var definition in definitions) definition.Dispose();
                }
                if (!exists) return false;

                if (layer != null) map.RemoveLayer(layer);
                try
                {
                    DeleteDefinition(geodatabase, datasetName);
                }
                catch
                {
                    // Restore the visible layer when schema deletion fails after it was removed.
                    if (layer != null)
                        LayerFactory.Instance.CreateLayer(new Uri(Path.Combine(defaultPath, datasetName)), map);
                    throw;
                }
                return true;
            }).Result;
        }

        public LayerSchema GetSchema(string layerName)
        {
            return QueuedTask.Run(() =>
            {
                FeatureLayer layer = FindLayer(layerName);
                if (layer == null) return null;

                using (FeatureClass fc = layer.GetFeatureClass())
                using (FeatureClassDefinition def = fc.GetDefinition())
                {
                    var schema = new LayerSchema
                    {
                        LayerName = layerName,
                        Source = SourceOf(fc),
                        Crs = DescribeSpatialReference(layer.GetSpatialReference()),
                        GeometryType = MapGeometryType(def.GetShapeType()),
                        ZEnabled = def.HasZ(),
                        Editable = layer.IsEditable
                    };

                    foreach (Field f in def.GetFields())
                    {
                        if (f.FieldType == FieldType.Geometry) continue;
                        if (f.FieldType == FieldType.GlobalID) schema.HasGlobalIds = true;

                        schema.Fields.Add(new FieldDefinition
                        {
                            Name = f.Name,
                            Type = MapFieldType(f.FieldType),
                            Length = f.Length,
                            Nullable = f.IsNullable,
                            Required = !f.IsNullable,
                            Editable = f.IsEditable,
                            Domain = DomainValues(f)
                        });
                    }
                    return schema;
                }
            }).Result;
        }

        private static string DescribeSpatialReference(SpatialReference spatialReference)
        {
            if (spatialReference == null) return null;
            return spatialReference.Wkid > 0
                ? "EPSG:" + spatialReference.Wkid.ToString(CultureInfo.InvariantCulture)
                : spatialReference.Name;
        }

        private static List<string> DomainValues(Field field)
        {
            using Domain domain = field?.GetDomain(null);
            if (!(domain is CodedValueDomain coded)) return new List<string>();

            return coded.GetCodedValuePairs().Keys
                .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
                .Where(value => value != null)
                .ToList();
        }

        public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName)
            => Read(layerName, useSelection: false);

        public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName)
            => Read(layerName, useSelection: true);

        public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName, IReadOnlyCollection<long> objectIds)
        {
            if (objectIds == null || objectIds.Count == 0) return new List<FeatureRecord>();
            return QueuedTask.Run(() =>
            {
                var records = new List<FeatureRecord>(objectIds.Count);
                FeatureLayer layer = FindLayer(layerName);
                if (layer == null) return (IReadOnlyList<FeatureRecord>)records;

                // In slices: an ObjectID list becomes an IN clause, which a data source caps.
                var ids = objectIds.ToList();
                for (int start = 0; start < ids.Count; start += 1000)
                {
                    var filter = new QueryFilter { ObjectIDs = ids.GetRange(start, Math.Min(1000, ids.Count - start)) };
                    using (RowCursor cursor = layer.Search(filter))
                        while (cursor.MoveNext())
                            using (Feature feature = (Feature)cursor.Current)
                                records.Add(ReadFeature(feature));
                }
                return (IReadOnlyList<FeatureRecord>)records;
            }).Result;
        }

        private IReadOnlyList<FeatureRecord> Read(string layerName, bool useSelection)
        {
            return QueuedTask.Run(() =>
            {
                var records = new List<FeatureRecord>();
                FeatureLayer layer = FindLayer(layerName);
                if (layer == null) return (IReadOnlyList<FeatureRecord>)records;

                QueryFilter filter = null;
                if (useSelection)
                {
                    var selection = layer.GetSelection();
                    var oids = selection.GetObjectIDs();
                    if (oids.Count == 0) return (IReadOnlyList<FeatureRecord>)records;
                    filter = new QueryFilter { ObjectIDs = oids };
                }

                using (RowCursor cursor = layer.Search(filter))
                {
                    while (cursor.MoveNext())
                    {
                        using (Feature feature = (Feature)cursor.Current)
                        {
                            records.Add(ReadFeature(feature));
                        }
                    }
                }
                return (IReadOnlyList<FeatureRecord>)records;
            }).Result;
        }

        private static FeatureRecord ReadFeature(Feature feature)
        {
            var record = new FeatureRecord
            {
                Geometry = ArcGISGeometryReader.ToNeutral(feature.GetShape()),
                Identity = new SyncIdentity { ArcGisObjectId = feature.GetObjectID() }
            };

            IReadOnlyList<Field> fields = feature.GetFields();
            for (int i = 0; i < fields.Count; i++)
            {
                Field f = fields[i];
                if (f.FieldType == FieldType.Geometry) continue;

                object value = feature[i];
                if (f.FieldType == FieldType.GlobalID && value != null &&
                    Guid.TryParse(value.ToString(), out Guid gid))
                {
                    record.Identity.ArcGisGlobalId = gid;
                }

                if (value != null)
                    record.Attributes[f.Name] = Stringify(value);
            }

            return record;
        }

        private static string Stringify(object value)
        {
            switch (value)
            {
                case null: return null;
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case float fl: return fl.ToString("R", CultureInfo.InvariantCulture);
                case DateTime dt: return dt.ToString("o", CultureInfo.InvariantCulture);
                case IFormattable fmt: return fmt.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        /// <summary>
        /// Workspace path plus feature class name -- the identity of the data behind a layer, which
        /// the layer's display name is not.
        /// </summary>
        private static string SourceOf(FeatureClass fc)
        {
            try
            {
                string path = null;
                try { path = fc.GetPath()?.ToString(); } catch { }
                if (!string.IsNullOrEmpty(path)) return path;

                using (var ds = fc.GetDatastore())
                    return ds.GetConnectionString() + "|" + fc.GetName();
            }
            catch { return null; }
        }

        private FeatureLayer FindLayer(string layerName) =>
            ActiveMap.FindLayer(layerName, ExpectedSource, SourceOfLayer);

        private static string SourceOfLayer(FeatureLayer layer)
        {
            try
            {
                using (var fc = layer.GetFeatureClass())
                    return fc == null ? null : SourceOf(fc);
            }
            catch { return null; }
        }

        private void EnsureTargetBeforeWrite(string layerName, FeatureLayer preparedLayer)
        {
            var current = FindLayer(layerName);
            if (current == null ||
                !string.Equals(current.URI, preparedLayer.URI, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(SourceOfLayer(current), SourceOfLayer(preparedLayer), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The ArcGIS layer target for '{layerName}' changed before the edit could be written. Preview again and rebind the link if needed.");
        }

        /// <summary>The spatial reference the georeference is built in; see <see cref="CrsLayer"/>.</summary>
        private SpatialReference GeoreferenceSpatialReference()
        {
            if (!string.IsNullOrWhiteSpace(CrsLayer))
            {
                var sr = FindLayer(CrsLayer)?.GetSpatialReference();
                if (sr != null) return sr;
            }
            return ActiveMap.Current?.SpatialReference;
        }

        private static CoreGeometryTarget MapGeometryType(GeometryType t)
        {
            switch (t)
            {
                case GeometryType.Point:
                case GeometryType.Multipoint:
                    return CoreGeometryTarget.PointZ;
                case GeometryType.Polyline: return CoreGeometryTarget.PolylineZ;
                case GeometryType.Polygon: return CoreGeometryTarget.PolygonZ;
                case GeometryType.Multipatch: return CoreGeometryTarget.Multipatch;
                default: return CoreGeometryTarget.Unsupported;
            }
        }

        private static CoreFieldType MapFieldType(FieldType t)
        {
            switch (t)
            {
                case FieldType.Integer:
                case FieldType.SmallInteger:
                case FieldType.OID:
                    return CoreFieldType.Integer;
                case FieldType.Double:
                case FieldType.Single:
                    return CoreFieldType.Double;
                case FieldType.Date:
                    return CoreFieldType.Date;
                case FieldType.GUID:
                case FieldType.GlobalID:
                    return CoreFieldType.Guid;
                default:
                    return CoreFieldType.Text;
            }
        }

        // ---- write paths ----

        public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
        {
            return QueuedTask.Run(() =>
            {
                var oids = new List<long>();
                FeatureLayer layer = FindLayer(layerName);
                if (layer == null || features == null || features.Count == 0)
                    return (IReadOnlyList<long>)oids;

                SpatialReference sr = layer.GetSpatialReference();
                Dictionary<string, FieldType> fieldTypes = FieldTypeMap(layer);
                GeometryType targetShape = ShapeTypeOf(layer);

                var op = new global::ArcGIS.Desktop.Editing.EditOperation { Name = "Rhino.Inside push (create)" };
                var tokens = new List<global::ArcGIS.Desktop.Editing.RowToken>();

                foreach (FeatureRecord f in features)
                {
                    var attrs = BuildAttributeDictionary(f, sr, fieldTypes, targetShape);
                    tokens.Add(op.Create(layer, attrs));
                }

                EnsureTargetBeforeWrite(layerName, layer);
                if (!op.IsEmpty && !op.Execute())
                    throw new InvalidOperationException("Create edit failed: " + op.ErrorMessage);

                foreach (var t in tokens) if (t.ObjectID.HasValue) oids.Add(t.ObjectID.Value);
                return (IReadOnlyList<long>)oids;
            }).Result;
        }

        public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
        {
            QueuedTask.Run(() =>
            {
                FeatureLayer layer = FindLayer(layerName);
                if (layer == null || features == null || features.Count == 0) return;

                SpatialReference sr = layer.GetSpatialReference();
                Dictionary<string, FieldType> fieldTypes = FieldTypeMap(layer);
                GeometryType targetShape = ShapeTypeOf(layer);

                var op = new global::ArcGIS.Desktop.Editing.EditOperation { Name = "Rhino.Inside push (update)" };
                foreach (FeatureRecord f in features)
                {
                    if (f.Identity == null || !f.Identity.ArcGisObjectId.HasValue) continue;
                    var attrs = BuildAttributeDictionary(f, sr, fieldTypes, targetShape);
                    op.Modify(layer, f.Identity.ArcGisObjectId.Value, attrs);
                }

                EnsureTargetBeforeWrite(layerName, layer);
                if (!op.IsEmpty && !op.Execute())
                    throw new InvalidOperationException("Update edit failed: " + op.ErrorMessage);
            }).Wait();
        }

        public void RefreshScene()
        {
            // Feature-layer edits refresh automatically; explicit scene-cache rebuilds are a
            // later wave (publishing). No-op for now.
        }

        // ---- georeferencing (ICrsProjector) ----

        public CoreXyz ProjectFromWgs84(double latitude, double longitude, double elevation)
        {
            return QueuedTask.Run(() =>
            {
                SpatialReference sr = GeoreferenceSpatialReference();
                if (sr == null) return new CoreXyz(longitude, latitude, elevation);

                MapPoint wgs = MapPointBuilderEx.CreateMapPoint(longitude, latitude, elevation, SpatialReferences.WGS84);
                if (!(GeometryEngine.Instance.Project(wgs, sr) is MapPoint projected))
                    return new CoreXyz(longitude, latitude, elevation);

                return new CoreXyz(projected.X, projected.Y, projected.HasZ ? projected.Z : elevation);
            }).Result;
        }

        /// <summary>
        /// The CRS's exact local frame at a WGS84 point, measured with ArcGIS's own geodesy: the point
        /// is moved 50 m each way along true east and true north on the ellipsoid
        /// (<see cref="IGeometryEngine.GeodeticMove"/>), the four points are projected into the CRS
        /// through the datum transformation ArcGIS selects (<see cref="DatumTransforms"/>), and the
        /// central differences give the CRS vectors of one ground metre. Web Mercator's scale, a
        /// State Plane zone's feet and scale factor, any projection's grid convergence, and the
        /// WGS84-to-NAD83 datum shift all come out of that measurement.
        /// </summary>
        public LocalFrame GetLocalFrame(double latitude, double longitude, double elevation)
        {
            return QueuedTask.Run(() =>
            {
                SpatialReference sr = GeoreferenceSpatialReference();
                if (sr == null) return null;
                var wgs84 = SpatialReferences.WGS84;
                var anchor = MapPointBuilderEx.CreateMapPoint(longitude, latitude, elevation, wgs84);
                const double half = 50.0;
                var datum = DatumTransforms.Between(wgs84, sr, longitude, latitude);

                MapPoint Toward(double azimuthDegrees) =>
                    (MapPoint)datum.Project(
                        GeometryEngine.Instance.GeodeticMove(new[] { anchor }, wgs84, half, LinearUnit.Meters,
                            azimuthDegrees * Math.PI / 180.0, GeodeticCurveType.Geodesic)[0], sr);

                var origin = (MapPoint)datum.Project(anchor, sr);
                MapPoint east = Toward(90), west = Toward(270), north = Toward(0), south = Toward(180);
                double metersPerUnit = (sr.Unit as LinearUnit)?.ConversionFactor ?? 1.0;
                var frame = new LocalFrame
                {
                    Origin = new CoreXyz(origin.X, origin.Y, origin.HasZ ? origin.Z : elevation / metersPerUnit),
                    EastPerMetre = new CoreXyz((east.X - west.X) / (2 * half), (east.Y - west.Y) / (2 * half), 0),
                    NorthPerMetre = new CoreXyz((north.X - south.X) / (2 * half), (north.Y - south.Y) / (2 * half), 0),
                    VerticalPerMetre = 1.0 / metersPerUnit,
                    DatumTransformation = datum.Description
                };
                LastFrame = frame;
                return frame;
            }).Result;
        }

        public CoreUnit GetCrsLinearUnit()
        {
            return QueuedTask.Run(() =>
            {
                SpatialReference sr = GeoreferenceSpatialReference();
                double metersPerUnit = (sr?.Unit as LinearUnit)?.ConversionFactor ?? 1.0;
                return MapLinearUnit(metersPerUnit);
            }).Result;
        }

        /// <summary>The CRS's linear unit in metres, exactly as ArcGIS defines it.</summary>
        public double GetCrsMetresPerUnit()
        {
            return QueuedTask.Run(() =>
                (GeoreferenceSpatialReference()?.Unit as LinearUnit)?.ConversionFactor ?? double.NaN).Result;
        }

        private static CoreUnit MapLinearUnit(double metersPerUnit)
        {
            if (Near(metersPerUnit, 1.0)) return CoreUnit.Meters;
            // The US survey foot (1200/3937 m) is read as the international foot, as it always has
            // been on this path; Clarke's, Indian and other feet are not, and go by their exact length.
            if (Near(metersPerUnit, 0.3048) || Near(metersPerUnit, 1200.0 / 3937.0)) return CoreUnit.Feet;
            if (Near(metersPerUnit, 0.0254)) return CoreUnit.Inches;
            if (Near(metersPerUnit, 0.9144)) return CoreUnit.Yards;
            if (Near(metersPerUnit, 1000.0)) return CoreUnit.Kilometers;
            if (Near(metersPerUnit, 0.01)) return CoreUnit.Centimeters;
            if (Near(metersPerUnit, 0.001)) return CoreUnit.Millimeters;
            return CoreUnit.Unknown;
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9 * Math.Abs(b);

        private static GeometryType ToArcGisGeometryType(CoreGeometryTarget target)
        {
            switch (target)
            {
                case CoreGeometryTarget.PointZ: return GeometryType.Point;
                case CoreGeometryTarget.PolylineZ: return GeometryType.Polyline;
                case CoreGeometryTarget.PolygonZ:
                case CoreGeometryTarget.PolygonExtrusion:
                    return GeometryType.Polygon;
                case CoreGeometryTarget.Multipatch:
                    return GeometryType.Multipatch;
                default:
                    throw new NotSupportedException($"A new ArcGIS layer cannot be inferred for {target} geometry.");
            }
        }

        private static FieldType ToArcGisFieldType(CoreFieldType type)
        {
            switch (type)
            {
                case CoreFieldType.Integer: return FieldType.Integer;
                case CoreFieldType.Double: return FieldType.Double;
                case CoreFieldType.Date: return FieldType.Date;
                case CoreFieldType.Guid: return FieldType.GUID;
                default: return FieldType.String;
            }
        }

        private static string UniqueName(string requestedName, ISet<string> usedNames)
        {
            if (!usedNames.Contains(requestedName)) return requestedName;
            for (var suffix = 1; ; suffix++)
            {
                var candidate = requestedName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                if (!usedNames.Contains(candidate)) return candidate;
            }
        }

        private static void DeleteDefinition(Geodatabase geodatabase, string name)
        {
            using var definition = geodatabase.GetDefinition<FeatureClassDefinition>(name);
            var schema = new SchemaBuilder(geodatabase);
            schema.Delete(new FeatureClassDescription(definition));
            if (!schema.Build())
                throw new InvalidOperationException("Feature class cleanup failed: " +
                                                    string.Join("; ", schema.ErrorMessages));
        }

        private static void SetOnGroundElevation(Layer layer)
        {
            if (layer == null) return;
            var definition = new ElevationTypeDefinition { ElevationType = LayerElevationType.OnGround };
            if (layer.CanSetElevationTypeDefinition(definition)) layer.SetElevationTypeDefinition(definition);
        }

        private static Dictionary<string, object> BuildAttributeDictionary(
            FeatureRecord f, SpatialReference sr, Dictionary<string, FieldType> fieldTypes,
            GeometryType targetShape)
        {
            var attrs = new Dictionary<string, object>();

            // Written to the layer's own shape type. Writing whatever the Rhino object happened to
            // be produces "no support for this geometry type" from ArcGIS, naming neither side.
            Geometry shape = ArcGISGeometryWriter.ToArcGIS(f.Geometry, sr, targetShape);
            if (shape != null) attrs["SHAPE"] = shape;

            if (f.Attributes != null)
            {
                foreach (var kv in f.Attributes)
                {
                    FieldType ft = fieldTypes.TryGetValue(kv.Key, out FieldType t) ? t : FieldType.String;
                    attrs[kv.Key] = ConvertValue(kv.Value, ft);
                }
            }
            return attrs;
        }

        /// <summary>The geometry type a layer's feature class actually stores.</summary>
        private static GeometryType ShapeTypeOf(FeatureLayer layer)
        {
            using (FeatureClass fc = layer.GetFeatureClass())
                return fc.GetDefinition().GetShapeType();
        }

        private static Dictionary<string, FieldType> FieldTypeMap(FeatureLayer layer)
        {
            var map = new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase);
            using (FeatureClass fc = layer.GetFeatureClass())
            using (FeatureClassDefinition def = fc.GetDefinition())
            {
                foreach (Field field in def.GetFields())
                    map[field.Name] = field.FieldType;
            }
            return map;
        }

        private static object ConvertValue(string raw, FieldType type)
        {
            if (raw == null) return null;
            switch (type)
            {
                case FieldType.Integer:
                case FieldType.SmallInteger:
                case FieldType.OID:
                    return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? (object)l : raw;
                case FieldType.Double:
                case FieldType.Single:
                    return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (object)d : raw;
                case FieldType.Date:
                    return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime dt) ? (object)dt : raw;
                default:
                    return raw;
            }
        }
    }
}
