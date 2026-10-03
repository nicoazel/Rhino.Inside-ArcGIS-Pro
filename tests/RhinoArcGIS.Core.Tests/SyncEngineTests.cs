using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class SyncEngineTests
    {
        private static LayerMappingProfile Profile()
        {
            return new LayerMappingProfile
            {
                RhinoUnits = UnitSystem.Meters,
                ArcGisCrs = "EPSG:3857",
                ProjectAnchor = new AnchorSettings(), // identity → Rhino == GIS
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

        private static string GeomHash(double x, double y) => Hashing.HashGeometry(NeutralGeometry.Point(new Xyz(x, y, 0)));

        private static RhinoObjectSnapshot Snap(long? oid, string assetType, double x, double y,
            string lastAssetValue, string lastGeomHash)
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
                Geometry = NeutralGeometry.Point(new Xyz(x, y, 0)),
                UserStrings = us
            };
        }

        private static FeatureRecord Feat(long oid, string assetType, double x, double y)
        {
            return new FeatureRecord
            {
                Identity = new SyncIdentity { ArcGisObjectId = oid },
                Geometry = NeutralGeometry.Point(new Xyz(x, y, 0)),
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { { "asset_type", assetType } }
            };
        }

        private static ObjectDecision Single(LayerMappingProfile p, IReadOnlyList<RhinoObjectSnapshot> r, IReadOnlyList<FeatureRecord> a)
        {
            ICoordinateMap map = new CoordinateTransform(p.ProjectAnchor.ToProjectAnchor());
            return new SyncEngine().BuildPlan(map, p.Layers[0], r, a).Decisions.Single();
        }

        [Fact]
        public void Object_only_in_rhino_is_new_in_rhino()
        {
            var p = Profile();
            var d = Single(p, new[] { Snap(null, "PV", 0, 0, null, null) }, new FeatureRecord[0]);
            Assert.Equal(SyncState.NewInRhino, d.State);
            Assert.Equal(SyncDirection.Push, d.Fields.Single().Direction);
        }

        [Fact]
        public void Object_only_in_arcgis_is_new_in_arcgis()
        {
            var p = Profile();
            var d = Single(p, new RhinoObjectSnapshot[0], new[] { Feat(10, "PV", 0, 0) });
            Assert.Equal(SyncState.NewInArcGis, d.State);
            Assert.Equal(SyncDirection.Pull, d.Fields.Single().Direction);
        }

        [Fact]
        public void Object_whose_feature_is_gone_is_deleted_in_arcgis_not_new()
        {
            // The Rhino object still carries the ArcGIS identity it was pulled with; the feature has
            // been deleted. Calling that "new in Rhino" would recreate it on apply.
            var p = Profile();
            var d = Single(p, new[] { Snap(10, "PV", 0, 0, "PV", GeomHash(0, 0)) }, new FeatureRecord[0]);
            Assert.Equal(SyncState.DeletedInArcGis, d.State);
            Assert.Equal(10, d.ArcGisObjectId);
        }

        [Fact]
        public void Global_id_wins_over_object_id_when_the_layer_has_one()
        {
            // A shapefile renumbers FIDs after a delete: the object recorded oid 10, but the feature
            // it came from is now oid 7 and the feature at oid 10 is a different one. GlobalIDs stay.
            var p = Profile();
            var mine = Guid.NewGuid();
            var snap = Snap(10, "PV", 0, 0, "PV", GeomHash(0, 0));
            snap.UserStrings[GisKeys.ArcGisGlobalId] = mine.ToString();

            var moved = Feat(7, "PV", 0, 0);   moved.Identity.ArcGisGlobalId = mine;
            var other = Feat(10, "XX", 9, 9);  other.Identity.ArcGisGlobalId = Guid.NewGuid();

            var plan = new SyncEngine().BuildPlan(new CoordinateTransform(p.ProjectAnchor.ToProjectAnchor()),
                p.Layers[0], new[] { snap }, new[] { moved, other });

            var matched = plan.Decisions.Single(x => x.RhinoGuid == snap.RhinoGuid);
            Assert.Equal(7, matched.ArcGisObjectId);
            Assert.Equal(SyncState.Clean, matched.State);
            Assert.Equal(SyncState.NewInArcGis, plan.Decisions.Single(x => x.RhinoGuid == null).State);
        }

        [Fact]
        public void Global_id_recorded_but_feature_lost_is_deleted_even_if_oid_reused()
        {
            var p = Profile();
            var snap = Snap(10, "PV", 0, 0, "PV", GeomHash(0, 0));
            snap.UserStrings[GisKeys.ArcGisGlobalId] = Guid.NewGuid().ToString();
            var reused = Feat(10, "XX", 9, 9); reused.Identity.ArcGisGlobalId = Guid.NewGuid();

            var plan = new SyncEngine().BuildPlan(new CoordinateTransform(p.ProjectAnchor.ToProjectAnchor()),
                p.Layers[0], new[] { snap }, new[] { reused });

            Assert.Equal(SyncState.DeletedInArcGis, plan.Decisions.Single(x => x.RhinoGuid == snap.RhinoGuid).State);
            Assert.Equal(SyncState.NewInArcGis, plan.Decisions.Single(x => x.RhinoGuid == null).State);
        }

        [Fact]
        public void Matched_unchanged_is_clean()
        {
            var p = Profile();
            var snap = Snap(10, "PV", 0, 0, "PV", GeomHash(0, 0));
            var d = Single(p, new[] { snap }, new[] { Feat(10, "PV", 0, 0) });
            Assert.Equal(SyncState.Clean, d.State);
        }

        [Fact]
        public void Changed_in_rhino_pushes()
        {
            var p = Profile();
            var snap = Snap(10, "NEW", 0, 0, "PV", GeomHash(0, 0));
            var d = Single(p, new[] { snap }, new[] { Feat(10, "PV", 0, 0) });
            Assert.Equal(SyncState.ModifiedInRhino, d.State);
            Assert.Equal(SyncDirection.Push, d.Fields.Single().Direction);
        }

        [Fact]
        public void Changed_in_arcgis_pulls()
        {
            var p = Profile();
            var snap = Snap(10, "PV", 0, 0, "PV", GeomHash(0, 0));
            var d = Single(p, new[] { snap }, new[] { Feat(10, "APPROVED", 0, 0) });
            Assert.Equal(SyncState.ModifiedInArcGis, d.State);
            Assert.Equal(SyncDirection.Pull, d.Fields.Single().Direction);
        }

        [Fact]
        public void Shared_changed_in_both_is_conflict()
        {
            var p = Profile();
            var snap = Snap(10, "R", 0, 0, "PV", GeomHash(0, 0));
            var d = Single(p, new[] { snap }, new[] { Feat(10, "A", 0, 0) });
            Assert.Equal(SyncState.Conflict, d.State);
            Assert.True(d.IsConflict);
        }

        [Fact]
        public void Geometry_changed_in_both_is_conflict()
        {
            var p = Profile();
            // baseline geom hash is (0,0); rhino now (5,5); arcgis now (9,9)
            var snap = Snap(10, "PV", 5, 5, "PV", GeomHash(0, 0));
            var d = Single(p, new[] { snap }, new[] { Feat(10, "PV", 9, 9) });
            Assert.Equal(ChangeSide.Both, d.GeometryChange);
            Assert.Equal(SyncState.Conflict, d.State);
        }

        [Fact]
        public void Geometry_changed_in_rhino_only_modifies_in_rhino()
        {
            var p = Profile();
            var snap = Snap(10, "PV", 5, 5, "PV", GeomHash(0, 0));
            var d = Single(p, new[] { snap }, new[] { Feat(10, "PV", 0, 0) });
            Assert.Equal(ChangeSide.Rhino, d.GeometryChange);
            Assert.Equal(SyncState.ModifiedInRhino, d.State);
        }

        [Fact]
        public void Rhino_side_baseline_lets_a_simplified_copy_read_as_clean()
        {
            // Rhino holds the feature at (5,5) -- its own, simplified representation -- while ArcGIS
            // holds (0,0). Each side matches its own baseline, so nothing has changed.
            var p = Profile();
            var snap = Snap(10, "PV", 5, 5, "PV", GeomHash(0, 0));
            snap.UserStrings[GisKeys.RhinoGeometryHash] = GeomHash(5, 5);
            var d = Single(p, new[] { snap }, new[] { Feat(10, "PV", 0, 0) });
            Assert.Equal(ChangeSide.None, d.GeometryChange);
            Assert.Equal(SyncState.Clean, d.State);

            // Moving the Rhino object away from its own baseline is still a Rhino edit.
            var moved = Snap(10, "PV", 6, 6, "PV", GeomHash(0, 0));
            moved.UserStrings[GisKeys.RhinoGeometryHash] = GeomHash(5, 5);
            var d2 = Single(p, new[] { moved }, new[] { Feat(10, "PV", 0, 0) });
            Assert.Equal(ChangeSide.Rhino, d2.GeometryChange);

            // And ArcGIS moving away from its baseline is an ArcGIS edit.
            var d3 = Single(p, new[] { snap }, new[] { Feat(10, "PV", 1, 1) });
            Assert.Equal(ChangeSide.ArcGis, d3.GeometryChange);
        }

        [Fact]
        public void Derived_metric_rounding_noise_is_not_an_independent_arcgis_edit()
        {
            var p = Profile();
            p.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "Shape_Area", ArcGisField = "Shape_Area", Owner = FieldOwnership.Derived
            });
            var snap = Snap(10, "PV", 0, 0, "PV", GeomHash(0, 0));
            snap.UserStrings["Shape_Area"] = "6458.34619140625";
            snap.UserStrings[GisKeys.FieldHashKey("Shape_Area")] = Hashing.HashField("6458.34619140625");

            var tinyStorageChange = Feat(10, "PV", 0, 0);
            tinyStorageChange.Attributes["Shape_Area"] = "6458.3463134765625";
            var clean = Single(p, new[] { snap }, new[] { tinyStorageChange });
            Assert.Equal(SyncState.Clean, clean.State);
            Assert.Equal(FieldChangeState.Unchanged, clean.Fields.Single(f => f.RhinoKey == "Shape_Area").State);

            var realChange = Feat(10, "PV", 0, 0);
            realChange.Attributes["Shape_Area"] = "6460";
            Assert.Equal(SyncState.ModifiedInArcGis, Single(p, new[] { snap }, new[] { realChange }).State);
        }

        // ---- ResolveField table ----

        [Theory]
        [InlineData(FieldOwnership.Shared, FieldChangeState.ChangedInRhino, SyncDirection.Push)]
        [InlineData(FieldOwnership.Shared, FieldChangeState.ChangedInArcGis, SyncDirection.Pull)]
        [InlineData(FieldOwnership.Shared, FieldChangeState.ChangedInBoth, SyncDirection.Conflict)]
        [InlineData(FieldOwnership.Shared, FieldChangeState.Unchanged, SyncDirection.None)]
        [InlineData(FieldOwnership.Derived, FieldChangeState.ChangedInArcGis, SyncDirection.Pull)]
        [InlineData(FieldOwnership.Derived, FieldChangeState.Unchanged, SyncDirection.None)]
        [InlineData(FieldOwnership.Locked, FieldChangeState.ChangedInBoth, SyncDirection.Pull)]
        [InlineData(FieldOwnership.Locked, FieldChangeState.ChangedInArcGis, SyncDirection.Pull)]
        [InlineData(FieldOwnership.Locked, FieldChangeState.ChangedInRhino, SyncDirection.Pull)]
        [InlineData(FieldOwnership.Locked, FieldChangeState.MissingInRhino, SyncDirection.Pull)]
        [InlineData(FieldOwnership.Locked, FieldChangeState.Unchanged, SyncDirection.None)]
        [InlineData(FieldOwnership.RhinoOwned, FieldChangeState.ChangedInRhino, SyncDirection.Push)]
        [InlineData(FieldOwnership.ArcGisOwned, FieldChangeState.ChangedInArcGis, SyncDirection.Pull)]
        public void ResolveField_directions(FieldOwnership owner, FieldChangeState state, SyncDirection expected)
            => Assert.Equal(expected, SyncEngine.ResolveField(owner, state, out _));

        [Fact]
        public void Editing_arcgis_owned_field_in_rhino_is_a_violation()
        {
            var dir = SyncEngine.ResolveField(FieldOwnership.ArcGisOwned, FieldChangeState.ChangedInRhino, out bool violation);
            Assert.True(violation);
            Assert.Equal(SyncDirection.None, dir);
        }

        [Fact]
        public void Editing_rhino_owned_field_in_arcgis_is_a_violation()
        {
            var dir = SyncEngine.ResolveField(FieldOwnership.RhinoOwned, FieldChangeState.ChangedInArcGis, out bool violation);
            Assert.True(violation);
            Assert.Equal(SyncDirection.None, dir);
        }
    }
}
