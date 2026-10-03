using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Reporting;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// Orchestrates a Rhino → ArcGIS push (spec 02 §1–§2). Classification, attribute ownership,
    /// type validation, identity, hashing, create-vs-update routing, and reporting all live here;
    /// the adapters only do SDK I/O. Tested with fake adapters.
    ///
    /// Wave 3 scope: Point / Polyline / Polygon(+height) / Multipatch push with create and update,
    /// attribute ownership push rules (spec 04 §6), type/domain validation, JSON fallback for
    /// unmapped fields, mixed-geometry splitting, and write-back of assigned identity. Both-sides
    /// conflict detection on update is Wave 4.
    /// </summary>
    public sealed class PushService
    {
        private readonly IArcGISAdapter _arcgis;
        private readonly IRhinoAdapter _rhino;

        public PushService(IArcGISAdapter arcgis, IRhinoAdapter rhino)
        {
            _arcgis = arcgis ?? throw new ArgumentNullException(nameof(arcgis));
            _rhino = rhino ?? throw new ArgumentNullException(nameof(rhino));
        }

        public SyncReport Push(LayerMappingProfile profile, LayerMapping layer, PushOptions options)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            options = options ?? PushOptions.Default;

            var report = new SyncReport(SyncOperation.Push);

            LayerSchema schema = _arcgis.GetSchema(layer.ArcGisLayer);
            ICoordinateMap map = GeoReferenceFactory.Create(_rhino, _arcgis, profile);

            IReadOnlyList<RhinoObjectSnapshot> snapshots = options.SelectedOnly
                ? _rhino.ReadSelectedObjects()
                : _rhino.ReadObjects(layer.RhinoLayer);

            var toCreate = new List<FeatureRecord>();
            var createSources = new List<PreparedFeature>();
            var toUpdate = new List<FeatureRecord>();
            var updateSources = new List<PreparedFeature>();
            var failed = new List<Guid>();

            foreach (var snapshot in snapshots)
            {
                bool isCopy = SyncIdentity.IsCopy(snapshot.RhinoGuid, snapshot.UserStrings);
                Guid syncGuid = isCopy ? Guid.NewGuid() : ReadGuid(snapshot.UserStrings, GisKeys.SyncGuid) ?? Guid.NewGuid();

                ClassificationResult cls = GeometryClassifier.Classify(snapshot.Descriptor);
                if (cls.Status == ClassificationStatus.Failed || cls.Status == ClassificationStatus.Unsupported)
                {
                    report.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Failed, cls.Note ?? "Unsupported geometry.");
                    failed.Add(snapshot.RhinoGuid);
                    continue;
                }

                // Mixed-geometry splitting (spec 02 §1): only push objects whose classification
                // matches the layer's target (default or an accepted alternate).
                if (layer.GeometryTarget != GeometryTarget.Unsupported &&
                    cls.Default != layer.GeometryTarget && !cls.Alternates.Contains(layer.GeometryTarget))
                {
                    report.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Skipped,
                        $"Classifies as {cls.Default} but layer target is {layer.GeometryTarget}.");
                    continue;
                }

                if (snapshot.Geometry == null || snapshot.Geometry.IsEmpty())
                {
                    report.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Skipped, "No supported geometry to push.");
                    continue;
                }

                var prepared = BuildPushAttributes(snapshot, layer, schema, report, syncGuid);
                prepared.IsCopy = isCopy;
                var gisGeometry = NeutralGeometryTransform.RhinoToGis(snapshot.Geometry, map);

                // Length / area columns follow the geometry being written (see DerivedFields).
                if (layer.Attributes != null)
                    foreach (FieldMapping fm in layer.Attributes)
                        if (fm.Owner == FieldOwnership.Derived && (schema == null || schema.FindField(fm.ArcGisField) != null))
                        {
                            string derived = DerivedFields.Compute(fm.ArcGisField, gisGeometry);
                            if (derived != null) prepared.ArcGisValues[fm.ArcGisField] = derived;
                        }

                long? oid = isCopy ? null : ReadLong(snapshot.UserStrings, GisKeys.ArcGisObjectId);
                var record = new FeatureRecord
                {
                    Target = layer.GeometryTarget != GeometryTarget.Unsupported ? layer.GeometryTarget : cls.Default,
                    Geometry = gisGeometry,
                    Attributes = prepared.ArcGisValues,
                    Identity = new SyncIdentity(syncGuid)
                    {
                        RhinoGuid = snapshot.RhinoGuid,
                        ArcGisObjectId = oid,
                        TargetLayer = layer.ArcGisLayer
                    }
                };

                prepared.GisGeometry = gisGeometry;
                prepared.ArcGisObjectId = oid;

                bool canUpdate = oid.HasValue && !options.CreateOnly;
                if (canUpdate)
                {
                    toUpdate.Add(record);
                    updateSources.Add(prepared);
                }
                else if (!options.UpdateOnly)
                {
                    toCreate.Add(record);
                    createSources.Add(prepared);
                }
                else
                {
                    report.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Skipped, "Update-only: no existing feature to update.");
                }
            }

            ApplyCreates(layer, toCreate, createSources, report);
            ApplyUpdates(layer, toUpdate, updateSources, report);

            if (failed.Count > 0) _rhino.IsolateFailed(failed);
            _arcgis.RefreshScene();

            return report;
        }

        private void ApplyCreates(LayerMapping layer, List<FeatureRecord> toCreate,
            List<PreparedFeature> sources, SyncReport report)
        {
            if (toCreate.Count == 0) return;

            IReadOnlyList<long> oids = _arcgis.CreateFeatures(layer.ArcGisLayer, toCreate);
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                long? oid = (oids != null && i < oids.Count) ? oids[i] : (long?)null;
                WriteBack(src, oid, layer);
                report.Add(src.SyncGuid, layer.ArcGisLayer, SyncOutcome.Created);
            }
        }

        private void ApplyUpdates(LayerMapping layer, List<FeatureRecord> toUpdate,
            List<PreparedFeature> sources, SyncReport report)
        {
            if (toUpdate.Count == 0) return;

            _arcgis.UpdateFeatures(layer.ArcGisLayer, toUpdate);
            foreach (var src in sources)
            {
                WriteBack(src, src.ArcGisObjectId, layer);
                report.Add(src.SyncGuid, layer.ArcGisLayer, SyncOutcome.Updated);
            }
        }

        /// <summary>Persist the post-push sync bookkeeping back onto the Rhino object.</summary>
        private void WriteBack(PreparedFeature src, long? oid, LayerMapping layer)
        {
            var us = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { GisKeys.SyncGuid, src.SyncGuid.ToString() },
                { GisKeys.RhinoObjectId, src.RhinoGuid.ToString() },
                { GisKeys.ArcGisLayer, layer.ArcGisLayer ?? string.Empty },
                { GisKeys.LastPushTime, DateTimeOffset.UtcNow.ToString("o") },
                { GisKeys.AttributeHash, Hashing.HashAttributes(src.DesignByRhinoKey) },
                { GisKeys.GeometryHash, Hashing.HashGeometry(src.GisGeometry) },
                { GisKeys.RhinoGeometryHash, Hashing.HashGeometry(src.GisGeometry) },
                { GisKeys.RhinoModelStamp, null },   // not taken here; see ModelStamp
                { GisKeys.SyncState, SyncState.Clean.ToString() }
            };
            if (oid.HasValue) us[GisKeys.ArcGisObjectId] = oid.Value.ToString();
            if (src.IsCopy)
            {
                us[GisKeys.ArcGisGlobalId] = null;
                us[GisKeys.PartOf] = null;
                us[GisKeys.PartIndex] = null;
            }
            foreach (var kv in src.DesignByRhinoKey)
                us[GisKeys.FieldHashKey(kv.Key)] = Hashing.HashField(kv.Value);

            _rhino.WriteUserStrings(src.RhinoGuid, us);
        }

        /// <summary>
        /// Map a Rhino object's user strings onto ArcGIS field values per ownership push rules,
        /// validating types/domains and routing unmapped fields to the JSON fallback (spec 04 §6, §8, §14).
        /// </summary>
        internal static PreparedFeature BuildPushAttributes(
            RhinoObjectSnapshot snapshot, LayerMapping layer, LayerSchema schema, SyncReport report, Guid syncGuid)
        {
            var result = new PreparedFeature
            {
                RhinoGuid = snapshot.RhinoGuid,
                SyncGuid = syncGuid,
                ArcGisValues = new Dictionary<string, string>(StringComparer.Ordinal),
                DesignByRhinoKey = new Dictionary<string, string>(StringComparer.Ordinal)
            };
            var jsonFallback = new Dictionary<string, string>(StringComparer.Ordinal);

            if (layer.Attributes != null)
            {
                foreach (FieldMapping map in layer.Attributes)
                {
                    string current = Get(snapshot.UserStrings, map.RhinoKey);
                    string lastHash = Get(snapshot.UserStrings, GisKeys.FieldHashKey(map.RhinoKey));
                    bool changedInRhino = string.IsNullOrEmpty(lastHash)
                        ? current != null
                        : Hashing.HashField(current) != lastHash;

                    // Wave 3 does not read the live ArcGIS value, so changedInArcGis is optimistic
                    // (false). Both-sides conflict detection on update arrives in Wave 4.
                    SyncAction action = AttributeRules.Push(map.Owner, changedInRhino, changedInArcGis: false);
                    if (action != SyncAction.Write && action != SyncAction.Recalculate)
                        continue;
                    if (current == null)
                    {
                        if (map.Required)
                            report?.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Warning,
                                $"{map.RhinoKey}: a value is required by the ArcGIS schema.");
                        else if (!FieldValueValidator.TryValidate(
                                     null, map.Type, map.Domain, map.Validators, out string missingError))
                            report?.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Warning,
                                $"{map.RhinoKey}: {missingError}");
                        continue;
                    }

                    if (!FieldValueValidator.TryValidate(
                            current, map.Type, map.Domain, map.Validators, out string error))
                    {
                        report?.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Warning, $"{map.RhinoKey}: {error}");
                        continue;
                    }

                    result.DesignByRhinoKey[map.RhinoKey] = current;

                    if (schema != null && schema.FindField(map.ArcGisField) == null)
                    {
                        // Field doesn't exist in the target schema: stash for the JSON fallback.
                        jsonFallback[map.RhinoKey] = current;
                        report?.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Warning,
                            $"Field '{map.ArcGisField}' missing in schema; routed to rhino_attrs_json.");
                    }
                    else
                    {
                        result.ArcGisValues[map.ArcGisField] = current;
                    }
                }
            }

            if (jsonFallback.Count > 0 && (schema == null || schema.FindField(GisKeys.RhinoAttrsJsonField) != null))
                result.ArcGisValues[GisKeys.RhinoAttrsJsonField] = JsonConvert.SerializeObject(jsonFallback);

            return result;
        }

        private static string Get(IReadOnlyDictionary<string, string> map, string key)
            => map != null && map.TryGetValue(key, out string v) ? v : null;

        private static Guid? ReadGuid(IReadOnlyDictionary<string, string> map, string key)
            => Guid.TryParse(Get(map, key), out Guid g) ? g : (Guid?)null;

        private static long? ReadLong(IReadOnlyDictionary<string, string> map, string key)
            => long.TryParse(Get(map, key), out long v) ? v : (long?)null;

        /// <summary>Carries per-object state from preparation through to write-back.</summary>
        internal sealed class PreparedFeature
        {
            public Guid RhinoGuid;
            public Guid SyncGuid;
            public bool IsCopy;
            public long? ArcGisObjectId;
            public Dictionary<string, string> ArcGisValues;
            public Dictionary<string, string> DesignByRhinoKey;
            public NeutralGeometry GisGeometry;
        }
    }
}
