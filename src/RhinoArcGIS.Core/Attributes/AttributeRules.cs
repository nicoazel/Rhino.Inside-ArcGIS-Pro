namespace RhinoArcGIS.Core.Attributes
{
    /// <summary>
    /// Pure decision tables for attribute push/pull behaviour (spec 04 §6 and §7).
    /// These functions have no side effects and are exhaustively unit-tested.
    /// </summary>
    public static class AttributeRules
    {
        /// <summary>
        /// Decide what to do with a field when pushing Rhino → ArcGIS.
        /// </summary>
        /// <param name="owner">Field ownership.</param>
        /// <param name="changedInRhino">Whether the Rhino value changed since the last sync.</param>
        /// <param name="changedInArcGis">Whether the ArcGIS value changed since the last sync.</param>
        public static SyncAction Push(FieldOwnership owner, bool changedInRhino, bool changedInArcGis)
        {
            switch (owner)
            {
                case FieldOwnership.RhinoOwned:
                    return SyncAction.Write;

                case FieldOwnership.ArcGisOwned:
                    // Never overwrite ArcGIS's authoritative value from a push.
                    return SyncAction.Preserve;

                case FieldOwnership.Shared:
                    // Push if unchanged in ArcGIS; flag if both changed; don't clobber a lone GIS edit.
                    if (changedInArcGis && changedInRhino) return SyncAction.FlagConflict;
                    if (changedInArcGis) return SyncAction.Preserve;
                    return SyncAction.Write;

                case FieldOwnership.Derived:
                    return SyncAction.Recalculate;

                case FieldOwnership.Locked:
                    return SyncAction.Preserve;

                case FieldOwnership.LocalOnlyRhino:
                case FieldOwnership.LocalOnlyArcGis:
                    return SyncAction.Skip;

                default:
                    return SyncAction.Skip;
            }
        }

        /// <summary>
        /// Decide what to do with a field when pulling ArcGIS → Rhino.
        /// </summary>
        public static SyncAction Pull(FieldOwnership owner, bool changedInRhino, bool changedInArcGis)
        {
            switch (owner)
            {
                case FieldOwnership.RhinoOwned:
                    // Do not overwrite Rhino's authoritative value on a pull (user may opt in separately).
                    return SyncAction.Preserve;

                case FieldOwnership.ArcGisOwned:
                    return SyncAction.Write;

                case FieldOwnership.Shared:
                    if (changedInArcGis && changedInRhino) return SyncAction.FlagConflict;
                    if (changedInRhino) return SyncAction.Preserve;
                    return SyncAction.Write;

                case FieldOwnership.Derived:
                    return SyncAction.Recalculate;

                case FieldOwnership.Locked:
                    // ArcGIS maintains it; Rhino shows it. Never pushed, always brought across.
                    return SyncAction.Write;

                case FieldOwnership.LocalOnlyRhino:
                    return SyncAction.Preserve;

                case FieldOwnership.LocalOnlyArcGis:
                    return SyncAction.Skip;

                default:
                    return SyncAction.Skip;
            }
        }
    }
}
