using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class DefaultProfileFactoryTests
    {
        private static LayerSchema Schema()
        {
            return new LayerSchema
            {
                LayerName = "parcels",
                GeometryType = GeometryTarget.PolygonZ,
                Fields =
                {
                    new FieldDefinition { Name = "parcel_id", Type = FieldType.Text },
                    new FieldDefinition { Name = "zoning", Type = FieldType.Text }
                }
            };
        }

        [Fact]
        public void Builds_one_layer_mapping_with_all_fields()
        {
            var p = DefaultProfileFactory.ForLayer(Schema(), "ARCGIS_CONTEXT::parcels", UnitSystem.Meters, "EPSG:3857");

            Assert.Single(p.Layers);
            var layer = p.Layers[0];
            Assert.Equal("parcels", layer.ArcGisLayer);
            Assert.Equal("ARCGIS_CONTEXT::parcels", layer.RhinoLayer);
            Assert.Equal(GeometryTarget.PolygonZ, layer.GeometryTarget);
            Assert.Equal(2, layer.Attributes.Count);
        }

        [Fact]
        public void Default_fields_are_arcgis_owned_and_readonly()
        {
            var p = DefaultProfileFactory.ForLayer(Schema(), "r", UnitSystem.Meters, "EPSG:3857");
            var field = p.Layers[0].Attributes[0];
            Assert.Equal(FieldOwnership.ArcGisOwned, field.Owner);
            Assert.True(field.ReadonlyInRhino);
        }

        [Fact]
        public void Default_profile_passes_validation()
        {
            var p = DefaultProfileFactory.ForLayer(Schema(), "r", UnitSystem.Meters, "EPSG:3857");
            Assert.False(ProfileValidation.Validate(p).IsBlocking);
        }
    }
}
