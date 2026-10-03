using System.Collections.Generic;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class ProfileTests
    {
        private static LayerMappingProfile ValidProfile()
        {
            return new LayerMappingProfile
            {
                ProjectName = "MUNA",
                RhinoUnits = UnitSystem.Meters,
                ArcGisCrs = "EPSG:3857",
                VerticalUnits = UnitSystem.Meters,
                ProjectAnchor = new AnchorSettings
                {
                    RhinoPoint = new[] { 0.0, 0.0, 0.0 },
                    GisPoint = new[] { 1000.0, 2000.0, 100.0 },
                    RotationDegrees = 0,
                    Scale = 1.0
                },
                Layers =
                {
                    new LayerMapping
                    {
                        Name = "Roof PV Zones",
                        RhinoLayer = "GIS::Roof::PV_Zones",
                        ArcGisLayer = "roof_pv_zones",
                        GeometryTarget = GeometryTarget.PolygonExtrusion,
                        Attributes =
                        {
                            new FieldMapping
                            {
                                RhinoKey = "height_m", ArcGisField = "height_m",
                                Type = FieldType.Double, Owner = FieldOwnership.RhinoOwned, Required = true
                            },
                            new FieldMapping
                            {
                                RhinoKey = "review_status", ArcGisField = "review_status",
                                Type = FieldType.Text, Owner = FieldOwnership.ArcGisOwned
                            }
                        }
                    }
                }
            };
        }

        [Fact]
        public void Profile_round_trips_through_json()
        {
            var original = ValidProfile();

            string json = ProfileJson.Serialize(original);
            var restored = ProfileJson.Deserialize(json);

            Assert.Equal(original.ProjectName, restored.ProjectName);
            Assert.Equal(original.RhinoUnits, restored.RhinoUnits);
            Assert.Equal(original.ArcGisCrs, restored.ArcGisCrs);
            Assert.Single(restored.Layers);
            Assert.Equal("GIS::Roof::PV_Zones", restored.Layers[0].RhinoLayer);
            Assert.Equal(GeometryTarget.PolygonExtrusion, restored.Layers[0].GeometryTarget);
            Assert.Equal(2, restored.Layers[0].Attributes.Count);
            Assert.Equal(FieldOwnership.RhinoOwned, restored.Layers[0].Attributes[0].Owner);
        }

        [Fact]
        public void Json_uses_snake_case_keys()
        {
            string json = ProfileJson.Serialize(ValidProfile());
            Assert.Contains("project_name", json);
            Assert.Contains("rhino_layer", json);
            Assert.Contains("geometry_target", json);
        }

        [Fact]
        public void Anchor_settings_convert_to_domain_anchor()
        {
            var anchor = ValidProfile().ProjectAnchor.ToProjectAnchor();
            Assert.Equal(1000.0, anchor.GisPoint.X, 9);
            Assert.Equal(1.0, anchor.Scale, 9);
        }

        [Fact]
        public void Valid_profile_passes_validation()
        {
            var result = ProfileValidation.Validate(ValidProfile());
            Assert.False(result.IsBlocking);
        }

        [Fact]
        public void Missing_crs_blocks()
        {
            var p = ValidProfile();
            p.ArcGisCrs = null;
            Assert.True(ProfileValidation.Validate(p).IsBlocking);
        }

        [Fact]
        public void Unknown_units_block()
        {
            var p = ValidProfile();
            p.RhinoUnits = UnitSystem.Unknown;
            Assert.True(ProfileValidation.Validate(p).IsBlocking);
        }

        [Fact]
        public void Duplicate_rhino_layer_blocks()
        {
            var p = ValidProfile();
            p.Layers.Add(new LayerMapping
            {
                Name = "dup",
                RhinoLayer = "GIS::Roof::PV_Zones", // same as existing
                ArcGisLayer = "other",
                GeometryTarget = GeometryTarget.PolygonZ
            });
            Assert.True(ProfileValidation.Validate(p).IsBlocking);
        }

        [Fact]
        public void Duplicate_arcgis_field_blocks()
        {
            var p = ValidProfile();
            p.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "another_key",
                ArcGisField = "height_m" // collides with existing mapping
            });
            Assert.True(ProfileValidation.Validate(p).IsBlocking);
        }

        [Fact]
        public void Authored_defaults_keep_system_fields_locked_and_can_share_design_fields()
        {
            var schema = Schema();

            var safe = ProfileAuthoring.Reconcile(schema, "Design", UnitSystem.Meters);
            Assert.Equal(FieldOwnership.Locked, safe.Layers[0].Attributes[0].Owner);
            Assert.Equal(FieldOwnership.ArcGisOwned, safe.Layers[0].Attributes[1].Owner);
            Assert.True(safe.Layers[0].Attributes[0].ReadonlyInRhino);

            var shared = ProfileAuthoring.Reconcile(
                schema, "Design", UnitSystem.Meters, makeDesignFieldsShared: true);
            Assert.Equal(FieldOwnership.Locked, shared.Layers[0].Attributes[0].Owner);
            Assert.Equal(FieldOwnership.Shared, shared.Layers[0].Attributes[1].Owner);
            Assert.False(shared.Layers[0].Attributes[1].ReadonlyInRhino);
        }

        [Fact]
        public void Reconcile_preserves_authored_rules_but_refreshes_live_schema_identity()
        {
            var schema = Schema();
            var saved = ProfileAuthoring.Reconcile(schema, "OldRhino", UnitSystem.Feet);
            var height = saved.Layers[0].Attributes[1];
            height.RhinoKey = "height_m";
            height.Owner = FieldOwnership.RhinoOwned;
            height.Units = "meters";
            height.Validators = new List<string> { "positive_number", "range:[0,200]" };
            saved.Layers[0].Attributes.Add(new FieldMapping
            {
                ArcGisField = "retired_field", RhinoKey = "old", Owner = FieldOwnership.Shared
            });

            schema.LayerName = "Buildings live";
            schema.Source = @"C:\data\project.gdb\Buildings";
            schema.Crs = "EPSG:26918";
            schema.Fields[1].Domain = new List<string> { "10", "20" };

            var reconciled = ProfileAuthoring.Reconcile(schema, "Rhino::Buildings", UnitSystem.Meters, saved);
            var mapped = reconciled.Layers[0].Attributes[1];

            Assert.Equal("Rhino::Buildings", reconciled.Layers[0].RhinoLayer);
            Assert.Equal("Buildings live", reconciled.Layers[0].ArcGisLayer);
            Assert.Equal(schema.Source, reconciled.Layers[0].ArcGisSource);
            Assert.Equal("EPSG:26918", reconciled.ArcGisCrs);
            Assert.DoesNotContain(reconciled.Layers[0].Attributes,
                mapping => mapping.ArcGisField == "retired_field");
            Assert.Equal("height_m", mapped.RhinoKey);
            Assert.Equal(FieldOwnership.RhinoOwned, mapped.Owner);
            Assert.Equal("meters", mapped.Units);
            Assert.Equal(new[] { "positive_number", "range:[0,200]" }, mapped.Validators);
            Assert.Equal(new[] { "10", "20" }, mapped.Domain);
        }

        [Fact]
        public void Duplicate_field_and_key_checks_are_case_insensitive()
        {
            var p = ValidProfile();
            p.Layers[0].Attributes.Add(new FieldMapping
            {
                RhinoKey = "HEIGHT_M",
                ArcGisField = "HEIGHT_M"
            });

            Assert.True(ProfileValidation.Validate(p).IsBlocking);
        }

        static LayerSchema Schema()
        {
            return new LayerSchema
            {
                LayerName = "Buildings",
                Source = @"C:\data\project.gdb\Buildings_v1",
                Crs = "EPSG:3857",
                GeometryType = GeometryTarget.Multipatch,
                Fields = new List<FieldDefinition>
                {
                    new FieldDefinition
                    {
                        Name = "OBJECTID", Type = FieldType.Integer,
                        Required = true, Editable = false
                    },
                    new FieldDefinition
                    {
                        Name = "height", Type = FieldType.Double,
                        Editable = true
                    }
                }
            };
        }
    }
}
