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
    public class PullServiceTests
    {
        private static LayerMappingProfile Profile()
        {
            return new LayerMappingProfile
            {
                ProjectName = "T",
                RhinoUnits = UnitSystem.Meters,
                ArcGisCrs = "EPSG:3857",
                ProjectAnchor = new AnchorSettings
                {
                    RhinoPoint = new[] { 0.0, 0.0, 0.0 },
                    GisPoint = new[] { 1000.0, 2000.0, 0.0 },
                    Scale = 1.0
                },
                Layers =
                {
                    new LayerMapping
                    {
                        Name = "Points",
                        RhinoLayer = "GIS::Pts",
                        ArcGisLayer = "pts",
                        GeometryTarget = GeometryTarget.PointZ,
                        Attributes =
                        {
                            new FieldMapping { RhinoKey = "asset_type", ArcGisField = "asset_type", Owner = FieldOwnership.Shared },
                            new FieldMapping { RhinoKey = "secret", ArcGisField = "secret", Owner = FieldOwnership.LocalOnlyArcGis }
                        }
                    }
                }
            };
        }

        private static FeatureRecord PointFeature(double x, double y)
        {
            return new FeatureRecord
            {
                Identity = new SyncIdentity { ArcGisObjectId = 42 },
                Target = GeometryTarget.PointZ,
                Geometry = NeutralGeometry.Point(new Xyz(x, y, 0)),
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "asset_type", "PV" },
                    { "secret", "do-not-pull" }
                }
            };
        }

        [Fact]
        public void Context_pull_creates_one_rhino_object_per_feature()
        {
            var arc = new FakeArcGis { Features = { PointFeature(1000, 2000), PointFeature(1010, 2000) } };
            var rhino = new FakeRhino();
            var svc = new PullService(arc, rhino);
            var profile = Profile();

            var report = svc.Pull(profile, profile.Layers[0], PullOptions.Context);

            Assert.Equal(2, rhino.Created.Count);
            Assert.Equal(2, report.CountOf(SyncOutcome.Created));
            Assert.Equal("GIS::Pts", rhino.Created[0].Layer);
        }

        [Fact]
        public void Pulled_geometry_is_transformed_into_rhino_space()
        {
            var arc = new FakeArcGis { Features = { PointFeature(1000, 2000) } };
            var rhino = new FakeRhino();
            var profile = Profile();

            new PullService(arc, rhino).Pull(profile, profile.Layers[0], PullOptions.Context);

            var created = rhino.Created[0].Geometry;
            // GIS (1000,2000) == anchor gis point -> rhino origin.
            Assert.Equal(0, created.Points[0].X, 6);
            Assert.Equal(0, created.Points[0].Y, 6);
        }

        [Fact]
        public void Mapped_field_is_written_but_local_only_arcgis_is_skipped()
        {
            var arc = new FakeArcGis { Features = { PointFeature(1000, 2000) } };
            var rhino = new FakeRhino();
            var profile = Profile();

            new PullService(arc, rhino).Pull(profile, profile.Layers[0], PullOptions.Context);

            var us = rhino.Created[0].UserStrings;
            Assert.Equal("PV", us["asset_type"]);
            Assert.False(us.ContainsKey("secret"));
        }

        [Fact]
        public void System_keys_are_written()
        {
            var arc = new FakeArcGis { Features = { PointFeature(1000, 2000) } };
            var rhino = new FakeRhino();
            var profile = Profile();

            new PullService(arc, rhino).Pull(profile, profile.Layers[0], PullOptions.Context);

            var us = rhino.Created[0].UserStrings;
            Assert.True(us.ContainsKey(GisKeys.SyncGuid));
            Assert.True(us.ContainsKey(GisKeys.GeometryHash));
            Assert.True(us.ContainsKey(GisKeys.AttributeHash));
            Assert.Equal("pts", us[GisKeys.ArcGisLayer]);
            Assert.Equal("EPSG:3857", us[GisKeys.SourceCrs]);
        }

        [Fact]
        public void Selected_only_reads_the_selection()
        {
            var arc = new FakeArcGis
            {
                Features = { PointFeature(1000, 2000), PointFeature(1010, 2000) },
                Selected = { PointFeature(1020, 2000) }
            };
            var rhino = new FakeRhino();
            var profile = Profile();

            new PullService(arc, rhino).Pull(profile, profile.Layers[0],
                new PullOptions { Mode = PullMode.Context, SelectedOnly = true });

            Assert.Single(rhino.Created);
        }

        [Fact]
        public void Unsupported_mode_creates_nothing_and_reports_skip()
        {
            var arc = new FakeArcGis { Features = { PointFeature(1000, 2000) } };
            var rhino = new FakeRhino();
            var profile = Profile();

            var report = new PullService(arc, rhino).Pull(profile, profile.Layers[0],
                new PullOptions { Mode = PullMode.AttributeOnly });

            Assert.Empty(rhino.Created);
            Assert.Equal(1, report.CountOf(SyncOutcome.Skipped));
        }

        // ---- fakes ----

        private sealed class FakeArcGis : IArcGISAdapter
        {
            public List<FeatureRecord> Features { get; } = new List<FeatureRecord>();
            public List<FeatureRecord> Selected { get; } = new List<FeatureRecord>();

            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) => Features;
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => Selected;

            public IReadOnlyList<string> GetLayerNames() => throw new NotImplementedException();
            public LayerSchema GetSchema(string layerName) => throw new NotImplementedException();
            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features) => throw new NotImplementedException();
            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features) => throw new NotImplementedException();
            public void RefreshScene() => throw new NotImplementedException();
        }

        private sealed class CreatedObject
        {
            public Guid Id;
            public NeutralGeometry Geometry;
            public string Layer;
            public Dictionary<string, string> UserStrings;
        }

        private sealed class FakeRhino : IRhinoAdapter
        {
            public List<CreatedObject> Created { get; } = new List<CreatedObject>();

            public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings)
            {
                var id = Guid.NewGuid();
                Created.Add(new CreatedObject
                {
                    Id = id,
                    Geometry = geometry,
                    Layer = layerName,
                    UserStrings = new Dictionary<string, string>(userStrings, StringComparer.Ordinal)
                });
                return id;
            }

            public UnitSystem GetUnits() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => throw new NotImplementedException();
            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName) =>
                Created.FindAll(created => created.Layer == layerName).ConvertAll(ToSnapshot);
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => throw new NotImplementedException();
            public RhinoObjectSnapshot ReadObject(Guid rhinoGuid) =>
                Created.Find(created => created.Id == rhinoGuid) is CreatedObject created ? ToSnapshot(created) : null;
            public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings)
            {
                var created = Created.Find(item => item.Id == rhinoGuid);
                if (created == null) return;
                foreach (var pair in userStrings) created.UserStrings[pair.Key] = pair.Value;
            }
            public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry) => throw new NotImplementedException();
            public void IsolateFailed(IEnumerable<Guid> rhinoGuids) => throw new NotImplementedException();

            private static RhinoObjectSnapshot ToSnapshot(CreatedObject created) => new RhinoObjectSnapshot
            {
                RhinoGuid = created.Id,
                Geometry = created.Geometry,
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.Point),
                UserStrings = new Dictionary<string, string>(created.UserStrings, StringComparer.Ordinal)
            };
        }
    }
}
