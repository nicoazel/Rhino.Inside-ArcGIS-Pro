using System;
using System.Collections.Generic;
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
    public class PushServiceTests
    {
        private static LayerMappingProfile Profile(GeometryTarget target = GeometryTarget.PointZ)
        {
            return new LayerMappingProfile
            {
                ProjectName = "T",
                RhinoUnits = UnitSystem.Meters,
                ArcGisCrs = "EPSG:3857",
                ProjectAnchor = new AnchorSettings(), // identity → Rhino==GIS for easy asserts
                Layers =
                {
                    new LayerMapping
                    {
                        Name = "Pts",
                        RhinoLayer = "GIS::Pts",
                        ArcGisLayer = "pts",
                        GeometryTarget = target,
                        Attributes =
                        {
                            new FieldMapping { RhinoKey = "asset_type", ArcGisField = "asset_type", Owner = FieldOwnership.Shared },
                            new FieldMapping { RhinoKey = "review_status", ArcGisField = "review_status", Owner = FieldOwnership.ArcGisOwned }
                        }
                    }
                }
            };
        }

        private static LayerSchema Schema()
        {
            return new LayerSchema
            {
                LayerName = "pts",
                GeometryType = GeometryTarget.PointZ,
                Fields =
                {
                    new FieldDefinition { Name = "asset_type", Type = FieldType.Text },
                    new FieldDefinition { Name = "review_status", Type = FieldType.Text },
                    new FieldDefinition { Name = "rhino_attrs_json", Type = FieldType.Text }
                }
            };
        }

        private static RhinoObjectSnapshot Point(double x, double y, Action<Dictionary<string, string>> attrs = null)
        {
            var us = new Dictionary<string, string>(StringComparer.Ordinal);
            attrs?.Invoke(us);
            return new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                LayerName = "GIS::Pts",
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.Point),
                Geometry = NeutralGeometry.Point(new Xyz(x, y, 0)),
                UserStrings = us
            };
        }

        [Fact]
        public void New_object_is_created_and_identity_written_back()
        {
            var rhino = new FakeRhino { Objects = { Point(5, 6, u => u["asset_type"] = "PV") } };
            var arc = new FakeArcGis { SchemaToReturn = Schema() };
            var profile = Profile();

            var report = new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.Single(arc.Created);
            Assert.Equal(1, report.CountOf(SyncOutcome.Created));
            // identity written back to the Rhino object
            Assert.True(rhino.Written.ContainsKey(rhino.Objects[0].RhinoGuid));
            var written = rhino.Written[rhino.Objects[0].RhinoGuid];
            Assert.True(written.ContainsKey(GisKeys.SyncGuid));
            Assert.Equal("1000", written[GisKeys.ArcGisObjectId]);
        }

        [Fact]
        public void Pushed_geometry_is_transformed_to_gis_space()
        {
            var rhino = new FakeRhino { Objects = { Point(5, 6) } };
            var arc = new FakeArcGis { SchemaToReturn = Schema() };
            var profile = Profile();

            new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            var geom = arc.Created[0].Geometry;
            Assert.Equal(5, geom.Points[0].X, 6);
            Assert.Equal(6, geom.Points[0].Y, 6);
        }

        [Fact]
        public void Arcgis_owned_field_is_not_pushed_but_shared_is()
        {
            var rhino = new FakeRhino
            {
                Objects = { Point(0, 0, u => { u["asset_type"] = "PV"; u["review_status"] = "approved"; }) }
            };
            var arc = new FakeArcGis { SchemaToReturn = Schema() };
            var profile = Profile();

            new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            var attrs = arc.Created[0].Attributes;
            Assert.Equal("PV", attrs["asset_type"]);
            Assert.False(attrs.ContainsKey("review_status"));
        }

        [Fact]
        public void Existing_object_id_routes_to_update()
        {
            var rhino = new FakeRhino
            {
                Objects = { Point(0, 0, u => { u["asset_type"] = "PV"; u[GisKeys.ArcGisObjectId] = "55"; }) }
            };
            var arc = new FakeArcGis { SchemaToReturn = Schema() };
            var profile = Profile();

            var report = new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.Empty(arc.Created);
            Assert.Single(arc.Updated);
            Assert.Equal(55, arc.Updated[0].Identity.ArcGisObjectId);
            Assert.Equal(1, report.CountOf(SyncOutcome.Updated));
        }

        [Fact]
        public void Geometry_not_matching_layer_target_is_split_out()
        {
            // Closed planar curve classifies as PolygonZ; layer target is PointZ → skipped.
            var snap = new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.ClosedPlanarCurve),
                Geometry = NeutralGeometry.Polygon(new[] { new Xyz(0, 0, 0), new Xyz(1, 0, 0), new Xyz(1, 1, 0) }),
                UserStrings = new Dictionary<string, string>(StringComparer.Ordinal)
            };
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { SchemaToReturn = Schema() };
            var profile = Profile(GeometryTarget.PointZ);

            var report = new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.Empty(arc.Created);
            Assert.Equal(1, report.CountOf(SyncOutcome.Skipped));
        }

        [Fact]
        public void Mesh_object_pushes_as_multipatch()
        {
            var mesh = new NeutralMesh
            {
                Vertices = { new Xyz(0, 0, 0), new Xyz(1, 0, 0), new Xyz(1, 1, 0), new Xyz(0, 1, 0) },
                Faces = { new[] { 0, 1, 2, 3 } }
            };
            var snap = new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.Mesh),
                Geometry = new NeutralGeometry { Kind = NeutralGeometryKind.Multipatch, Mesh = mesh },
                UserStrings = new Dictionary<string, string>(StringComparer.Ordinal)
            };
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { SchemaToReturn = Schema() };
            var profile = Profile(GeometryTarget.Multipatch);

            var report = new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.Single(arc.Created);
            Assert.Equal(1, report.CountOf(SyncOutcome.Created));
            Assert.Equal(NeutralGeometryKind.Multipatch, arc.Created[0].Geometry.Kind);
            Assert.Equal(4, arc.Created[0].Geometry.Mesh.Vertices.Count);
        }

        [Fact]
        public void Unmapped_field_goes_to_json_fallback()
        {
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "extra", ArcGisField = "extra_field", Owner = FieldOwnership.Shared
            });
            var rhino = new FakeRhino
            {
                Objects = { Point(0, 0, u => { u["asset_type"] = "PV"; u["extra"] = "foo"; }) }
            };
            var arc = new FakeArcGis { SchemaToReturn = Schema() }; // schema has no extra_field, has rhino_attrs_json

            new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            var attrs = arc.Created[0].Attributes;
            Assert.False(attrs.ContainsKey("extra_field"));
            Assert.True(attrs.ContainsKey(GisKeys.RhinoAttrsJsonField));
            Assert.Contains("foo", attrs[GisKeys.RhinoAttrsJsonField]);
        }

        [Fact]
        public void Invalid_value_is_warned_and_not_written()
        {
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "height_m", ArcGisField = "height_m", Type = FieldType.Double, Owner = FieldOwnership.RhinoOwned
            });
            var schema = Schema();
            schema.Fields.Add(new FieldDefinition { Name = "height_m", Type = FieldType.Double });

            var rhino = new FakeRhino { Objects = { Point(0, 0, u => u["height_m"] = "tall") } };
            var arc = new FakeArcGis { SchemaToReturn = schema };

            var report = new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.False(arc.Created[0].Attributes.ContainsKey("height_m"));
            Assert.Equal(1, report.CountOf(SyncOutcome.Warning));
        }

        [Fact]
        public void Profile_validator_is_enforced_before_arcgis_write()
        {
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "height_m", ArcGisField = "height_m", Type = FieldType.Double,
                Owner = FieldOwnership.RhinoOwned, Validators = new List<string> { "positive_number" }
            });
            var schema = Schema();
            schema.Fields.Add(new FieldDefinition { Name = "height_m", Type = FieldType.Double });
            var rhino = new FakeRhino { Objects = { Point(0, 0, u => u["height_m"] = "-4") } };
            var arc = new FakeArcGis { SchemaToReturn = schema };

            var report = new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.False(arc.Created[0].Attributes.ContainsKey("height_m"));
            Assert.Contains(report.Entries,
                entry => entry.Outcome == SyncOutcome.Warning && entry.Message.Contains("positive"));
        }

        [Fact]
        public void Missing_value_is_reported_for_non_empty_profile_rule()
        {
            var profile = Profile();
            profile.Layers[0].Attributes[0].Validators = new List<string> { "non_empty" };
            var rhino = new FakeRhino { Objects = { Point(0, 0) } };
            var arc = new FakeArcGis { SchemaToReturn = Schema() };

            var report = new PushService(arc, rhino).Push(profile, profile.Layers[0], PushOptions.Default);

            Assert.Single(arc.Created);
            Assert.False(arc.Created[0].Attributes.ContainsKey("asset_type"));
            Assert.Contains(report.Entries,
                entry => entry.Outcome == SyncOutcome.Warning && entry.Message.Contains("must not be empty"));
        }

        // ---- fakes ----

        private sealed class FakeArcGis : IArcGISAdapter
        {
            public LayerSchema SchemaToReturn;
            public List<FeatureRecord> Created { get; } = new List<FeatureRecord>();
            public List<FeatureRecord> Updated { get; } = new List<FeatureRecord>();

            public LayerSchema GetSchema(string layerName) => SchemaToReturn;

            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                var oids = new List<long>();
                long next = 1000;
                foreach (var f in features) { Created.Add(f); oids.Add(next++); }
                return oids;
            }

            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
                => Updated.AddRange(features);

            public void RefreshScene() { }

            public IReadOnlyList<string> GetLayerNames() => throw new NotImplementedException();
            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) => throw new NotImplementedException();
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => throw new NotImplementedException();
        }

        private sealed class FakeRhino : IRhinoAdapter
        {
            public List<RhinoObjectSnapshot> Objects { get; } = new List<RhinoObjectSnapshot>();
            public Dictionary<Guid, IReadOnlyDictionary<string, string>> Written { get; } =
                new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
            public List<Guid> Isolated { get; } = new List<Guid>();

            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName) => Objects;
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => Objects;
            public RhinoObjectSnapshot ReadObject(Guid rhinoGuid) =>
                Objects.Find(o => o.RhinoGuid == rhinoGuid);

            public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings)
                => Written[rhinoGuid] = userStrings;

            public void IsolateFailed(IEnumerable<Guid> rhinoGuids) => Isolated.AddRange(rhinoGuids);

            public UnitSystem GetUnits() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => throw new NotImplementedException();
            public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings) => throw new NotImplementedException();
            public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry) => throw new NotImplementedException();
        }
    }
}
