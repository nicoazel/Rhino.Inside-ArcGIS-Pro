namespace RhinoArcGIS.Core.Attributes
{
    /// <summary>The decision the core makes for a single field during a push or pull.</summary>
    public enum SyncAction
    {
        /// <summary>Write the source value to the destination.</summary>
        Write = 0,

        /// <summary>Leave the destination untouched (do not overwrite).</summary>
        Preserve,

        /// <summary>Skip this field entirely for this direction (local-only / ignored).</summary>
        Skip,

        /// <summary>Recompute a derived value and write it.</summary>
        Recalculate,

        /// <summary>Both sides changed: hold for human conflict review.</summary>
        FlagConflict,

        /// <summary>An ownership rule was violated (e.g. editing an ArcGIS-owned field in Rhino).</summary>
        WarnIgnore
    }
}
