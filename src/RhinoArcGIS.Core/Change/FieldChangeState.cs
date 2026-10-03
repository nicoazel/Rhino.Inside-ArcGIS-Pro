namespace RhinoArcGIS.Core.Change
{
    /// <summary>Field-level change states (spec 06 §6).</summary>
    public enum FieldChangeState
    {
        Unchanged = 0,
        ChangedInRhino,
        ChangedInArcGis,
        ChangedInBoth,
        MissingInRhino,
        MissingInArcGis,
        InvalidInRhino,
        InvalidInArcGis,
        OwnershipViolation
    }
}
