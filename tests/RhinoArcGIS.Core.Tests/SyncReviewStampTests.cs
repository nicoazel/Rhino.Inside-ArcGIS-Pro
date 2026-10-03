using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public sealed class SyncReviewStampTests
    {
        [Fact]
        public void Rhino_geometry_and_every_user_string_change_the_stamp()
        {
            var item = RhinoObject();
            var original = Compute(rhino: new[] { item });

            item.Geometry.Points[0] = new Xyz(1.0000000000000002, 2, 3);
            var moved = Compute(rhino: new[] { item });
            item.UserStrings["design_note"] = "changed";
            var retagged = Compute(rhino: new[] { item });

            Assert.NotEqual(original, moved);
            Assert.NotEqual(moved, retagged);
        }

        [Fact]
        public void Arcgis_ids_globalids_geometry_and_attributes_change_the_stamp()
        {
            var feature = Feature();
            var original = Compute(features: new[] { feature });

            feature.Identity.ArcGisObjectId++;
            var newObjectId = Compute(features: new[] { feature });
            feature.Identity.ArcGisObjectId--;
            feature.Identity.ArcGisGlobalId = Guid.NewGuid();
            var newGlobalId = Compute(features: new[] { feature });
            feature.Geometry.Points[0] = new Xyz(9, 8, 7);
            var moved = Compute(features: new[] { feature });
            feature.Attributes["name"] = "changed";
            var edited = Compute(features: new[] { feature });

            Assert.NotEqual(original, newObjectId);
            Assert.NotEqual(original, newGlobalId);
            Assert.NotEqual(newGlobalId, moved);
            Assert.NotEqual(moved, edited);
        }

        [Fact]
        public void Document_and_georeference_state_change_the_stamp()
        {
            var anchor = new EarthAnchor
            {
                IsSet = true,
                Latitude = 41.2,
                Longitude = -87.3,
                ModelBasePoint = new Xyz(10, 20, 0),
                ModelUnits = UnitSystem.Meters
            };
            var original = Compute(serial: 4, mode: "local-frame", anchor: anchor);

            var differentDocument = Compute(serial: 5, mode: "local-frame", anchor: anchor);
            var differentMode = Compute(serial: 4, mode: "planar", anchor: anchor);
            anchor.NorthAngleDegrees = 1;
            var differentAnchor = Compute(serial: 4, mode: "local-frame", anchor: anchor);

            Assert.NotEqual(original, differentDocument);
            Assert.NotEqual(original, differentMode);
            Assert.NotEqual(original, differentAnchor);
        }

        [Fact]
        public void Rhino_ledger_and_arcgis_schema_changes_change_the_stamp()
        {
            var schema = new LayerSchema
            {
                LayerName = "Parcels",
                Source = @"D:\data.gdb\Parcels",
                Crs = "EPSG:26916",
                Fields = new List<FieldDefinition>
                {
                    new FieldDefinition { Name = "height", Type = RhinoArcGIS.Core.Attributes.FieldType.Double }
                }
            };
            var original = Compute(ledger: "g:1", schema: schema);

            var differentLedger = Compute(ledger: "g:2", schema: schema);
            schema.Fields[0].Editable = false;
            var differentSchema = Compute(ledger: "g:1", schema: schema);

            Assert.NotEqual(original, differentLedger);
            Assert.NotEqual(original, differentSchema);
        }

        [Fact]
        public void Canonical_collection_order_and_attribute_framing_are_stable()
        {
            var first = RhinoObject();
            var second = RhinoObject();
            second.RhinoGuid = Guid.NewGuid();
            var a = Feature();
            var b = Feature();
            b.Identity.ArcGisObjectId = 2;
            a.Attributes.Clear();
            b.Attributes.Clear();
            a.Attributes["a"] = "bc";
            b.Attributes["ab"] = "c";

            var forward = Compute(rhino: new[] { first, second }, features: new[] { a, b });
            var reversed = Compute(rhino: new[] { second, first }, features: new[] { b, a });
            Assert.Equal(forward, reversed);
        }

        static string Compute(uint serial = 1, string mode = "test", EarthAnchor anchor = null,
            IEnumerable<RhinoObjectSnapshot> rhino = null, IEnumerable<FeatureRecord> features = null,
            string ledger = null, LayerSchema schema = null) =>
            SyncReviewStamp.Compute(serial, mode, anchor, ledger, schema, rhino, features);

        static RhinoObjectSnapshot RhinoObject() => new RhinoObjectSnapshot
        {
            RhinoGuid = Guid.NewGuid(),
            LayerName = "Design",
            Geometry = NeutralGeometry.Point(new Xyz(1, 2, 3)),
            UserStrings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["gis.sync_guid"] = Guid.NewGuid().ToString("D"),
                ["design_note"] = "original"
            }
        };

        static FeatureRecord Feature() => new FeatureRecord
        {
            Identity = new RhinoArcGIS.Core.Identity.SyncIdentity
            {
                ArcGisObjectId = 1,
                ArcGisGlobalId = Guid.NewGuid()
            },
            Geometry = NeutralGeometry.Point(new Xyz(4, 5, 6)),
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "original" }
        };
    }
}
