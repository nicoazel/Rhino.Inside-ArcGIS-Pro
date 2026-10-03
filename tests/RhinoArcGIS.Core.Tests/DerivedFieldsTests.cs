using System.Collections.Generic;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class DerivedFieldsTests
    {
        static List<Xyz> Rect(double x0, double y0, double x1, double y1) => new List<Xyz>
        {
            new Xyz(x0, y0, 0), new Xyz(x1, y0, 0), new Xyz(x1, y1, 0), new Xyz(x0, y1, 0)
        };

        [Theory]
        [InlineData("Shape_Length", DerivedMetric.Length)]
        [InlineData("Shape_Leng", DerivedMetric.Length)]
        [InlineData("Shape_Le_1", DerivedMetric.Length)]
        [InlineData("SHAPE_len", DerivedMetric.Length)]
        [InlineData("Shape_Area", DerivedMetric.Area)]
        [InlineData("Shape_Ar_1", DerivedMetric.Area)]
        [InlineData("Shape", DerivedMetric.None)]
        [InlineData("Shape_STLength", DerivedMetric.None)]
        [InlineData("area_gross", DerivedMetric.None)]
        [InlineData("", DerivedMetric.None)]
        public void Field_names_map_to_metrics(string field, DerivedMetric expected)
            => Assert.Equal(expected, DerivedFields.MetricOf(field));

        [Fact]
        public void Rectangle_area_and_perimeter()
        {
            var polygon = new NeutralGeometry { Kind = NeutralGeometryKind.Polygon, Rings = { Rect(0, 0, 30, 20) } };

            Assert.Equal("600", DerivedFields.Compute("Shape_Area", polygon));
            Assert.Equal("100", DerivedFields.Compute("Shape_Leng", polygon));
        }

        [Fact]
        public void Ring_orientation_does_not_matter_and_nested_rings_are_holes()
        {
            var outer = Rect(0, 0, 30, 20);
            var hole = Rect(5, 5, 10, 10);
            hole.Reverse();                       // same orientation as the outer ring: still a hole
            var island = Rect(100, 100, 102, 101);
            var polygon = new NeutralGeometry { Kind = NeutralGeometryKind.Polygon, Rings = { hole, outer, island } };

            Assert.Equal(600 - 25 + 2, DerivedFields.Measure(DerivedMetric.Area, polygon));
            Assert.Equal(100 + 20 + 6, DerivedFields.Measure(DerivedMetric.Length, polygon));
        }

        [Fact]
        public void Polyline_length_sums_every_part()
        {
            var line = new NeutralGeometry
            {
                Kind = NeutralGeometryKind.Polyline,
                Parts =
                {
                    new List<Xyz> { new Xyz(0, 0, 0), new Xyz(3, 4, 0) },
                    new List<Xyz> { new Xyz(10, 0, 0), new Xyz(10, 2, 0), new Xyz(12, 2, 0) }
                }
            };

            Assert.Equal(9, DerivedFields.Measure(DerivedMetric.Length, line));
            Assert.Null(DerivedFields.Compute("Shape_Area", line));

            var single = NeutralGeometry.Polyline(new[] { new Xyz(0, 0, 0), new Xyz(0, 5, 0) });
            Assert.Equal("5", DerivedFields.Compute("Shape_Length", single));
        }

        [Fact]
        public void Points_have_no_metrics()
        {
            var point = NeutralGeometry.Point(new Xyz(1, 2, 3));
            Assert.Null(DerivedFields.Compute("Shape_Length", point));
            Assert.Null(DerivedFields.Compute("Shape_Area", point));
            Assert.Null(DerivedFields.Compute("Shape_Area", null));
        }
    }
}
