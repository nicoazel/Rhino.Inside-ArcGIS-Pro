using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class GeoReferenceTests
    {
        [Fact]
        public void FromProjectAnchor_matches_legacy_coordinate_transform()
        {
            var anchor = new ProjectAnchor(new Xyz(10, 20, 5), new Xyz(1000, 2000, 100), 33.7, 2.5);
            var legacy = new CoordinateTransform(anchor);
            var geo = GeoReference.FromProjectAnchor(anchor);

            foreach (var p in new[] { new Xyz(0, 0, 0), new Xyz(42.5, -17.25, 8), new Xyz(-100, 200, 3) })
            {
                var a = legacy.RhinoToGis(p);
                var b = geo.ModelToGis(p);
                Assert.Equal(a.X, b.X, 6);
                Assert.Equal(a.Y, b.Y, 6);
                Assert.Equal(a.Z, b.Z, 6);
            }
        }

        [Fact]
        public void Model_base_point_maps_to_projected_origin()
        {
            // feet model, modelToEarth = scale 0.3048 (ft->m), anchor projected at (500000,4000000)
            var geo = GeoReference.FromModelToEarth(AffineTransform.Scale(0.3048), new Xyz(500000, 4000000, 0), UnitSystem.Meters);

            var origin = geo.ModelToGis(new Xyz(0, 0, 0));
            Assert.Equal(500000, origin.X, 6);
            Assert.Equal(4000000, origin.Y, 6);
        }

        [Fact]
        public void Feet_model_offset_lands_at_correct_metre_coordinate()
        {
            var geo = GeoReference.FromModelToEarth(AffineTransform.Scale(0.3048), new Xyz(500000, 4000000, 0), UnitSystem.Meters);

            // 100 ft east, 200 ft north → 30.48 m, 60.96 m from origin
            var gis = geo.ModelToGis(new Xyz(100, 200, 0));
            Assert.Equal(500030.48, gis.X, 6);
            Assert.Equal(4000060.96, gis.Y, 6);
        }

        [Fact]
        public void Round_trips_model_to_gis_to_model()
        {
            var geo = GeoReference.FromModelToEarth(AffineTransform.Scale(0.3048), new Xyz(500000, 4000000, 12), UnitSystem.Meters);
            var p = new Xyz(123.4, -56.7, 8.0);
            var back = geo.GisToModel(geo.ModelToGis(p));
            Assert.Equal(p.X, back.X, 6);
            Assert.Equal(p.Y, back.Y, 6);
            Assert.Equal(p.Z, back.Z, 6);
        }

        [Fact]
        public void Unit_length_scale_is_correct_for_metre_crs()
        {
            var geo = GeoReference.FromModelToEarth(AffineTransform.Scale(0.3048), new Xyz(0, 0, 0), UnitSystem.Meters);
            // 1 model foot is 0.3048 GIS metres
            Assert.Equal(0.3048, geo.ModelLengthToGis(1.0), 9);
        }

        [Fact]
        public void Feet_crs_keeps_feet_model_one_to_one()
        {
            // model feet, CRS also feet → net length scale 1.0
            var geo = GeoReference.FromModelToEarth(AffineTransform.Scale(0.3048), new Xyz(0, 0, 0), UnitSystem.Feet);
            Assert.Equal(1.0, geo.ModelLengthToGis(1.0), 6);

            var gis = geo.ModelToGis(new Xyz(100, 200, 0));
            Assert.Equal(100, gis.X, 4);
            Assert.Equal(200, gis.Y, 4);
        }

        [Fact]
        public void FromAnchorParameters_no_rotation_places_correctly()
        {
            var anchor = new EarthAnchor
            {
                ModelUnits = UnitSystem.Meters,
                ModelBasePoint = new Xyz(0, 0, 0),
                NorthAngleDegrees = 0,
                IsSet = true
            };
            var geo = GeoReference.FromAnchorParameters(anchor, new Xyz(500, 600, 0), UnitSystem.Meters);

            var gis = geo.ModelToGis(new Xyz(10, 20, 0));
            Assert.Equal(510, gis.X, 6);
            Assert.Equal(620, gis.Y, 6);
        }

        [Fact]
        public void FromAnchorParameters_respects_non_zero_model_base_point()
        {
            var anchor = new EarthAnchor
            {
                ModelUnits = UnitSystem.Meters,
                ModelBasePoint = new Xyz(100, 100, 0),
                NorthAngleDegrees = 0,
                IsSet = true
            };
            var geo = GeoReference.FromAnchorParameters(anchor, new Xyz(500, 600, 0), UnitSystem.Meters);

            // the base point itself maps to the projected origin
            var atOrigin = geo.ModelToGis(new Xyz(100, 100, 0));
            Assert.Equal(500, atOrigin.X, 6);
            Assert.Equal(600, atOrigin.Y, 6);
        }
    }
}
