using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Reporting;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class SyncServiceTests
    {
        private static LayerMappingProfile Profile()
        {
            return new LayerMappingProfile
            {
                RhinoUnits = UnitSystem.Meters,
                ArcGisCrs = "EPSG:3857",
                ProjectAnchor = new AnchorSettings(),
                Layers =
                {
                    new LayerMapping
                    {
                        Name = "Pts", RhinoLayer = "GIS::Pts", ArcGisLayer = "pts",
                        GeometryTarget = GeometryTarget.PointZ,
                        Attributes = { new FieldMapping { RhinoKey = "asset_type", ArcGisField = "asset_type", Owner = FieldOwnership.Shared } }
                    }
                }
            };
        }

        private static LayerSchema Schema() => new LayerSchema
        {
            LayerName = "pts",
            GeometryType = GeometryTarget.PointZ,
            Fields = { new FieldDefinition { Name = "asset_type", Type = FieldType.Text } }
        };

        private static RhinoObjectSnapshot Snap(long? oid, string assetType, string lastAssetValue, string lastGeomHash)
        {
            var us = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { GisKeys.SyncGuid, Guid.NewGuid().ToString() },
                { "asset_type", assetType }
            };
            if (oid.HasValue) us[GisKeys.ArcGisObjectId] = oid.Value.ToString();
            if (lastAssetValue != null) us[GisKeys.FieldHashKey("asset_type")] = Hashing.HashField(lastAssetValue);
            if (lastGeomHash != null) us[GisKeys.GeometryHash] = lastGeomHash;
            return new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.Point),
                Geometry = NeutralGeometry.Point(new Xyz(0, 0, 0)),
                UserStrings = us
            };
        }

        private static FeatureRecord Feat(long oid, string assetType) => new FeatureRecord
        {
            Identity = new SyncIdentity { ArcGisObjectId = oid },
            Geometry = NeutralGeometry.Point(new Xyz(0, 0, 0)),
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { { "asset_type", assetType } }
        };

        [Fact]
        public void New_in_rhino_creates_arcgis_feature_and_rebaselines()
        {
            var rhino = new FakeRhino { Objects = { Snap(null, "PV", null, null) } };
            var arc = new FakeArcGis { Schema = Schema() };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Single(arc.Created);
            Assert.Equal("PV", arc.Created[0].Attributes["asset_type"]);
            var guid = rhino.Objects[0].RhinoGuid;
            Assert.True(rhino.Written.ContainsKey(guid));
            Assert.Equal("1000", rhino.Written[guid][GisKeys.ArcGisObjectId]);
            Assert.Equal(arc.Features[0].Identity.ArcGisGlobalId.Value.ToString(),
                         rhino.Written[guid][GisKeys.ArcGisGlobalId]);
        }

        [Fact]
        public void Changed_write_context_after_planning_prevents_arcgis_create_and_rhino_baseline()
        {
            var rhino = new FakeRhino { Objects = { Snap(null, "PV", null, null) } };
            var arc = new FakeArcGis { Schema = Schema() };
            bool contextValid = true;
            arc.BeforeSchemaRead = () => contextValid = false;
            var service = new SyncService(arc, rhino)
            {
                ValidateWriteContext = () =>
                {
                    if (!contextValid) throw new InvalidOperationException("The active Rhino document changed.");
                }
            };
            var profile = Profile();

            Assert.Throws<InvalidOperationException>(() =>
                service.Sync(profile, profile.Layers[0], new SyncOptions()));

            Assert.Empty(arc.Created);
            Assert.Empty(arc.Features);
            Assert.Empty(rhino.Written);
        }

        [Fact]
        public void Changed_write_context_after_planning_prevents_arcgis_update_and_rhino_baseline()
        {
            var geometryHash = Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)));
            var snap = Snap(10, "edited", "PV", geometryHash);
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "PV") } };
            bool contextValid = true;
            arc.BeforeSchemaRead = () => contextValid = false;
            var profile = Profile();
            var service = new SyncService(arc, rhino)
            {
                ValidateWriteContext = () =>
                {
                    if (!contextValid) throw new InvalidOperationException("The active Rhino document changed.");
                }
            };

            Assert.Throws<InvalidOperationException>(() => service.Sync(profile, profile.Layers[0], new SyncOptions()));

            Assert.Empty(arc.Updated);
            Assert.Equal("PV", arc.Features.Single().Attributes["asset_type"]);
            Assert.Empty(rhino.Written);
            Assert.Equal(Hashing.HashField("PV"), snap.UserStrings[GisKeys.FieldHashKey("asset_type")]);
        }

        [Fact]
        public void New_in_rhino_reports_missing_non_empty_profile_value()
        {
            var snap = Snap(null, null, null, null);
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema() };
            var profile = Profile();
            profile.Layers[0].Attributes[0].Validators = new List<string> { "non_empty" };

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Single(arc.Created);
            Assert.False(arc.Created[0].Attributes.ContainsKey("asset_type"));
            Assert.Contains(report.Entries,
                entry => entry.Outcome == SyncOutcome.Warning && entry.Message.Contains("must not be empty"));
        }

        [Fact]
        public void New_mesh_in_rhino_creates_multipatch_feature()
        {
            var mesh = new NeutralMesh
            {
                Vertices = { new Xyz(0, 0, 0), new Xyz(1, 0, 0), new Xyz(1, 1, 0), new Xyz(0, 1, 0) },
                Faces = { new[] { 0, 1, 2, 3 } }
            };
            var snap = Snap(null, "PV", null, null);
            snap.Descriptor = new GeometryDescriptor(RhinoGeometryKind.Mesh);
            snap.Geometry = new NeutralGeometry { Kind = NeutralGeometryKind.Multipatch, Mesh = mesh };

            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema() };
            var profile = Profile();
            profile.Layers[0].GeometryTarget = GeometryTarget.Multipatch;

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Single(arc.Created);
            Assert.Equal(NeutralGeometryKind.Multipatch, arc.Created[0].Geometry.Kind);
            var guid = rhino.Objects[0].RhinoGuid;
            Assert.Equal("1000", rhino.Written[guid][GisKeys.ArcGisObjectId]);
        }

        [Fact]
        public void New_in_rhino_with_no_convertible_geometry_is_skipped_not_created()
        {
            // A SubD (or block instance, or hatch) has no neutral-geometry conversion yet, so the
            // adapter hands back a snapshot with Geometry == null. Pushing it must not create a
            // shapeless ArcGIS feature, and must not mark the Rhino object as synced either.
            var snap = Snap(null, "PV", null, null);
            snap.Descriptor = new GeometryDescriptor(RhinoGeometryKind.SubD);
            snap.Geometry = null;

            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema() };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Empty(arc.Created);
            Assert.Equal(1, report.CountOf(SyncOutcome.Skipped));
            Assert.False(rhino.Written.ContainsKey(rhino.Objects[0].RhinoGuid));
        }

        [Fact]
        public void New_in_rhino_baseline_adopts_what_arcgis_stored()
        {
            // The schema has a field the Rhino object never set; ArcGIS fills it with a default on
            // create. The baseline written back to Rhino must carry that default, or the next
            // preview reports the object modified in ArcGIS and any Rhino edit becomes a conflict.
            var rhino = new FakeRhino { Objects = { Snap(null, "PV", null, null) } };
            var schema = Schema();
            schema.Fields.Add(new FieldDefinition { Name = "notes", Type = FieldType.Text });
            var arc = new FakeArcGis { Schema = schema };
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping { RhinoKey = "notes", ArcGisField = "notes", Owner = FieldOwnership.Shared });

            new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            var written = rhino.Written[rhino.Objects[0].RhinoGuid];
            Assert.Equal(" ", written["notes"]);
            Assert.Equal(Hashing.HashField(" "), written[GisKeys.FieldHashKey("notes")]);
            Assert.Equal("PV", written["asset_type"]);

            // Second pass with the baseline in place: nothing to do.
            var snap = rhino.Objects[0];
            foreach (var kv in written) snap.UserStrings[kv.Key] = kv.Value;
            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.All(preview.Entries, e => Assert.Equal(SyncState.Clean.ToString(), e.Message));
        }

        [Fact]
        public void Rejected_authored_field_keeps_its_value_and_baseline_while_other_changes_sync()
        {
            var original = NeutralGeometry.Point(new Xyz(0, 0, 0));
            var changed = NeutralGeometry.Point(new Xyz(3, 4, 5));
            var snap = Snap(10, "new type", "old type", Hashing.HashGeometry(original));
            snap.Geometry = changed;
            snap.UserStrings["status"] = "";
            snap.UserStrings[GisKeys.FieldHashKey("status")] = Hashing.HashField("active");
            snap.UserStrings[GisKeys.AttributeHash] = "previous aggregate hash";

            var feature = Feat(10, "old type");
            feature.Attributes["status"] = "active";
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { feature } };
            arc.Schema.Fields.Add(new FieldDefinition { Name = "status", Type = FieldType.Text });
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "status", ArcGisField = "status", Owner = FieldOwnership.Shared,
                Validators = new List<string> { "non_empty" }
            });

            var applied = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Single(arc.Updated);
            Assert.Equal("new type", arc.Features[0].Attributes["asset_type"]);
            Assert.Equal("active", arc.Features[0].Attributes["status"]);
            Assert.Equal(changed.Points[0].X, arc.Features[0].Geometry.Points[0].X);
            Assert.Equal("", snap.UserStrings["status"]);
            Assert.Equal(Hashing.HashField("active"), snap.UserStrings[GisKeys.FieldHashKey("status")]);
            Assert.DoesNotContain("status", rhino.Written[snap.RhinoGuid].Keys);
            Assert.DoesNotContain(GisKeys.FieldHashKey("status"), rhino.Written[snap.RhinoGuid].Keys);
            Assert.DoesNotContain(GisKeys.AttributeHash, rhino.Written[snap.RhinoGuid].Keys);
            Assert.Equal("previous aggregate hash", snap.UserStrings[GisKeys.AttributeHash]);
            Assert.Contains(applied.Entries, entry => entry.Outcome == SyncOutcome.Warning &&
                entry.Message.Contains("status") && entry.Message.Contains("must not be empty"));

            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.Single(preview.Entries);
            Assert.Contains("status", preview.Entries[0].Detail);
            Assert.Equal(SyncState.ModifiedInRhino.ToString(), preview.Entries[0].Message);
        }

        [Fact]
        public void Rejected_domain_and_type_values_keep_their_previous_field_baselines()
        {
            var snap = Snap(10, "PV", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0))));
            snap.UserStrings["category"] = "unsupported";
            snap.UserStrings[GisKeys.FieldHashKey("category")] = Hashing.HashField("solar");
            snap.UserStrings["rank"] = "many";
            snap.UserStrings[GisKeys.FieldHashKey("rank")] = Hashing.HashField("1");
            var feature = Feat(10, "PV");
            feature.Attributes["category"] = "solar";
            feature.Attributes["rank"] = "1";

            var schema = Schema();
            schema.Fields.Add(new FieldDefinition { Name = "category", Type = FieldType.Text });
            schema.Fields.Add(new FieldDefinition { Name = "rank", Type = FieldType.Integer });
            var arc = new FakeArcGis { Schema = schema, Features = { feature } };
            var rhino = new FakeRhino { Objects = { snap } };
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "category", ArcGisField = "category", Owner = FieldOwnership.Shared,
                Domain = new List<string> { "solar", "wind" }
            });
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "rank", ArcGisField = "rank", Owner = FieldOwnership.Shared,
                Type = FieldType.Integer
            });

            var applied = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Equal("solar", arc.Features[0].Attributes["category"]);
            Assert.Equal("1", arc.Features[0].Attributes["rank"]);
            Assert.Equal("unsupported", snap.UserStrings["category"]);
            Assert.Equal("many", snap.UserStrings["rank"]);
            Assert.Equal(Hashing.HashField("solar"), snap.UserStrings[GisKeys.FieldHashKey("category")]);
            Assert.Equal(Hashing.HashField("1"), snap.UserStrings[GisKeys.FieldHashKey("rank")]);
            Assert.Equal(2, applied.CountOf(SyncOutcome.Warning));

            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.NotEqual(SyncState.Clean.ToString(), preview.Entries.Single().Message);
            Assert.Contains("category", preview.Entries.Single().Detail);
            Assert.Contains("rank", preview.Entries.Single().Detail);
        }

        [Fact]
        public void Rejected_field_baseline_survives_an_arcgis_geometry_replacement()
        {
            var original = NeutralGeometry.Point(new Xyz(0, 0, 0));
            var arcgisGeometry = NeutralGeometry.Point(new Xyz(7, 8, 9));
            var snap = Snap(10, "PV", "PV", Hashing.HashGeometry(original));
            snap.UserStrings["status"] = "";
            snap.UserStrings[GisKeys.FieldHashKey("status")] = Hashing.HashField("active");
            snap.UserStrings[GisKeys.AttributeHash] = "previous aggregate hash";
            var feature = Feat(10, "PV");
            feature.Geometry = arcgisGeometry;
            feature.Attributes["status"] = "active";

            var schema = Schema();
            schema.Fields.Add(new FieldDefinition { Name = "status", Type = FieldType.Text });
            var arc = new FakeArcGis { Schema = schema, Features = { feature } };
            var rhino = new FakeRhino { Objects = { snap } };
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "status", ArcGisField = "status", Owner = FieldOwnership.Shared,
                Validators = new List<string> { "non_empty" }
            });

            var applied = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Equal(7, snap.Geometry.Points[0].X);
            Assert.Equal("", snap.UserStrings["status"]);
            Assert.Equal(Hashing.HashField("active"), snap.UserStrings[GisKeys.FieldHashKey("status")]);
            Assert.DoesNotContain("status", rhino.Written[snap.RhinoGuid].Keys);
            Assert.DoesNotContain(GisKeys.FieldHashKey("status"), rhino.Written[snap.RhinoGuid].Keys);
            Assert.DoesNotContain(GisKeys.AttributeHash, rhino.Written[snap.RhinoGuid].Keys);
            Assert.Equal("previous aggregate hash", snap.UserStrings[GisKeys.AttributeHash]);
            Assert.Contains(applied.Entries, entry => entry.Outcome == SyncOutcome.Warning &&
                entry.Message.Contains("must not be empty"));

            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.Equal(SyncState.ModifiedInRhino.ToString(), preview.Entries.Single().Message);
            Assert.Contains("status", preview.Entries.Single().Detail);
        }

        [Fact]
        public void New_rhino_object_keeps_an_invalid_domain_value_unresolved()
        {
            var snap = Snap(null, "PV", null, null);
            snap.UserStrings["category"] = "unsupported";
            snap.UserStrings[GisKeys.FieldHashKey("category")] = Hashing.HashField("solar");
            var schema = Schema();
            schema.Fields.Add(new FieldDefinition { Name = "category", Type = FieldType.Text });
            var arc = new FakeArcGis { Schema = schema };
            var rhino = new FakeRhino { Objects = { snap } };
            var profile = Profile();
            profile.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "category", ArcGisField = "category", Owner = FieldOwnership.Shared,
                Domain = new List<string> { "solar", "wind" }
            });

            var applied = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Single(arc.Created);
            Assert.DoesNotContain("category", arc.Created[0].Attributes.Keys);
            Assert.Equal("unsupported", snap.UserStrings["category"]);
            Assert.Equal(Hashing.HashField("solar"), snap.UserStrings[GisKeys.FieldHashKey("category")]);
            Assert.DoesNotContain("category", rhino.Written[snap.RhinoGuid].Keys);
            Assert.DoesNotContain(GisKeys.FieldHashKey("category"), rhino.Written[snap.RhinoGuid].Keys);
            Assert.Contains(applied.Entries, entry => entry.Outcome == SyncOutcome.Warning &&
                entry.Message.Contains("category") && entry.Message.Contains("domain"));

            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.NotEqual(SyncState.Clean.ToString(), preview.Entries.Single().Message);
            Assert.Contains("category", preview.Entries.Single().Detail);
        }

        [Fact]
        public void Pull_only_never_writes_to_arcgis_and_holds_rhino_changes()
        {
            var rhino = new FakeRhino { Objects = { Snap(null, "PV", null, null) } };            // new in Rhino
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(20, "A") } };       // new in ArcGIS
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PullOnly });

            Assert.Empty(arc.Created);
            Assert.Empty(arc.Updated);
            Assert.Single(rhino.CreatedGeometry);                                    // the ArcGIS feature came in
            Assert.Equal(1, report.CountOf(SyncOutcome.Held));                       // the Rhino object is held
            Assert.DoesNotContain(rhino.Written.Keys, k => k == rhino.Objects[0].RhinoGuid); // and not rebaselined
        }

        [Fact]
        public void Pull_only_still_pulls_an_arcgis_side_edit()
        {
            var clean = Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)));
            var rhino = new FakeRhino { Objects = { Snap(10, "PV", "PV", clean) } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "A") } };   // edited in ArcGIS
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PullOnly });

            Assert.Empty(arc.Updated);
            Assert.Equal("A", rhino.Written[rhino.Objects[0].RhinoGuid]["asset_type"]);
            Assert.Equal(1, report.CountOf(SyncOutcome.Updated));
            Assert.Equal(0, report.CountOf(SyncOutcome.Held));
        }

        [Fact]
        public void Pull_only_replaces_arcgis_changed_geometry_without_writing_to_arcgis_and_repreviews_clean()
        {
            var original = NeutralGeometry.Point(new Xyz(0, 0, 0));
            var changed = NeutralGeometry.Point(new Xyz(4, 5, 6));
            var snap = Snap(10, "PV", "PV", Hashing.HashGeometry(original));
            var feature = Feat(10, "PV");
            feature.Geometry = changed;
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { feature } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PullOnly });

            Assert.Empty(arc.Created);
            Assert.Empty(arc.Updated);
            Assert.Equal(4, rhino.ReplacedGeometry[snap.RhinoGuid].Points[0].X);
            Assert.Equal(5, rhino.ReplacedGeometry[snap.RhinoGuid].Points[0].Y);
            Assert.Equal(6, rhino.Objects[0].Geometry.Points[0].Z);
            Assert.Equal(Hashing.HashGeometry(changed), rhino.Written[snap.RhinoGuid][GisKeys.GeometryHash]);
            Assert.Equal(1, report.CountOf(SyncOutcome.Updated));

            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.Single(preview.Entries);
            Assert.Equal(SyncState.Clean.ToString(), preview.Entries[0].Message);
        }

        [Fact]
        public void Two_way_replaces_arcgis_changed_geometry_on_the_existing_rhino_object()
        {
            var original = NeutralGeometry.Point(new Xyz(0, 0, 0));
            var changed = NeutralGeometry.Point(new Xyz(8, 3, 2));
            var snap = Snap(10, "PV", "PV", Hashing.HashGeometry(original));
            var feature = Feat(10, "PV");
            feature.Geometry = changed;
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { feature } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Empty(arc.Updated);
            Assert.True(rhino.ReplacedGeometry.ContainsKey(snap.RhinoGuid));
            Assert.Equal(snap.RhinoGuid, rhino.Objects[0].RhinoGuid);
            Assert.Equal(8, rhino.Objects[0].Geometry.Points[0].X);
            Assert.Equal(1, report.CountOf(SyncOutcome.Updated));
        }

        [Fact]
        public void Failed_geometry_replacement_is_reported_and_does_not_advance_the_baseline()
        {
            var original = NeutralGeometry.Point(new Xyz(0, 0, 0));
            var changed = NeutralGeometry.Point(new Xyz(9, 9, 9));
            var baseline = Hashing.HashGeometry(original);
            var snap = Snap(10, "PV", "PV", baseline);
            var feature = Feat(10, "PV");
            feature.Geometry = changed;
            var rhino = new FakeRhino { FailReplacement = true, Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { feature } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PullOnly });

            Assert.Empty(arc.Updated);
            Assert.Empty(rhino.ReplacedGeometry);
            Assert.Empty(rhino.Written);
            Assert.Equal(baseline, snap.UserStrings[GisKeys.GeometryHash]);
            Assert.Contains(snap.RhinoGuid, rhino.Isolated);
            Assert.Equal(1, report.CountOf(SyncOutcome.Failed));

            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.Equal(SyncState.ModifiedInArcGis.ToString(), preview.Entries.Single().Message);
        }

        [Fact]
        public void Pull_only_holds_a_rhino_side_edit_without_rebaselining()
        {
            var clean = Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)));
            var rhino = new FakeRhino { Objects = { Snap(10, "R", "PV", clean) } };        // edited in Rhino
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "PV") } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PullOnly });

            Assert.Empty(arc.Updated);
            Assert.Empty(rhino.Written);
            Assert.Equal(1, report.CountOf(SyncOutcome.Held));

            // Still a Rhino change on the next look, because nothing rebaselined it away.
            var again = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.Equal(1, again.CountOf(SyncOutcome.Updated));
        }

        [Fact]
        public void Push_only_still_pushes_a_rhino_side_edit()
        {
            var clean = Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)));
            var rhino = new FakeRhino { Objects = { Snap(10, "R", "PV", clean) } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "PV") } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PushOnly });

            Assert.Single(arc.Updated);
            Assert.Equal("R", arc.Updated[0].Attributes["asset_type"]);
            Assert.Equal(1, report.CountOf(SyncOutcome.Updated));
            Assert.Equal(0, report.CountOf(SyncOutcome.Held));
        }

        [Fact]
        public void Push_only_holds_an_arcgis_side_edit_without_rebaselining()
        {
            var clean = Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)));
            var rhino = new FakeRhino { Objects = { Snap(10, "PV", "PV", clean) } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "A") } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PushOnly });

            Assert.Empty(arc.Updated);
            Assert.Empty(rhino.Written);
            Assert.Equal(1, report.CountOf(SyncOutcome.Held));
        }

        [Fact]
        public void Derived_metric_columns_are_computed_from_the_pushed_geometry()
        {
            // A shapefile's Shape_Leng / Shape_Area are plain columns; a feature pushed from Rhino
            // gets them from the geometry that is going out, not from anything Rhino holds.
            var rect = new List<Xyz> { new Xyz(0, 0, 0), new Xyz(30, 0, 0), new Xyz(30, 20, 0), new Xyz(0, 20, 0) };
            var snap = Snap(null, "PV", null, null);
            snap.Descriptor = new GeometryDescriptor(RhinoGeometryKind.ClosedPlanarCurve);
            snap.Geometry = new NeutralGeometry { Kind = NeutralGeometryKind.Polygon, Rings = { rect } };
            snap.UserStrings["Shape_Area"] = "1";                    // a stale Rhino copy is ignored

            var schema = Schema();
            schema.GeometryType = GeometryTarget.PolygonZ;
            schema.Fields.Add(new FieldDefinition { Name = "Shape_Area", Type = FieldType.Double });
            schema.Fields.Add(new FieldDefinition { Name = "Shape_Leng", Type = FieldType.Double });
            var profile = Profile();
            profile.Layers[0].GeometryTarget = GeometryTarget.PolygonZ;
            profile.Layers[0].Attributes.Add(new FieldMapping { RhinoKey = "Shape_Area", ArcGisField = "Shape_Area", Type = FieldType.Double, Owner = FieldOwnership.Derived });
            profile.Layers[0].Attributes.Add(new FieldMapping { RhinoKey = "Shape_Leng", ArcGisField = "Shape_Leng", Type = FieldType.Double, Owner = FieldOwnership.Derived });

            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = schema };
            new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Single(arc.Created);
            Assert.Equal("600", arc.Created[0].Attributes["Shape_Area"]);
            Assert.Equal("100", arc.Created[0].Attributes["Shape_Leng"]);

            // And the Rhino baseline adopts what ArcGIS stored, so Rhino shows the real metric.
            Assert.Equal("600", rhino.Written[snap.RhinoGuid]["Shape_Area"]);
        }

        [Fact]
        public void A_conflict_is_held_as_a_conflict_whatever_the_direction()
        {
            var clean = Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)));
            foreach (var direction in new[] { SyncDirectionMode.PullOnly, SyncDirectionMode.PushOnly })
            {
                var rhino = new FakeRhino { Objects = { Snap(10, "R", "PV", clean) } };
                var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "A") } };
                var profile = Profile();

                var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                    new SyncOptions { Direction = direction, Conflicts = ConflictResolution.Manual });

                Assert.Empty(arc.Updated);
                Assert.Empty(rhino.Written);
                Assert.Equal(1, report.CountOf(SyncOutcome.Conflict));
                Assert.Equal(0, report.CountOf(SyncOutcome.Held));
            }
        }

        [Fact]
        public void Manual_geometry_conflict_is_held_without_replacing_or_writing_either_side()
        {
            var baselineGeometry = NeutralGeometry.Point(new Xyz(0, 0, 0));
            var snap = Snap(10, "PV", "PV", Hashing.HashGeometry(baselineGeometry));
            snap.Geometry = NeutralGeometry.Point(new Xyz(1, 0, 0)); // changed in Rhino
            var feature = Feat(10, "PV");
            feature.Geometry = NeutralGeometry.Point(new Xyz(2, 0, 0)); // changed in ArcGIS
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { feature } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Conflicts = ConflictResolution.Manual });

            Assert.Empty(arc.Created);
            Assert.Empty(arc.Updated);
            Assert.Empty(rhino.ReplacedGeometry);
            Assert.Empty(rhino.Written);
            Assert.Equal(1, report.CountOf(SyncOutcome.Conflict));
        }

        [Fact]
        public void Push_only_never_creates_in_rhino_and_holds_arcgis_changes()
        {
            var rhino = new FakeRhino { Objects = { Snap(null, "PV", null, null) } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(20, "A") } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PushOnly });

            Assert.Single(arc.Created);
            Assert.Empty(rhino.CreatedGeometry);
            Assert.Equal(1, report.CountOf(SyncOutcome.Held));
        }

        [Fact]
        public void Deleted_in_arcgis_is_held_not_recreated()
        {
            var rhino = new FakeRhino { Objects = { Snap(10, "PV", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)))) } };
            var arc = new FakeArcGis { Schema = Schema() };   // feature 10 is gone
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());

            Assert.Empty(arc.Created);
            Assert.Empty(rhino.ReplacedGeometry);
            Assert.Empty(rhino.Written);
            Assert.Equal(1, report.CountOf(SyncOutcome.Conflict));
            Assert.Contains("no longer exists", report.Entries[0].Message);
        }

        [Fact]
        public void Changed_source_is_warned_about_then_adopted_on_apply()
        {
            var snap = Snap(10, "PV", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0))));
            snap.UserStrings[GisKeys.ArcGisSource] = @"C:\data\original\pts.shp";
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "PV") } };
            var profile = Profile();
            profile.Layers[0].ArcGisSource = @"C:\data\other-copy\pts.shp";

            // Preview: one layer-level warning naming both, and the comparison still runs.
            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            var warning = preview.Entries.Single(e => e.Outcome == SyncOutcome.Warning);
            Assert.Equal(Guid.Empty, warning.SyncGuid);
            Assert.Contains("original", warning.Message);
            Assert.Contains("other-copy", warning.Message);
            Assert.Contains(preview.Entries, e => e.Message == SyncState.Clean.ToString());
            Assert.Empty(rhino.Written);

            // Apply accepts the new source: it is recorded on the object, so the warning clears.
            new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions());
            // Source acceptance and baseline repair can issue separate writes for one object;
            // assert the resulting user string rather than only the final adapter call payload.
            Assert.Equal(@"C:\data\other-copy\pts.shp", snap.UserStrings[GisKeys.ArcGisSource]);
            var again = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);
            Assert.DoesNotContain(again.Entries, e => e.Outcome == SyncOutcome.Warning);

            // Same source (any case), or no record at all: no warning.
            profile.Layers[0].ArcGisSource = @"c:\DATA\OTHER-COPY\pts.shp";
            Assert.DoesNotContain(new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview).Entries,
                e => e.Outcome == SyncOutcome.Warning);
        }

        [Fact]
        public void Changed_source_is_not_adopted_for_a_manual_conflict()
        {
            var snap = Snap(10, "Rhino edit", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0))));
            snap.UserStrings[GisKeys.ArcGisSource] = @"C:\data\original\pts.shp";
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "ArcGIS edit") } };
            var profile = Profile();
            profile.Layers[0].ArcGisSource = @"C:\data\other-copy\pts.shp";

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Conflicts = ConflictResolution.Manual });

            Assert.Equal(@"C:\data\original\pts.shp", snap.UserStrings[GisKeys.ArcGisSource]);
            Assert.Empty(rhino.Written);
            Assert.Empty(arc.Updated);
            Assert.Contains(report.Entries, entry => entry.Outcome == SyncOutcome.Conflict);
            Assert.Contains(report.Entries, entry => entry.Outcome == SyncOutcome.Warning &&
                entry.Message.Contains("Source changed"));
        }

        [Fact]
        public void Changed_source_is_not_adopted_for_a_one_way_held_edit()
        {
            var snap = Snap(10, "Rhino edit", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0))));
            snap.UserStrings[GisKeys.ArcGisSource] = @"C:\data\original\pts.shp";
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "PV") } };
            var profile = Profile();
            profile.Layers[0].ArcGisSource = @"C:\data\other-copy\pts.shp";

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Direction = SyncDirectionMode.PullOnly });

            Assert.Equal(@"C:\data\original\pts.shp", snap.UserStrings[GisKeys.ArcGisSource]);
            Assert.Empty(rhino.Written);
            Assert.Empty(arc.Updated);
            Assert.Contains(report.Entries, entry => entry.Outcome == SyncOutcome.Held);
            Assert.Contains(report.Entries, entry => entry.Outcome == SyncOutcome.Warning &&
                entry.Message.Contains("Source changed"));
        }

        [Fact]
        public void Changed_source_is_not_adopted_when_arcgis_update_fails()
        {
            var snap = Snap(10, "Rhino edit", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0))));
            snap.UserStrings[GisKeys.ArcGisSource] = @"C:\data\original\pts.shp";
            var rhino = new FakeRhino { Objects = { snap } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "PV") }, FailUpdate = true };
            var profile = Profile();
            profile.Layers[0].ArcGisSource = @"C:\data\other-copy\pts.shp";

            Assert.Throws<InvalidOperationException>(() =>
                new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions()));

            Assert.Equal(@"C:\data\original\pts.shp", snap.UserStrings[GisKeys.ArcGisSource]);
            Assert.Empty(rhino.Written);
            Assert.Empty(arc.Updated);
        }

        [Fact]
        public void Conflict_in_manual_mode_is_held_and_nothing_is_written()
        {
            var rhino = new FakeRhino { Objects = { Snap(10, "R", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)))) } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "A") } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Conflicts = ConflictResolution.Manual });

            Assert.Empty(arc.Created);
            Assert.Empty(arc.Updated);
            Assert.Empty(rhino.Written);
            Assert.Equal(1, report.CountOf(SyncOutcome.Conflict));
        }

        [Fact]
        public void Conflict_prefer_arcgis_pulls_value_into_rhino()
        {
            var rhino = new FakeRhino { Objects = { Snap(10, "R", "PV", Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(0, 0, 0)))) } };
            var arc = new FakeArcGis { Schema = Schema(), Features = { Feat(10, "A") } };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0],
                new SyncOptions { Conflicts = ConflictResolution.PreferArcGis });

            Assert.Empty(arc.Updated);
            var guid = rhino.Objects[0].RhinoGuid;
            Assert.Equal("A", rhino.Written[guid]["asset_type"]);
            Assert.Equal(1, report.CountOf(SyncOutcome.Updated));
        }

        [Fact]
        public void Preview_mode_applies_nothing()
        {
            var rhino = new FakeRhino { Objects = { Snap(null, "PV", null, null) } };
            var arc = new FakeArcGis { Schema = Schema() };
            var profile = Profile();

            var report = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], SyncOptions.Preview);

            Assert.Empty(arc.Created);
            Assert.Empty(rhino.Written);
            Assert.Equal(1, report.CountOf(SyncOutcome.Created)); // reported as a plan entry, not applied
        }

        // ---- fakes ----

        private sealed class FakeArcGis : IArcGISAdapter
        {
            public LayerSchema Schema;
            public List<FeatureRecord> Features { get; } = new List<FeatureRecord>();
            public List<FeatureRecord> Created { get; } = new List<FeatureRecord>();
            public List<FeatureRecord> Updated { get; } = new List<FeatureRecord>();
            public bool FailUpdate { get; set; }
            public Action BeforeSchemaRead { get; set; }
            public LayerSchema GetSchema(string layerName)
            {
                BeforeSchemaRead?.Invoke();
                return Schema;
            }
            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) => Features;

            /// <summary>
            /// Behaves like a store: a created feature gets an object id and becomes readable, with
            /// the schema's other fields at a default, the way a shapefile fills in blanks.
            /// </summary>
            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                var oids = new List<long>(); long n = 1000 + Features.Count;
                foreach (var f in features)
                {
                    Created.Add(f);
                    var stored = new FeatureRecord
                    {
                        Identity = new SyncIdentity { ArcGisObjectId = n, ArcGisGlobalId = Guid.NewGuid() },
                        Geometry = f.Geometry,
                        Attributes = new Dictionary<string, string>(f.Attributes, StringComparer.Ordinal)
                    };
                    if (Schema != null)
                        foreach (var field in Schema.Fields)
                            if (!stored.Attributes.ContainsKey(field.Name)) stored.Attributes[field.Name] = " ";
                    Features.Add(stored);
                    oids.Add(n++);
                }
                return oids;
            }
            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                if (FailUpdate) throw new InvalidOperationException("Update rejected by test adapter.");
                Updated.AddRange(features);
                foreach (var update in features)
                {
                    var stored = Features.FirstOrDefault(f => f.Identity?.ArcGisObjectId == update.Identity?.ArcGisObjectId);
                    if (stored == null) continue;
                    if (update.Geometry != null) stored.Geometry = update.Geometry;
                    if (update.Attributes != null)
                        foreach (var field in update.Attributes) stored.Attributes[field.Key] = field.Value;
                }
            }
            public void RefreshScene() { }

            public IReadOnlyList<string> GetLayerNames() => throw new NotImplementedException();
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => throw new NotImplementedException();
        }

        private sealed class FakeRhino : IRhinoAdapter
        {
            public List<RhinoObjectSnapshot> Objects { get; } = new List<RhinoObjectSnapshot>();
            public Dictionary<Guid, IReadOnlyDictionary<string, string>> Written { get; } =
                new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
            public List<NeutralGeometry> CreatedGeometry { get; } = new List<NeutralGeometry>();
            public Dictionary<Guid, NeutralGeometry> ReplacedGeometry { get; } =
                new Dictionary<Guid, NeutralGeometry>();
            public List<Guid> Isolated { get; } = new List<Guid>();
            public bool FailReplacement { get; set; }

            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName) => Objects;
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => Objects;
            public RhinoObjectSnapshot ReadObject(Guid rhinoGuid) =>
                Objects.FirstOrDefault(o => o.RhinoGuid == rhinoGuid);
            public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings)
            {
                Written[rhinoGuid] = userStrings;
                var snap = ReadObject(rhinoGuid);
                if (snap != null)
                    foreach (var kv in userStrings) snap.UserStrings[kv.Key] = kv.Value;
            }
            public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings)
            { CreatedGeometry.Add(geometry); return Guid.NewGuid(); }
            public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry)
            {
                if (FailReplacement) throw new InvalidOperationException("Replacement rejected by test adapter.");
                var snap = ReadObject(rhinoGuid)
                    ?? throw new InvalidOperationException($"Object {rhinoGuid} was not found.");
                snap.Geometry = geometry;
                ReplacedGeometry[rhinoGuid] = geometry;
            }

            public UnitSystem GetUnits() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => throw new NotImplementedException();
            public void IsolateFailed(IEnumerable<Guid> rhinoGuids) => Isolated.AddRange(rhinoGuids);
        }
    }
}
