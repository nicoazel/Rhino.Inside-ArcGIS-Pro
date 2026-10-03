namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>Per-object classification status (spec 03 §3).</summary>
    public enum ClassificationStatus
    {
        Ready = 0,
        ReadyWithWarnings,
        NeedsUserDecision,
        Repairable,
        Unsupported,
        Skipped,
        Failed
    }
}
