using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    /// <summary>
    /// The exact local frame. The GIS side measures its CRS's frame at the anchor (the ArcGIS
    /// adapter does it with GeodeticMove and Project); the core must turn that into a model map
    /// that honours scale, grid convergence and units -- and only for documents that asked for it.
    /// </summary>
    public class GeoReferenceLocalFrameTests
    {
        static EarthAnchor Anchor(double north = 0) => new EarthAnchor
        {
            Latitude = 40.7813, Longitude = -73.96635, IsSet = true, ModelUnits = UnitSystem.Meters,
            ModelBasePoint = new Xyz(0, 0, 0), NorthAngleDegrees = north
        };

        /// <summary>A CRS described only by its measured frame, as the ArcGIS adapter reports it.</summary>
        sealed class MeasuredCrs : IArcGISAdapter, ICrsProjector, ILocalFrameProjector
        {
            readonly LocalFrame _frame;
            readonly UnitSystem _units;
            public MeasuredCrs(double scale, double convergenceDegrees, UnitSystem units)
            {
                // One ground metre east and north, turned by the convergence, scaled, in CRS units.
                double a = convergenceDegrees * Math.PI / 180, perMetre = scale / units.MetersPerUnit();
                _units = units;
                _frame = new LocalFrame
                {
                    Origin = new Xyz(583_000, 4_514_000, 0),
                    EastPerMetre = new Xyz(perMetre * Math.Cos(a), perMetre * Math.Sin(a), 0),
                    NorthPerMetre = new Xyz(-perMetre * Math.Sin(a), perMetre * Math.Cos(a), 0),
                    VerticalPerMetre = 1 / units.MetersPerUnit()
                };
            }
            public LocalFrame GetLocalFrame(double lat, double lon, double h) => _frame;
            public Xyz ProjectFromWgs84(double lat, double lon, double h) => _frame.Origin;
            public UnitSystem GetCrsLinearUnit() => _units;
            public IReadOnlyList<string> GetLayerNames() => Array.Empty<string>();
            public LayerSchema GetSchema(string layerName) => null;
            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) => Array.Empty<FeatureRecord>();
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => Array.Empty<FeatureRecord>();
            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features) => Array.Empty<long>();
            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features) { }
            public void RefreshScene() { }
        }

        sealed class Doc : IRhinoAdapter, IEarthAnchorSource, IDocumentStringStore
        {
            public readonly Dictionary<string, string> Strings = new Dictionary<string, string>();
            public EarthAnchor Anchor;
            public EarthAnchor GetEarthAnchor() => Anchor;
            public AffineTransform GetModelToEarthMetres() => AffineTransform.Identity;
            public string GetDocumentString(string key) => Strings.TryGetValue(key, out var v) ? v : null;
            public void SetDocumentString(string key, string value) => Strings[key] = value;
            public UnitSystem GetUnits() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => Array.Empty<string>();
            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName) => Array.Empty<RhinoObjectSnapshot>();
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => Array.Empty<RhinoObjectSnapshot>();
            public RhinoObjectSnapshot ReadObject(Guid rhinoGuid) => null;
            public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings) { }
            public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings) => Guid.Empty;
            public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry) { }
            public void IsolateFailed(IEnumerable<Guid> rhinoGuids) { }
        }

        static ICoordinateMap Exact(MeasuredCrs crs, double north = 0)
        {
            var doc = new Doc { Anchor = Anchor(north) };
            doc.Strings[GeoReferenceFactory.ModeKey] = GeoReferenceFactory.LocalFrameMode;
            return GeoReferenceFactory.Create(doc, crs, new LayerMappingProfile());
        }

        static double Distance(Xyz a, Xyz b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        [Fact]
        public void A_rhino_metre_becomes_the_crs_length_of_a_ground_metre()
        {
            // Web Mercator at New York: about 1.32 units per ground metre.
            var map = Exact(new MeasuredCrs(1.3217, 0, UnitSystem.Meters));
            var o = map.ModelToGis(new Xyz(0, 0, 0));
            Assert.InRange(Distance(o, map.ModelToGis(new Xyz(100, 0, 0))), 132.169, 132.171);
            Assert.InRange(Distance(o, map.ModelToGis(new Xyz(0, 100, 0))), 132.169, 132.171);
        }

        [Fact]
        public void Grid_convergence_and_feet_are_honoured_and_the_planar_frame_misses_them()
        {
            // A State Plane-like zone: feet, scale 0.99995, grid north 1.2 degrees off true north.
            var crs = new MeasuredCrs(0.99995, 1.2, UnitSystem.Feet);
            var map = Exact(crs);
            var o = map.ModelToGis(new Xyz(0, 0, 0));
            var p = map.ModelToGis(new Xyz(300, 0, 0));
            Assert.InRange(Distance(o, p), 300 * 0.99995 / 0.3048 - 0.001, 300 * 0.99995 / 0.3048 + 0.001);
            Assert.InRange(Math.Atan2(p.Y - o.Y, p.X - o.X) * 180 / Math.PI, 1.199, 1.201);
            Assert.InRange(map.ModelToGis(new Xyz(0, 0, 10)).Z - o.Z, 32.80, 32.81); // 10 m of height in feet

            var planar = GeoReference.FromAnchorParameters(Anchor(), crs.ProjectFromWgs84(0, 0, 0), UnitSystem.Feet);
            Assert.True(Distance(planar.ModelToGis(new Xyz(300, 0, 0)), p) > 15, "1.2 degrees over 300 m is about 20 ft");
        }

        [Fact]
        public void A_rotated_model_round_trips_and_points_where_its_anchor_says()
        {
            var map = Exact(new MeasuredCrs(1.3217, -0.4, UnitSystem.Meters), north: 29);
            foreach (var q in new[] { new Xyz(0, 0, 0), new Xyz(143.7, -84.2, 12.5), new Xyz(-230, 135, 0) })
            {
                var back = map.GisToModel(map.ModelToGis(q));
                Assert.True(Distance(back, q) < 1e-6 && Math.Abs(back.Z - q.Z) < 1e-6);
            }
            // Model +Y is 29 degrees east of true north; in a grid turned -0.4 degrees that is 29.4 from grid north.
            var o = map.ModelToGis(new Xyz(0, 0, 0));
            var up = map.ModelToGis(new Xyz(0, 100, 0));
            Assert.InRange(Math.Atan2(up.X - o.X, up.Y - o.Y) * 180 / Math.PI, 29.39, 29.41);
        }

        sealed class PerVertexCrs : IArcGISAdapter, ICrsProjector, IGeodeticMapProvider
        {
            public readonly ICoordinateMap Map = new GeoReference(AffineTransform.Scale(2.0));
            public ICoordinateMap CreateGeodeticMap(EarthAnchor anchor) => Map;
            public Xyz ProjectFromWgs84(double lat, double lon, double h) => new Xyz(0, 0, 0);
            public UnitSystem GetCrsLinearUnit() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => Array.Empty<string>();
            public LayerSchema GetSchema(string layerName) => null;
            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) => Array.Empty<FeatureRecord>();
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => Array.Empty<FeatureRecord>();
            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features) => Array.Empty<long>();
            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features) { }
            public void RefreshScene() { }
        }

        [Fact]
        public void Exact_documents_prefer_the_gis_per_vertex_map_and_planar_ones_never_get_it()
        {
            var crs = new PerVertexCrs();
            var doc = new Doc { Anchor = Anchor() };
            doc.Strings[GeoReferenceFactory.ModeKey] = GeoReferenceFactory.LocalFrameMode;
            Assert.Same(crs.Map, GeoReferenceFactory.Create(doc, crs, new LayerMappingProfile()));

            doc.Strings[GeoReferenceFactory.ModeKey] = GeoReferenceFactory.PlanarMode;
            Assert.NotSame(crs.Map, GeoReferenceFactory.Create(doc, crs, new LayerMappingProfile()));
        }

        [Fact]
        public void Documents_synced_with_the_planar_frame_keep_it()
        {
            var crs = new MeasuredCrs(1.3217, 0, UnitSystem.Meters);
            var doc = new Doc { Anchor = Anchor() };
            var planar = GeoReferenceFactory.Create(doc, crs, new LayerMappingProfile());
            var o = planar.ModelToGis(new Xyz(0, 0, 0));
            Assert.InRange(Distance(o, planar.ModelToGis(new Xyz(100, 0, 0))), 99.999, 100.001);

            doc.Strings[GeoReferenceFactory.ModeKey] = GeoReferenceFactory.PlanarMode;
            var stillPlanar = GeoReferenceFactory.Create(doc, crs, new LayerMappingProfile());
            Assert.InRange(Distance(o, stillPlanar.ModelToGis(new Xyz(100, 0, 0))), 99.999, 100.001);
        }
    }
}
