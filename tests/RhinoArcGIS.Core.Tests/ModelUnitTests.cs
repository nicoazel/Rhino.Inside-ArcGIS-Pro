using System;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    /// <summary>
    /// Every model unit places geometry at the same ground position: a point drawn 100 m east of
    /// the anchor lands 100 m east whatever the document measures in, including units the enum does
    /// not name (they arrive as Rhino's own metres-per-unit).
    /// </summary>
    public class ModelUnitTests
    {
        static EarthAnchor Anchor(UnitSystem units, double metresPerUnit = 0) => new EarthAnchor
        {
            Latitude = 40.78, Longitude = -73.97, IsSet = true, ModelBasePoint = new Xyz(0, 0, 0),
            ModelUnits = units, MetresPerModelUnit = metresPerUnit
        };

        public static TheoryData<UnitSystem, double> Units => new TheoryData<UnitSystem, double>
        {
            { UnitSystem.Microns, 0 }, { UnitSystem.Millimeters, 0 }, { UnitSystem.Centimeters, 0 },
            { UnitSystem.Decimeters, 0 }, { UnitSystem.Meters, 0 }, { UnitSystem.Kilometers, 0 },
            { UnitSystem.Inches, 0 }, { UnitSystem.Feet, 0 }, { UnitSystem.Yards, 0 }, { UnitSystem.Miles, 0 },
            { UnitSystem.Other, 20.1168 },   // a custom "chain"
            { UnitSystem.Other, 1852.0 },    // nautical miles
        };

        [Theory]
        [MemberData(nameof(Units))]
        public void A_hundred_metres_is_a_hundred_metres_in_every_unit(UnitSystem units, double metresPerUnit)
        {
            var anchor = Anchor(units, metresPerUnit);
            Assert.True(anchor.IsValid);
            double perUnit = anchor.ModelToMetres();
            var map = GeoReference.FromAnchorParameters(anchor, new Xyz(583_000, 4_514_000, 0), UnitSystem.Meters);

            var p = map.ModelToGis(new Xyz(100 / perUnit, -40 / perUnit, 12 / perUnit));
            Assert.Equal(583_100, p.X, 6);
            Assert.Equal(4_513_960, p.Y, 6);
            Assert.Equal(12, p.Z, 6);
            Assert.Equal(100 / perUnit, map.GisLengthToModel(100), 6);

            var back = map.GisToModel(p);
            Assert.Equal(100 / perUnit, back.X, 6);
        }

        [Fact]
        public void Rhino_supplied_length_wins_over_the_nominal_one()
        {
            // Should the two ever disagree, the application's own scale is the truth.
            var anchor = Anchor(UnitSystem.Feet, 0.3048);
            Assert.Equal(0.3048, anchor.ModelToMetres());
            Assert.Equal(0.9144, Anchor(UnitSystem.Yards).ModelToMetres());
        }

        [Fact]
        public void Documents_without_units_are_not_georeferenced()
        {
            Assert.False(Anchor(UnitSystem.Unknown).IsValid);
            Assert.False(Anchor(UnitSystem.Other).IsValid);      // no length given
            Assert.False(Anchor(UnitSystem.Other, double.NaN).IsValid);
        }

        [Fact]
        public void A_crs_unit_the_enum_does_not_name_is_used_exactly_not_as_metres()
        {
            const double clarkeFoot = 0.3047972654;
            var map = GeoReference.FromAnchorParameters(Anchor(UnitSystem.Meters), new Xyz(0, 0, 0), clarkeFoot);
            Assert.Equal(100 / clarkeFoot, map.ModelToGis(new Xyz(100, 0, 0)).X, 6);
        }
    }
}
