using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Grasshopper;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class GrasshopperPlanningTests
    {
        static MapLayerReference Layer(string map = "map", string source = "data") => new() {
            Project = "project-session", MapUri = map, LayerUri = "layer-uri", Source = source, Name = "Buildings" };
        static LayerSchema Schema() => new() { Source = "data", Editable = true, GeometryType = GeometryTarget.PointZ,
            Fields = new() { new() { Name = "height", Type = FieldType.Double },
                new() { Name = "name", Type = FieldType.Text, Length = 8 },
                new() { Name = "OBJECTID", Type = FieldType.Integer, Editable = false },
                new() { Name = "required", Nullable = false } } };
        static FeatureRecord Record(long oid = 4) => new() { Identity = new SyncIdentity { ArcGisObjectId = oid, ArcGisGlobalId = new Guid("215da506-ae31-4e4f-a29d-c7d8c9d704ca") },
            Geometry = NeutralGeometry.Point(new Xyz(1, 2, 3)), Attributes = new() { ["height"] = "12", ["name"] = null } };
        static FeatureDraft Edit(FeatureRecord record) => new() { Source = Layer(), ObjectId = record.Identity.ArcGisObjectId,
            GlobalId = record.Identity.ArcGisGlobalId, Baseline = FeaturePlanning.Version(record), Geometry = record.Geometry,
            Changes = new() { ["height"] = "24" } };
        static MapUpdatePlan Plan(FeatureDraft edit, FeatureRecord record) => FeaturePlanning.Plan(Layer(), Schema(), new[] { edit }, new[] { record });

        [Fact] public void Attribute_update_omits_geometry_and_preserves_identity()
        { var r = Record(); var p = Plan(Edit(r), r); Assert.True(p.CanCommit); Assert.Null(p.Patches[0].Values.Geometry); Assert.Equal(4, p.Patches[0].ObjectId); }
        [Fact] public void Globalid_does_not_fall_back_to_reused_objectid()
        { var r = Record(); var e = Edit(r); r.Identity.ArcGisGlobalId = Guid.NewGuid(); Assert.False(Plan(e, r).CanCommit); }
        [Fact] public void Globalid_matches_when_objectid_has_changed_but_requires_a_fresh_baseline()
        { var r = Record(); var e = Edit(r); r.Identity.ArcGisObjectId = 9; Assert.Contains(Plan(e, r).Issues, s => s.Contains("changed since read")); }
        [Fact] public void Same_layer_name_in_another_map_is_rejected()
        { var r = Record(); var e = Edit(r); e.Source = Layer("other-map"); Assert.False(Plan(e, r).CanCommit); }
        [Fact] public void Dataset_replacement_is_rejected()
        { var r = Record(); var e = Edit(r); e.Source = Layer(source: "another-dataset"); Assert.False(Plan(e, r).CanCommit); }
        [Fact] public void Missing_target_is_not_recreated()
        { var r = Record(); Assert.False(FeaturePlanning.Plan(Layer(), Schema(), new[] { Edit(r) }, Array.Empty<FeatureRecord>()).CanCommit); }
        [Fact] public void Repeated_target_is_rejected()
        { var r = Record(); Assert.False(FeaturePlanning.Plan(Layer(), Schema(), new[] { Edit(r), Edit(r) }, new[] { r }).CanCommit); }
        [Theory] [InlineData("OBJECTID", "8")] [InlineData("unknown", "x")] [InlineData("height", "oops")] [InlineData("name", "more-than-eight")] [InlineData("required", null)]
        public void Invalid_or_protected_fields_block_the_plan(string field, string value)
        { var r = Record(); var e = Edit(r); e.Changes = new() { [field] = value }; Assert.False(Plan(e, r).CanCommit); }
        [Fact] public void Upstream_edits_cannot_mutate_a_reviewed_plan()
        { var r = Record(); var e = Edit(r); var p = Plan(e, r); e.Changes["height"] = "999"; p.Patches[0].Values.Attributes["height"] = "111"; Assert.Equal("24", p.Patches[0].Values.Attributes["height"]); }
        [Fact] public void Remote_field_edits_invalidate_baseline()
        { var r = Record(); var e = Edit(r); r.Attributes["height"] = "15"; Assert.False(Plan(e, r).CanCommit); }
        [Fact] public void Null_and_empty_string_have_different_versions()
        { var r = Record(); string before = FeaturePlanning.Version(r); r.Attributes["name"] = ""; Assert.NotEqual(before, FeaturePlanning.Version(r)); }
        [Fact] public void Mesh_topology_changes_invalidate_baseline()
        { var r = Record(); r.Geometry = new NeutralGeometry { Kind = NeutralGeometryKind.Multipatch, Mesh = new NeutralMesh {
            Vertices = new() { new Xyz(0,0,0), new Xyz(1,0,0), new Xyz(0,1,0) }, Faces = new() { new[] { 0, 1, 2 } } } };
            var before = FeaturePlanning.Version(r); r.Geometry.Mesh.Faces[0] = new[] { 2, 1, 0 }; Assert.NotEqual(before, FeaturePlanning.Version(r)); }
        [Fact] public void Geometry_kind_mismatch_blocks_write()
        { var r = Record(); var e = Edit(r); e.ReplaceGeometry = true; e.Geometry = NeutralGeometry.Polyline(new[] { new Xyz(0,0,0), new Xyz(1,1,0) }); Assert.False(Plan(e, r).CanCommit); }
        [Fact] public void No_changes_produces_no_commit()
        { var r = Record(); var e = Edit(r); e.Changes.Clear(); Assert.False(Plan(e, r).CanCommit); }
        [Theory] [InlineData("12", FieldType.Integer, typeof(long))] [InlineData("2.5", FieldType.Double, typeof(double))] [InlineData("true", FieldType.Boolean, typeof(bool))]
        public void Typed_values_keep_their_types(string value, FieldType type, Type expected) => Assert.IsType(expected, FeaturePlanning.TypedValue(value, type));
        [Fact] public void Draft_copy_preserves_coordinates_and_is_independent()
        { var r = Record(); var e = Edit(r); var copy = e.Copy(); Assert.Equal(3, copy.Geometry.Points[0].Z); copy.Changes.Clear(); Assert.Single(e.Changes); }
        [Fact] public void Nonfinite_coordinates_are_rejected()
        { var errors = new List<string>(); FeaturePlanning.ValidateGeometry(NeutralGeometry.Point(new Xyz(double.NaN, 0, 0)), GeometryTarget.PointZ, errors); Assert.NotEmpty(errors); }
        [Theory] [InlineData("8.34.26223.11001", 10, false)] [InlineData("8.36.26279.7001", 10, false)]
        [InlineData("8.36.26282.15001", 10, true)] [InlineData("8.34.26223.11001", 8, true)]
        public void Net10_preflight_rejects_rhino_before_vendor_hotfix(string version, int runtime, bool supported) =>
            Assert.Equal(supported, RuntimeCompatibility.GrasshopperIssue(Version.Parse(version), runtime) == null);
        [Fact] public void Multipart_lines_stay_separate_when_reassembled()
        {
            var a = NeutralGeometry.Polyline(new[] { new Xyz(0,0,0), new Xyz(1,1,0) });
            var b = NeutralGeometry.Polyline(new[] { new Xyz(10,10,0), new Xyz(11,11,0) });
            var joined = FeaturePlanning.Combine(new[] { a, b }); Assert.Equal(2, joined.Parts.Count);
            Assert.Equal(10, joined.Parts[1][0].X); joined.Parts[0].Clear(); Assert.Equal(2, a.Points.Count);
        }
        [Fact] public void Mixed_geometry_pieces_are_rejected() => Assert.Throws<ArgumentException>(() =>
            FeaturePlanning.Combine(new[] { NeutralGeometry.Point(new Xyz(0,0,0)), NeutralGeometry.Polyline(new[] { new Xyz(0,0,0), new Xyz(1,1,0) }) }));
        [Fact] public void Coordinate_system_replacement_changes_the_binding()
        { var a = Layer(); var b = Layer(); a.CrsDefinition = "first"; b.CrsDefinition = "second"; Assert.False(a.Matches(b)); }
        [Fact] public void Plans_freeze_the_reviewed_schema()
        { var r = Record(); var schema = Schema(); var p = FeaturePlanning.Plan(Layer(), schema, new[] { Edit(r) }, new[] { r });
            schema.Fields[0].Type = FieldType.Text; Assert.NotEqual(p.SchemaVersion, FeaturePlanning.SchemaVersion(schema)); }
    }
}
