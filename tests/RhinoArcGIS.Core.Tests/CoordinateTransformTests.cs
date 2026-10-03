using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class CoordinateTransformTests
    {
        [Fact]
        public void Identity_anchor_leaves_coordinates_unchanged()
        {
            var t = new CoordinateTransform(ProjectAnchor.Identity);
            var p = new Xyz(3, 4, 5);

            var gis = t.RhinoToGis(p);

            Assert.Equal(3, gis.X, 9);
            Assert.Equal(4, gis.Y, 9);
            Assert.Equal(5, gis.Z, 9);
        }

        [Fact]
        public void Anchor_rhino_point_maps_to_gis_point()
        {
            var anchor = new ProjectAnchor(new Xyz(10, 20, 5), new Xyz(1000, 2000, 100), 30, 2);
            var t = new CoordinateTransform(anchor);

            var gis = t.RhinoToGis(anchor.RhinoPoint);

            Assert.Equal(1000, gis.X, 9);
            Assert.Equal(2000, gis.Y, 9);
            Assert.Equal(100, gis.Z, 9);
        }

        [Fact]
        public void RhinoToGis_then_GisToRhino_round_trips()
        {
            var anchor = new ProjectAnchor(new Xyz(10, 20, 5), new Xyz(1000, 2000, 100), 33.7, 2.5);
            var t = new CoordinateTransform(anchor);
            var original = new Xyz(42.5, -17.25, 8.0);

            var back = t.GisToRhino(t.RhinoToGis(original));

            Assert.Equal(original.X, back.X, 9);
            Assert.Equal(original.Y, back.Y, 9);
            Assert.Equal(original.Z, back.Z, 9);
        }

        [Fact]
        public void Ninety_degree_rotation_swaps_axes()
        {
            var anchor = new ProjectAnchor(new Xyz(0, 0, 0), new Xyz(0, 0, 0), 90, 1);
            var t = new CoordinateTransform(anchor);

            var gis = t.RhinoToGis(new Xyz(1, 0, 0));

            // (1,0) rotated +90° about Z → (0,1)
            Assert.Equal(0, gis.X, 9);
            Assert.Equal(1, gis.Y, 9);
        }

        [Fact]
        public void Relative_elevation_mode_does_not_add_anchor_z()
        {
            var anchor = new ProjectAnchor(new Xyz(0, 0, 0), new Xyz(0, 0, 100), 0, 1);
            var t = new CoordinateTransform(anchor, ElevationMode.Relative);

            var gis = t.RhinoToGis(new Xyz(0, 0, 3));

            Assert.Equal(3, gis.Z, 9);
        }
    }
}
