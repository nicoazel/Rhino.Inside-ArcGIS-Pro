using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Reporting;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    /// <summary>
    /// End-to-end pull/push/sync exercised through the real services, the GeoReferenceFactory, and
    /// the Earth Anchor Point path. The model is in FEET and the anchor sits at a projected origin,
    /// so these prove unit and location alignment survive a full round trip.
    /// </summary>
    public class EndToEndTests
    {
        private const double FeetToM = 0.3048;
        private const double Ox = 500000.0;   // anchor projected easting (metres)
        private const double Oy = 4000000.0;  // anchor projected northing (metres)

        private static LayerMappingProfile Profile()
        {
            return new LayerMappingProfile
            {
                RhinoUnits = UnitSystem.Feet,
                ArcGisCrs = "EPSG:32611",
                ProjectAnchor = new AnchorSettings(), // ignored: EAP path is used
                Layers =
                {
                    new LayerMapping
                    {
                        Name = "pts", RhinoLayer = "GIS::pts", ArcGisLayer = "pts",
                        GeometryTarget = GeometryTarget.PointZ,
                        Attributes = { new FieldMapping { RhinoKey = "asset_type", ArcGisField = "asset_type", Owner = FieldOwnership.Shared } }
                    }
                }
            };
        }

        [Fact]
        public void Pull_then_push_round_trips_location_and_units()
        {
            // A feature 100 ft east / 200 ft north of the anchor, expressed in projected metres.
            double px = Ox + 100 * FeetToM, py = Oy + 200 * FeetToM;
            var arc = new E2EArcGis();
            arc.Features.Add(Feature(7, "PV", px, py));
            var rhino = new E2ERhino();
            var profile = Profile();

            new PullService(arc, rhino).Pull(profile, profile.Layers[0], PullOptions.Context);

            // Pulled into model space it must be exactly (100, 200) feet.
            Assert.Single(rhino.Objects);
            var modelPt = rhino.Objects[0].Geometry.Points[0];
            Assert.Equal(100, modelPt.X, 4);
            Assert.Equal(200, modelPt.Y, 4);

            // Pushing it straight back must land at the original projected coordinate.
            new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.Single(arc.Updated);
            var gis = arc.Updated[0].Geometry.Points[0];
            Assert.Equal(px, gis.X, 3);
            Assert.Equal(py, gis.Y, 3);
        }

        [Fact]
        public void New_rhino_object_pushes_to_correct_projected_location()
        {
            var arc = new E2EArcGis();
            var rhino = new E2ERhino();
            rhino.Objects.Add(ModelPoint("GIS::pts", 10, 20, u => u["asset_type"] = "PV"));
            var profile = Profile();

            new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.Single(arc.Created);
            var gis = arc.Created[0].Geometry.Points[0];
            Assert.Equal(Ox + 10 * FeetToM, gis.X, 3);
            Assert.Equal(Oy + 20 * FeetToM, gis.Y, 3);
        }

        [Fact]
        public void Pull_then_unchanged_sync_is_clean()
        {
            double px = Ox + 100 * FeetToM, py = Oy + 200 * FeetToM;
            var arc = new E2EArcGis();
            arc.Features.Add(Feature(7, "PV", px, py));
            var rhino = new E2ERhino();
            var profile = Profile();

            new PullService(arc, rhino).Pull(profile, profile.Layers[0], PullOptions.Context);
            arc.Created.Clear();
            arc.Updated.Clear();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Conflicts = ConflictResolution.Manual });

            Assert.Empty(arc.Created);
            Assert.Empty(arc.Updated);
            Assert.Equal(0, report.CountOf(SyncOutcome.Created));
            Assert.Equal(0, report.CountOf(SyncOutcome.Updated));
            Assert.Equal(0, report.CountOf(SyncOutcome.Conflict));
        }

        [Fact]
        public void Edit_in_rhino_then_sync_pushes_only_that_change()
        {
            double px = Ox + 100 * FeetToM, py = Oy + 200 * FeetToM;
            var arc = new E2EArcGis();
            arc.Features.Add(Feature(7, "PV", px, py));
            var rhino = new E2ERhino();
            var profile = Profile();

            new PullService(arc, rhino).Pull(profile, profile.Layers[0], PullOptions.Context);
            arc.Created.Clear();
            arc.Updated.Clear();

            // Edit the attribute in Rhino.
            rhino.Objects[0].UserStrings["asset_type"] = "PV canopy";

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Conflicts = ConflictResolution.Manual });

            Assert.Single(arc.Updated);
            Assert.Equal("PV canopy", arc.Updated[0].Attributes["asset_type"]);
            Assert.Equal(1, report.CountOf(SyncOutcome.Updated));
        }

        // ---- helpers ----

        private static FeatureRecord Feature(long oid, string assetType, double x, double y)
            => new FeatureRecord
            {
                Identity = new SyncIdentity { ArcGisObjectId = oid },
                Geometry = NeutralGeometry.Point(new Xyz(x, y, 0)),
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { { "asset_type", assetType } }
            };

        private static RhinoObjectSnapshot ModelPoint(string layer, double x, double y, Action<Dictionary<string, string>> attrs = null)
        {
            var us = new Dictionary<string, string>(StringComparer.Ordinal);
            attrs?.Invoke(us);
            return new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                LayerName = layer,
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.Point),
                Geometry = NeutralGeometry.Point(new Xyz(x, y, 0)),
                UserStrings = us
            };
        }

        // Stateful Rhino fake with Earth Anchor: model units feet, model→ENU metres = scale 0.3048.
        private sealed class E2ERhino : IRhinoAdapter, IEarthAnchorSource
        {
            public List<RhinoObjectSnapshot> Objects { get; } = new List<RhinoObjectSnapshot>();

            public EarthAnchor GetEarthAnchor() => new EarthAnchor
            {
                Latitude = 36.0, Longitude = -115.0, Elevation = 0,
                ModelBasePoint = new Xyz(0, 0, 0), ModelUnits = UnitSystem.Feet, NorthAngleDegrees = 0, IsSet = true
            };
            public AffineTransform GetModelToEarthMetres() => AffineTransform.Scale(FeetToM);

            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName)
                => Objects.Where(o => o.LayerName == layerName).ToList();
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => Objects;
            public RhinoObjectSnapshot ReadObject(Guid rhinoGuid) =>
                Objects.FirstOrDefault(o => o.RhinoGuid == rhinoGuid);

            public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings)
            {
                var snap = new RhinoObjectSnapshot
                {
                    RhinoGuid = Guid.NewGuid(),
                    LayerName = layerName,
                    Descriptor = new GeometryDescriptor(Infer(geometry.Kind)),
                    Geometry = geometry,
                    UserStrings = new Dictionary<string, string>(userStrings, StringComparer.Ordinal)
                };
                Objects.Add(snap);
                return snap.RhinoGuid;
            }

            public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings)
            {
                var snap = Objects.FirstOrDefault(o => o.RhinoGuid == rhinoGuid);
                if (snap == null) return;
                foreach (var kv in userStrings) snap.UserStrings[kv.Key] = kv.Value;
            }

            public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry)
            {
                var snap = Objects.FirstOrDefault(o => o.RhinoGuid == rhinoGuid)
                    ?? throw new InvalidOperationException($"Object {rhinoGuid} was not found.");
                snap.Geometry = geometry;
                snap.Descriptor = new GeometryDescriptor(Infer(geometry.Kind));
            }

            public UnitSystem GetUnits() => UnitSystem.Feet;
            public IReadOnlyList<string> GetLayerNames() => Objects.Select(o => o.LayerName).Distinct().ToList();
            public void IsolateFailed(IEnumerable<Guid> rhinoGuids) { }

            private static RhinoGeometryKind Infer(NeutralGeometryKind k)
            {
                switch (k)
                {
                    case NeutralGeometryKind.Point: return RhinoGeometryKind.Point;
                    case NeutralGeometryKind.Polyline: return RhinoGeometryKind.OpenCurve;
                    case NeutralGeometryKind.Polygon: return RhinoGeometryKind.ClosedPlanarCurve;
                    default: return RhinoGeometryKind.Mesh;
                }
            }
        }

        // Stateful ArcGIS fake with a fixed projection: anchor lat/long → (Ox, Oy), CRS metres.
        private sealed class E2EArcGis : IArcGISAdapter, ICrsProjector
        {
            public List<FeatureRecord> Features { get; } = new List<FeatureRecord>();
            public List<FeatureRecord> Created { get; } = new List<FeatureRecord>();
            public List<FeatureRecord> Updated { get; } = new List<FeatureRecord>();
            private long _nextOid = 1000;

            public Xyz ProjectFromWgs84(double latitude, double longitude, double elevation) => new Xyz(Ox, Oy, elevation);
            public UnitSystem GetCrsLinearUnit() => UnitSystem.Meters;

            public LayerSchema GetSchema(string layerName) => new LayerSchema
            {
                LayerName = layerName,
                GeometryType = GeometryTarget.PointZ,
                Fields = { new FieldDefinition { Name = "asset_type", Type = FieldType.Text } }
            };

            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) => Features.ToList();
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => Features.ToList();

            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                var oids = new List<long>();
                foreach (var f in features)
                {
                    long oid = _nextOid++;
                    f.Identity = f.Identity ?? new SyncIdentity();
                    f.Identity.ArcGisObjectId = oid;
                    Features.Add(f);
                    Created.Add(f);
                    oids.Add(oid);
                }
                return oids;
            }

            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                foreach (var f in features)
                {
                    Updated.Add(f);
                    long? oid = f.Identity?.ArcGisObjectId;
                    if (!oid.HasValue) continue;
                    int idx = Features.FindIndex(x => x.Identity != null && x.Identity.ArcGisObjectId == oid);
                    if (idx >= 0) Features[idx] = f;
                }
            }

            public void RefreshScene() { }
            public IReadOnlyList<string> GetLayerNames() => new List<string> { "pts" };
        }
    }
}
