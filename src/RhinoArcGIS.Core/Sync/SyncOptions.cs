namespace RhinoArcGIS.Core.Sync
{
    /// <summary>How to resolve both-sides conflicts during a full sync (spec 01 sync philosophy:
    /// never silently overwrite; default is to hold for manual review).</summary>
    public enum ConflictResolution
    {
        /// <summary>Leave conflicts unresolved in the conflict queue for the user to decide.</summary>
        Manual = 0,

        /// <summary>Resolve conflicts in Rhino's favour (push).</summary>
        PreferRhino,

        /// <summary>Resolve conflicts in ArcGIS's favour (pull).</summary>
        PreferArcGis
    }

    /// <summary>Which way a sync is allowed to write.</summary>
    public enum SyncDirectionMode
    {
        /// <summary>Push Rhino changes and pull ArcGIS changes.</summary>
        TwoWay = 0,

        /// <summary>Never write to ArcGIS. Rhino-side changes are reported and held.</summary>
        PullOnly,

        /// <summary>Never create or change objects in Rhino from ArcGIS. ArcGIS-side changes are reported and held.</summary>
        PushOnly
    }

    /// <summary>Options for a full bidirectional sync run.</summary>
    public sealed class SyncOptions
    {
        public ConflictResolution Conflicts { get; set; } = ConflictResolution.Manual;

        /// <summary>
        /// Which way an apply may write. A preview always shows both sides; the direction only
        /// decides what an apply is allowed to do with what it found.
        /// </summary>
        public SyncDirectionMode Direction { get; set; } = SyncDirectionMode.TwoWay;

        /// <summary>If false, compute the plan and report it but apply no edits (dry run / preflight).</summary>
        public bool Apply { get; set; } = true;

        public static SyncOptions Preview => new SyncOptions { Apply = false };
    }
}
