using System;
using ArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using CoreXyz = RhinoArcGIS.Core.Spatial.Xyz;

namespace RhinoArcGIS.ArcGIS
{
    /// <summary>
    /// Maps every coordinate individually between Rhino model space and a layer's CRS through the
    /// earth anchor, with ArcGIS doing all of the geodesy.
    /// </summary>
    /// <remarks>
    /// Rhino's anchor turns model coordinates into metres east and north of the anchor point. A
    /// point that far away, in that direction, is found on the WGS84 ellipsoid by walking the
    /// geodesic from the anchor (<see cref="IGeometryEngine.GeodeticMove"/>), then projected into
    /// the layer's CRS through ArcGIS's datum transformation (<see cref="DatumTransforms"/>). The
    /// way back projects to WGS84 and asks ArcGIS for the geodesic distance and azimuth from the
    /// anchor (<see cref="IGeometryEngine.GeodeticDistanceAndAzimuth(MapPoint, MapPoint, GeodeticCurveType, LinearUnit, out double, out double)"/>).
    ///
    /// A single affine frame measured at the anchor is exact there but drifts with distance in a
    /// projection whose scale varies across the site -- a few millimetres at 300 m in Web Mercator
    /// at New York, growing with the square of the distance. Per-vertex mapping has no such drift:
    /// every vertex is placed by the projection itself.
    /// </remarks>
    public sealed class GeodeticCoordinateMap : ICoordinateMap
    {
        readonly SpatialReference _target;
        readonly MapPoint _anchor;
        readonly DatumTransforms.Choice _toTarget, _toWgs84;
        readonly AffineTransform _modelToEnu, _enuToModel;
        readonly double _originZ, _verticalPerMetre, _groundScale, _modelToMetres;

        public GeodeticCoordinateMap(EarthAnchor anchor, SpatialReference target)
        {
            if (anchor == null) throw new ArgumentNullException(nameof(anchor));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            var wgs84 = SpatialReferences.WGS84;
            _anchor = MapPointBuilderEx.CreateMapPoint(anchor.Longitude, anchor.Latitude, wgs84);
            _toTarget = DatumTransforms.Between(wgs84, target, anchor.Longitude, anchor.Latitude);
            _toWgs84 = DatumTransforms.Between(target, wgs84, anchor.Longitude, anchor.Latitude);
            DatumTransformation = _toTarget.Description;

            _modelToMetres = anchor.ModelToMetres() > 0 ? anchor.ModelToMetres() : 1.0;
            _modelToEnu =
                AffineTransform.Scale(_modelToMetres)
                    .Compose(AffineTransform.RotationZ(-anchor.NorthAngleDegrees))
                    .Compose(AffineTransform.Translation(new CoreXyz(-anchor.ModelBasePoint.X, -anchor.ModelBasePoint.Y, -anchor.ModelBasePoint.Z)));
            _enuToModel = _modelToEnu.Inverse();

            double metersPerUnit = (target.Unit as LinearUnit)?.ConversionFactor ?? 1.0;
            _verticalPerMetre = 1.0 / metersPerUnit;
            _originZ = anchor.Elevation * _verticalPerMetre;

            // Only for scalar lengths (extrusion heights and the like): the projection's scale at
            // the anchor, measured across 100 m of ground.
            var a = ModelToGis(new CoreXyz(anchor.ModelBasePoint.X, anchor.ModelBasePoint.Y, 0));
            var b = GroundPoint(100.0, 0.0);
            _groundScale = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / 100.0;
        }

        /// <summary>The datum transformation from WGS84 to the layer's CRS, as ArcGIS names it.</summary>
        public string DatumTransformation { get; }

        public CoreXyz ModelToGis(CoreXyz p)
        {
            var enu = _modelToEnu.Apply(p);
            var ground = GroundPoint(enu.X, enu.Y);
            return new CoreXyz(ground.X, ground.Y, _originZ + enu.Z * _verticalPerMetre);
        }

        public CoreXyz GisToModel(CoreXyz p)
        {
            var point = MapPointBuilderEx.CreateMapPoint(p.X, p.Y, _target);
            var wgs = (MapPoint)_toWgs84.Project(point, SpatialReferences.WGS84);
            double distance = GeometryEngine.Instance.GeodeticDistanceAndAzimuth(_anchor, wgs,
                GeodeticCurveType.Geodesic, LinearUnit.Meters, out double azimuthDegrees, out _);
            // The SDK reference says radians; Pro 3.7 returns degrees clockwise from north (measured:
            // due east comes back as 89.9996). GeodeticMove, by contrast, does take radians.
            double azimuth = azimuthDegrees * Math.PI / 180.0;
            double east = distance * Math.Sin(azimuth), north = distance * Math.Cos(azimuth);
            if (double.IsNaN(distance) || distance == 0) east = north = 0;
            return _enuToModel.Apply(new CoreXyz(east, north, (p.Z - _originZ) / _verticalPerMetre));
        }

        public double ModelLengthToGis(double length) => length * _modelToMetres * _groundScale;

        public double GisLengthToModel(double length) =>
            _groundScale == 0 ? length : length / (_modelToMetres * _groundScale);

        /// <summary>The target-CRS position of the ground point east/north metres from the anchor.</summary>
        MapPoint GroundPoint(double east, double north)
        {
            double distance = Math.Sqrt(east * east + north * north);
            MapPoint wgs = distance < 1e-12
                ? _anchor
                : GeometryEngine.Instance.GeodeticMove(new[] { _anchor }, SpatialReferences.WGS84, distance,
                    LinearUnit.Meters, Math.Atan2(east, north), GeodeticCurveType.Geodesic)[0];
            return (MapPoint)_toTarget.Project(wgs, _target);
        }
    }
}
