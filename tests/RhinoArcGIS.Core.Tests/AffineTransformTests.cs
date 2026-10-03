using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class AffineTransformTests
    {
        [Fact]
        public void Identity_is_a_no_op()
        {
            var p = new Xyz(3, -4, 5);
            var r = AffineTransform.Identity.Apply(p);
            Assert.Equal(3, r.X, 9);
            Assert.Equal(-4, r.Y, 9);
            Assert.Equal(5, r.Z, 9);
        }

        [Fact]
        public void Translation_offsets_point()
        {
            var r = AffineTransform.Translation(new Xyz(10, 20, 30)).Apply(new Xyz(1, 2, 3));
            Assert.Equal(11, r.X, 9);
            Assert.Equal(22, r.Y, 9);
            Assert.Equal(33, r.Z, 9);
        }

        [Fact]
        public void Scale_multiplies_point()
        {
            var r = AffineTransform.Scale(2.0).Apply(new Xyz(1, 2, 3));
            Assert.Equal(2, r.X, 9);
            Assert.Equal(4, r.Y, 9);
            Assert.Equal(6, r.Z, 9);
        }

        [Fact]
        public void RotationZ_90_maps_x_to_y()
        {
            var r = AffineTransform.RotationZ(90).Apply(new Xyz(1, 0, 0));
            Assert.Equal(0, r.X, 9);
            Assert.Equal(1, r.Y, 9);
        }

        [Fact]
        public void Compose_applies_right_then_left()
        {
            // scale then translate: T.Compose(S) applies S first
            var t = AffineTransform.Translation(new Xyz(100, 0, 0)).Compose(AffineTransform.Scale(2.0));
            var r = t.Apply(new Xyz(1, 0, 0));
            Assert.Equal(102, r.X, 9); // 1*2 + 100
        }

        [Fact]
        public void Inverse_round_trips_a_composed_transform()
        {
            var t = AffineTransform.Translation(new Xyz(500, 600, 7))
                .Compose(AffineTransform.RotationZ(33.3))
                .Compose(AffineTransform.Scale(2.5));
            var inv = t.Inverse();
            var p = new Xyz(12.5, -8.25, 4.0);

            var back = inv.Apply(t.Apply(p));
            Assert.Equal(p.X, back.X, 9);
            Assert.Equal(p.Y, back.Y, 9);
            Assert.Equal(p.Z, back.Z, 9);
        }

        [Fact]
        public void FromRowMajor_extracts_linear_and_translation()
        {
            // scale 2 with translation (10,20,30)
            var m = new double[]
            {
                2, 0, 0, 10,
                0, 2, 0, 20,
                0, 0, 2, 30,
                0, 0, 0, 1
            };
            var t = AffineTransform.FromRowMajor4x4(m);
            var r = t.Apply(new Xyz(1, 1, 1));
            Assert.Equal(12, r.X, 9);
            Assert.Equal(22, r.Y, 9);
            Assert.Equal(32, r.Z, 9);
        }

        [Fact]
        public void ScaleFactor_reports_uniform_scale()
        {
            Assert.Equal(2.5, AffineTransform.Scale(2.5).ScaleFactor(), 9);
            Assert.Equal(1.0, AffineTransform.RotationZ(45).ScaleFactor(), 9);
        }
    }
}
