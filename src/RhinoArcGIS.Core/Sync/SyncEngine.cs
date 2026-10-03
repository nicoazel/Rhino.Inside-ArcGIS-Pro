using System;
using System.Collections.Generic;
using System.Globalization;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// Pure bidirectional sync decision engine (spec 04 §11–§12, 06 §6). Given the current Rhino
    /// snapshots, the current ArcGIS features, and the per-field hashes recorded at the last sync
    /// (stored on the Rhino objects), it produces a <see cref="SyncPlan"/> describing what should
    /// move which way and which objects are in conflict. No side effects — fully unit-tested.
    ///
    /// Matching uses the cross-reference recorded on the Rhino object. <c>gis.arcgis_globalid</c>
    /// is preferred when the layer has GlobalIDs, because ObjectIDs are only stable within a
    /// geodatabase -- a shapefile renumbers its FIDs when features are deleted -- and
    /// <c>gis.arcgis_objectid</c> is the fallback. <c>gis.sync_guid</c> is the stable master id.
    ///
    /// A Rhino object that carries an ArcGIS identity but matches nothing is <see
    /// cref="SyncState.DeletedInArcGis"/>, not new: treating it as new is what would recreate a
    /// feature someone deleted on purpose. The reverse -- a feature deleted in Rhino -- cannot be
    /// told from one that was never pulled, since nothing records what a pull produced, so it is
    /// reported as new in ArcGIS.
    /// </summary>
    public sealed class SyncEngine
    {
        public SyncPlan BuildPlan(
            ICoordinateMap map, LayerMapping layer,
            IReadOnlyList<RhinoObjectSnapshot> rhinoObjects, IReadOnlyList<FeatureRecord> arcgisFeatures)
            => BuildPlan(map, layer, rhinoObjects, arcgisFeatures, null);

        /// <summary>
        /// Builds the plan. <paramref name="ledger"/> is what the Rhino layer held at the last sync;
        /// with it, a feature whose Rhino object is gone reads as deleted in Rhino rather than new.
        /// </summary>
        /// <remarks>
        /// Matching runs in two passes so that the result does not depend on the order Rhino
        /// enumerates its objects. Every Rhino object first names the feature it claims and how
        /// sure the claim is; features are then handed out strongest claim first:
        ///
        /// 1. identity match whose ArcGIS geometry still equals the recorded baseline;
        /// 2. geometry-baseline match on a layer without GlobalIDs -- the same feature under a new
        ///    ObjectID, which is what a shapefile does to every row after a deleted one;
        /// 3. identity match whose geometry changed (an ordinary edit on either side).
        ///
        /// A Rhino object that loses a claim to another object carrying the same identity is a
        /// copy (copy/paste, array, mirror): it is new work, not a feature deleted in ArcGIS.
        /// </remarks>
        public SyncPlan BuildPlan(
            ICoordinateMap map, LayerMapping layer,
            IReadOnlyList<RhinoObjectSnapshot> rhinoObjects, IReadOnlyList<FeatureRecord> arcgisFeatures,
            SyncLedger ledger)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            rhinoObjects = rhinoObjects ?? new List<RhinoObjectSnapshot>();
            arcgisFeatures = arcgisFeatures ?? new List<FeatureRecord>();

            var arcgisByOid = new Dictionary<long, FeatureRecord>();
            var arcgisByGlobalId = new Dictionary<Guid, FeatureRecord>();
            foreach (var f in arcgisFeatures)
            {
                if (f.Identity == null) continue;
                if (f.Identity.ArcGisObjectId.HasValue) arcgisByOid[f.Identity.ArcGisObjectId.Value] = f;
                if (f.Identity.ArcGisGlobalId.HasValue) arcgisByGlobalId[f.Identity.ArcGisGlobalId.Value] = f;
            }
            bool byGlobalId = arcgisByGlobalId.Count > 0;

            // Geometry hashes, computed once and only when a claim needs them.
            var featureHash = new Dictionary<FeatureRecord, string>();
            string HashOf(FeatureRecord f)
            {
                if (!featureHash.TryGetValue(f, out var h)) featureHash[f] = h = Hashing.HashGeometry(f.Geometry);
                return h;
            }
            Dictionary<string, List<FeatureRecord>> byHash = null;
            FeatureRecord UniqueByHash(string hash)
            {
                if (byHash == null)
                {
                    byHash = new Dictionary<string, List<FeatureRecord>>(StringComparer.Ordinal);
                    foreach (var f in arcgisFeatures)
                    {
                        var h = HashOf(f);
                        if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = new List<FeatureRecord>();
                        list.Add(f);
                    }
                }
                return byHash.TryGetValue(hash, out var found) && found.Count == 1 ? found[0] : null;
            }

            // ---- pass 1: every Rhino object states its claim
            var claims = new List<Claim>(rhinoObjects.Count);
            foreach (var snap in rhinoObjects)
            {
                var claim = new Claim
                {
                    Snapshot = snap,
                    SyncGuid = ReadGuid(snap.UserStrings, GisKeys.SyncGuid),
                    GlobalId = ReadGuid(snap.UserStrings, GisKeys.ArcGisGlobalId),
                    ObjectId = ReadLong(snap.UserStrings, GisKeys.ArcGisObjectId)
                };
                claims.Add(claim);

                // Copies inherit all user strings but have their own Rhino id. They cannot claim
                // the original feature, regardless of geometry, read order, or sync scope.
                claim.IsCopy = SyncIdentity.IsCopy(snap.RhinoGuid, snap.UserStrings);
                if (claim.IsCopy) continue;

                string baseline = Get(snap.UserStrings, GisKeys.GeometryHash);
                FeatureRecord byIdentity = null;
                if (claim.GlobalId.HasValue && byGlobalId)
                    arcgisByGlobalId.TryGetValue(claim.GlobalId.Value, out byIdentity);
                else if (claim.ObjectId.HasValue && !(claim.GlobalId.HasValue && byGlobalId))
                    arcgisByOid.TryGetValue(claim.ObjectId.Value, out byIdentity);

                if (byIdentity != null && (baseline == null || HashOf(byIdentity) == baseline))
                {
                    claim.Feature = byIdentity;
                    claim.Strength = 3;
                    continue;
                }

                // ObjectIDs are only as stable as the data source keeps them. On a layer without
                // GlobalIDs, the feature whose geometry is exactly the recorded baseline -- and the
                // only one that is -- is this object's feature, whatever its ObjectID is now.
                if (!byGlobalId && claim.ObjectId.HasValue && !string.IsNullOrEmpty(baseline))
                {
                    var moved = UniqueByHash(baseline);
                    if (moved != null)
                    {
                        claim.Feature = moved;
                        claim.Strength = 2;
                        claim.Repaired = !ReferenceEquals(moved, byIdentity);
                        continue;
                    }
                }

                if (byIdentity != null)
                {
                    claim.Feature = byIdentity;
                    claim.Strength = 1;
                }
            }

            // ---- pass 2: strongest claim wins each feature. Between equally strong claims (on
            // legacy objects without an owner stamp), prefer untouched geometry. Stamped copies
            // were excluded above, so moving an original never gives its link to a copy.
            // The Rhino geometry's hash in GIS space, computed at most once per object -- and not
            // at all for an object whose model stamp shows it untouched since its baseline was
            // taken under this georeference (see ModelStamp): the baseline is that hash.
            string fingerprint = null;
            string RhinoHashOf(Claim c)
            {
                if (c.RhinoHash == null)
                {
                    var us = c.Snapshot.UserStrings;
                    string baseline = Get(us, GisKeys.RhinoGeometryHash) ?? Get(us, GisKeys.GeometryHash);
                    string stamp = Get(us, GisKeys.RhinoModelStamp);
                    if (baseline != null && stamp != null &&
                        ModelStamp.Matches(stamp, c.Snapshot.Geometry, fingerprint ?? (fingerprint = ModelStamp.Fingerprint(map))))
                    {
                        c.RhinoHash = baseline;
                    }
                    else
                    {
                        var gis = c.Snapshot.Geometry != null ? NeutralGeometryTransform.RhinoToGis(c.Snapshot.Geometry, map) : null;
                        c.RhinoHash = Hashing.HashGeometry(gis);
                    }
                }
                return c.RhinoHash;
            }

            bool RhinoUntouched(Claim c)
            {
                string rhinoBaseline = Get(c.Snapshot.UserStrings, GisKeys.RhinoGeometryHash)
                                       ?? Get(c.Snapshot.UserStrings, GisKeys.GeometryHash);
                return rhinoBaseline != null && RhinoHashOf(c) == rhinoBaseline;
            }

            var owner = new Dictionary<FeatureRecord, Claim>();
            for (int strength = 3; strength >= 1; strength--)
                foreach (var claim in claims)
                {
                    if (claim.Strength != strength) continue;
                    if (!owner.TryGetValue(claim.Feature, out var current))
                        owner[claim.Feature] = claim;
                    else if (current.Strength == strength && !RhinoUntouched(current) && RhinoUntouched(claim))
                        owner[claim.Feature] = claim;
                }

            var identityHolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in owner.Values) identityHolders.Add(IdentityKey(claim));

            var plan = new SyncPlan();
            foreach (var claim in claims)
            {
                var snap = claim.Snapshot;
                bool won = claim.Feature != null && owner.TryGetValue(claim.Feature, out var holder) &&
                           ReferenceEquals(holder, claim);
                if (claim.IsCopy)
                {
                    var copy = NewInRhino(Guid.NewGuid(), snap, layer);
                    copy.IsCopy = true;
                    copy.Note = "Copy of a tracked object; it will be created as a new feature";
                    plan.Decisions.Add(copy);
                }
                else if (won)
                {
                    var decision = Matched(claim.SyncGuid ?? Guid.NewGuid(), snap, claim.Feature, layer,
                        RhinoHashOf(claim), HashOf(claim.Feature));
                    if (claim.Repaired)
                    {
                        decision.IdentityRepaired = true;
                        decision.Note = "ObjectID changed in ArcGIS; matched by geometry";
                    }
                    plan.Decisions.Add(decision);
                }
                else if (claim.GlobalId.HasValue || claim.ObjectId.HasValue)
                {
                    // Another object carrying this very identity won the feature: this is a copy.
                    // Losing to a renumbered neighbour is not the same thing -- that neighbour
                    // records a different identity, and this object's feature really is gone.
                    if (identityHolders.Contains(IdentityKey(claim)))
                    {
                        var copy = NewInRhino(Guid.NewGuid(), snap, layer);
                        copy.IsCopy = true;
                        copy.Note = "Copy of a tracked object; it will be created as a new feature";
                        plan.Decisions.Add(copy);
                    }
                    else
                        plan.Decisions.Add(DeletedInArcGis(claim.SyncGuid ?? Guid.NewGuid(), snap, claim.ObjectId, claim.GlobalId));
                }
                else
                    plan.Decisions.Add(NewInRhino(claim.SyncGuid ?? Guid.NewGuid(), snap, layer));
            }

            foreach (var f in arcgisFeatures)
            {
                if (owner.ContainsKey(f)) continue;
                if (ledger != null && ledger.ContainsFeature(f, HashOf(f)))
                    plan.Decisions.Add(DeletedInRhino(f));
                else
                    plan.Decisions.Add(NewInArcGis(f, layer));
            }

            return plan;
        }

        /// <summary>One Rhino object's claim on a feature, before claims are settled.</summary>
        private sealed class Claim
        {
            public RhinoObjectSnapshot Snapshot;
            public Guid? SyncGuid;
            public Guid? GlobalId;
            public long? ObjectId;
            public FeatureRecord Feature;
            public int Strength;
            public bool Repaired;
            public bool IsCopy;
            public string RhinoHash;
        }

        /// <summary>
        /// Everything a copy duplicates from its original: the ArcGIS identity and the sync guid.
        /// Two objects that merely name the same ObjectID -- one of them after a shapefile reused
        /// the number -- have different sync guids and are not copies of each other.
        /// </summary>
        private static string IdentityKey(Claim claim) =>
            (claim.GlobalId.HasValue ? "g:" + claim.GlobalId.Value.ToString("D")
             : claim.ObjectId.HasValue ? "o:" + claim.ObjectId.Value.ToString(CultureInfo.InvariantCulture)
             : string.Empty) + "|" + (claim.SyncGuid?.ToString("D") ?? string.Empty);

        private static ObjectDecision DeletedInRhino(FeatureRecord feature)
            => new ObjectDecision
            {
                ArcGisObjectId = feature.Identity?.ArcGisObjectId,
                ArcGisGlobalId = feature.Identity?.ArcGisGlobalId,
                State = SyncState.DeletedInRhino,
                ArcGisSource = feature,
                Note = "Rhino object was deleted; the ArcGIS feature is kept"
            };

        private static ObjectDecision DeletedInArcGis(Guid syncGuid, RhinoObjectSnapshot snap, long? oid, Guid? globalId)
            => new ObjectDecision
            {
                SyncGuid = syncGuid,
                RhinoGuid = snap.RhinoGuid,
                ArcGisObjectId = oid,
                ArcGisGlobalId = globalId,
                State = SyncState.DeletedInArcGis,
                RhinoSource = snap
            };

        private static ObjectDecision Matched(Guid syncGuid, RhinoObjectSnapshot snap, FeatureRecord feature,
            LayerMapping layer, string rhinoHash, string arcgisHash)
        {
            var decision = new ObjectDecision
            {
                SyncGuid = syncGuid,
                RhinoGuid = snap.RhinoGuid,
                ArcGisObjectId = feature.Identity != null ? feature.Identity.ArcGisObjectId : null,
                ArcGisGlobalId = feature.Identity != null ? feature.Identity.ArcGisGlobalId : null,
                RhinoSource = snap,
                ArcGisSource = feature
            };

            bool rhinoChange = false, arcgisChange = false, conflict = false;

            if (layer.Attributes != null)
            {
                foreach (FieldMapping map in layer.Attributes)
                {
                    string rhinoVal = Get(snap.UserStrings, map.RhinoKey);
                    string arcgisVal = feature.Attributes != null && feature.Attributes.TryGetValue(map.ArcGisField, out string av) ? av : null;
                    string lastHash = Get(snap.UserStrings, GisKeys.FieldHashKey(map.RhinoKey));

                    FieldChangeState state = ChangeDetector.Detect(rhinoVal, arcgisVal, lastHash);
                    if (map.Owner == FieldOwnership.Derived && DerivedValuesEquivalent(rhinoVal, arcgisVal))
                        state = FieldChangeState.Unchanged;
                    SyncDirection dir = ResolveField(map.Owner, state, out bool violation);

                    decision.Fields.Add(new FieldDecision
                    {
                        RhinoKey = map.RhinoKey,
                        ArcGisField = map.ArcGisField,
                        Owner = map.Owner,
                        State = state,
                        Direction = dir,
                        RhinoValue = rhinoVal,
                        ArcGisValue = arcgisVal,
                        OwnershipViolation = violation
                    });

                    if (dir == SyncDirection.Push) rhinoChange = true;
                    else if (dir == SyncDirection.Pull) arcgisChange = true;
                    else if (dir == SyncDirection.Conflict) conflict = true;
                }
            }

            decision.GeometryChange = GeometryChange(snap, rhinoHash, arcgisHash);
            if (decision.GeometryChange == ChangeSide.Rhino) rhinoChange = true;
            else if (decision.GeometryChange == ChangeSide.ArcGis) arcgisChange = true;
            else if (decision.GeometryChange == ChangeSide.Both) conflict = true;

            decision.State = ResolveObjectState(conflict, rhinoChange, arcgisChange);
            return decision;
        }

        private static ObjectDecision NewInRhino(Guid syncGuid, RhinoObjectSnapshot snap, LayerMapping layer)
        {
            var decision = new ObjectDecision
            {
                SyncGuid = syncGuid,
                RhinoGuid = snap.RhinoGuid,
                State = SyncState.NewInRhino,
                GeometryChange = ChangeSide.Rhino,
                RhinoSource = snap
            };

            if (layer.Attributes != null)
            {
                foreach (FieldMapping map in layer.Attributes)
                {
                    string rhinoVal = Get(snap.UserStrings, map.RhinoKey);
                    if (rhinoVal == null) continue;
                    SyncAction action = AttributeRules.Push(map.Owner, changedInRhino: true, changedInArcGis: false);
                    if (action != SyncAction.Write && action != SyncAction.Recalculate) continue;
                    decision.Fields.Add(new FieldDecision
                    {
                        RhinoKey = map.RhinoKey,
                        ArcGisField = map.ArcGisField,
                        Owner = map.Owner,
                        State = FieldChangeState.MissingInArcGis,
                        Direction = SyncDirection.Push,
                        RhinoValue = rhinoVal
                    });
                }
            }
            return decision;
        }

        private static ObjectDecision NewInArcGis(FeatureRecord feature, LayerMapping layer)
        {
            var decision = new ObjectDecision
            {
                ArcGisObjectId = feature.Identity != null ? feature.Identity.ArcGisObjectId : null,
                ArcGisGlobalId = feature.Identity != null ? feature.Identity.ArcGisGlobalId : null,
                State = SyncState.NewInArcGis,
                GeometryChange = ChangeSide.ArcGis,
                ArcGisSource = feature
            };

            if (layer.Attributes != null)
            {
                foreach (FieldMapping map in layer.Attributes)
                {
                    if (map.Owner == FieldOwnership.LocalOnlyArcGis) continue;
                    string arcgisVal = feature.Attributes != null && feature.Attributes.TryGetValue(map.ArcGisField, out string av) ? av : null;
                    if (arcgisVal == null) continue;
                    decision.Fields.Add(new FieldDecision
                    {
                        RhinoKey = map.RhinoKey,
                        ArcGisField = map.ArcGisField,
                        Owner = map.Owner,
                        State = FieldChangeState.MissingInRhino,
                        Direction = SyncDirection.Pull,
                        ArcGisValue = arcgisVal
                    });
                }
            }
            return decision;
        }

        /// <summary>
        /// Decide a single field's direction from its ownership and change state. Encodes the
        /// bidirectional ownership rules (spec 04 §6–§7, §12); sets <paramref name="violation"/>
        /// when a side edited a field it does not own.
        /// </summary>
        public static SyncDirection ResolveField(FieldOwnership owner, FieldChangeState state, out bool violation)
        {
            violation = false;
            switch (owner)
            {
                case FieldOwnership.LocalOnlyRhino:
                case FieldOwnership.LocalOnlyArcGis:
                    return SyncDirection.None;

                case FieldOwnership.Locked:
                case FieldOwnership.Derived:
                    // ArcGIS's value is the truth: geometry metrics a geodatabase maintains
                    // (Locked), or ones recomputed from the geometry whenever it is pushed
                    // (Derived; see DerivedFields). Rhino shows them and never drives them, so a
                    // Rhino-side edit is a violation that the ArcGIS value overwrites, and an
                    // ArcGIS-side change or a missing Rhino copy is pulled.
                    if (state == FieldChangeState.ChangedInRhino || state == FieldChangeState.ChangedInBoth)
                    {
                        violation = true;
                        return SyncDirection.Pull;
                    }
                    if (state == FieldChangeState.ChangedInArcGis || state == FieldChangeState.MissingInRhino)
                        return SyncDirection.Pull;
                    return SyncDirection.None;

                case FieldOwnership.RhinoOwned:
                    if (state == FieldChangeState.ChangedInArcGis) { violation = true; return SyncDirection.None; }
                    if (state == FieldChangeState.ChangedInBoth) { violation = true; return SyncDirection.Push; }
                    if (state == FieldChangeState.ChangedInRhino || state == FieldChangeState.MissingInArcGis) return SyncDirection.Push;
                    if (state == FieldChangeState.MissingInRhino) return SyncDirection.Pull;
                    return SyncDirection.None;

                case FieldOwnership.ArcGisOwned:
                    if (state == FieldChangeState.ChangedInRhino) { violation = true; return SyncDirection.None; }
                    if (state == FieldChangeState.ChangedInBoth) { violation = true; return SyncDirection.Pull; }
                    if (state == FieldChangeState.ChangedInArcGis || state == FieldChangeState.MissingInRhino) return SyncDirection.Pull;
                    return SyncDirection.None;

                case FieldOwnership.Shared:
                    switch (state)
                    {
                        case FieldChangeState.ChangedInRhino:
                        case FieldChangeState.MissingInArcGis:
                            return SyncDirection.Push;
                        case FieldChangeState.ChangedInArcGis:
                        case FieldChangeState.MissingInRhino:
                            return SyncDirection.Pull;
                        case FieldChangeState.ChangedInBoth:
                            return SyncDirection.Conflict;
                        default:
                            return SyncDirection.None;
                    }

                default:
                    return SyncDirection.None;
            }
        }

        /// <summary>
        /// Shape metrics are recomputed from coordinates by ArcGIS. A shapefile can perturb the
        /// last digits after a translated geometry is stored (for example 6458.346191 vs
        /// 6458.346313 square units); that is numeric storage noise, not an independent edit that
        /// belongs in change review.
        /// </summary>
        private static bool DerivedValuesEquivalent(string rhinoValue, string arcGisValue)
        {
            if (string.Equals(rhinoValue, arcGisValue, StringComparison.Ordinal)) return true;
            if (!double.TryParse(rhinoValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double rhino) ||
                !double.TryParse(arcGisValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double arcGis))
                return false;

            double scale = Math.Max(1.0, Math.Max(Math.Abs(rhino), Math.Abs(arcGis)));
            return Math.Abs(rhino - arcGis) <= scale * 1e-7;
        }

        /// <param name="rhinoHash">The Rhino geometry's hash, taken in GIS space.</param>
        /// <param name="arcgisHash">The ArcGIS feature's geometry hash.</param>
        private static ChangeSide GeometryChange(RhinoObjectSnapshot snap, string rhinoHash, string arcgisHash)
        {
            string lastHash = Get(snap.UserStrings, GisKeys.GeometryHash);

            // Each side is judged against how it held the geometry at the last sync. Rhino's copy of
            // a feature is not vertex-for-vertex ArcGIS's (see GisKeys.RhinoGeometryHash), so a
            // single baseline would read an untouched pull as edited on one side or the other.
            string lastRhinoHash = Get(snap.UserStrings, GisKeys.RhinoGeometryHash) ?? lastHash;

            if (string.IsNullOrEmpty(lastHash))
                return rhinoHash == arcgisHash ? ChangeSide.None : ChangeSide.Both;

            bool changedRhino = rhinoHash != lastRhinoHash;
            bool changedArcGis = arcgisHash != lastHash;
            if (changedRhino && changedArcGis) return ChangeSide.Both;
            if (changedRhino) return ChangeSide.Rhino;
            if (changedArcGis) return ChangeSide.ArcGis;
            return ChangeSide.None;
        }

        private static SyncState ResolveObjectState(bool conflict, bool rhinoChange, bool arcgisChange)
        {
            if (conflict) return SyncState.Conflict;
            if (rhinoChange && arcgisChange) return SyncState.ModifiedInBoth;
            if (rhinoChange) return SyncState.ModifiedInRhino;
            if (arcgisChange) return SyncState.ModifiedInArcGis;
            return SyncState.Clean;
        }

        private static string Get(IReadOnlyDictionary<string, string> map, string key)
            => map != null && map.TryGetValue(key, out string v) ? v : null;

        private static Guid? ReadGuid(IReadOnlyDictionary<string, string> map, string key)
            => Guid.TryParse(Get(map, key), out Guid g) ? g : (Guid?)null;

        private static long? ReadLong(IReadOnlyDictionary<string, string> map, string key)
            => long.TryParse(Get(map, key), out long v) ? v : (long?)null;
    }
}
