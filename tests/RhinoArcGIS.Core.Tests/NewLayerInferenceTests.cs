using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class NewLayerInferenceTests
    {
        static RhinoObjectSnapshot Obj(RhinoGeometryKind kind, params (string key, string value)[] strings)
        {
            var snap = new RhinoObjectSnapshot { RhinoGuid = Guid.NewGuid(), Descriptor = new GeometryDescriptor(kind) };
            foreach (var (key, value) in strings) snap.UserStrings[key] = value;
            return snap;
        }

        [Fact]
        public void Majority_geometry_wins_and_the_rest_are_counted_as_skipped()
        {
            var plan = NewLayerInference.Plan(new[]
            {
                Obj(RhinoGeometryKind.OpenCurve), Obj(RhinoGeometryKind.OpenCurve), Obj(RhinoGeometryKind.Point)
            }, "Roads");

            Assert.Equal(GeometryTarget.PolylineZ, plan.Target);
            Assert.Equal(3, plan.ObjectCount);
            Assert.Equal(2, plan.MatchingCount);
        }

        [Fact]
        public void Closed_planar_curves_and_extrusions_make_a_polygon_layer()
        {
            var plan = NewLayerInference.Plan(new[]
            {
                Obj(RhinoGeometryKind.ClosedPlanarCurve), Obj(RhinoGeometryKind.Extrusion)
            }, "Buildings");

            Assert.Equal(GeometryTarget.PolygonZ, plan.Target);
            Assert.Equal(2, plan.MatchingCount);
        }

        [Fact]
        public void An_empty_layer_has_no_target_and_meshes_make_multipatch()
        {
            Assert.Equal(GeometryTarget.Unsupported, NewLayerInference.Plan(new RhinoObjectSnapshot[0], "x").Target);
            var meshPlan = NewLayerInference.Plan(new[]
            {
                Obj(RhinoGeometryKind.Mesh), Obj(RhinoGeometryKind.ClosedBrep), Obj(RhinoGeometryKind.Point)
            }, "Buildings");

            Assert.Equal(GeometryTarget.Multipatch, meshPlan.Target);
            Assert.Equal(2, meshPlan.MatchingCount);
        }

        [Fact]
        public void Converted_geometry_decides_the_new_layer_shape()
        {
            var convertedBrep = Obj(RhinoGeometryKind.ClosedBrep);
            convertedBrep.Geometry = new NeutralGeometry
            {
                Kind = NeutralGeometryKind.Multipatch,
                Mesh = new NeutralMesh
                {
                    Vertices = new List<Xyz>
                    {
                        new Xyz(0, 0, 0), new Xyz(1, 0, 0), new Xyz(0, 1, 1)
                    },
                    Faces = new List<int[]> { new[] { 0, 1, 2 } }
                }
            };

            var unsupported = Obj(RhinoGeometryKind.SubD);
            var plan = NewLayerInference.Plan(new[] { convertedBrep, unsupported }, "Buildings");

            Assert.Equal(GeometryTarget.Multipatch, plan.Target);
            Assert.Equal(1, plan.MatchingCount);
        }

        [Fact]
        public void Fields_come_from_user_text_and_are_typed_by_their_values()
        {
            var plan = NewLayerInference.Plan(new[]
            {
                Obj(RhinoGeometryKind.Point, ("note", "from rhino"), ("rank", "3"), ("height", "12.5"), ("mixed", "7")),
                Obj(RhinoGeometryKind.Point, ("note", "second"), ("rank", "7"), ("height", "3"), ("mixed", "n/a"),
                    (GisKeys.SyncGuid, Guid.NewGuid().ToString()), ("gis.field_hash.note", "abc"))
            }, "Pts");

            Assert.Equal(new[] { "note", "rank", "height", "mixed" }, plan.Fields.Select(f => f.Name));
            Assert.Equal(FieldType.Text, plan.Fields[0].Type);
            Assert.True(plan.Fields[0].Length >= 50);
            Assert.Equal(FieldType.Integer, plan.Fields[1].Type);
            Assert.Equal(FieldType.Double, plan.Fields[2].Type);
            Assert.Equal(FieldType.Text, plan.Fields[3].Type);
        }

        [Fact]
        public void A_key_with_no_values_becomes_a_text_field()
        {
            var plan = NewLayerInference.Plan(new[] { Obj(RhinoGeometryKind.Point, ("empty", "")) }, "Pts");
            Assert.Single(plan.Fields);
            Assert.Equal(FieldType.Text, plan.Fields[0].Type);
        }

        [Fact]
        public void Geodatabase_system_field_names_are_not_recreated_from_user_text()
        {
            var plan = NewLayerInference.Plan(new[]
            {
                Obj(RhinoGeometryKind.Point,
                    ("OBJECTID", "12"), ("GlobalID", Guid.NewGuid().ToString()),
                    ("Shape_Length", "4.5"), ("note", "keep me"))
            }, "Pts");

            Assert.Single(plan.Fields);
            Assert.Equal("note", plan.Fields[0].Name);
        }

        [Theory]
        [InlineData("Site Plan::Trees", "Site_Plan_Trees")]
        [InlineData("2024 survey", "L_2024_survey")]
        [InlineData("   ", "RhinoLayer")]
        [InlineData("Ünïcode-name!", "n_code_name")]
        [InlineData("trailing___", "trailing")]
        public void Names_are_made_safe_for_a_geodatabase(string raw, string expected)
            => Assert.Equal(expected, NewLayerInference.SanitizeName(raw, "RhinoLayer", 60));

        [Fact]
        public void Long_names_are_cut_and_colliding_field_names_keep_the_first()
        {
            var longName = new string('a', 80);
            Assert.Equal(60, NewLayerInference.SanitizeName(longName, "x", 60).Length);

            var plan = NewLayerInference.Plan(new[] { Obj(RhinoGeometryKind.Point, ("a b", "1"), ("a_b", "x")) }, "p");
            Assert.Single(plan.Fields);
            Assert.Equal("a_b", plan.Fields[0].Name);
            Assert.Equal(FieldType.Integer, plan.Fields[0].Type);
        }
    }
}
