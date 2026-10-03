using System;

namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// Converts coordinates between Rhino model space and GIS space using a <see cref="ProjectAnchor"/>.
    ///
    /// Rhino → GIS pipeline: translate by -RhinoPoint, scale, rotate about Z, translate by +GisPoint.
    /// GIS → Rhino is the exact inverse, so the transform round-trips.
    /// </summary>
    public sealed class CoordinateTransform : ICoordinateMap
    {
        private readonly ProjectAnchor _anchor;
        private readonly double _cos;
        private readonly double _sin;
        private readonly ElevationMode _elevationMode;

        public CoordinateTransform(ProjectAnchor anchor, ElevationMode elevationMode = ElevationMode.Absolute)
        {
            _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
            double theta = anchor.RotationDegrees * Math.PI / 180.0;
            _cos = Math.Cos(theta);
            _sin = Math.Sin(theta);
            _elevationMode = elevationMode;
        }

        public ProjectAnchor Anchor => _anchor;

        /// <summary>Transform a Rhino-space coordinate into GIS space.</summary>
        public Xyz RhinoToGis(Xyz p)
        {
            double dx = (p.X - _anchor.RhinoPoint.X) * _anchor.Scale;
            double dy = (p.Y - _anchor.RhinoPoint.Y) * _anchor.Scale;
            double dz = (p.Z - _anchor.RhinoPoint.Z) * _anchor.Scale;

            double xr = dx * _cos - dy * _sin;
            double yr = dx * _sin + dy * _cos;

            double z = _elevationMode == ElevationMode.Absolute
                ? dz + _anchor.GisPoint.Z
                : dz; // relative: leave Z relative to anchor elevation

            return new Xyz(xr + _anchor.GisPoint.X, yr + _anchor.GisPoint.Y, z);
        }

        /// <summary>Transform a GIS-space coordinate back into Rhino space (inverse of <see cref="RhinoToGis"/>).</summary>
        public Xyz GisToRhino(Xyz p)
        {
            double dx = p.X - _anchor.GisPoint.X;
            double dy = p.Y - _anchor.GisPoint.Y;
            double dz = _elevationMode == ElevationMode.Absolute
                ? p.Z - _anchor.GisPoint.Z
                : p.Z;

            // Inverse rotation about Z.
            double xr = dx * _cos + dy * _sin;
            double yr = -dx * _sin + dy * _cos;

            // Inverse scale.
            xr /= _anchor.Scale;
            yr /= _anchor.Scale;
            dz /= _anchor.Scale;

            return new Xyz(xr + _anchor.RhinoPoint.X, yr + _anchor.RhinoPoint.Y, dz + _anchor.RhinoPoint.Z);
        }

        /// <summary>Convert a scalar length (e.g. an extrusion height) from Rhino units to GIS units.</summary>
        public double RhinoLengthToGis(double length) => length * _anchor.Scale;

        /// <summary>Convert a scalar length from GIS units to Rhino units.</summary>
        public double GisLengthToRhino(double length) => length / _anchor.Scale;

        // ICoordinateMap (model == Rhino, gis == GIS)
        public Xyz ModelToGis(Xyz p) => RhinoToGis(p);
        public Xyz GisToModel(Xyz p) => GisToRhino(p);
        public double ModelLengthToGis(double length) => RhinoLengthToGis(length);
        public double GisLengthToModel(double length) => GisLengthToRhino(length);
    }
}
