using System;
using System.Collections.Generic;

namespace RhinoArcGIS.Core.Identity
{
    /// <summary>
    /// Cross-reference identity carried by every synchronized object (spec 06 §5).
    /// <see cref="SyncGuid"/> is the master key; Rhino GUIDs and ArcGIS ObjectIDs are
    /// volatile and must never be the sole identity.
    /// </summary>
    public sealed class SyncIdentity
    {
        public Guid SyncGuid { get; set; }
        public Guid? RhinoGuid { get; set; }
        public Guid? ArcGisGlobalId { get; set; }
        public long? ArcGisObjectId { get; set; }

        public string SourceFile { get; set; }
        public string SourceLayer { get; set; }
        public string TargetLayer { get; set; }

        public DateTimeOffset? LastPushTime { get; set; }
        public DateTimeOffset? LastPullTime { get; set; }
        public string LastModifiedBy { get; set; }

        public SyncState State { get; set; } = SyncState.Clean;

        public SyncIdentity() { }

        public SyncIdentity(Guid syncGuid)
        {
            SyncGuid = syncGuid;
        }

        /// <summary>Mint a fresh identity with a new sync GUID.</summary>
        public static SyncIdentity NewIdentity() => new SyncIdentity(Guid.NewGuid());

        public bool HasStableIdentity => SyncGuid != Guid.Empty;

        /// <summary>Whether these tags were inherited from a different Rhino object.</summary>
        public static bool IsCopy(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings)
            => userStrings != null && userStrings.TryGetValue(GisKeys.RhinoObjectId, out var owner) &&
               IsCopy(rhinoGuid, owner);

        public static bool IsCopy(Guid rhinoGuid, string recordedOwner)
            => Guid.TryParse(recordedOwner, out var id) && id != Guid.Empty && id != rhinoGuid;
    }
}
