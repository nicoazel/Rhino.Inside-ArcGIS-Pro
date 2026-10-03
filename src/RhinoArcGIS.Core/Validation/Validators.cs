using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Validation
{
    /// <summary>
    /// Universal and coordinate validators (spec 03 §4–§5). Target-specific validators
    /// (point/polyline/polygon/multipatch, spec 03 §6) are added in Waves 2–3 once geometry
    /// conversion lands; the structure here is what they plug into.
    /// </summary>
    public static class Validators
    {
        /// <summary>Run the universal checks that apply to every push and pull.</summary>
        public static ValidationResult Universal(ValidationContext ctx, ValidationResult result = null)
        {
            result = result ?? new ValidationResult();
            if (ctx == null) return result.Add(ValidationIssue.Error("ctx.null", "No validation context."));

            if (!ctx.HasProjectAnchor)
                result.Add(ValidationIssue.Error("anchor.missing", "No project anchor / CRS is defined."));

            if (!ctx.RhinoUnits.IsKnown())
                result.Add(ValidationIssue.Error("units.unknown", "Rhino unit system is unknown."));

            if (!ctx.HasStableIdentity)
                result.Add(ValidationIssue.Warning("identity.missing", "Object has no stable sync identity; one can be assigned."));

            if (!ctx.HasNonEmptyGeometry)
                result.Add(ValidationIssue.Error("geometry.empty", "Object has empty geometry."));

            if (!ctx.TargetLayerExists)
                result.Add(ValidationIssue.Error("layer.missing", "Target layer does not exist."));

            if (!ctx.TargetGeometryTypeMatches)
                result.Add(ValidationIssue.Error("layer.geomtype", "Target layer geometry type does not match."));

            if (!ctx.RequiredFieldsExist)
                result.Add(ValidationIssue.Error("fields.required", "Required target fields are missing."));

            if (!ctx.HasWritePermission)
                result.Add(ValidationIssue.Error("permission.write", "No write permission on the target."));

            return result;
        }

        /// <summary>Run the coordinate sanity checks (spec 03 §5).</summary>
        public static ValidationResult Coordinate(ValidationContext ctx, ValidationResult result = null)
        {
            result = result ?? new ValidationResult();
            if (ctx == null) return result.Add(ValidationIssue.Error("ctx.null", "No validation context."));

            if (ctx.RepresentativePoint.HasValue)
            {
                Xyz p = ctx.RepresentativePoint.Value;

                if (!ctx.OriginIsIntentional && p.X == 0.0 && p.Y == 0.0)
                    result.Add(ValidationIssue.Warning("coord.origin", "Object sits at 0,0; this is often an un-anchored mistake."));

                if (ctx.ExtentMin.HasValue && ctx.ExtentMax.HasValue)
                {
                    Xyz min = ctx.ExtentMin.Value, max = ctx.ExtentMax.Value;
                    bool inX = p.X >= min.X && p.X <= max.X;
                    bool inY = p.Y >= min.Y && p.Y <= max.Y;
                    if (!inX || !inY)
                        result.Add(ValidationIssue.Warning("coord.extent", "Object falls outside the expected project extent."));
                }

                if (ctx.MinZ.HasValue && p.Z < ctx.MinZ.Value)
                    result.Add(ValidationIssue.Warning("coord.zlow", "Z is below the expected vertical range."));
                if (ctx.MaxZ.HasValue && p.Z > ctx.MaxZ.Value)
                    result.Add(ValidationIssue.Warning("coord.zhigh", "Z is above the expected vertical range."));
            }

            return result;
        }
    }
}
