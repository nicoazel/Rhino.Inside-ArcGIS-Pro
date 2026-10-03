namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// SDK-neutral snapshot of Rhino's <c>EarthAnchorPoint</c> (spec: DESIGN_georeferencing.md).
    /// The Rhino adapter fills this from <c>RhinoDoc.EarthAnchorPoint</c>; the core uses it to build
    /// a <see cref="GeoReference"/> (together with the projected origin supplied by the GIS side).
    /// </summary>
    public sealed class EarthAnchor
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Elevation { get; set; }

        /// <summary>The model-space point that sits at the lat/long above.</summary>
        public Xyz ModelBasePoint { get; set; }

        /// <summary>Model units, needed to scale model coordinates to metres.</summary>
        public UnitSystem ModelUnits { get; set; } = UnitSystem.Unknown;

        /// <summary>
        /// Metres in one model unit as the modelling application states it (Rhino's own unit
        /// scale, including custom units). 0 when not supplied; <see cref="ModelUnits"/> then decides.
        /// </summary>
        public double MetresPerModelUnit { get; set; }

        /// <summary>Metres in one model unit, or NaN when the units are not known.</summary>
        public double ModelToMetres() =>
            MetresPerModelUnit > 0 && !double.IsInfinity(MetresPerModelUnit) ? MetresPerModelUnit : ModelUnits.MetersPerUnit();

        /// <summary>Angle (degrees, CCW) from model +Y to true north. 0 when model north is +Y.</summary>
        public double NorthAngleDegrees { get; set; }

        /// <summary>True once a real earth location has been set in Rhino.</summary>
        public bool IsSet { get; set; }

        public bool IsValid => IsSet && ModelToMetres() > 0;
    }
}
