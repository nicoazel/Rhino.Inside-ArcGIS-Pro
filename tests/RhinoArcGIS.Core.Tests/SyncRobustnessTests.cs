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
    /// <summary>
    /// The things people actually do between two apps that a clean single-session run never does:
    /// copy tracked objects, delete them in Rhino, pull twice, and let a shapefile renumber its
    /// FIDs. Each test runs the real <see cref="SyncService"/> / <see cref="PullService"/> against
    /// stateful fakes, round after round, and checks the next preview is honest.
    /// </summary>
    public class SyncRobustnessTests
    {
        // ---- copies of tracked objects

        [Fact]
        public void Copy_of_a_tracked_object_previews_as_new_in_rhino_not_deleted_in_arcgis()
        {
            var world = World.Pulled(3);
            world.Rhino.Duplicate(world.Rhino.Objects[1]);

            var preview = world.Preview();

            Assert.Equal(3, Count(preview, SyncState.Clean));
            Assert.Equal(1, Count(preview, SyncState.NewInRhino));
            Assert.Equal(0, Count(preview, SyncState.DeletedInArcGis));
            Assert.Contains(preview.Entries, e => e.Detail != null && e.Detail.Contains("Copy"));
        }

        [Fact]
        public void Applying_a_copy_creates_one_feature_and_gives_the_copy_its_own_identity()
        {
            var world = World.Pulled(3);
            var original = world.Rhino.Objects[0];
            var copy = world.Rhino.Duplicate(original);

            world.Apply();

            Assert.Equal(4, world.ArcGis.Features.Count);
            Assert.NotEqual(original.UserStrings[GisKeys.SyncGuid], copy.UserStrings[GisKeys.SyncGuid]);
            Assert.NotEqual(original.UserStrings[GisKeys.ArcGisObjectId], copy.UserStrings[GisKeys.ArcGisObjectId]);

            // Round after round, nothing is pushed again and nothing reads as a copy.
            for (int round = 0; round < 3; round++)
            {
                var preview = world.Preview();
                Assert.Equal(4, Count(preview, SyncState.Clean));
                Assert.Equal(4, preview.Entries.Count);
                world.Apply();
                Assert.Equal(4, world.ArcGis.Features.Count);
            }
        }

        [Fact]
        public void Copy_on_a_geodatabase_layer_drops_the_originals_globalid()
        {
            var world = World.Pulled(2, globalIds: true);
            var original = world.Rhino.Objects[0];
            var copy = world.Rhino.Duplicate(original);

            world.Apply();

            Assert.Equal(3, world.ArcGis.Features.Count);
            Assert.NotEqual(original.UserStrings[GisKeys.ArcGisGlobalId], copy.UserStrings[GisKeys.ArcGisGlobalId]);
            Assert.Equal(3, Count(world.Preview(), SyncState.Clean));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void Alt_drag_copy_keeps_its_own_identity_even_when_the_original_moves(
            bool copyFirst, bool moveOriginal, bool globalIds)
        {
            var world = World.Pulled(1, globalIds);
            var original = world.Rhino.Objects[0];
            string originalOid = original.UserStrings[GisKeys.ArcGisObjectId];
            var copy = world.Rhino.Duplicate(original);
            copy.Geometry = NeutralGeometry.Point(new Xyz(300, 300, 0));
            if (moveOriginal) original.Geometry = NeutralGeometry.Point(new Xyz(500, 500, 0));
            if (copyFirst) world.Rhino.Objects.Reverse();
            var beforePreview = new Dictionary<string, string>(copy.UserStrings);

            var preview = world.Preview();
            Assert.Equal(1, Count(preview, SyncState.NewInRhino));
            Assert.Equal(moveOriginal ? 1 : 0, Count(preview, SyncState.ModifiedInRhino));
            Assert.Equal(SyncState.NewInRhino.ToString(), preview.Entries.Single(e => e.RhinoGuid == copy.RhinoGuid).Message);
            Assert.Equal(beforePreview, copy.UserStrings); // Preview never repairs or adopts tags.

            world.Apply();
            Assert.Equal(2, world.ArcGis.Features.Count);
            Assert.Equal(originalOid, original.UserStrings[GisKeys.ArcGisObjectId]);
            Assert.NotEqual(originalOid, copy.UserStrings[GisKeys.ArcGisObjectId]);
            Assert.Equal(original.RhinoGuid.ToString(), original.UserStrings[GisKeys.RhinoObjectId]);
            Assert.Equal(copy.RhinoGuid.ToString(), copy.UserStrings[GisKeys.RhinoObjectId]);
            Assert.Equal(Hashing.HashGeometry(original.Geometry), Hashing.HashGeometry(world.ArcGis.Features
                .Single(f => f.Identity.ArcGisObjectId.ToString() == originalOid).Geometry));
            Assert.Equal(2, Count(world.Preview(), SyncState.Clean));
        }

        [Fact]
        public void An_untouched_copy_cannot_take_the_link_from_a_moved_original()
        {
            var world = World.Pulled(1);
            var original = world.Rhino.Objects[0];
            var copy = world.Rhino.Duplicate(original);
            original.Geometry = NeutralGeometry.Point(new Xyz(500, 500, 0));
            world.Rhino.Objects.Reverse();
            var preview = world.Preview();
            Assert.Equal(SyncState.NewInRhino.ToString(), preview.Entries.Single(e => e.RhinoGuid == copy.RhinoGuid).Message);
            Assert.Equal(SyncState.ModifiedInRhino.ToString(), preview.Entries.Single(e => e.RhinoGuid == original.RhinoGuid).Message);
        }

        [Fact]
        public void Copy_without_original_in_scope_is_new_and_cannot_take_over_or_prevent_restoring_it()
        {
            var world = World.Pulled(1);
            var original = world.Rhino.Objects[0];
            var copy = world.Rhino.Duplicate(original);
            copy.Geometry = NeutralGeometry.Point(new Xyz(300, 300, 0));
            world.Rhino.Objects.Remove(original);

            var preview = world.Preview();
            Assert.Equal(1, Count(preview, SyncState.NewInRhino));
            Assert.Equal(1, Count(preview, SyncState.DeletedInRhino));
            Assert.Equal(0, Count(preview, SyncState.ModifiedInRhino));
            Assert.Equal(1, world.Pull().CountOf(SyncOutcome.Created));
            world.Apply();
            Assert.Equal(2, world.ArcGis.Features.Count);
            Assert.Equal(2, Count(world.Preview(), SyncState.Clean));
        }

        [Fact]
        public void Clean_legacy_objects_get_owner_tags_on_apply_only()
        {
            var world = World.Pulled(1);
            var original = world.Rhino.Objects[0];
            original.UserStrings.Remove(GisKeys.RhinoObjectId);
            Assert.Equal(1, Count(world.Preview(), SyncState.Clean));
            Assert.False(original.UserStrings.ContainsKey(GisKeys.RhinoObjectId));
            world.Apply();
            Assert.Equal(original.RhinoGuid.ToString(), original.UserStrings[GisKeys.RhinoObjectId]);
            Assert.Empty(world.ArcGis.Updated);

            world.Rhino.Duplicate(original);
            original.Geometry = NeutralGeometry.Point(new Xyz(500, 500, 0));
            Assert.Equal(1, Count(world.Preview(), SyncState.NewInRhino));
            Assert.Equal(1, Count(world.Preview(), SyncState.ModifiedInRhino));
        }

        [Fact]
        public void Sync_pull_create_also_stamps_ownership()
        {
            var world = new World();
            world.ArcGis.AddFeature(new Xyz(0, 0, 0), "point");
            world.Apply();
            var original = Assert.Single(world.Rhino.Objects);
            Assert.Equal(original.RhinoGuid.ToString(), original.UserStrings[GisKeys.RhinoObjectId]);
            world.Rhino.Duplicate(original);
            Assert.Equal(1, Count(world.Preview(), SyncState.NewInRhino));
        }

        [Fact]
        public void Direct_push_of_a_copy_creates_instead_of_updating_the_original()
        {
            var world = World.Pulled(1, globalIds: true);
            var original = world.Rhino.Objects[0];
            var copy = world.Rhino.Duplicate(original);
            copy.Geometry = NeutralGeometry.Point(new Xyz(300, 300, 0));
            world.Rhino.Objects.Remove(original);
            var report = new PushService(world.ArcGis, world.Rhino).Push(world.Profile, world.Layer, PushOptions.Default);
            Assert.Equal(1, report.CountOf(SyncOutcome.Created));
            Assert.Empty(world.ArcGis.Updated);
            Assert.NotEqual(original.UserStrings[GisKeys.SyncGuid], copy.UserStrings[GisKeys.SyncGuid]);
            Assert.False(copy.UserStrings.ContainsKey(GisKeys.ArcGisGlobalId));
            Assert.Equal(copy.RhinoGuid.ToString(), copy.UserStrings[GisKeys.RhinoObjectId]);
        }

        // ---- shapefile FID renumbering

        [Fact]
        public void Shapefile_renumbering_after_a_delete_rematches_by_geometry_and_repairs_ids()
        {
            var world = World.Pulled(5);
            var deletedHash = Hashing.HashGeometry(world.ArcGis.Features[2].Geometry);
            world.ArcGis.DeleteAndRenumber(2);

            var preview = world.Preview();
            Assert.Equal(4, Count(preview, SyncState.Clean));
            Assert.Equal(1, Count(preview, SyncState.DeletedInArcGis));
            Assert.Equal(0, Count(preview, SyncState.ModifiedInArcGis));
            Assert.Equal(0, Count(preview, SyncState.NewInArcGis));

            var apply = world.Apply();
            Assert.Equal(0, apply.CountOf(SyncOutcome.Created));
            // Rhino objects now record the feature's current FID, so matching is by id again.
            var repaired = world.Rhino.Objects.Where(o => o.UserStrings[GisKeys.GeometryHash] != deletedHash).ToList();
            Assert.Equal(4, repaired.Count);
            foreach (var snap in repaired)
            {
                var oid = long.Parse(snap.UserStrings[GisKeys.ArcGisObjectId]);
                var feature = world.ArcGis.Features.Single(f => f.Identity.ArcGisObjectId == oid);
                Assert.Equal(Hashing.HashGeometry(feature.Geometry), snap.UserStrings[GisKeys.GeometryHash]);
            }
            Assert.Equal(4, world.ArcGis.Features.Count);
            Assert.Empty(world.ArcGis.Updated);

            // The object whose feature was deleted still names FID 2, which is now another row's
            // number. It must stay "deleted in ArcGIS", not turn into a copy and be pushed again.
            for (int round = 0; round < 2; round++)
            {
                var again = world.Preview();
                Assert.Equal(4, Count(again, SyncState.Clean));
                Assert.Equal(1, Count(again, SyncState.DeletedInArcGis));
                Assert.Equal(0, Count(again, SyncState.NewInRhino));
                world.Apply();
                Assert.Equal(4, world.ArcGis.Features.Count);
            }
        }

        [Fact]
        public void New_feature_that_reuses_a_renumbered_fid_is_pulled_not_held_as_deleted()
        {
            // Found live: save a shapefile delete (FIDs shift down), then create a feature in
            // ArcGIS. It takes the old last FID, which the ledger still listed -- and read as
            // "deleted in Rhino", it was never pulled.
            var world = World.Pulled(3);
            world.Apply();
            world.ArcGis.DeleteAndRenumber(0);
            world.ArcGis.AddFeature(new Xyz(900, 9, 0), "created-after-save");
            Assert.Equal(2, world.ArcGis.Features.Last().Identity.ArcGisObjectId);

            var preview = world.Preview();
            Assert.Equal(1, Count(preview, SyncState.NewInArcGis));
            Assert.Equal(0, Count(preview, SyncState.DeletedInRhino));
            Assert.Equal(1, Count(preview, SyncState.DeletedInArcGis));

            world.Apply();
            Assert.Contains(world.Rhino.Objects, o => o.UserStrings.TryGetValue("asset_type", out var v) && v == "created-after-save");
            var again = world.Preview();
            Assert.Equal(3, Count(again, SyncState.Clean));
            Assert.Equal(1, Count(again, SyncState.DeletedInArcGis));
        }

        [Fact]
        public void Rhino_delete_is_still_recognised_after_the_fids_are_renumbered()
        {
            var world = World.Pulled(4);
            world.Apply();
            world.Rhino.Objects.RemoveAt(3);          // deleted in Rhino: feature FID 3
            world.ArcGis.DeleteAndRenumber(0);        // ArcGIS save: that feature is FID 2 now

            var preview = world.Preview();
            Assert.Equal(1, Count(preview, SyncState.DeletedInRhino));
            Assert.Equal(0, Count(preview, SyncState.NewInArcGis));
        }

        [Fact]
        public void Renumbered_feature_edited_in_rhino_updates_the_right_row()
        {
            var world = World.Pulled(4);
            world.ArcGis.DeleteAndRenumber(0);
            var last = world.Rhino.Objects[3];
            last.UserStrings["asset_type"] = "edited";

            world.Apply();

            var updated = Assert.Single(world.ArcGis.Updated);
            // Feature 3 is FID 2 after the renumbering; the edit must land there, not on FID 3.
            Assert.Equal(2, updated.Identity.ArcGisObjectId);
            Assert.Equal("edited", world.ArcGis.Features.Single(f => f.Identity.ArcGisObjectId == 2).Attributes["asset_type"]);
        }

        // ---- derived shape metrics

        [Fact]
        public void Pushing_a_reshaped_feature_leaves_no_phantom_arcgis_change_in_its_metrics()
        {
            // Found live: a Rhino edit that changes a feature's length is pushed with a recomputed
            // Shape_Leng, but the baseline kept the old value -- so the very next preview reported
            // "Modified in ArcGIS: Shape_Leng" for a change Rhino itself had just made.
            var world = World.Pulled(0);
            world.Layer.GeometryTarget = GeometryTarget.PolylineZ;
            world.Layer.Attributes.Add(new FieldMapping { RhinoKey = "Shape_Leng", ArcGisField = "Shape_Leng", Owner = FieldOwnership.Derived });
            world.ArcGis.ExtraFields.Add(new FieldDefinition { Name = "Shape_Leng", Type = FieldType.Double });
            world.ArcGis.Features.Add(new FeatureRecord
            {
                Identity = new SyncIdentity { ArcGisObjectId = 0 },
                Geometry = NeutralGeometry.Polyline(new[] { new Xyz(0, 0, 0), new Xyz(10, 0, 0) }),
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { { "asset_type", "line" }, { "Shape_Leng", "10" } }
            });
            world.Pull();
            Assert.Equal(1, Count(world.Preview(), SyncState.Clean));

            world.Rhino.Objects[0].Geometry = NeutralGeometry.Polyline(new[] { new Xyz(0, 0, 0), new Xyz(25, 0, 0) });
            Assert.Equal(1, Count(world.Preview(), SyncState.ModifiedInRhino));
            world.Apply();

            Assert.Equal("25", world.ArcGis.Features[0].Attributes["Shape_Leng"]);
            Assert.Equal("25", world.Rhino.Objects[0].UserStrings["Shape_Leng"]);
            var after = world.Preview();
            Assert.Equal(1, Count(after, SyncState.Clean));
            Assert.Equal(0, after.Entries.Count(e => e.Message != SyncState.Clean.ToString()));
        }

        [Fact]
        public void Pushing_a_moved_feature_into_a_geodatabase_leaves_no_phantom_change_in_its_locked_metrics()
        {
            // Found live: a geodatabase maintains Shape_Length itself (a Locked field). Pushing a
            // moved shape into a WGS84 feature class changed its length in degrees, the baseline kept
            // the old value, and the next preview reported "Modified in ArcGIS: Shape_Length".
            var world = World.Pulled(0);
            world.Layer.GeometryTarget = GeometryTarget.PolylineZ;
            world.Layer.Attributes.Add(new FieldMapping { RhinoKey = "Shape_Length", ArcGisField = "Shape_Length", Owner = FieldOwnership.Locked });
            world.ArcGis.ExtraFields.Add(new FieldDefinition { Name = "Shape_Length", Type = FieldType.Double });
            world.ArcGis.MaintainsLength = true;
            world.ArcGis.Features.Add(new FeatureRecord
            {
                Identity = new SyncIdentity { ArcGisObjectId = 0 },
                Geometry = NeutralGeometry.Polyline(new[] { new Xyz(0, 0, 0), new Xyz(10, 0, 0) }),
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { { "asset_type", "line" }, { "Shape_Length", "10" } }
            });
            world.Pull();

            world.Rhino.Objects[0].Geometry = NeutralGeometry.Polyline(new[] { new Xyz(0, 0, 0), new Xyz(0, 40, 0) });
            world.Apply();

            Assert.Equal("40", world.ArcGis.Features[0].Attributes["Shape_Length"]);   // the store recomputed it
            Assert.Equal("40", world.Rhino.Objects[0].UserStrings["Shape_Length"]);   // and Rhino's baseline took it
            Assert.Equal(1, Count(world.Preview(), SyncState.Clean));
        }

        // ---- objects linked to another layer

        [Fact]
        public void Objects_linked_to_another_layer_are_left_alone_not_resourced()
        {
            // Found live: objects pulled from a State Plane layer, synced against a brand-new empty
            // layer, came out "deleted in ArcGIS" (nothing created) and had their recorded source
            // rewritten to the new layer, breaking their original link.
            var world = World.Pulled(3);
            var before = world.Rhino.Objects.Select(o => o.UserStrings[GisKeys.ArcGisSource]).ToList();
            var other = new World();
            foreach (var o in world.Rhino.Objects) other.Rhino.Objects.Add(o);
            other.Layer.ArcGisLayer = "brand_new";
            other.Layer.ArcGisSource = @"C:\data\new.gdb\brand_new";

            var apply = other.Apply();

            Assert.Empty(other.ArcGis.Features);
            Assert.Equal(before, world.Rhino.Objects.Select(o => o.UserStrings[GisKeys.ArcGisSource]).ToList());
            Assert.Contains(apply.Entries, e => e.Outcome == SyncOutcome.Warning && e.Message.Contains("linked to ArcGIS layer 'pts'"));
            Assert.Equal(3, Count(world.Preview(), SyncState.Clean));   // the original link is untouched
        }

        [Fact]
        public void A_renamed_layer_and_a_same_named_copy_still_belong_to_the_link()
        {
            var renamed = World.Pulled(2);
            renamed.Layer.ArcGisLayer = "pts_renamed";                     // same source, new name
            Assert.Equal(2, Count(renamed.Preview(), SyncState.Clean));

            var moved = World.Pulled(2);
            moved.Layer.ArcGisSource = @"C:\elsewhere\pts.shp";           // same name, new source
            var p = moved.Preview();
            Assert.Equal(2, Count(p, SyncState.Clean));
            Assert.Contains(p.Entries, e => e.Outcome == SyncOutcome.Warning && e.Message.Contains("Source changed"));
        }

        // ---- deleted in Rhino

        [Fact]
        public void Object_deleted_in_rhino_is_held_not_pulled_back()
        {
            var world = World.Pulled(3);
            world.Apply(); // establishes the ledger the way any sync does
            world.Rhino.Objects.RemoveAt(1);

            var preview = world.Preview();
            Assert.Equal(1, Count(preview, SyncState.DeletedInRhino));
            Assert.Equal(0, Count(preview, SyncState.NewInArcGis));

            var apply = world.Apply();
            Assert.Equal(2, world.Rhino.Objects.Count);
            Assert.Equal(3, world.ArcGis.Features.Count);
            Assert.Equal(1, apply.CountOf(SyncOutcome.Held));

            // It stays flagged, run after run, until someone decides.
            Assert.Equal(1, Count(world.Preview(), SyncState.DeletedInRhino));
        }

        [Fact]
        public void Pull_after_a_rhino_delete_restores_just_that_object()
        {
            var world = World.Pulled(3);
            world.Rhino.Objects.RemoveAt(0);
            Assert.Equal(1, Count(world.Preview(), SyncState.DeletedInRhino));

            var pull = world.Pull();

            Assert.Equal(1, pull.CountOf(SyncOutcome.Created));
            Assert.Equal(2, pull.CountOf(SyncOutcome.Skipped));
            Assert.Equal(3, world.Rhino.Objects.Count);
            Assert.Equal(3, Count(world.Preview(), SyncState.Clean));
        }

        [Fact]
        public void Deleting_the_feature_in_arcgis_settles_a_rhino_delete()
        {
            var world = World.Pulled(3);
            var gone = world.Rhino.Objects[2];
            world.Rhino.Objects.Remove(gone);
            world.ArcGis.Features.RemoveAll(f => f.Identity.ArcGisObjectId.ToString() == gone.UserStrings[GisKeys.ArcGisObjectId]);

            var preview = world.Preview();
            Assert.Equal(2, preview.Entries.Count);
            Assert.Equal(2, Count(preview, SyncState.Clean));
        }

        [Fact]
        public void Without_a_ledger_a_missing_object_is_still_new_in_arcgis()
        {
            // Documents from before the ledger existed keep their old behaviour until the next apply.
            var world = World.Pulled(2);
            world.Rhino.Strings.Clear();
            world.Rhino.Objects.RemoveAt(0);

            Assert.Equal(1, Count(world.Preview(), SyncState.NewInArcGis));
        }

        [Fact]
        public void Ledger_recorded_for_another_source_is_ignored()
        {
            var world = World.Pulled(2);
            world.Layer.ArcGisSource = @"C:\other\copy.shp";
            world.Rhino.Objects.RemoveAt(0);

            Assert.Equal(0, Count(world.Preview(), SyncState.DeletedInRhino));
        }

        // ---- pulling twice

        [Fact]
        public void Pulling_twice_creates_nothing_the_second_time()
        {
            var world = World.Pulled(4);

            var again = world.Pull();

            Assert.Equal(0, again.CountOf(SyncOutcome.Created));
            Assert.Equal(4, again.CountOf(SyncOutcome.Skipped));
            Assert.Equal(4, world.Rhino.Objects.Count);
            Assert.Equal(4, Count(world.Preview(), SyncState.Clean));
        }

        // ---- a long mixed session

        [Fact]
        public void Mixed_edits_on_both_sides_settle_in_one_apply_and_stay_settled()
        {
            var world = World.Pulled(6);
            world.Apply();

            world.Rhino.Objects[0].Geometry = NeutralGeometry.Point(new Xyz(1000, 1, 0));   // moved in Rhino
            world.Rhino.Objects[1].UserStrings["asset_type"] = "rhino-edit";                 // attribute in Rhino
            world.ArcGis.Features[2].Attributes["asset_type"] = "gis-edit";                 // attribute in ArcGIS
            world.ArcGis.Features[3].Geometry = NeutralGeometry.Point(new Xyz(2000, 2, 0)); // moved in ArcGIS
            world.Rhino.Objects.RemoveAt(4);                                                 // deleted in Rhino
            world.Rhino.Duplicate(world.Rhino.Objects[4]);                                   // copy of feature 5
            world.ArcGis.AddFeature(new Xyz(3000, 3, 0), "gis-new");                         // new in ArcGIS
            world.Rhino.AddNew(new Xyz(4000, 4, 0), "rhino-new");                            // new in Rhino

            var preview = world.Preview();
            Assert.Equal(2, Count(preview, SyncState.ModifiedInRhino));
            Assert.Equal(2, Count(preview, SyncState.ModifiedInArcGis));
            Assert.Equal(1, Count(preview, SyncState.DeletedInRhino));
            Assert.Equal(2, Count(preview, SyncState.NewInRhino));
            Assert.Equal(1, Count(preview, SyncState.NewInArcGis));
            Assert.Equal(1, Count(preview, SyncState.Clean));

            world.Apply();

            Assert.Equal(9, world.ArcGis.Features.Count);   // 6 + gis-new + copy + rhino-new
            Assert.Equal(8, world.Rhino.Objects.Count);     // 6 - deleted + copy + rhino-new + gis-new
            Assert.Equal("rhino-edit", world.ArcGis.Features[1].Attributes["asset_type"]);
            Assert.Equal("gis-edit", world.Rhino.Objects.Single(o => o.UserStrings[GisKeys.ArcGisObjectId] == "2").UserStrings["asset_type"]);

            for (int round = 0; round < 3; round++)
            {
                var again = world.Preview();
                Assert.Equal(8, Count(again, SyncState.Clean));
                Assert.Equal(1, Count(again, SyncState.DeletedInRhino));
                Assert.Equal(9, again.Entries.Count);
                world.Apply();
            }
            Assert.Equal(9, world.ArcGis.Features.Count);
            Assert.Equal(8, world.Rhino.Objects.Count);
        }

        [Fact]
        public void Ping_pong_edits_on_one_object_never_duplicate_or_conflict()
        {
            var world = World.Pulled(2);
            var snap = world.Rhino.Objects[0];
            var oid = long.Parse(snap.UserStrings[GisKeys.ArcGisObjectId]);

            for (int round = 0; round < 5; round++)
            {
                snap.UserStrings["asset_type"] = "rhino-" + round;
                var pushed = world.Preview();
                Assert.Equal(1, Count(pushed, SyncState.ModifiedInRhino));
                world.Apply();
                Assert.Equal("rhino-" + round, world.ArcGis.Features.Single(f => f.Identity.ArcGisObjectId == oid).Attributes["asset_type"]);

                world.ArcGis.Features.Single(f => f.Identity.ArcGisObjectId == oid).Attributes["asset_type"] = "gis-" + round;
                var pulled = world.Preview();
                Assert.Equal(1, Count(pulled, SyncState.ModifiedInArcGis));
                world.Apply();
                Assert.Equal("gis-" + round, snap.UserStrings["asset_type"]);

                Assert.Equal(2, Count(world.Preview(), SyncState.Clean));
            }
            Assert.Equal(2, world.ArcGis.Features.Count);
            Assert.Equal(2, world.Rhino.Objects.Count);
        }

        // ---- plumbing

        private static int Count(SyncReport report, SyncState state) =>
            report.Entries.Count(e => e.Message == state.ToString());

        /// <summary>A pulled layer on both sides, driven through the real services.</summary>
        private sealed class World
        {
            public StoreRhino Rhino { get; } = new StoreRhino();
            public StoreArcGis ArcGis { get; } = new StoreArcGis();
            public LayerMappingProfile Profile { get; } = new LayerMappingProfile
            {
                RhinoUnits = UnitSystem.Meters,
                ArcGisCrs = "EPSG:3857",
                ProjectAnchor = new AnchorSettings(),
                Layers =
                {
                    new LayerMapping
                    {
                        Name = "Pts", RhinoLayer = "Pts", ArcGisLayer = "pts", ArcGisSource = @"C:\data\pts.shp",
                        GeometryTarget = GeometryTarget.PointZ,
                        Attributes = { new FieldMapping { RhinoKey = "asset_type", ArcGisField = "asset_type", Owner = FieldOwnership.Shared } }
                    }
                }
            };

            public LayerMapping Layer => Profile.Layers[0];

            public static World Pulled(int count, bool globalIds = false)
            {
                var world = new World();
                world.ArcGis.GlobalIds = globalIds;
                for (int i = 0; i < count; i++) world.ArcGis.AddFeature(new Xyz(i * 10, i, 0), "type-" + i);
                var pull = world.Pull();
                Assert.Equal(count, pull.CountOf(SyncOutcome.Created));
                return world;
            }

            public SyncReport Pull() =>
                new PullService(ArcGis, Rhino).Pull(Profile, Layer, PullOptions.Context);

            public SyncReport Preview() =>
                new SyncService(ArcGis, Rhino).Sync(Profile, Layer, SyncOptions.Preview);

            public SyncReport Apply() =>
                new SyncService(ArcGis, Rhino).Sync(Profile, Layer, new SyncOptions());
        }

        private sealed class StoreArcGis : IArcGISAdapter
        {
            public bool GlobalIds;
            public List<FeatureRecord> Features { get; } = new List<FeatureRecord>();
            public List<FeatureRecord> Updated { get; } = new List<FeatureRecord>();

            public List<FieldDefinition> ExtraFields { get; } = new List<FieldDefinition>();

            public LayerSchema GetSchema(string layerName)
            {
                var schema = new LayerSchema
                {
                    LayerName = "pts",
                    GeometryType = GeometryTarget.PointZ,
                    Fields = { new FieldDefinition { Name = "asset_type", Type = FieldType.Text } }
                };
                schema.Fields.AddRange(ExtraFields);
                return schema;
            }

            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) =>
                Features.Select(Clone).ToList();

            public void AddFeature(Xyz at, string assetType) => Features.Add(new FeatureRecord
            {
                Identity = new SyncIdentity
                {
                    ArcGisObjectId = NextOid(),
                    ArcGisGlobalId = GlobalIds ? Guid.NewGuid() : (Guid?)null
                },
                Geometry = NeutralGeometry.Point(at),
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { { "asset_type", assetType } }
            });

            /// <summary>What a shapefile does on save: the row goes, and every later FID moves down.</summary>
            public void DeleteAndRenumber(int index)
            {
                Features.RemoveAt(index);
                for (int i = 0; i < Features.Count; i++) Features[i].Identity.ArcGisObjectId = i;
            }

            long NextOid() => Features.Count == 0 ? 0 : Features.Max(f => f.Identity.ArcGisObjectId.Value) + 1;

            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                var oids = new List<long>();
                foreach (var f in features)
                {
                    var oid = NextOid();
                    Features.Add(new FeatureRecord
                    {
                        Identity = new SyncIdentity { ArcGisObjectId = oid, ArcGisGlobalId = GlobalIds ? Guid.NewGuid() : (Guid?)null },
                        Geometry = f.Geometry,
                        Attributes = new Dictionary<string, string>(f.Attributes, StringComparer.Ordinal)
                    });
                    oids.Add(oid);
                }
                return oids;
            }

            /// <summary>Like a geodatabase: Shape_Length follows the stored geometry.</summary>
            public bool MaintainsLength;

            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                foreach (var f in features)
                {
                    Updated.Add(f);
                    var stored = Features.Single(x => x.Identity.ArcGisObjectId == f.Identity.ArcGisObjectId);
                    if (f.Geometry != null) stored.Geometry = f.Geometry;
                    foreach (var kv in f.Attributes) stored.Attributes[kv.Key] = kv.Value;
                    if (MaintainsLength && f.Geometry != null)
                    {
                        double length = 0;
                        var pts = f.Geometry.Points;
                        for (int i = 1; i < pts.Count; i++)
                            length += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
                        stored.Attributes["Shape_Length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }

            static FeatureRecord Clone(FeatureRecord f) => new FeatureRecord
            {
                Identity = new SyncIdentity { ArcGisObjectId = f.Identity.ArcGisObjectId, ArcGisGlobalId = f.Identity.ArcGisGlobalId },
                Geometry = f.Geometry,
                Attributes = new Dictionary<string, string>(f.Attributes, StringComparer.Ordinal)
            };

            public void RefreshScene() { }
            public IReadOnlyList<string> GetLayerNames() => new[] { "pts" };
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => ReadFeatures(layerName);
        }

        private sealed class StoreRhino : IRhinoAdapter, IDocumentStringStore
        {
            public List<RhinoObjectSnapshot> Objects { get; } = new List<RhinoObjectSnapshot>();
            public Dictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            public RhinoObjectSnapshot Duplicate(RhinoObjectSnapshot source)
            {
                var copy = new RhinoObjectSnapshot
                {
                    RhinoGuid = Guid.NewGuid(),
                    Descriptor = source.Descriptor,
                    Geometry = source.Geometry,
                    UserStrings = new Dictionary<string, string>(source.UserStrings, StringComparer.Ordinal)
                };
                Objects.Add(copy);
                return copy;
            }

            public void AddNew(Xyz at, string assetType) => Objects.Add(new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.Point),
                Geometry = NeutralGeometry.Point(at),
                UserStrings = new Dictionary<string, string>(StringComparer.Ordinal) { { "asset_type", assetType } }
            });

            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName) => Objects.ToList();
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => Objects.ToList();
            public RhinoObjectSnapshot ReadObject(Guid rhinoGuid) => Objects.FirstOrDefault(o => o.RhinoGuid == rhinoGuid);

            public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings)
            {
                var snap = ReadObject(rhinoGuid) ?? throw new InvalidOperationException("missing object");
                foreach (var kv in userStrings)
                {
                    if (kv.Value == null) snap.UserStrings.Remove(kv.Key);
                    else snap.UserStrings[kv.Key] = kv.Value;
                }
            }

            public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings)
            {
                var snap = new RhinoObjectSnapshot
                {
                    RhinoGuid = Guid.NewGuid(),
                    Descriptor = new GeometryDescriptor(RhinoGeometryKind.Point),
                    Geometry = geometry,
                    UserStrings = new Dictionary<string, string>(userStrings, StringComparer.Ordinal)
                };
                Objects.Add(snap);
                return snap.RhinoGuid;
            }

            public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry) =>
                ReadObject(rhinoGuid).Geometry = geometry;

            public string GetDocumentString(string key) => Strings.TryGetValue(key, out var v) ? v : null;

            public void SetDocumentString(string key, string value)
            {
                if (value == null) Strings.Remove(key);
                else Strings[key] = value;
            }

            public UnitSystem GetUnits() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => new[] { "Pts" };
            public void IsolateFailed(IEnumerable<Guid> rhinoGuids) { }
        }
    }
}
