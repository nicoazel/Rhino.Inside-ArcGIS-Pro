namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// Linear unit systems the tool understands. Unit awareness is mandatory: the spec
    /// (03 §5) requires that the Rhino unit system is known before any sync.
    /// </summary>
    /// <remarks>
    /// The named systems cover what a Rhino model is usually drawn in. <see cref="Other"/> is any
    /// further unit the host application knows exactly (Rhino's custom units, nautical miles,
    /// mils ...): its length travels separately as metres per unit, see
    /// <see cref="EarthAnchor.MetresPerModelUnit"/>. New members go at the end; profiles store
    /// the names.
    /// </remarks>
    public enum UnitSystem
    {
        Unknown = 0,
        Millimeters,
        Centimeters,
        Meters,
        Kilometers,
        Inches,
        Feet,
        Miles,
        Microns,
        Decimeters,
        Yards,
        Other
    }

    public static class UnitSystemExtensions
    {
        /// <summary>Number of metres in one unit of the given system; NaN when it has no fixed length.</summary>
        public static double MetersPerUnit(this UnitSystem system)
        {
            switch (system)
            {
                case UnitSystem.Microns: return 1e-6;
                case UnitSystem.Millimeters: return 0.001;
                case UnitSystem.Centimeters: return 0.01;
                case UnitSystem.Decimeters: return 0.1;
                case UnitSystem.Meters: return 1.0;
                case UnitSystem.Kilometers: return 1000.0;
                case UnitSystem.Inches: return 0.0254;
                case UnitSystem.Feet: return 0.3048;
                case UnitSystem.Yards: return 0.9144;
                case UnitSystem.Miles: return 1609.344;
                default: return double.NaN;
            }
        }

        /// <summary>Scale factor to convert a length in <paramref name="from"/> units to <paramref name="to"/> units.</summary>
        public static double ConversionFactor(UnitSystem from, UnitSystem to)
        {
            return from.MetersPerUnit() / to.MetersPerUnit();
        }

        public static bool IsKnown(this UnitSystem system) => system != UnitSystem.Unknown;
    }
}
