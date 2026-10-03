using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Orchestrates an ArcGIS → Rhino pull (spec 02 §4). All decisions (transform, attribute
    /// ownership, identity, hashing, reporting) live here; the adapters only do SDK I/O. This is
    /// what makes pull testable with fake adapters.
    ///
    /// Wave 2 implements the create-style modes (Context, EditableLinked) plus selection scoping.
    /// The update-style modes (attribute-only, geometry-only, query, delta) require matching to
    /// existing Rhino objects and are scheduled for Wave 4.
    /// </summary>
    public sealed class PullService
    {
        private readonly IArcGISAdapter _arcgis;
        private readonly IRhinoAdapter _rhino;

        public PullService(IArcGISAdapter arcgis, IRhinoAdapter rhino)
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

        private void Say(string message) => Progress?.Invoke(message);

        private Action<int, int> Counting(string what) =>
            Progress == null ? (Action<int, int>)null
                : (done, total) => Say($"{what} {done.ToString("N0", CultureInfo.InvariantCulture)} / {total.ToString("N0", CultureInfo.InvariantCulture)}…");

        public SyncReport Pull(LayerMappingProfile profile, LayerMapping layer, PullOptions options)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            options = options ?? PullOptions.Context;

            var report = new SyncReport(SyncOperation.Pull);

            if (!options.IsCreateMode)
            {
                report.Add(Guid.Empty, layer.ArcGisLayer, SyncOutcome.Skipped,
                    $"Pull mode '{options.Mode}' is not supported until Wave 4.");
                return report;
            }

            var total = System.Diagnostics.Stopwatch.StartNew();
            Say("Placing the layer on the earth anchor…");
            ICoordinateMap map = report.Time("georef", () => GeoReferenceFactory.Create(_rhino, _arcgis, profile));

            Say($"Reading features from '{layer.ArcGisLayer}'…");
            IReadOnlyList<FeatureRecord> features = report.Time("read.arcgis", () => options.SelectedOnly
                ? _arcgis.ReadSelectedFeatures(layer.ArcGisLayer)
                : _arcgis.ReadFeatures(layer.ArcGisLayer));
            var notice = LargeLayerNotice.ForPull(features.Count);
            if (notice != null) Notice?.Invoke(notice);

            // Pulling is safe to repeat: a feature the Rhino layer already holds a linked object for
            // is left to Sync, rather than pulled a second time as a duplicate that would then read
            // as a copy. A pull after objects were deleted in Rhino brings back just those.
            Say($"Checking what '{layer.RhinoLayer}' already holds…");
            var alreadyInRhino = report.Time("read.rhino", () => LinkedIdentities(layer));
            var ledger = SyncLedger.Load(_rhino, layer.RhinoLayer, layer.ArcGisSource);
            var ledgerChanged = false;

            // Every feature is decided first and the creates go to Rhino as one batch; report rows
            // keep the layer's feature order.
            var rows = new List<SyncReportEntry>(features.Count);
            var candidates = new List<(FeatureRecord feature, Guid syncGuid)>();
            var candidateRows = new List<SyncReportEntry>();
            string fingerprint = ModelStamp.Fingerprint(map);

            foreach (var feature in features)
            {
                Guid syncGuid = feature.Identity != null && feature.Identity.SyncGuid != Guid.Empty
                    ? feature.Identity.SyncGuid
                    : Guid.NewGuid();
                var key = SyncLedger.KeyOf(feature);
                var row = new SyncReportEntry { SyncGuid = syncGuid, Operation = SyncOperation.Pull, Target = layer.ArcGisLayer };
                rows.Add(row);
                try
                {
                    if (key != null && alreadyInRhino.Contains(key))
                    {
                        row.Outcome = SyncOutcome.Skipped;
                        row.Message = "Already in Rhino; Sync updates it.";
                        row.ArcGisObjectId = feature.Identity?.ArcGisObjectId;
                        continue;
                    }

                    if (feature.Geometry == null || feature.Geometry.IsEmpty())
                    {
                        row.Outcome = SyncOutcome.Skipped;
                        row.Message = "Feature has no supported geometry (multipatch read-back arrives in Wave 3).";
                        continue;
                    }

                    candidates.Add((feature, syncGuid));
                    candidateRows.Add(row);
                }
                catch (Exception ex)
                {
                    row.Outcome = SyncOutcome.Failed;
                    row.Message = ex.Message;
                }
            }

            var prepared = report.Time("prepare", () => PullPreparation.Prepare(
                candidates, layer, profile, map, options.Mode, fingerprint, Parallelism,
                Counting(Parallelism > 1 ? $"Converting features for Rhino ({Parallelism} threads)" : "Converting features for Rhino")));
            var pending = new List<(SyncReportEntry row, FeatureRecord feature, PreparedPull prepared)>();
            var creates = new List<NewRhinoObject>();
            for (int i = 0; i < prepared.Length; i++)
            {
                if (prepared[i].Error != null)
                {
                    candidateRows[i].Outcome = SyncOutcome.Failed;
                    candidateRows[i].Message = prepared[i].Error;
                    continue;
                }
                creates.Add(new NewRhinoObject { Geometry = prepared[i].RhinoGeometry, UserStrings = prepared[i].UserStrings });
                pending.Add((candidateRows[i], candidates[i].feature, prepared[i]));
            }

            var made = report.Time("create", () => RhinoBatch.Create(_rhino, layer.RhinoLayer, creates, Counting("Creating Rhino objects")));
            var baselines = new List<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>>();
            for (int i = 0; i < pending.Count; i++)
            {
                var (row, feature, sent) = pending[i];
                string arcgisHash = sent.ArcGisHash;
                var result = i < made.Count ? made[i] : null;
                if (result == null || result.Id == Guid.Empty)
                {
                    row.Outcome = SyncOutcome.Failed;
                    row.Message = result?.Error ?? "Rhino did not create the object.";
                    continue;
                }

                var baseline = RhinoBaseline(result.Id, result.ReadBack, map, sent, fingerprint);
                if (baseline != null) baselines.Add(new KeyValuePair<Guid, IReadOnlyDictionary<string, string>>(result.Id, baseline));
                row.Outcome = SyncOutcome.Created;
                row.ArcGisObjectId = feature.Identity?.ArcGisObjectId;
                row.RhinoGuid = result.Id;
                ledger.Add(SyncLedger.EntryOf(feature, arcgisHash));
                ledgerChanged = true;
            }
            if (baselines.Count > 0) Say("Recording how Rhino stored the geometry…");
            report.Time("readback", () => RhinoBatch.WriteUserStrings(_rhino, baselines));
            foreach (var row in rows) report.Add(row);

            if (ledgerChanged)
            {
                Say("Saving sync records…");
                try { report.Time("ledger.save", () => ledger.Save(_rhino, layer.RhinoLayer)); }
                catch { /* bookkeeping for the next preview; the pull itself succeeded */ }
            }
            report.AddPhase("total", total.ElapsedMilliseconds);
            return report;
        }

        /// <summary>
        /// ArcGIS identities already carried by objects on the target Rhino layer that were pulled
        /// from this layer's data source (or record no source).
        /// </summary>
        private HashSet<string> LinkedIdentities(LayerMapping layer)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<RhinoObjectSnapshot> existing;
            try { existing = RhinoBatch.ReadObjects(_rhino, layer.RhinoLayer); }
            catch { return keys; }
            if (existing == null) return keys;

            foreach (var snap in existing)
            {
                var strings = snap.UserStrings;
                if (strings == null) continue;
                if (SyncIdentity.IsCopy(snap.RhinoGuid, strings)) continue;
                if (!string.IsNullOrEmpty(layer.ArcGisSource) &&
                    strings.TryGetValue(GisKeys.ArcGisSource, out var source) && !string.IsNullOrEmpty(source) &&
                    !string.Equals(source, layer.ArcGisSource, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (strings.TryGetValue(GisKeys.ArcGisGlobalId, out var g) && Guid.TryParse(g, out var globalId))
                    keys.Add(SyncLedger.KeyOf(globalId, null));
                if (strings.TryGetValue(GisKeys.ArcGisObjectId, out var o) &&
                    long.TryParse(o, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var oid))
                    keys.Add(SyncLedger.KeyOf(null, oid));
            }
            return keys;
        }

        /// <summary>
        /// Reads a just-created object back and records how Rhino represents it, when that differs
        /// from the ArcGIS geometry it was made from.
        /// </summary>
        /// <remarks>
        /// Rhino is not vertex-faithful: a planar Brep built from a parcel ring drops segments shorter
        /// than the model tolerance and collapses degenerate spurs, so the read-back hashes
        /// differently from the feature it came from. Without its own baseline the object would read
        /// as "modified in Rhino" from the moment it was pulled, and an apply would push Rhino's
        /// simplification back over the original. The key is written only when the two differ, which
        /// for most features they do not.
        /// </remarks>
        /// <returns>
        /// The user strings to correct, or null when geometry and object ownership are recorded. The
        /// model stamp written with the object was taken from the geometry sent to Rhino; when
        /// Rhino kept something else, it is retaken from what Rhino kept.
        /// </returns>
        internal static IReadOnlyDictionary<string, string> RhinoBaseline(Guid rhinoId, RhinoObjectSnapshot readBack,
            ICoordinateMap map, PreparedPull sent, string fingerprint)
        {
            var fix = new Dictionary<string, string>(StringComparer.Ordinal);
            if (readBack?.UserStrings == null ||
                !readBack.UserStrings.TryGetValue(GisKeys.RhinoObjectId, out var owner) || owner != rhinoId.ToString())
                fix[GisKeys.RhinoObjectId] = rhinoId.ToString();
            // An adapter that cannot read back leaves the shared baseline in place.
            if (readBack == null || readBack.Geometry == null) return fix.Count == 0 ? null : fix;

            // Kept exactly as sent (the usual case), the object maps back to the hash prepared with
            // it; only geometry Rhino changed on the way in is mapped again.
            string stamp = ModelStamp.Of(readBack.Geometry, fingerprint);
            sent.UserStrings.TryGetValue(GisKeys.RhinoModelStamp, out var sentStamp);
            string rhinoHash = stamp == sentStamp && sent.RhinoHash != null
                ? sent.RhinoHash
                : Hashing.HashGeometry(NeutralGeometryTransform.RhinoToGis(readBack.Geometry, map));

            if (rhinoHash != sent.ArcGisHash) fix[GisKeys.RhinoGeometryHash] = rhinoHash;
            if (stamp != sentStamp) fix[GisKeys.RhinoModelStamp] = stamp;
            return fix.Count == 0 ? null : fix;
        }

        /// <summary>
        /// Build the Rhino user strings for a pulled feature: eligible mapped design fields plus
        /// the <c>gis.*</c> system metadata. On a create-style pull every readable field is written
        /// except those owned exclusively by ArcGIS as local-only (spec 04 §7).
        /// </summary>
        internal static IReadOnlyDictionary<string, string> BuildPullUserStrings(
            FeatureRecord feature, LayerMapping layer, LayerMappingProfile profile, Guid syncGuid, PullMode mode,
            string modelStamp = null)
        {
            var userStrings = new Dictionary<string, string>(StringComparer.Ordinal);
            var designForHash = new Dictionary<string, string>(StringComparer.Ordinal);

            if (layer.Attributes != null)
            {
                foreach (FieldMapping map in layer.Attributes)
                {
                    if (map.Owner == FieldOwnership.LocalOnlyArcGis)
                        continue; // stays in GIS, not pulled

                    if (feature.Attributes != null &&
                        feature.Attributes.TryGetValue(map.ArcGisField, out string value) &&
                        value != null)
                    {
                        userStrings[map.RhinoKey] = value;
                        designForHash[map.RhinoKey] = value;
                        // Record the per-field hash so future change detection has a baseline.
                        userStrings[GisKeys.FieldHashKey(map.RhinoKey)] = Hashing.HashField(value);
                    }
                }
            }

            // System / sync metadata.
            userStrings[GisKeys.SyncGuid] = syncGuid.ToString();
            if (feature.Identity != null)
            {
                if (feature.Identity.ArcGisGlobalId.HasValue)
                    userStrings[GisKeys.ArcGisGlobalId] = feature.Identity.ArcGisGlobalId.Value.ToString();
                if (feature.Identity.ArcGisObjectId.HasValue)
                    userStrings[GisKeys.ArcGisObjectId] = feature.Identity.ArcGisObjectId.Value.ToString();
            }
            userStrings[GisKeys.ArcGisLayer] = layer.ArcGisLayer ?? string.Empty;
            if (!string.IsNullOrEmpty(layer.ArcGisSource)) userStrings[GisKeys.ArcGisSource] = layer.ArcGisSource;
            if (!string.IsNullOrEmpty(profile.ArcGisCrs))
                userStrings[GisKeys.SourceCrs] = profile.ArcGisCrs;
            userStrings[GisKeys.LastPullTime] = DateTimeOffset.UtcNow.ToString("o");
            userStrings[GisKeys.AttributeHash] = Hashing.HashAttributes(designForHash);
            userStrings[GisKeys.GeometryHash] = Hashing.HashGeometry(feature.Geometry);
            if (modelStamp != null) userStrings[GisKeys.RhinoModelStamp] = modelStamp;
            // Freshly pulled objects start Clean; editable-linked vs context only differs in whether
            // a later push is allowed, which the profile's push mode governs.
            userStrings[GisKeys.SyncState] = SyncState.Clean.ToString();

            return userStrings;
        }
    }
}
