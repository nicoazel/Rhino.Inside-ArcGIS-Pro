using System.Collections.Generic;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class HashingTests
    {
        [Fact]
        public void HashAttributes_is_order_independent()
        {
            var a = new Dictionary<string, string> { { "asset_type", "PV" }, { "height_m", "3.2" } };
            var b = new Dictionary<string, string> { { "height_m", "3.2" }, { "asset_type", "PV" } };

            Assert.Equal(Hashing.HashAttributes(a), Hashing.HashAttributes(b));
        }

        [Fact]
        public void HashAttributes_ignores_system_namespace_keys()
        {
            var withSystem = new Dictionary<string, string>
            {
                { "asset_type", "PV" },
                { GisKeys.SyncGuid, "anything" },
                { GisKeys.GeometryHash, "deadbeef" }
            };
            var withoutSystem = new Dictionary<string, string> { { "asset_type", "PV" } };

            Assert.Equal(Hashing.HashAttributes(withoutSystem), Hashing.HashAttributes(withSystem));
        }

        [Fact]
        public void HashAttributes_differs_when_value_changes()
        {
            var a = new Dictionary<string, string> { { "height_m", "3.2" } };
            var b = new Dictionary<string, string> { { "height_m", "3.3" } };

            Assert.NotEqual(Hashing.HashAttributes(a), Hashing.HashAttributes(b));
        }

        [Fact]
        public void HashField_treats_null_and_empty_the_same()
            => Assert.Equal(Hashing.HashField(null), Hashing.HashField(string.Empty));

        [Fact]
        public void HashPoints_is_stable_for_equal_sequences()
        {
            var p1 = new[] { new Xyz(1, 2, 3), new Xyz(4, 5, 6) };
            var p2 = new[] { new Xyz(1, 2, 3), new Xyz(4, 5, 6) };

            Assert.Equal(Hashing.HashPoints(p1), Hashing.HashPoints(p2));
        }

        [Fact]
        public void HashPoints_rounds_below_tolerance()
        {
            var p1 = new[] { new Xyz(1.0000000, 2, 3) };
            var p2 = new[] { new Xyz(1.0000000004, 2, 3) }; // within 6 dp

            Assert.Equal(Hashing.HashPoints(p1, 6), Hashing.HashPoints(p2, 6));
        }

        [Fact]
        public void HashPoints_order_matters()
        {
            var p1 = new[] { new Xyz(1, 2, 3), new Xyz(4, 5, 6) };
            var p2 = new[] { new Xyz(4, 5, 6), new Xyz(1, 2, 3) };

            Assert.NotEqual(Hashing.HashPoints(p1), Hashing.HashPoints(p2));
        }

        // ---- HashGeometry: the same shape in a different representation is the same hash ----

        static List<Xyz> Ring(params (double x, double y)[] pts)
        {
            var list = new List<Xyz>();
            foreach (var p in pts) list.Add(new Xyz(p.x, p.y, 0));
            return list;
        }

        static NeutralGeometry Polygon(params List<Xyz>[] rings)
        {
            var g = new NeutralGeometry { Kind = NeutralGeometryKind.Polygon };
            g.Rings.AddRange(rings);
            return g;
        }

        [Fact]
        public void HashGeometry_ring_ignores_start_vertex_orientation_and_closing_point()
        {
            var closedFromA = Ring((0, 0), (10, 0), (10, 5), (0, 5), (0, 0));   // closed, starts at (0,0)
            var openFromC = Ring((10, 5), (0, 5), (0, 0), (10, 0));              // open, starts at (10,5)
            var reversed = Ring((0, 5), (10, 5), (10, 0), (0, 0));               // other way round

            var h1 = Hashing.HashGeometry(Polygon(closedFromA));
            Assert.Equal(h1, Hashing.HashGeometry(Polygon(openFromC)));
            Assert.Equal(h1, Hashing.HashGeometry(Polygon(reversed)));
        }

        [Fact]
        public void HashGeometry_ring_order_does_not_matter_but_shape_does()
        {
            var outer = Ring((0, 0), (10, 0), (10, 10), (0, 10));
            var hole = Ring((2, 2), (4, 2), (4, 4), (2, 4));
            var otherHole = Ring((2, 2), (5, 2), (5, 5), (2, 5));

            Assert.Equal(Hashing.HashGeometry(Polygon(outer, hole)), Hashing.HashGeometry(Polygon(hole, outer)));
            Assert.NotEqual(Hashing.HashGeometry(Polygon(outer, hole)), Hashing.HashGeometry(Polygon(outer, otherHole)));
        }

        [Fact]
        public void HashGeometry_polyline_parts_are_order_and_direction_independent()
        {
            var a = Ring((0, 0), (5, 1), (10, 0));
            var b = Ring((20, 0), (30, 0));
            var bReversed = Ring((30, 0), (20, 0));

            var g1 = new NeutralGeometry { Kind = NeutralGeometryKind.Polyline, Points = a };
            g1.Parts.Add(a); g1.Parts.Add(b);
            var g2 = new NeutralGeometry { Kind = NeutralGeometryKind.Polyline, Points = bReversed };
            g2.Parts.Add(bReversed); g2.Parts.Add(a);

            Assert.Equal(Hashing.HashGeometry(g1), Hashing.HashGeometry(g2));

            // A single-part line described by Points alone equals the same line held as one part.
            var single = NeutralGeometry.Polyline(a);
            var asPart = new NeutralGeometry { Kind = NeutralGeometryKind.Polyline };
            asPart.Parts.Add(a);
            Assert.Equal(Hashing.HashGeometry(single), Hashing.HashGeometry(asPart));

            // Moving a vertex is a change.
            var moved = NeutralGeometry.Polyline(Ring((0, 0), (5, 2), (10, 0)));
            Assert.NotEqual(Hashing.HashGeometry(single), Hashing.HashGeometry(moved));
        }

        [Fact]
        public void HashGeometry_rounds_transform_noise_and_negative_zero()
        {
            var exact = NeutralGeometry.Point(new Xyz(0, 100, 0));
            var noisy = NeutralGeometry.Point(new Xyz(-1e-9, 100.00000001, 1e-12));

            Assert.Equal(Hashing.HashGeometry(exact), Hashing.HashGeometry(noisy));
        }
    }
}
