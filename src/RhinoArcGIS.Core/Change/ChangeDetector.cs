namespace RhinoArcGIS.Core.Change
{
    /// <summary>
    /// Determines, for a single field, what changed relative to the last synced value using
    /// the stored per-field hash (spec 04 §11). "Changed" means the current side's value hashes
    /// differently from the last-synced hash.
    /// </summary>
    public static class ChangeDetector
    {
        /// <summary>
        /// Classify a field given the current Rhino and ArcGIS values and the hash recorded at
        /// the last successful sync. A null/absent value on a side is treated as missing.
        /// </summary>
        /// <param name="rhinoValue">Current Rhino value, or null if the field is absent in Rhino.</param>
        /// <param name="arcgisValue">Current ArcGIS value, or null if the field is absent in ArcGIS.</param>
        /// <param name="lastSyncedHash">Per-field hash stored at the last sync, or null if never synced.</param>
        public static FieldChangeState Detect(string rhinoValue, string arcgisValue, string lastSyncedHash)
        {
            bool rhinoMissing = rhinoValue == null;
            bool arcgisMissing = arcgisValue == null;

            if (rhinoMissing && arcgisMissing) return FieldChangeState.Unchanged;
            if (rhinoMissing) return FieldChangeState.MissingInRhino;
            if (arcgisMissing) return FieldChangeState.MissingInArcGis;

            // Never synced before: compare the two sides directly.
            if (string.IsNullOrEmpty(lastSyncedHash))
            {
                bool equalNow = Hashing.HashField(rhinoValue) == Hashing.HashField(arcgisValue);
                return equalNow ? FieldChangeState.Unchanged : FieldChangeState.ChangedInBoth;
            }

            bool changedInRhino = Hashing.HashField(rhinoValue) != lastSyncedHash;
            bool changedInArcGis = Hashing.HashField(arcgisValue) != lastSyncedHash;

            if (changedInRhino && changedInArcGis) return FieldChangeState.ChangedInBoth;
            if (changedInRhino) return FieldChangeState.ChangedInRhino;
            if (changedInArcGis) return FieldChangeState.ChangedInArcGis;
            return FieldChangeState.Unchanged;
        }

        /// <summary>Convenience: did the Rhino side change relative to the last sync?</summary>
        public static bool ChangedInRhino(string rhinoValue, string lastSyncedHash)
            => !string.IsNullOrEmpty(lastSyncedHash) && Hashing.HashField(rhinoValue ?? string.Empty) != lastSyncedHash;

        /// <summary>Convenience: did the ArcGIS side change relative to the last sync?</summary>
        public static bool ChangedInArcGis(string arcgisValue, string lastSyncedHash)
            => !string.IsNullOrEmpty(lastSyncedHash) && Hashing.HashField(arcgisValue ?? string.Empty) != lastSyncedHash;
    }
}
