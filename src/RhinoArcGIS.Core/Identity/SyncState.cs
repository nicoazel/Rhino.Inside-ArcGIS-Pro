namespace RhinoArcGIS.Core.Identity
{
    /// <summary>
    /// Object-level sync states (spec 06 §6). Computed by the interop core from change
    /// detection; persisted on the Rhino object under <see cref="GisKeys.SyncState"/>.
    /// </summary>
    public enum SyncState
    {
        Clean = 0,
        NewInRhino,
        NewInArcGis,
        ModifiedInRhino,
        ModifiedInArcGis,
        ModifiedInBoth,
        DeletedInRhino,
        DeletedInArcGis,
        Orphaned,
        Conflict,
        Unsupported
    }
}
