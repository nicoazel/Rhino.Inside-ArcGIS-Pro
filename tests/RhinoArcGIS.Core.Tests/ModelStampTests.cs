using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    /// <summary>
    /// The model stamp lets planning skip mapping untouched Rhino objects into the CRS. It must
    /// never let a real change through: every case here is judged the same with and without it.
    /// </summary>
    public class ModelStampTests
    {
        static readonly ICoordinateMap A = new GeoReference(AffineTransform.Translation(new Xyz(1000, 2000, 0)));
        static readonly ICoordinateMap B = new GeoReference(AffineTransform.Translation(new Xyz(1000.5, 2000, 0)));

        static readonly LayerMapping Layer = new LayerMapping
        {
            Name = "l", RhinoLayer = "l", ArcGisLayer = "l", GeometryTarget = GeometryTarget.PolylineZ
        };

        static FeatureRecord Feature() => new FeatureRecord
        {
            Target = GeometryTarget.PolylineZ,
            Geometry = new NeutralGeometry
            {
                Kind = NeutralGeometryKind.Polyline,
                Parts = new List<List<Xyz>> { new List<Xyz> { new Xyz(1010, 2010, 5), new Xyz(1050, 2012, 5), new Xyz(1090, 2030, 5) } }
            },
            Identity = new SyncIdentity { ArcGisObjectId = 7 }
        };

        /// <summary>A Rhino object baselined against <paramref name="f"/> under <paramref name="map"/>.</summary>
        static RhinoObjectSnapshot Pulled(FeatureRecord f, ICoordinateMap map, bool stamp = true)
        {
            var model = NeutralGeometryTransform.GisToRhino(f.Geometry, map);
            var us = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { GisKeys.SyncGuid, Guid.NewGuid().ToString() },
                { GisKeys.ArcGisObjectId, "7" },
                { GisKeys.GeometryHash, Hashing.HashGeometry(f.Geometry) }
            };
            if (stamp) us[GisKeys.RhinoModelStamp] = ModelStamp.Of(model, ModelStamp.Fingerprint(map));
            return new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(), LayerName = "l",
                Descriptor = new GeometryDescriptor(RhinoGeometryKind.OpenCurve), Geometry = model, UserStrings = us
            };
        }

        static SyncState Plan(ICoordinateMap map, RhinoObjectSnapshot snap, FeatureRecord f) =>
            new SyncEngine().BuildPlan(map, Layer, new[] { snap }, new[] { f }).Decisions.Single().State;

        static RhinoObjectSnapshot WithoutStamp(RhinoObjectSnapshot s)
        {
            var us = new Dictionary<string, string>(s.UserStrings, StringComparer.Ordinal);
            us.Remove(GisKeys.RhinoModelStamp);
            return new RhinoObjectSnapshot { RhinoGuid = s.RhinoGuid, LayerName = s.LayerName, Descriptor = s.Descriptor, Geometry = s.Geometry, UserStrings = us };
        }

        [Fact]
        public void An_untouched_object_is_clean_with_or_without_its_stamp()
        {
            var f = Feature();
            var snap = Pulled(f, A);
            Assert.Equal(SyncState.Clean, Plan(A, snap, f));
            Assert.Equal(SyncState.Clean, Plan(A, WithoutStamp(snap), f));
        }

        [Fact]
        public void A_moved_object_is_modified_even_though_it_carries_a_stamp()
        {
            var f = Feature();
            var snap = Pulled(f, A);
            snap.Geometry.Parts[0][1] = new Xyz(snap.Geometry.Parts[0][1].X + 0.25, snap.Geometry.Parts[0][1].Y, 5);
            Assert.Equal(SyncState.ModifiedInRhino, Plan(A, snap, f));
        }

        [Fact]
        public void A_move_far_below_the_crs_hash_rounding_still_invalidates_the_stamp()
        {
            // The CRS hash rounds to 1e-4; the stamp must not, or a model in kilometres could
            // hide a 5 cm edit. The plan then falls back and judges it exactly as before.
            var f = Feature();
            var snap = Pulled(f, A);
            var stamp = snap.UserStrings[GisKeys.RhinoModelStamp];
            snap.Geometry.Parts[0][0] = new Xyz(snap.Geometry.Parts[0][0].X + 1e-7, snap.Geometry.Parts[0][0].Y, 5);
            Assert.False(ModelStamp.Matches(stamp, snap.Geometry, ModelStamp.Fingerprint(A)));
            Assert.Equal(Plan(A, WithoutStamp(snap), f), Plan(A, snap, f));
        }

        [Fact]
        public void A_changed_georeference_is_judged_as_if_there_were_no_stamp()
        {
            var f = Feature();
            var snap = Pulled(f, A);
            Assert.NotEqual(ModelStamp.Fingerprint(A), ModelStamp.Fingerprint(B));
            var withStamp = Plan(B, snap, f);
            Assert.Equal(Plan(B, WithoutStamp(snap), f), withStamp);
            Assert.Equal(SyncState.ModifiedInRhino, withStamp);
        }

        [Fact]
        public void A_stamp_from_other_geometry_never_vouches_for_this_one()
        {
            var f = Feature();
            var snap = Pulled(f, A);
            var other = Pulled(f, B);
            snap.UserStrings[GisKeys.RhinoModelStamp] = other.UserStrings[GisKeys.RhinoModelStamp];
            snap.Geometry.Parts[0][2] = new Xyz(0, 0, 0);
            Assert.Equal(SyncState.ModifiedInRhino, Plan(A, snap, f));
        }
    }
}
