using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class NeutralGeometryTransformTests
    {
        [Fact]
        public void GisToRhino_with_identity_is_unchanged()
        {
            var g = NeutralGeometry.Point(new Xyz(5, 6, 7));
            var t = new CoordinateTransform(ProjectAnchor.Identity);

            var r = NeutralGeometryTransform.GisToRhino(g, t);

            Assert.Equal(NeutralGeometryKind.Point, r.Kind);
            Assert.Equal(5, r.Points[0].X, 9);
            Assert.Equal(6, r.Points[0].Y, 9);
            Assert.Equal(7, r.Points[0].Z, 9);
        }

        [Fact]
        public void GisToRhino_maps_gis_anchor_to_rhino_origin()
        {
            var anchor = new ProjectAnchor(new Xyz(0, 0, 0), new Xyz(1000, 2000, 0), 0, 1);
            var t = new CoordinateTransform(anchor);
            var g = NeutralGeometry.Point(new Xyz(1000, 2000, 0));

            var r = NeutralGeometryTransform.GisToRhino(g, t);

            Assert.Equal(0, r.Points[0].X, 9);
            Assert.Equal(0, r.Points[0].Y, 9);
        }

        [Fact]
        public void Height_is_scaled_into_rhino_units()
        {
            var anchor = new ProjectAnchor(new Xyz(0, 0, 0), new Xyz(0, 0, 0), 0, 2.0);
            var t = new CoordinateTransform(anchor);
            var g = new NeutralGeometry { Kind = NeutralGeometryKind.Polygon, Height = 10.0 };

            var r = NeutralGeometryTransform.GisToRhino(g, t);

            // GIS length 10 with scale 2 (rhino->gis) becomes 5 rhino units.
            Assert.Equal(5.0, r.Height.Value, 9);
        }

        [Fact]
        public void Polygon_rings_are_transformed()
        {
            var anchor = new ProjectAnchor(new Xyz(0, 0, 0), new Xyz(100, 100, 0), 0, 1);
            var t = new CoordinateTransform(anchor);
            var g = NeutralGeometry.Polygon(new[]
            {
                new Xyz(100, 100, 0), new Xyz(110, 100, 0), new Xyz(110, 110, 0)
            });

            var r = NeutralGeometryTransform.GisToRhino(g, t);

            Assert.Equal(0, r.Rings[0][0].X, 9);
            Assert.Equal(10, r.Rings[0][1].X, 9);
        }

        [Fact]
        public void RhinoToGis_then_GisToRhino_round_trips_geometry()
        {
            var anchor = new ProjectAnchor(new Xyz(1, 2, 3), new Xyz(500, 600, 7), 21.5, 3.0);
            var t = new CoordinateTransform(anchor);
            var g = NeutralGeometry.Polyline(new[] { new Xyz(10, 11, 12), new Xyz(13, 14, 15) });

            var back = NeutralGeometryTransform.GisToRhino(NeutralGeometryTransform.RhinoToGis(g, t), t);

            Assert.Equal(10, back.Points[0].X, 6);
            Assert.Equal(15, back.Points[1].Z, 6);
        }
    }
}
