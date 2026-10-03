using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class PlanarRingTriangulationTests
    {
        [Fact]
        public void Concave_ring_is_tessellated_inside_its_boundary_with_matching_area()
        {
            Xyz[] ring =
            {
                new Xyz(0, 0, 0), new Xyz(3, 0, 0), new Xyz(3, 1, 0),
                new Xyz(1, 1, 0), new Xyz(1, 3, 0), new Xyz(0, 3, 0)
            };

            IReadOnlyList<int[]> triangles = PlanarRingTriangulation.Triangulate(ring);

            Assert.Equal(ring.Length - 2, triangles.Count);
            Assert.Equal(5.0, triangles.Sum(t => Math.Abs(TwiceArea(ring[t[0]], ring[t[1]], ring[t[2]])) / 2.0), 10);
            foreach (int[] triangle in triangles)
            {
                Xyz a = ring[triangle[0]], b = ring[triangle[1]], c = ring[triangle[2]];
                Assert.True(InsideL(a));
                Assert.True(InsideL(b));
                Assert.True(InsideL(c));
                Assert.True(InsideL(Midpoint(a, b)));
                Assert.True(InsideL(Midpoint(b, c)));
                Assert.True(InsideL(Midpoint(c, a)));
                Assert.True(InsideL(new Xyz((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, 0)));
            }
        }

        [Fact]
        public void Reversed_winding_is_preserved()
        {
            Xyz[] ring =
            {
                new Xyz(0, 0, 0), new Xyz(0, 2, 0), new Xyz(1, 2, 0),
                new Xyz(1, 1, 0), new Xyz(3, 1, 0), new Xyz(3, 0, 0)
            };

            IReadOnlyList<int[]> triangles = PlanarRingTriangulation.Triangulate(ring);

            Assert.Equal(ring.Length - 2, triangles.Count);
            Assert.All(triangles, t => Assert.True(TwiceArea(ring[t[0]], ring[t[1]], ring[t[2]]) < 0));
            Assert.Equal(-4.0, triangles.Sum(t => TwiceArea(ring[t[0]], ring[t[1]], ring[t[2]]) / 2.0), 10);
        }

        [Fact]
        public void Vertical_and_tilted_planar_rings_are_supported()
        {
            Xyz[] vertical =
            {
                new Xyz(2, 0, 0), new Xyz(2, 3, 0), new Xyz(2, 3, 2), new Xyz(2, 0, 2)
            };
            Xyz[] tilted =
            {
                new Xyz(0, 0, 0), new Xyz(2, 0, 2), new Xyz(2, 1, 2), new Xyz(0, 1, 0)
            };

            Assert.Equal(2, PlanarRingTriangulation.Triangulate(vertical).Count);
            Assert.Equal(2, PlanarRingTriangulation.Triangulate(tilted).Count);
        }

        [Fact]
        public void Repeated_closure_and_consecutive_duplicate_points_are_removed()
        {
            Xyz[] ring =
            {
                new Xyz(0, 0, 0), new Xyz(2, 0, 0), new Xyz(2, 0, 0),
                new Xyz(2, 1, 0), new Xyz(0, 1, 0), new Xyz(0, 0, 0)
            };

            IReadOnlyList<int[]> triangles = PlanarRingTriangulation.Triangulate(ring);

            Assert.Equal(2, triangles.Count);
            Assert.Equal(2.0, triangles.Sum(t => Math.Abs(TwiceArea(ring[t[0]], ring[t[1]], ring[t[2]])) / 2.0), 10);
            Assert.DoesNotContain(triangles.SelectMany(t => t), index => index == ring.Length - 1);
        }

        [Fact]
        public void Multi_ring_group_is_rejected_instead_of_filling_holes()
        {
            IReadOnlyList<Xyz>[] group =
            {
                new[] { new Xyz(0, 0), new Xyz(4, 0), new Xyz(4, 4), new Xyz(0, 4) },
                new[] { new Xyz(1, 1), new Xyz(2, 1), new Xyz(2, 2), new Xyz(1, 2) }
            };

            NotSupportedException error = Assert.Throws<NotSupportedException>(
                () => PlanarRingTriangulation.TriangulateRingGroup(group));

            Assert.Contains("multiple rings", error.Message);
        }

        [Fact]
        public void Degenerate_collinear_ring_returns_no_faces_and_nonplanar_input_is_rejected()
        {
            Assert.Empty(PlanarRingTriangulation.Triangulate(new[]
            {
                new Xyz(0, 0), new Xyz(1, 1), new Xyz(2, 2), new Xyz(0, 0)
            }));

            Assert.Throws<ArgumentException>(() => PlanarRingTriangulation.Triangulate(new[]
            {
                new Xyz(0, 0, 0), new Xyz(1, 0, 0), new Xyz(1, 1, 0.1), new Xyz(0, 1, 0)
            }));

            Assert.Throws<ArgumentException>(() => PlanarRingTriangulation.Triangulate(new[]
            {
                new Xyz(0, 0), new Xyz(2, 2), new Xyz(0, 2), new Xyz(2, 0)
            }));
        }

        static double TwiceArea(Xyz a, Xyz b, Xyz c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        static Xyz Midpoint(Xyz a, Xyz b) => new Xyz((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0);

        static bool InsideL(Xyz p) =>
            p.X >= -1e-10 && p.Y >= -1e-10 && p.X <= 3 + 1e-10 && p.Y <= 3 + 1e-10 &&
            !(p.X > 1 + 1e-10 && p.Y > 1 + 1e-10);
    }
}
