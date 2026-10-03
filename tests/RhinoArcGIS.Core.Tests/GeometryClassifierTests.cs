using RhinoArcGIS.Core.Geometry;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class GeometryClassifierTests
    {
        private static GeometryDescriptor Desc(RhinoGeometryKind kind)
            => new GeometryDescriptor(kind);

        [Fact]
        public void Point_maps_to_point_z_ready()
        {
            var r = GeometryClassifier.Classify(Desc(RhinoGeometryKind.Point));
            Assert.Equal(GeometryTarget.PointZ, r.Default);
            Assert.Equal(ClassificationStatus.Ready, r.Status);
        }

        [Fact]
        public void Closed_planar_curve_maps_to_polygon_with_polyline_alternate()
        {
            var r = GeometryClassifier.Classify(Desc(RhinoGeometryKind.ClosedPlanarCurve));
            Assert.Equal(GeometryTarget.PolygonZ, r.Default);
            Assert.Contains(GeometryTarget.PolylineZ, r.Alternates);
        }

        [Fact]
        public void Open_curve_maps_to_polyline()
        {
            var r = GeometryClassifier.Classify(Desc(RhinoGeometryKind.OpenCurve));
            Assert.Equal(GeometryTarget.PolylineZ, r.Default);
        }

        [Fact]
        public void Closed_brep_maps_to_multipatch_with_extrusion_alternate()
        {
            var r = GeometryClassifier.Classify(Desc(RhinoGeometryKind.ClosedBrep));
            Assert.Equal(GeometryTarget.Multipatch, r.Default);
            Assert.Contains(GeometryTarget.PolygonExtrusion, r.Alternates);
        }

        [Fact]
        public void Open_brep_warns()
        {
            var r = GeometryClassifier.Classify(Desc(RhinoGeometryKind.OpenBrep));
            Assert.Equal(GeometryTarget.Multipatch, r.Default);
            Assert.Equal(ClassificationStatus.ReadyWithWarnings, r.Status);
        }

        [Fact]
        public void Subd_needs_user_decision()
        {
            var r = GeometryClassifier.Classify(Desc(RhinoGeometryKind.SubD));
            Assert.Equal(ClassificationStatus.NeedsUserDecision, r.Status);
        }

        [Fact]
        public void Extrusion_maps_to_polygon_extrusion()
        {
            var r = GeometryClassifier.Classify(Desc(RhinoGeometryKind.Extrusion));
            Assert.Equal(GeometryTarget.PolygonExtrusion, r.Default);
        }

        [Fact]
        public void Empty_geometry_fails()
        {
            var d = new GeometryDescriptor(RhinoGeometryKind.Point) { IsEmpty = true };
            var r = GeometryClassifier.Classify(d);
            Assert.Equal(ClassificationStatus.Failed, r.Status);
            Assert.Equal(GeometryTarget.Unsupported, r.Default);
        }

        [Fact]
        public void Null_descriptor_fails()
        {
            var r = GeometryClassifier.Classify(null);
            Assert.Equal(ClassificationStatus.Failed, r.Status);
        }
    }
}
