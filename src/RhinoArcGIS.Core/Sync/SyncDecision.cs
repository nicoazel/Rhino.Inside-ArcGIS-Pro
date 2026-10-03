using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Identity;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>Which side(s) a value changed on since the last sync.</summary>
    public enum ChangeSide
    {
        None = 0,
        Rhino,
        ArcGis,
        Both
    }

    /// <summary>The direction a single field should move during a bidirectional sync.</summary>
    public enum SyncDirection
    {
        None = 0,
        Push,
        Pull,
        Conflict
    }

    /// <summary>Per-field outcome of the sync decision (spec 04 §11–§12).</summary>
    public sealed class FieldDecision
    {
        public string RhinoKey { get; set; }
        public string ArcGisField { get; set; }
        public FieldOwnership Owner { get; set; }
        public FieldChangeState State { get; set; }
        public SyncDirection Direction { get; set; }
        public string RhinoValue { get; set; }
        public string ArcGisValue { get; set; }

        /// <summary>True when a side edited a field it does not own (spec 04 §12 last row).</summary>
        public bool OwnershipViolation { get; set; }
    }

    /// <summary>Per-object outcome of the sync decision (spec 06 §6).</summary>
    public sealed class ObjectDecision
    {
        public System.Guid SyncGuid { get; set; }
        public System.Guid? RhinoGuid { get; set; }
        public long? ArcGisObjectId { get; set; }
        public System.Guid? ArcGisGlobalId { get; set; }

        public SyncState State { get; set; } = SyncState.Clean;
        public ChangeSide GeometryChange { get; set; } = ChangeSide.None;
        public List<FieldDecision> Fields { get; set; } = new List<FieldDecision>();

        // Source data carried through to the apply step.
        public RhinoObjectSnapshot RhinoSource { get; set; }
        public FeatureRecord ArcGisSource { get; set; }

        /// <summary>
        /// A Rhino object that carries another object's sync identity -- a copy, paste, or array of
        /// a tracked object. It is new work, so it is pushed as a new feature and its copied
        /// identity is replaced rather than left to compete with the original.
        /// </summary>
        public bool IsCopy { get; set; }

        /// <summary>
        /// The feature was found under a different ObjectID than the Rhino object records -- a
        /// shapefile renumbers its FIDs when rows are deleted -- and was matched by its geometry
        /// baseline instead. An apply writes the current ObjectID back.
        /// </summary>
        public bool IdentityRepaired { get; set; }

        /// <summary>Short, user-facing explanation for the review list.</summary>
        public string Note { get; set; }

        public bool IsConflict => State == SyncState.Conflict;

        /// <summary>
        /// What changed, for the review list: "geometry", field names, or the note. Empty when the
        /// object is clean.
        /// </summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Note)) parts.Add(Note);
            if (State != SyncState.NewInRhino && State != SyncState.NewInArcGis)
            {
                if (GeometryChange == ChangeSide.Both) parts.Add("geometry (both sides)");
                else if (GeometryChange != ChangeSide.None) parts.Add("geometry");
                var fields = Fields.Where(f => f.Direction != SyncDirection.None).Select(f =>
                    f.Direction == SyncDirection.Conflict ? f.ArcGisField + " (both sides)" : f.ArcGisField).ToList();
                if (fields.Count > 0) parts.Add(string.Join(", ", fields));
            }
            return string.Join("; ", parts);
        }
    }

    /// <summary>The full plan produced by <see cref="SyncEngine"/> for one layer.</summary>
    public sealed class SyncPlan
    {
        public List<ObjectDecision> Decisions { get; } = new List<ObjectDecision>();

        public IEnumerable<ObjectDecision> Conflicts => Decisions.Where(d => d.IsConflict);
        public int Count(SyncState state) => Decisions.Count(d => d.State == state);
        public int ConflictCount => Decisions.Count(d => d.IsConflict);
    }
}
