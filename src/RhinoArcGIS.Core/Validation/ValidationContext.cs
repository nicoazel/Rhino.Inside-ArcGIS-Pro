using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Validation
{
    /// <summary>
    /// Inputs the universal and coordinate validators reason about (spec 03 §4–§5). Adapters
    /// populate this from the live Rhino/ArcGIS state; the validators themselves stay pure.
    /// </summary>
    public sealed class ValidationContext
    {
        // Universal (spec 03 §4)
        public bool HasProjectAnchor { get; set; }
        public UnitSystem RhinoUnits { get; set; } = UnitSystem.Unknown;
        public bool HasStableIdentity { get; set; }
        public bool HasNonEmptyGeometry { get; set; }
        public bool TargetLayerExists { get; set; }
        public bool TargetGeometryTypeMatches { get; set; }
        public bool RequiredFieldsExist { get; set; } = true;
        public bool HasWritePermission { get; set; } = true;

        // Coordinate (spec 03 §5)
        public Xyz? RepresentativePoint { get; set; }

        /// <summary>Expected project extent in GIS coordinates (min/max), if known.</summary>
        public Xyz? ExtentMin { get; set; }
        public Xyz? ExtentMax { get; set; }

        /// <summary>Allowed vertical range in GIS units, if known.</summary>
        public double? MinZ { get; set; }
        public double? MaxZ { get; set; }

        /// <summary>True if a representative point exactly at the origin is intentional.</summary>
        public bool OriginIsIntentional { get; set; }
    }
}
