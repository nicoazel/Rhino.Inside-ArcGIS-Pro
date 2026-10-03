using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Reporting;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// Full bidirectional sync (spec 02 §7). Computes a <see cref="SyncPlan"/> with
    /// <see cref="SyncEngine"/>, optionally resolves conflicts per policy, and applies the result:
    /// push (create/update) Rhino-authoritative changes, pull (create / attribute and geometry update)
    /// ArcGIS-authoritative changes, and re-baseline the synced objects' hashes. Conflicts left in
    /// Manual mode are reported and held, never silently overwritten.
    ///
    /// Geometry pulled onto an existing Rhino object replaces it in place so its GUID, attributes,
    /// and sync identity remain stable. A failed replacement is reported and never re-baselined.
    /// </summary>
    public sealed class SyncService
    {
        private readonly IArcGISAdapter _arcgis;
        private readonly IRhinoAdapter _rhino;
        private readonly SyncEngine _engine = new SyncEngine();

        public SyncService(IArcGISAdapter arcgis, IRhinoAdapter rhino)
        {
            _arcgis = arcgis ?? throw new ArgumentNullException(nameof(arcgis));
            _rhino = rhino ?? throw new ArgumentNullException(nameof(rhino));
        }

        /// <summary>
        /// Threads to prepare pulled features on (map, hash, build user strings); see
        /// <see cref="PullPreparation"/>. 1, the default, keeps everything on the calling thread.
        /// </summary>
        public int Parallelism { get; set; } = 1;

        /// <summary>
        /// What the run is doing now ("Creating Rhino objects 40,000 / 100,000"), raised from the
        /// thread the run is on. For a progress line; never needed for the result.
        /// </summary>
        public Action<string> Progress { get; set; }

        /// <summary>
        /// A heads-up worth keeping on screen for the whole run: a large layer and roughly how long
        /// it will take (see <see cref="LargeLayerNotice"/>).
        /// </summary>
        public Action<string> Notice { get; set; }

        /// <summary>
        /// Validates that the apply context is still current immediately before writing to ArcGIS.
        /// The host supplies a document/link guard so a switched Rhino document cannot commit
        /// GIS edits from a plan built against the previous document.
        /// </summary>
        public Action ValidateWriteContext { get; set; }

        private void Say(string message) => Progress?.Invoke(message);

        private Action<int, int> Counting(string what) =>
            Progress == null ? (Action<int, int>)null
                : (done, total) => Say($"{what} {done.ToString("N0", CultureInfo.InvariantCulture)} / {total.ToString("N0", CultureInfo.InvariantCulture)}…");

        public SyncReport Sync(LayerMappingProfile profile, LayerMapping layer, SyncOptions options)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            options = options ?? new SyncOptions();

            var report = new SyncReport(SyncOperation.Sync);
            var total = System.Diagnostics.Stopwatch.StartNew();
            try { return Sync(profile, layer, options, report); }
            finally { report.AddPhase("total", total.ElapsedMilliseconds); }
        }

        private SyncReport Sync(LayerMappingProfile profile, LayerMapping layer, SyncOptions options, SyncReport report)
        {
            Say($"Reading '{layer.RhinoLayer}' in Rhino…");
            IReadOnlyList<RhinoObjectSnapshot> rhinoObjects = report.Time("read.rhino",
                () => RhinoBatch.ReadObjects(_rhino, layer.RhinoLayer, Counting("Reading Rhino objects")));
            Say($"Reading features from '{layer.ArcGisLayer}'…");
            IReadOnlyList<FeatureRecord> arcgisFeatures = report.Time("read.arcgis", () => _arcgis.ReadFeatures(layer.ArcGisLayer));
            var notice = LargeLayerNotice.ForSync(Math.Max(rhinoObjects.Count, arcgisFeatures.Count), options.Apply);
            if (notice != null) Notice?.Invoke(notice);

            // Objects that belong to another link are not this link's to compare, adopt or push.
            var foreign = LinkedElsewhere(rhinoObjects, layer, out string foreignWarning);
            if (foreign.Count > 0)
            {
                var own = new List<RhinoObjectSnapshot>(rhinoObjects.Count);
                foreach (var snap in rhinoObjects) if (!foreign.Contains(snap)) own.Add(snap);
                rhinoObjects = own;
            }
            ICoordinateMap map = report.Time("georef", () => GeoReferenceFactory.Create(_rhino, _arcgis, profile));
            SyncLedger ledger = report.Time("ledger.load", () => SyncLedger.Load(_rhino, layer.RhinoLayer, layer.ArcGisSource));
            Say($"Comparing {rhinoObjects.Count.ToString("N0", CultureInfo.InvariantCulture)} Rhino object(s) with " +
                $"{arcgisFeatures.Count.ToString("N0", CultureInfo.InvariantCulture)} feature(s)…");
            SyncPlan plan = report.Time("plan", () => _engine.BuildPlan(map, layer, rhinoObjects, arcgisFeatures, ledger));

            var staleSource = ObjectsWithOtherSource(rhinoObjects, layer, out string sourceWarning);
            var staleSourceIds = new HashSet<Guid>(staleSource.Select(snap => snap.RhinoGuid));
            if (sourceWarning != null)
                report.Add(Guid.Empty, layer.ArcGisLayer, SyncOutcome.Warning, sourceWarning);
            if (foreignWarning != null)
                report.Add(Guid.Empty, layer.ArcGisLayer, SyncOutcome.Warning, foreignWarning);

            if (!options.Apply)
            {
                foreach (var d in plan.Decisions)
                    Describe(report.Add(d.SyncGuid, layer.ArcGisLayer, ToOutcome(d.State), d.State.ToString()), d);
                return report;
            }

            LayerSchema schema = _arcgis.GetSchema(layer.ArcGisLayer);
            var byKey = BuildFieldIndex(layer);

            // Resolve conflicts per policy (Manual leaves them as conflicts).
            foreach (var d in plan.Decisions)
                if (d.IsConflict) ResolveConflicts(d, options.Conflicts);

            // ---- PUSH (Rhino -> ArcGIS): batch creates and updates ----
            var creates = new List<FeatureRecord>();
            var createSources = new List<ObjectDecision>();
            var updates = new List<FeatureRecord>();
            var updateSources = new List<ObjectDecision>();
            var rejectedPushFields = new Dictionary<Guid, HashSet<string>>();

            // Rhino guids the push skipped for having no ArcGIS-side conversion (SubD, block
            // instances, hatches: RhinoGeometryReader.ToNeutral returns null for these). Without
            // this, the pull/re-baseline loop below still matches the decision's untouched
            // NewInRhino state and reports it Created -- a real row with a null shape, and a Rhino
            // object marked synced for a push that never happened.
            var pushSkipped = new HashSet<Guid>();

            var buildPush = System.Diagnostics.Stopwatch.StartNew();
            foreach (var d in plan.Decisions)
            {
                if (d.IsConflict || Held(d, options.Direction)) continue;

                bool pushGeom = d.GeometryChange == ChangeSide.Rhino || d.State == SyncState.NewInRhino;
                List<FieldDecision> pushFields = d.Fields.Where(f => f.Direction == SyncDirection.Push).ToList();
                bool hasConvertibleGeometry = d.RhinoSource?.Geometry != null && !d.RhinoSource.Geometry.IsEmpty();

                if (d.State == SyncState.NewInRhino)
                {
                    if (!hasConvertibleGeometry)
                    {
                        report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Skipped,
                            "Rhino geometry has no supported conversion to push (e.g. SubD, block instance, or hatch).");
                        if (d.RhinoGuid.HasValue) pushSkipped.Add(d.RhinoGuid.Value);
                        continue;
                    }

                    var rec = BuildPushRecord(d, byKey, schema, map, true, pushFields, report, layer,
                        RejectedFieldsFor(d, rejectedPushFields));
                    if (rec != null) { creates.Add(rec); createSources.Add(d); }
                }
                else if (d.State == SyncState.ModifiedInRhino || d.State == SyncState.ModifiedInBoth)
                {
                    if (pushGeom && !hasConvertibleGeometry) pushGeom = false;
                    if ((pushFields.Count > 0 || pushGeom) && d.ArcGisObjectId.HasValue)
                    {
                        var rec = BuildPushRecord(d, byKey, schema, map, pushGeom, pushFields, report, layer,
                            RejectedFieldsFor(d, rejectedPushFields));
                        if (rec != null)
                        {
                            rec.Identity.ArcGisObjectId = d.ArcGisObjectId;
                            updates.Add(rec);
                            updateSources.Add(d);
                        }
                    }
                }
            }

            report.AddPhase("push.build", buildPush.ElapsedMilliseconds);

            if (creates.Count + updates.Count > 0)
                Say($"Writing {(creates.Count + updates.Count).ToString("N0", CultureInfo.InvariantCulture)} change(s) to ArcGIS…");
            IReadOnlyList<long> newOids = creates.Count > 0
                ? report.Time("push.create", () =>
                {
                    ValidateWriteContext?.Invoke();
                    return _arcgis.CreateFeatures(layer.ArcGisLayer, creates);
                })
                : new List<long>();
            if (updates.Count > 0) report.Time("push.update", () =>
            {
                ValidateWriteContext?.Invoke();
                _arcgis.UpdateFeatures(layer.ArcGisLayer, updates);
            });

            for (int i = 0; i < createSources.Count; i++)
                createSources[i].ArcGisObjectId = (newOids != null && i < newOids.Count) ? newOids[i] : (long?)null;

            if (createSources.Count > 0 || updateSources.Count > 0)
            {
                var pushed = new List<ObjectDecision>(createSources);
                pushed.AddRange(updateSources);
                report.Time("push.adopt", () => AdoptPushedFeatures(pushed, layer, rejectedPushFields));
            }

            // ---- PULL (ArcGIS -> Rhino) + re-baseline ----
            // Rhino writes are collected and made as batches after the loop (see RhinoBatch); each
            // decision's report row is added here, in plan order, and completed once its batch ran.
            var pullCreates = new PendingCreates { Fingerprint = ModelStamp.Fingerprint(map) };
            var replacements = new List<(ObjectDecision d, SyncReportEntry row, GeometryReplacement replacement,
                HashSet<string> rejectedFields)>();
            var baselines = new List<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>>();

            foreach (var d in plan.Decisions)
            {
                if (d.IsConflict)
                {
                    report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Conflict, "Held for manual review.");
                    continue;
                }

                if (Held(d, options.Direction))
                {
                    // Reported so it stays visible, and its baseline is left alone so it is still a
                    // change next time -- a one-way sync holds the other side's edits, it does not
                    // swallow them.
                    report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Held,
                        options.Direction == SyncDirectionMode.PullOnly
                            ? "Held: this sync is pull-only, so Rhino changes are not written to ArcGIS."
                            : "Held: this sync is push-only, so ArcGIS changes are not written to Rhino.");
                    continue;
                }

                // Already reported (and skipped) by the push loop above; do not re-baseline it here
                // as if the push had gone through.
                if (d.RhinoGuid.HasValue && pushSkipped.Contains(d.RhinoGuid.Value)) continue;

                switch (d.State)
                {
                    case SyncState.NewInArcGis:
                        QueuePullCreate(d, layer, report, pullCreates);
                        break;

                    case SyncState.DeletedInArcGis:
                        // Neither recreated in ArcGIS nor removed from Rhino: deleting is the user's
                        // call, and the object keeps its identity so the decision can be revisited.
                        Describe(report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Conflict,
                            "Feature no longer exists in ArcGIS; the Rhino object is held, not recreated."), d);
                        break;

                    case SyncState.DeletedInRhino:
                        // The mirror case: the feature is neither deleted in ArcGIS nor brought back
                        // into Rhino. Pull restores it; deleting it in ArcGIS settles it.
                        Describe(report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Held,
                            "Deleted in Rhino; the ArcGIS feature is kept. Pull restores the Rhino object, or delete the feature in ArcGIS."), d);
                        break;

                    case SyncState.Clean:
                        // Upgrade legacy objects on apply, never on preview. Also record a repaired
                        // shapefile ObjectID so the next run can match by id again.
                        if (d.RhinoGuid.HasValue)
                        {
                            var identity = new Dictionary<string, string>(StringComparer.Ordinal);
                            if (d.RhinoSource?.UserStrings == null ||
                                !d.RhinoSource.UserStrings.ContainsKey(GisKeys.RhinoObjectId))
                                identity[GisKeys.RhinoObjectId] = d.RhinoGuid.Value.ToString();
                            if (d.IdentityRepaired && d.ArcGisObjectId.HasValue)
                                identity[GisKeys.ArcGisObjectId] = d.ArcGisObjectId.Value.ToString(CultureInfo.InvariantCulture);
                            // Source acceptance is part of a completed decision's normal write.
                            // Conflicts, held/deleted objects, and failed operations never reach it.
                            if (staleSourceIds.Contains(d.RhinoGuid.Value))
                                identity[GisKeys.ArcGisSource] = layer.ArcGisSource;
                            if (identity.Count > 0)
                                baselines.Add(new KeyValuePair<Guid, IReadOnlyDictionary<string, string>>(d.RhinoGuid.Value, identity));
                        }
                        break;

                    case SyncState.NewInRhino:
                    case SyncState.ModifiedInRhino:
                    case SyncState.ModifiedInArcGis:
                    case SyncState.ModifiedInBoth:
                        if (d.GeometryChange == ChangeSide.ArcGis)
                        {
                            // Baselined after the replacement, from the geometry Rhino then holds.
                            var replacement = PrepareGeometryUpdate(d, layer, map, report);
                            if (replacement != null)
                                replacements.Add((d, report.Add(d.SyncGuid, layer.ArcGisLayer, ToOutcome(d.State)),
                                    replacement, RejectedFieldsFor(d, rejectedPushFields)));
                            break;
                        }
                        if (d.RhinoGuid.HasValue)
                            baselines.Add(new KeyValuePair<Guid, IReadOnlyDictionary<string, string>>(
                                d.RhinoGuid.Value, BuildBaseline(d, layer, profile, map, pullCreates.Fingerprint,
                                    RejectedFieldsFor(d, rejectedPushFields))));
                        Describe(report.Add(d.SyncGuid, layer.ArcGisLayer, ToOutcome(d.State)), d);
                        break;

                    default:
                        break;
                }
            }

            report.Time("pull.create", () => ApplyPullCreates(pullCreates, layer, profile, map, baselines));
            report.Time("pull.geometry", () => ApplyGeometryUpdates(replacements, layer, profile, map, pullCreates.Fingerprint, baselines));
            report.Time("baseline.write", () => RhinoBatch.WriteUserStrings(_rhino, baselines, Counting("Recording sync baselines")));
            Say("Saving sync records…");

            report.Time("ledger.save", () => SaveLedger(plan, layer, pushSkipped));
            _arcgis.RefreshScene();
            return report;
        }

        /// <summary>
        /// Whether a one-way sync must leave this object alone: pull-only holds anything Rhino
        /// changed, push-only holds anything ArcGIS changed. Objects changed on both sides are held
        /// either way, since applying half of them would rebaseline away the other half.
        /// </summary>
        private static bool Held(ObjectDecision d, SyncDirectionMode direction)
        {
            switch (direction)
            {
                case SyncDirectionMode.PullOnly:
                    return d.State == SyncState.NewInRhino || d.State == SyncState.ModifiedInRhino ||
                           d.State == SyncState.ModifiedInBoth;
                case SyncDirectionMode.PushOnly:
                    return d.State == SyncState.NewInArcGis || d.State == SyncState.ModifiedInArcGis ||
                           d.State == SyncState.ModifiedInBoth || d.State == SyncState.DeletedInArcGis;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The objects that were pulled from a different data source than the layer now under that
        /// name in the map, with a warning to show if there are any.
        /// </summary>
        /// <remarks>
        /// The pairing is by map label, and labels are not unique across projects or over time: a
        /// week later the same name can sit over a copy, an older extract, or something unrelated.
        /// Every pulled object records where it came from. A disagreement is worth saying out loud
        /// -- but not worth refusing over: a copy of the data with the same ids is exactly what
        /// someone comparing or moving a project has, and the plan the engine builds is the honest
        /// answer to "how do these compare". Objects without the record (pulled before it existed,
        /// or drawn in Rhino) are not held against the layer.
        /// </remarks>
        /// <summary>
        /// Objects on the Rhino layer that are linked to a different ArcGIS layer: both the layer
        /// name and the data source they record differ from this link's.
        /// </summary>
        /// <remarks>
        /// One Rhino object carries one link. Objects pulled from layer A and then synced against an
        /// unrelated layer B used to be read as B's features "deleted in ArcGIS" -- nothing was
        /// created -- and, worse, the source-change rule below rewrote their recorded source to B,
        /// quietly breaking their link to A. A renamed layer (same source) and a same-named layer
        /// over moved data (same name) still belong here; only when neither matches is an object
        /// someone else's, and it is then left completely alone.
        /// </remarks>
        private static HashSet<RhinoObjectSnapshot> LinkedElsewhere(IReadOnlyList<RhinoObjectSnapshot> rhinoObjects,
            LayerMapping layer, out string warning)
        {
            warning = null;
            var foreign = new HashSet<RhinoObjectSnapshot>();
            if (string.IsNullOrEmpty(layer.ArcGisSource)) return foreign;

            string otherLayer = null;
            foreach (var snap in rhinoObjects)
            {
                var strings = snap.UserStrings;
                if (strings == null) continue;
                if (!strings.TryGetValue(GisKeys.ArcGisLayer, out var recordedLayer) || string.IsNullOrEmpty(recordedLayer)) continue;
                if (!strings.TryGetValue(GisKeys.ArcGisSource, out var recordedSource) || string.IsNullOrEmpty(recordedSource)) continue;
                if (string.Equals(recordedLayer, layer.ArcGisLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(recordedSource, layer.ArcGisSource, StringComparison.OrdinalIgnoreCase)) continue;
                foreign.Add(snap);
                otherLayer = otherLayer ?? recordedLayer;
            }

            if (foreign.Count > 0)
                warning = $"{foreign.Count} object(s) on '{layer.RhinoLayer}' are linked to ArcGIS layer '{otherLayer}', not " +
                          $"'{layer.ArcGisLayer}'; they were left alone. Publish a copy, or move their link, to use them here.";
            return foreign;
        }

        private static List<RhinoObjectSnapshot> ObjectsWithOtherSource(IReadOnlyList<RhinoObjectSnapshot> rhinoObjects,
            LayerMapping layer, out string warning)
        {
            warning = null;
            var stale = new List<RhinoObjectSnapshot>();
            if (string.IsNullOrEmpty(layer.ArcGisSource)) return stale;

            string recordedExample = null;
            foreach (var snap in rhinoObjects)
            {
                if (!snap.UserStrings.TryGetValue(GisKeys.ArcGisSource, out var recorded) || string.IsNullOrEmpty(recorded)) continue;
                if (string.Equals(recorded, layer.ArcGisSource, StringComparison.OrdinalIgnoreCase)) continue;
                stale.Add(snap);
                recordedExample = recordedExample ?? recorded;
            }

            if (stale.Count > 0)
                warning = $"Source changed: {stale.Count} object(s) on '{layer.RhinoLayer}' were pulled from {recordedExample} " +
                          $"but '{layer.ArcGisLayer}' now reads from {layer.ArcGisSource}. " +
                          "The comparison below is against the new data. Completed Apply decisions accept it as the source; held or failed objects retain their original source.";
            return stale;
        }

        /// <summary>
        /// Reads the features a push just created or updated back from ArcGIS and folds their stored
        /// attribute values into the decisions, so the baseline written to Rhino matches what ArcGIS
        /// holds.
        /// </summary>
        /// <remarks>
        /// A feature created from a Rhino object arrives in ArcGIS with every field it did not
        /// supply set to the layer's default -- blank text, zero numbers, a fresh object id. Rhino
        /// knows none of those values, so without this the very next preview reported the object as
        /// modified in ArcGIS, and an attribute then edited in Rhino was raised as a conflict against
        /// the default it had never seen. Pulling the defaults into the baseline closes that gap:
        /// the object reads as clean, and a later Rhino edit is just a Rhino edit. The same applies
        /// to a value pushed into an existing feature: a shapefile pads or truncates text to the
        /// field's width, and the baseline has to be what survived.
        /// </remarks>
        private void AdoptPushedFeatures(List<ObjectDecision> pushed, LayerMapping layer,
            IReadOnlyDictionary<Guid, HashSet<string>> rejectedFields)
        {
            var wanted = new HashSet<long>();
            foreach (var d in pushed)
                if (d.ArcGisObjectId.HasValue) wanted.Add(d.ArcGisObjectId.Value);
            if (wanted.Count == 0) return;

            var fresh = new Dictionary<long, FeatureRecord>();
            var read = _arcgis is IFeatureLookup lookup
                ? lookup.ReadFeatures(layer.ArcGisLayer, wanted)
                : _arcgis.ReadFeatures(layer.ArcGisLayer);
            foreach (var f in read)
                if (f.Identity != null && f.Identity.ArcGisObjectId.HasValue && wanted.Contains(f.Identity.ArcGisObjectId.Value))
                    fresh[f.Identity.ArcGisObjectId.Value] = f;

            foreach (var d in pushed)
            {
                if (!d.ArcGisObjectId.HasValue || !fresh.TryGetValue(d.ArcGisObjectId.Value, out var feature)) continue;
                d.ArcGisSource = feature;
                d.ArcGisGlobalId = feature.Identity?.ArcGisGlobalId;
                if (layer.Attributes == null || feature.Attributes == null) continue;

                foreach (var map in layer.Attributes)
                {
                    if (map.Owner == FieldOwnership.LocalOnlyArcGis) continue;
                    if (rejectedFields != null && rejectedFields.TryGetValue(d.SyncGuid, out var rejected) &&
                        rejected.Contains(map.RhinoKey)) continue;
                    if (!feature.Attributes.TryGetValue(map.ArcGisField, out var value) || value == null) continue;

                    var existing = d.Fields.Find(f => f.RhinoKey == map.RhinoKey);
                    if (existing != null)
                    {
                        // The push has happened; what matters for the baseline is the value as ArcGIS
                        // now stores it, which a shapefile may have padded or trimmed. Flipping the
                        // direction makes BuildBaseline take that stored value. Fields that were not
                        // pushed keep whatever direction the plan gave them.
                        // Geometry metrics change with a pushed shape without a field decision of
                        // their own: Derived ones we recomputed and wrote (a shapefile), Locked ones
                        // the geodatabase maintains itself. ArcGIS's value is the new truth for both,
                        // or the next preview reads the push back as an ArcGIS edit.
                        if (existing.Direction == SyncDirection.Push ||
                            ((existing.Owner == FieldOwnership.Derived || existing.Owner == FieldOwnership.Locked) &&
                             existing.Direction == SyncDirection.None))
                        {
                            existing.ArcGisValue = value;
                            existing.Direction = SyncDirection.Pull;
                        }
                        continue;
                    }

                    d.Fields.Add(new FieldDecision
                    {
                        RhinoKey = map.RhinoKey,
                        ArcGisField = map.ArcGisField,
                        Owner = map.Owner,
                        State = FieldChangeState.MissingInRhino,
                        Direction = SyncDirection.Pull,
                        ArcGisValue = value
                    });
                }
            }
        }

        /// <summary>Features to be created in Rhino, with their decisions and report rows.</summary>
        private sealed class PendingCreates
        {
            public readonly List<ObjectDecision> Decisions = new List<ObjectDecision>();
            public readonly List<SyncReportEntry> Rows = new List<SyncReportEntry>();
            public readonly List<(FeatureRecord feature, Guid syncGuid)> Features = new List<(FeatureRecord, Guid)>();
            public string Fingerprint;
        }

        private static void QueuePullCreate(ObjectDecision d, LayerMapping layer, SyncReport report, PendingCreates creates)
        {
            if (d.ArcGisSource.Geometry == null || d.ArcGisSource.Geometry.IsEmpty())
            {
                report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Skipped, "ArcGIS feature has no supported geometry to pull.");
                return;
            }

            Guid syncGuid = d.SyncGuid != Guid.Empty ? d.SyncGuid : Guid.NewGuid();
            creates.Decisions.Add(d);
            creates.Rows.Add(report.Add(syncGuid, layer.ArcGisLayer, SyncOutcome.Created));
            creates.Features.Add((d.ArcGisSource, syncGuid));
        }

        private void ApplyPullCreates(PendingCreates creates, LayerMapping layer, LayerMappingProfile profile,
            ICoordinateMap map, List<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> baselines)
        {
            var prepared = PullPreparation.Prepare(creates.Features, layer, profile, map, PullMode.EditableLinked,
                creates.Fingerprint, Parallelism, Counting("Converting features for Rhino"));
            var objects = new List<NewRhinoObject>();
            var sentIndex = new List<int>();
            for (int i = 0; i < prepared.Length; i++)
            {
                var p = prepared[i];
                if (p.Error == null && p.RhinoGeometry != null && !p.RhinoGeometry.IsEmpty())
                {
                    objects.Add(new NewRhinoObject { Geometry = p.RhinoGeometry, UserStrings = p.UserStrings });
                    sentIndex.Add(i);
                    continue;
                }
                creates.Rows[i].Outcome = p.Error != null ? SyncOutcome.Failed : SyncOutcome.Skipped;
                creates.Rows[i].Message = p.Error ?? "ArcGIS feature has no supported geometry to pull.";
            }

            var made = RhinoBatch.Create(_rhino, layer.RhinoLayer, objects, Counting("Creating Rhino objects"));
            for (int k = 0; k < sentIndex.Count; k++)
            {
                int i = sentIndex[k];
                var d = creates.Decisions[i];
                var row = creates.Rows[i];
                var result = k < made.Count ? made[k] : null;
                if (result == null || result.Id == Guid.Empty)
                {
                    row.Outcome = SyncOutcome.Failed;
                    row.Message = result?.Error ?? "Rhino did not create the object.";
                    continue;
                }

                var baseline = PullService.RhinoBaseline(result.Id, result.ReadBack, map, prepared[i], creates.Fingerprint);
                if (baseline != null) baselines.Add(new KeyValuePair<Guid, IReadOnlyDictionary<string, string>>(result.Id, baseline));
                d.RhinoGuid = result.Id;
                Describe(row, d);
            }
        }

        /// <summary>
        /// The replacement to make for an ArcGIS geometry edit, or null (with the failure reported)
        /// when there is nothing to replace or nothing Rhino can represent.
        /// </summary>
        private static GeometryReplacement PrepareGeometryUpdate(ObjectDecision d, LayerMapping layer, ICoordinateMap map,
            SyncReport report)
        {
            if (!d.RhinoGuid.HasValue || d.ArcGisSource?.Geometry == null)
            {
                report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Failed,
                    "ArcGIS geometry could not be applied because the linked object or source geometry is missing.");
                return null;
            }

            NeutralGeometry rhinoGeometry =
                NeutralGeometryTransform.GisToRhino(d.ArcGisSource.Geometry, map);
            if (rhinoGeometry == null || rhinoGeometry.IsEmpty())
            {
                report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Failed,
                    "ArcGIS geometry has no supported Rhino representation; the linked object was not changed.");
                return null;
            }
            return new GeometryReplacement { Id = d.RhinoGuid.Value, Geometry = rhinoGeometry };
        }

        /// <summary>
        /// Replaces the Rhino geometry of every ArcGIS geometry edit, then baselines each from the
        /// representation Rhino actually retained: planar Breps and meshes can be normalized while
        /// being written, so hashing only the input would advance to a baseline the next preview
        /// can never reproduce. A failed replacement is reported, selected, and never re-baselined.
        /// </summary>
        private void ApplyGeometryUpdates(
            List<(ObjectDecision d, SyncReportEntry row, GeometryReplacement replacement, HashSet<string> rejectedFields)> updates,
            LayerMapping layer, LayerMappingProfile profile, ICoordinateMap map, string fingerprint,
            List<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> baselines)
        {
            if (updates.Count == 0) return;
            var results = RhinoBatch.ReplaceGeometries(_rhino, updates.Select(u => u.replacement).ToList(),
                Counting("Updating Rhino geometry"));
            var failed = new List<Guid>();
            for (int i = 0; i < updates.Count; i++)
            {
                var (d, row, replacement, rejectedFields) = updates[i];
                var result = i < results.Count ? results[i] : null;
                if (result == null || result.Error != null || result.ReadBack?.Geometry == null)
                {
                    failed.Add(replacement.Id);
                    row.Outcome = SyncOutcome.Failed;
                    row.Message = "Could not update Rhino geometry: " +
                                  (result?.Error ?? "Rhino could not read the replacement geometry back.");
                    continue;
                }

                d.RhinoSource = result.ReadBack;
                baselines.Add(new KeyValuePair<Guid, IReadOnlyDictionary<string, string>>(
                    replacement.Id, BuildBaseline(d, layer, profile, map, fingerprint, rejectedFields)));
                Describe(row, d);
            }

            if (failed.Count > 0)
            {
                try { _rhino.IsolateFailed(failed); }
                catch { /* reporting the replacement failure must not be masked by selection UI */ }
            }
        }

        /// <summary>Re-route a conflicted object's fields/geometry by policy (Manual keeps the conflict).</summary>
        private static void ResolveConflicts(ObjectDecision d, ConflictResolution policy)
        {
            if (policy == ConflictResolution.Manual) return;

            SyncDirection winner = policy == ConflictResolution.PreferRhino ? SyncDirection.Push : SyncDirection.Pull;
            foreach (var f in d.Fields)
                if (f.Direction == SyncDirection.Conflict) f.Direction = winner;

            if (d.GeometryChange == ChangeSide.Both)
                d.GeometryChange = policy == ConflictResolution.PreferRhino ? ChangeSide.Rhino : ChangeSide.ArcGis;

            bool rhino = d.Fields.Any(f => f.Direction == SyncDirection.Push) || d.GeometryChange == ChangeSide.Rhino;
            bool arc = d.Fields.Any(f => f.Direction == SyncDirection.Pull) || d.GeometryChange == ChangeSide.ArcGis;
            d.State = rhino && arc ? SyncState.ModifiedInBoth
                : rhino ? SyncState.ModifiedInRhino
                : arc ? SyncState.ModifiedInArcGis
                : SyncState.Clean;
        }

        private FeatureRecord BuildPushRecord(ObjectDecision d, IReadOnlyDictionary<string, FieldMapping> byKey,
            LayerSchema schema, ICoordinateMap coords, bool includeGeometry, List<FieldDecision> fields,
            SyncReport report, LayerMapping layer, HashSet<string> rejectedFields)
        {
            NeutralGeometry gisGeometry = null;
            if (d.RhinoSource != null && d.RhinoSource.Geometry != null)
                gisGeometry = NeutralGeometryTransform.RhinoToGis(d.RhinoSource.Geometry, coords);

            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);

            // Geometry metrics the layer keeps in columns are a function of what is being written,
            // not of anything Rhino holds, so they are recomputed whenever the geometry goes out.
            if (includeGeometry && gisGeometry != null && layer.Attributes != null)
                foreach (var map in layer.Attributes)
                    if (map.Owner == FieldOwnership.Derived && (schema == null || schema.FindField(map.ArcGisField) != null))
                    {
                        string derived = DerivedFields.Compute(map.ArcGisField, gisGeometry);
                        if (derived != null) attrs[map.ArcGisField] = derived;
                    }

            // New Rhino objects do not have a FieldDecision for a missing user-text key. Validate
            // required/non-empty authored fields explicitly so absence is visible before ArcGIS
            // receives a partial row.
            var decidedKeys = new HashSet<string>(fields.Select(field => field.RhinoKey), StringComparer.Ordinal);
            foreach (var map in byKey.Values)
            {
                if (decidedKeys.Contains(map.RhinoKey) ||
                    (map.Owner != FieldOwnership.RhinoOwned && map.Owner != FieldOwnership.Shared))
                    continue;
                var current = d.RhinoSource?.UserStrings != null &&
                              d.RhinoSource.UserStrings.TryGetValue(map.RhinoKey, out var value)
                    ? value
                    : null;
                if (current != null) continue;
                if (map.Required)
                    report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Warning,
                        $"{map.RhinoKey}: a value is required by the ArcGIS schema.");
                else if (!FieldValueValidator.TryValidate(
                             null, map.Type, map.Domain, map.Validators, out string missingError))
                    report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Warning,
                        $"{map.RhinoKey}: {missingError}");
            }

            foreach (var f in fields)
            {
                FieldMapping map = byKey.TryGetValue(f.RhinoKey, out var m) ? m : null;
                if (map == null || map.Owner == FieldOwnership.Derived) continue;

                if (f.RhinoValue == null)
                {
                    if (map.Required)
                        report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Warning,
                            $"{f.RhinoKey}: a value is required by the ArcGIS schema.");
                    else if (!FieldValueValidator.TryValidate(
                                 null, map.Type, map.Domain, map.Validators, out string missingError))
                        report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Warning,
                            $"{f.RhinoKey}: {missingError}");
                    rejectedFields?.Add(f.RhinoKey);
                    continue;
                }

                if (!FieldValueValidator.TryValidate(
                        f.RhinoValue, map.Type, map.Domain, map.Validators, out string error))
                {
                    report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Warning, $"{f.RhinoKey}: {error}");
                    rejectedFields?.Add(f.RhinoKey);
                    continue;
                }
                if (schema != null && schema.FindField(map.ArcGisField) == null)
                {
                    report.Add(d.SyncGuid, layer.ArcGisLayer, SyncOutcome.Warning, $"Field '{map.ArcGisField}' missing in schema; skipped.");
                    rejectedFields?.Add(f.RhinoKey);
                    continue;
                }
                attrs[map.ArcGisField] = f.RhinoValue;
            }

            return new FeatureRecord
            {
                Target = layer.GeometryTarget,
                Geometry = includeGeometry ? gisGeometry : null,
                Attributes = attrs,
                Identity = new SyncIdentity(d.SyncGuid) { RhinoGuid = d.RhinoGuid ?? Guid.Empty, TargetLayer = layer.ArcGisLayer }
            };
        }

        /// <summary>
        /// Compute the post-sync baseline user strings for a Rhino object: apply any pulled values,
        /// then record fresh per-field/geometry/attribute hashes so the next sync sees it as Clean.
        /// </summary>
        private static IReadOnlyDictionary<string, string> BuildBaseline(ObjectDecision d, LayerMapping layer,
            LayerMappingProfile profile, ICoordinateMap map, string fingerprint,
            ISet<string> rejectedFields = null)
        {
            var us = new Dictionary<string, string>(StringComparer.Ordinal);
            var design = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var f in d.Fields)
            {
                if (rejectedFields != null && rejectedFields.Contains(f.RhinoKey))
                {
                    // Do not write either the rejected field or its aggregate hash. RhinoBatch
                    // merges only supplied keys, so the authored value and old per-field baseline
                    // remain intact and the next preview still sees the unresolved edit.
                    continue;
                }
                // Final Rhino-side value: pulled fields take the ArcGIS value, others keep the Rhino value.
                string finalValue = f.Direction == SyncDirection.Pull ? f.ArcGisValue : f.RhinoValue;
                if (finalValue == null) continue;
                us[f.RhinoKey] = finalValue;
                us[GisKeys.FieldHashKey(f.RhinoKey)] = Hashing.HashField(finalValue);
                design[f.RhinoKey] = finalValue;
            }

            // Geometry baseline, one per side (see GisKeys.RhinoGeometryHash). The ArcGIS side is
            // what ArcGIS holds now -- after a push, that is the feature as re-read by
            // AdoptPushedFeatures; after a pull update, RhinoSource is the replacement as re-read.
            string rhinoHash = null, arcHash = null;
            if (d.RhinoSource != null && d.RhinoSource.Geometry != null)
                rhinoHash = Hashing.HashGeometry(NeutralGeometryTransform.RhinoToGis(d.RhinoSource.Geometry, map));

            if (d.ArcGisSource != null && d.ArcGisSource.Geometry != null)
                arcHash = Hashing.HashGeometry(d.ArcGisSource.Geometry);
            arcHash = arcHash ?? rhinoHash;

            us[GisKeys.SyncGuid] = d.SyncGuid.ToString();
            if (d.RhinoGuid.HasValue) us[GisKeys.RhinoObjectId] = d.RhinoGuid.Value.ToString();
            if (d.State == SyncState.NewInRhino)
            {
                // A copied or orphaned multipart piece now owns a feature of its own.
                us[GisKeys.PartOf] = null;
                us[GisKeys.PartIndex] = null;
            }
            if (d.ArcGisObjectId.HasValue) us[GisKeys.ArcGisObjectId] = d.ArcGisObjectId.Value.ToString();
            if (d.ArcGisGlobalId.HasValue) us[GisKeys.ArcGisGlobalId] = d.ArcGisGlobalId.Value.ToString();
            // A copy arrived carrying the original's GlobalID; a feature without one must not keep it.
            else if (d.IsCopy) us[GisKeys.ArcGisGlobalId] = null;
            us[GisKeys.ArcGisLayer] = layer.ArcGisLayer ?? string.Empty;
            if (!string.IsNullOrEmpty(layer.ArcGisSource)) us[GisKeys.ArcGisSource] = layer.ArcGisSource;
            if (!string.IsNullOrEmpty(profile.ArcGisCrs)) us[GisKeys.SourceCrs] = profile.ArcGisCrs;
            if (arcHash != null) us[GisKeys.GeometryHash] = arcHash;
            if (rhinoHash != null) us[GisKeys.RhinoGeometryHash] = rhinoHash;
            // Taken from the same geometry as rhinoHash, or removed: a stamp must never vouch for
            // a baseline it was not taken with.
            us[GisKeys.RhinoModelStamp] = rhinoHash != null ? ModelStamp.Of(d.RhinoSource.Geometry, fingerprint) : null;
            // The aggregate hash is compatibility metadata; preserve it when one of its inputs
            // was rejected. Per-field hashes remain the authoritative change detector.
            if (rejectedFields == null || rejectedFields.Count == 0)
                us[GisKeys.AttributeHash] = Hashing.HashAttributes(design);
            us[GisKeys.LastPushTime] = DateTimeOffset.UtcNow.ToString("o");
            us[GisKeys.LastPullTime] = DateTimeOffset.UtcNow.ToString("o");
            us[GisKeys.SyncState] = SyncState.Clean.ToString();
            return us;
        }

        private static HashSet<string> RejectedFieldsFor(ObjectDecision d,
            IDictionary<Guid, HashSet<string>> rejectedFields)
        {
            if (d == null) return null;
            if (!rejectedFields.TryGetValue(d.SyncGuid, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                rejectedFields.Add(d.SyncGuid, keys);
            }
            return keys;
        }

        private static Dictionary<string, FieldMapping> BuildFieldIndex(LayerMapping layer)
        {
            var map = new Dictionary<string, FieldMapping>(StringComparer.Ordinal);
            if (layer.Attributes != null)
                foreach (var a in layer.Attributes)
                    if (!string.IsNullOrEmpty(a.RhinoKey)) map[a.RhinoKey] = a;
            return map;
        }

        /// <summary>Adds what the review list needs to identify and explain a row.</summary>
        private static void Describe(SyncReportEntry entry, ObjectDecision d)
        {
            entry.ArcGisObjectId = d.ArcGisObjectId;
            entry.RhinoGuid = d.RhinoGuid;
            var detail = d.Describe();
            if (!string.IsNullOrEmpty(detail)) entry.Detail = detail;
        }

        /// <summary>
        /// Records which features the Rhino layer now holds a linked object for, so a later run can
        /// tell an object deleted in Rhino from a feature that was never brought across.
        /// </summary>
        private void SaveLedger(SyncPlan plan, LayerMapping layer, HashSet<Guid> pushSkipped)
        {
            var ledger = SyncLedger.Empty(layer.ArcGisSource);
            foreach (var d in plan.Decisions)
            {
                switch (d.State)
                {
                    case SyncState.NewInArcGis:
                        // Only once the pull has actually made its Rhino object.
                        if (d.RhinoGuid.HasValue) ledger.Add(LedgerEntry(d));
                        break;
                    case SyncState.NewInRhino:
                        if (d.RhinoGuid.HasValue && !pushSkipped.Contains(d.RhinoGuid.Value) && d.ArcGisObjectId.HasValue)
                            ledger.Add(LedgerEntry(d));
                        break;
                    case SyncState.DeletedInArcGis:
                        break;
                    default:
                        // Matched objects, and features deleted in Rhino, which stay flagged until
                        // someone decides what to do with them.
                        ledger.Add(LedgerEntry(d));
                        break;
                }
            }
            try { ledger.Save(_rhino, layer.RhinoLayer); }
            catch { /* the ledger sharpens the next preview; it must never fail an apply that succeeded */ }
        }

        /// <summary>The feature as ArcGIS holds it after this run, recorded the way SyncLedger wants.</summary>
        private static string LedgerEntry(ObjectDecision d) =>
            d.ArcGisSource != null ? SyncLedger.EntryOf(d.ArcGisSource) : SyncLedger.KeyOf(d.ArcGisGlobalId, d.ArcGisObjectId);

        private static SyncOutcome ToOutcome(SyncState state)
        {
            switch (state)
            {
                case SyncState.NewInRhino:
                case SyncState.NewInArcGis:
                    return SyncOutcome.Created;
                case SyncState.ModifiedInRhino:
                case SyncState.ModifiedInArcGis:
                case SyncState.ModifiedInBoth:
                    return SyncOutcome.Updated;
                case SyncState.Conflict:
                    return SyncOutcome.Conflict;
                default:
                    return SyncOutcome.Skipped;
            }
        }
    }
}
