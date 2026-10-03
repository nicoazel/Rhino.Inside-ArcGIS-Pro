using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Reporting;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Drives the interop stack from the add-in: pairs one ArcGIS layer with one Rhino layer and
    /// runs either a preview or an apply through <see cref="SyncService"/>.
    /// </summary>
    /// <remarks>
    /// Threading: operations run behind a background task and a shared async gate. The Rhino
    /// adapter marshals document access back to the captured UI thread; ArcGISAdapter marshals
    /// geodatabase work to ArcGIS Pro's main CIM thread. Review callbacks also run on the UI thread.
    ///
    /// RhinoAdapter is a RhinoCommon type, so touching it sits behind a NoInlining boundary for the
    /// same reason every member of <see cref="RhinoHost"/> does: the assembly must not be forced to
    /// load before the netcore resolver is in place.
    /// </remarks>
    internal static class SyncCoordinator
    {
        static System.Windows.Threading.Dispatcher _uiDispatcher;
        static readonly AsyncSerialGate ExecutionGate = new AsyncSerialGate();
        static readonly object ShutdownLock = new object();
        static int _shutdownLease;
        static System.ComponentModel.CancelEventArgs _shutdownArgs;

        /// <summary>Whether application shutdown has reserved the coordinator against new work.</summary>
        internal static bool IsShutdownRequested => ShutdownIsReserved();

        static bool ShutdownIsReserved()
        {
            lock (ShutdownLock)
            {
                ReconcileCancelledShutdown();
                return _shutdownLease != 0;
            }
        }

        static void ReconcileCancelledShutdown()
        {
            if (_shutdownLease == 0 || _shutdownArgs == null || !_shutdownArgs.Cancel) return;
            _shutdownLease = 0;
            _shutdownArgs = null;
        }

        /// <summary>
        /// Reserves shutdown only when no synchronization or host action is active. A later close
        /// cancellation must call <see cref="CancelShutdown"/> to release the reservation.
        /// </summary>
        internal static bool TryBeginShutdown(System.ComponentModel.CancelEventArgs args)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            lock (ShutdownLock)
            {
                ReconcileCancelledShutdown();
                if (ExecutionGate.IsBusy) return false;
                // A previous subscriber can cancel after ours returns. Keep its event args so a
                // later gate entry or close attempt can release that stale reservation.
                _shutdownLease = 1;
                _shutdownArgs = args;
                return true;
            }
        }

        internal static void CancelShutdown()
        {
            lock (ShutdownLock)
            {
                _shutdownLease = 0;
                _shutdownArgs = null;
            }
        }

        static Task<T> RunGatedAsync<T>(Func<Task<T>> operation) => ExecutionGate.RunAsync(async () =>
        {
            if (ShutdownIsReserved())
                throw new InvalidOperationException("Rhino.Inside is closing; new synchronization and host actions are unavailable.");
            return await operation().ConfigureAwait(false);
        });

        /// <summary>
        /// Whether design attributes may be edited in Rhino and pushed back.
        /// </summary>
        /// <remarks>
        /// Off by default, which keeps every field ArcGIS-owned and read-only in Rhino: safest when
        /// the GIS is the record of truth, and it avoids acting on Rhino user text that is easy to
        /// change by accident. Turned on, design fields become Shared -- either side may edit, and a
        /// both-sides change is raised as a conflict rather than silently overwritten -- which is
        /// what allows geometry authored in Rhino to carry its attributes out to the shapefile.
        /// Identity and geometry-derived fields stay locked either way.
        /// </remarks>
        internal static bool AttributesEditableInRhino { get; set; }

        /// <summary>
        /// Whether a pull, preview or apply is in progress. A pull or apply changes Rhino object by
        /// object; anything that re-reads the document on each change should wait for the end.
        /// </summary>
        internal static bool IsBusy => ExecutionGate.IsBusy;

        /// <summary>Runs a host-level action on the UI thread while holding the shared sync gate.</summary>
        internal static Task<T> RunHostActionAsync<T>(Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Initialize();
            return RunGatedAsync(() => Task.Run(() => OnUi(action)));
        }

        /// <summary>
        /// What the running pull, preview or apply is doing, for a progress line. Raised off the UI
        /// thread, at most every quarter second for counts; phase changes always get through.
        /// </summary>
        internal static event Action<string> ProgressChanged;

        /// <summary>A heads-up for the whole run (a large layer and roughly how long); null clears it.</summary>
        internal static event Action<string> NoticeChanged;

        static string _lastPhase;
        static DateTime _lastProgress;
        static readonly object ProgressLock = new object();

        // Reports can come from several pull-preparation workers at once.
        static void ReportProgress(string message)
        {
            lock (ProgressLock) ReportProgressLocked(message);
        }

        static void ReportProgressLocked(string message)
        {
            // Counted messages ("... 4,000 / 100,000...") share a phase; only those are throttled.
            int cut = message.IndexOfAny("0123456789".ToCharArray());
            string phase = cut > 0 ? message.Substring(0, cut) : message;
            var now = DateTime.UtcNow;
            bool counted = message.Contains(" / ");
            if (counted && phase == _lastPhase && (now - _lastProgress).TotalMilliseconds < 250 && !IsFinalCount(message))
                return;
            _lastPhase = phase;
            _lastProgress = now;
            ProgressChanged?.Invoke(message);
        }

        /// <summary>"... 100,000 / 100,000..." -- the last count of a phase always gets through.</summary>
        static bool IsFinalCount(string message)
        {
            int slash = message.IndexOf(" / ", StringComparison.Ordinal);
            if (slash < 0) return false;
            int start = message.LastIndexOf(' ', slash - 1) + 1;
            string done = message.Substring(start, slash - start);
            string rest = message.Substring(slash + 3);
            int end = 0;
            while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == ',')) end++;
            return done == rest.Substring(0, end);
        }

        static void BeginRun() { _lastPhase = null; NoticeChanged?.Invoke(null); }

        static void ReportNotice(string notice) => NoticeChanged?.Invoke(notice);

        internal const string GeodesyThreadsEnvironmentVariable = "RHINOINSIDE_ARCGIS_GEODESY_THREADS";

        /// <summary>
        /// Threads a pull maps features into model space on (see PullPreparation). Half the cores,
        /// at most 8, so Pro stays responsive; RHINOINSIDE_ARCGIS_GEODESY_THREADS overrides it, and 1
        /// runs it serially.
        /// </summary>
        internal static int GeodesyThreads
        {
            get
            {
                if (int.TryParse(Environment.GetEnvironmentVariable(GeodesyThreadsEnvironmentVariable), out int configured) &&
                    configured > 0)
                    return configured;
                return Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));
            }
        }

        /// <summary>
        /// Captures the UI thread the Rhino half must run on. Called once, from the UI thread.
        /// </summary>
        internal static void Initialize()
        {
            _uiDispatcher = _uiDispatcher ?? System.Windows.Application.Current?.Dispatcher
                            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        }

        /// <summary>
        /// Computes the plan and reports it without writing anything -- the diff view.
        /// </summary>
        internal static Task<SyncReport> PreviewAsync(string arcgisLayer, string rhinoLayer,
                                                      SyncDirectionMode direction = SyncDirectionMode.TwoWay,
                                                      string profileJson = null,
                                                      string expectedArcGisSource = null) =>
            RunAsync(arcgisLayer, rhinoLayer, new SyncOptions { Apply = false, Direction = direction }, profileJson,
                expectedArcGisSource);

        /// <summary>
        /// Applies the plan. Conflicts left as <see cref="ConflictResolution.Manual"/> are reported
        /// and held rather than overwritten.
        /// </summary>
        internal static Task<SyncReport> ApplyAsync(string arcgisLayer, string rhinoLayer, ConflictResolution conflicts,
                                                    SyncDirectionMode direction = SyncDirectionMode.TwoWay,
                                                    string profileJson = null,
                                                    string expectedArcGisSource = null) =>
            RunAsync(arcgisLayer, rhinoLayer,
                new SyncOptions { Apply = true, Conflicts = conflicts, Direction = direction }, profileJson,
                expectedArcGisSource);

        /// <summary>Freshly previews, presents that report for explicit local review, then applies while holding the run gate.</summary>
        internal static Task<SyncReport> ReviewAndApplyAsync(string arcgisLayer, string rhinoLayer,
            ConflictResolution conflicts, SyncDirectionMode direction, string profileJson,
            string expectedArcGisSource, Func<SyncReport, bool> review)
        {
            if (review == null) throw new ArgumentNullException(nameof(review));
            Initialize();
            return RunGatedAsync(() => Task.Run(() =>
            {
                var rhino = string.IsNullOrWhiteSpace(rhinoLayer) ? arcgisLayer : rhinoLayer;
                var beforePreview = CaptureReviewStamp(arcgisLayer, rhino, expectedArcGisSource);
                var preview = RunCore(arcgisLayer, rhino,
                    new SyncOptions { Apply = false, Direction = direction }, profileJson, expectedArcGisSource);
                var reviewedState = CaptureReviewStamp(arcgisLayer, rhino, expectedArcGisSource);
                if (!string.Equals(beforePreview, reviewedState, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Rhino or ArcGIS inputs changed while the review preview was being computed. Nothing was applied; preview again.");
                if (!OnUi(() => review(preview)))
                    throw new InvalidOperationException("Local review denied or cancelled; nothing was applied.");

                var approvedState = CaptureReviewStamp(arcgisLayer, rhino, expectedArcGisSource);
                if (!string.Equals(reviewedState, approvedState, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Rhino or ArcGIS inputs changed while the review dialog was open. Nothing was applied; preview again.");

                // No cross-host transaction lock exists: an external ArcGIS/Rhino edit can still
                // land after this final read and before Apply begins. The adapter rechecks the
                // pinned layer URI/source immediately before writes, but cannot lock external edits.
                return RunCore(arcgisLayer, rhino,
                    new SyncOptions { Apply = true, Conflicts = conflicts, Direction = direction }, profileJson,
                    expectedArcGisSource);
            }));
        }

        /// <summary>Loads the active layer schema and reconciles its saved per-link profile.</summary>
        internal static Task<ProfileDraft> GetProfileDraftAsync(string arcgisLayer, string rhinoLayer,
                                                                 string profileJson,
                                                                 string expectedArcGisSource = null)
        {
            Initialize();
            return RunGatedAsync(() => Task.Run(() =>
                GetProfileDraft(arcgisLayer, rhinoLayer, profileJson, expectedArcGisSource)));
        }

        /// <summary>
        /// Infers and creates a new ArcGIS feature class for an existing Rhino layer. Feature
        /// creation is a separate step so the pane can establish the persisted link before its
        /// first ordinary <see cref="SyncService"/> apply writes objects and identities.
        /// </summary>
        internal static Task<NewLayerCreation> CreateLayerFromRhinoAsync(string rhinoLayer, string requestedName)
        {
            Initialize();
            return RunGatedAsync(() => Task.Run(() => CreateLayerFromRhino(rhinoLayer, requestedName)));
        }

        /// <summary>Read-only schema inference for the pane's before-creation summary.</summary>
        internal static NewLayerPlan PlanLayerFromRhino(string rhinoLayer, string requestedName)
        {
            Initialize();
            return PlanLayerFromRhinoCore(rhinoLayer, requestedName);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static NewLayerCreation CreateLayerFromRhino(string rhinoLayer, string requestedName)
        {
            EnsureEarthAnchor();
            var plan = PlanLayerFromConvertedRhinoCore(rhinoLayer, requestedName);
            if (plan.Target == RhinoArcGIS.Core.Geometry.GeometryTarget.Unsupported || plan.MatchingCount == 0)
                throw new InvalidOperationException(
                    $"Rhino layer '{rhinoLayer}' has no point, curve, planar polygon, mesh, or Brep geometry to publish.");

            var geodatabase = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath;
            if (string.IsNullOrWhiteSpace(geodatabase))
                throw new InvalidOperationException("The ArcGIS project has no default geodatabase.");

            // ArcGIS refuses schema changes while edits are pending -- and the previous layer
            // created here leaves its initial push pending -- so a second "create" in a row failed
            // with "cannot be invoked ... when editing is in progress". Save first, with consent.
            var project = ArcGIS.Desktop.Core.Project.Current;
            if (project != null && project.HasEdits)
            {
                var consent = RhinoHost.UnsavedWork != RhinoHost.UnsavedWorkPolicy.Prompt ||
                    OnUi(() => ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                        "ArcGIS has unsaved edits. Creating a feature class changes the geodatabase schema, " +
                        "which ArcGIS only allows once edits are saved.\n\nSave all edits now and continue?",
                        "Rhino.Inside", System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes);
                if (!consent)
                    throw new InvalidOperationException("Creating the layer was cancelled: save or discard ArcGIS edits first.");
                if (!project.SaveEditsAsync().GetAwaiter().GetResult())
                    throw new InvalidOperationException("ArcGIS could not save its pending edits, so the new layer was not created.");
            }

            var arcgis = new RhinoArcGIS.ArcGIS.ArcGISAdapter();
            var layerName = arcgis.CreateFeatureClass(geodatabase, plan.Name, plan.Target, plan.Fields);
            return new NewLayerCreation(layerName, geodatabase, plan);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static NewLayerPlan PlanLayerFromRhinoCore(string rhinoLayer, string requestedName)
        {
            if (!RhinoHost.IsStarted)
                throw new InvalidOperationException("Start Rhino before creating an ArcGIS layer.");
            if (string.IsNullOrWhiteSpace(rhinoLayer))
                throw new ArgumentException("Choose a Rhino layer first.", nameof(rhinoLayer));

            // This preview runs during ordinary pane refreshes. Capture only cheap descriptors and
            // user text; the create command re-plans from actual converted payloads before writing.
            var objects = OnUi(() => RhinoHost.GetLayerPlanSnapshots(rhinoLayer));
            return NewLayerInference.Plan(objects,
                string.IsNullOrWhiteSpace(requestedName) ? rhinoLayer : requestedName);
        }

        static NewLayerPlan PlanLayerFromConvertedRhinoCore(string rhinoLayer, string requestedName)
        {
            if (!RhinoHost.IsStarted)
                throw new InvalidOperationException("Start Rhino before creating an ArcGIS layer.");
            if (string.IsNullOrWhiteSpace(rhinoLayer))
                throw new ArgumentException("Choose a Rhino layer first.", nameof(rhinoLayer));

            var rhino = new UiThreadRhinoAdapter(new RhinoArcGIS.Rhino.RhinoAdapter(), _uiDispatcher);
            var objects = rhino.ReadObjects(rhinoLayer);
            return NewLayerInference.Plan(objects,
                string.IsNullOrWhiteSpace(requestedName) ? rhinoLayer : requestedName);
        }

        /// <summary>
        /// Runs off the UI thread on purpose; see <see cref="UiThreadRhinoAdapter"/> for why doing
        /// it on the UI thread deadlocks against the ArcGIS main CIM thread.
        /// </summary>
        static Task<SyncReport> RunAsync(string arcgisLayer, string rhinoLayer, SyncOptions options,
                                         string profileJson, string expectedArcGisSource)
        {
            Initialize();
            return RunGatedAsync(() => Task.Run(() =>
                Run(arcgisLayer, rhinoLayer, options, profileJson, expectedArcGisSource)));
        }

        static SyncReport Run(string arcgisLayer, string rhinoLayer, SyncOptions options, string profileJson,
                              string expectedArcGisSource)
        {
            if (!RhinoHost.IsStarted)
                throw new InvalidOperationException("Start Rhino before running a sync.");

            if (string.IsNullOrWhiteSpace(arcgisLayer))
                throw new ArgumentException("Choose an ArcGIS layer first.", nameof(arcgisLayer));

            // Default the Rhino layer to the ArcGIS layer's name, which is what a pull creates.
            var rhino = string.IsNullOrWhiteSpace(rhinoLayer) ? arcgisLayer : rhinoLayer;

            return RunCore(arcgisLayer, rhino, options, profileJson, expectedArcGisSource);
        }

        /// <summary>
        /// Makes sure the Rhino document is georeferenced before any transform is built.
        /// </summary>
        /// <remarks>
        /// GeoReferenceFactory only uses the earth anchor when the document actually has one; with
        /// no anchor it silently falls back to an identity transform, and map coordinates land in
        /// model space unchanged -- state plane feet puts this data about 1.9 million units from the
        /// origin. Neither pull nor sync sets an anchor of its own, so one is established here, at
        /// the centre of the current map view, the first time it is needed.
        /// </remarks>
        static void EnsureEarthAnchor()
        {
            OnUi(() => { EnsureGeoreferenceMode(); return true; });
            var anchor = OnUi(() => RhinoHost.GetEarthAnchor());
            if (anchor != null && anchor.IsSet) return;

            var centre = GisUtil.GetMapCentreAsync().GetAwaiter().GetResult();
            if (centre == null) return;

            OnUi(() => { RhinoHost.SetEarthAnchor(centre.Latitude, centre.Longitude, 0.0, 0.0); return true; });
        }

        /// <summary>Validates existing georeference state without writing document metadata.</summary>
        static void RequireInitializedEarthAnchor()
        {
            var key = RhinoArcGIS.Core.Spatial.GeoReferenceFactory.ModeKey;
            var mode = OnUi(() => RhinoHost.GetDocumentStrings(key));
            var anchor = OnUi(() => RhinoHost.GetEarthAnchor());
            if (mode == null || !mode.TryGetValue(key, out var storedMode) || string.IsNullOrWhiteSpace(storedMode) ||
                anchor == null || !anchor.IsSet)
                throw new InvalidOperationException(
                    "Preview is read-only and needs an initialized Rhino georeference. Use Earth anchor → Set from map centre, then preview again.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CaptureReviewStamp(string arcgisLayer, string rhinoLayer, string expectedArcGisSource)
        {
            if (!RhinoHost.IsStarted)
                throw new InvalidOperationException("Start Rhino before reviewing a synchronization.");

            var key = RhinoArcGIS.Core.Spatial.GeoReferenceFactory.ModeKey;
            var rhino = new UiThreadRhinoAdapter(new RhinoArcGIS.Rhino.RhinoAdapter(), _uiDispatcher);
            var contextBefore = ReadReviewRhinoContext(rhino, key, rhinoLayer);
            var rhinoObjects = rhino.ReadObjects(rhinoLayer);

            var arcgis = new RhinoArcGIS.ArcGIS.ArcGISAdapter
            { CrsLayer = arcgisLayer, ExpectedSource = expectedArcGisSource };
            var schema = arcgis.GetSchema(arcgisLayer);
            if (schema == null)
                throw new InvalidOperationException($"ArcGIS layer '{arcgisLayer}' is not available from the saved data source.");
            var features = arcgis.ReadFeatures(arcgisLayer);

            var contextAfter = ReadReviewRhinoContext(rhino, key, rhinoLayer);
            var contextStampBefore = SyncReviewStamp.Compute(contextBefore.DocumentSerial, contextBefore.Mode,
                contextBefore.Anchor, contextBefore.Ledger, null, null, null);
            var contextStampAfter = SyncReviewStamp.Compute(contextAfter.DocumentSerial, contextAfter.Mode,
                contextAfter.Anchor, contextAfter.Ledger, null, null, null);
            if (!string.Equals(contextStampBefore, contextStampAfter, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The active Rhino document or georeference changed while the review inputs were being read. Nothing was applied; retry the review.");

            return SyncReviewStamp.Compute(contextBefore.DocumentSerial, contextBefore.Mode,
                contextBefore.Anchor, contextBefore.Ledger, schema, rhinoObjects, features);
        }

        static ReviewRhinoContext ReadReviewRhinoContext(UiThreadRhinoAdapter rhino, string modeKey,
                                                          string rhinoLayer)
        {
            return OnUi(() =>
            {
                var stored = RhinoHost.GetDocumentStrings(modeKey);
                string mode = null;
                if (stored != null) stored.TryGetValue(modeKey, out mode);
                return new ReviewRhinoContext
                {
                    DocumentSerial = RhinoHost.GetActiveDocumentSerial(),
                    Mode = mode,
                    Anchor = rhino.GetEarthAnchor(),
                    Ledger = ((IDocumentStringStore)rhino).GetDocumentString(SyncLedger.DocumentKey(rhinoLayer))
                };
            });
        }

        sealed class ReviewRhinoContext
        {
            internal uint DocumentSerial { get; set; }
            internal string Mode { get; set; }
            internal EarthAnchor Anchor { get; set; }
            internal string Ledger { get; set; }
        }

        /// <summary>
        /// Records, once per document, which georeference it syncs with. A document with nothing
        /// synced yet gets the exact local frame; one that already holds synced objects keeps the
        /// planar frame its baselines were computed with, since switching would read every object
        /// as moved. See <see cref="RhinoArcGIS.Core.Spatial.GeoReferenceFactory.ModeKey"/>.
        /// </summary>
        internal static string EnsureGeoreferenceMode()
        {
            var key = RhinoArcGIS.Core.Spatial.GeoReferenceFactory.ModeKey;
            var stored = RhinoHost.GetDocumentStrings(key);
            if (stored.TryGetValue(key, out var mode) && !string.IsNullOrEmpty(mode)) return mode;
            mode = RhinoHost.GetTrackedLayers().Count == 0
                ? RhinoArcGIS.Core.Spatial.GeoReferenceFactory.LocalFrameMode
                : RhinoArcGIS.Core.Spatial.GeoReferenceFactory.PlanarMode;
            RhinoHost.SetDocumentStrings(new Dictionary<string, string> { { key, mode } });
            return mode;
        }

        static T OnUi<T>(Func<T> func)
        {
            Initialize();
            return _uiDispatcher == null || _uiDispatcher.CheckAccess() ? func() : _uiDispatcher.Invoke(func);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static SyncReport RunCore(string arcgisLayer, string rhinoLayer, SyncOptions options,
                                  string profileJson, string expectedArcGisSource)
        {
            if (options.Apply) EnsureEarthAnchor();
            else RequireInitializedEarthAnchor();

            var arcgis = new RhinoArcGIS.ArcGIS.ArcGISAdapter
            { CrsLayer = arcgisLayer, ExpectedSource = expectedArcGisSource };
            var rhino = new UiThreadRhinoAdapter(new RhinoArcGIS.Rhino.RhinoAdapter(), _uiDispatcher);

            var schema = arcgis.GetSchema(arcgisLayer);
            if (schema == null)
                throw new InvalidOperationException($"ArcGIS layer '{arcgisLayer}' is not available in the active map or scene.");

            var profile = BuildProfile(schema, rhinoLayer, rhino.GetUnits(), profileJson);

            BeginRun();
            var report = new SyncService(arcgis, rhino)
            {
                Parallelism = GeodesyThreads, Progress = ReportProgress, Notice = ReportNotice
            }.Sync(profile, profile.Layers[0], options);
            if (arcgis.LastFrame?.DatumUnresolved == true ||
                arcgis.LastGeodeticMap?.DatumTransformation?.StartsWith("none available", StringComparison.Ordinal) == true)
                report.Add(Guid.Empty, arcgisLayer, RhinoArcGIS.Core.Reporting.SyncOutcome.Warning,
                    $"'{arcgisLayer}' is on a different datum from WGS84 and ArcGIS has no transformation for it here; " +
                    "positions are off by the datum shift (often a metre or more).");

            if (options.Apply) OnUi(() => { RhinoHost.RedrawViews(); return true; });
            return report;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ProfileDraft GetProfileDraft(string arcgisLayer, string rhinoLayer, string profileJson,
                                             string expectedArcGisSource)
        {
            if (!RhinoHost.IsStarted)
                throw new InvalidOperationException("Start Rhino before editing a synchronization profile.");
            if (string.IsNullOrWhiteSpace(arcgisLayer))
                throw new ArgumentException("Choose a complete layer pair first.", nameof(arcgisLayer));

            var effectiveRhinoLayer = string.IsNullOrWhiteSpace(rhinoLayer) ? arcgisLayer : rhinoLayer;
            var arcgis = new RhinoArcGIS.ArcGIS.ArcGISAdapter
            { CrsLayer = arcgisLayer, ExpectedSource = expectedArcGisSource };
            var rhino = new UiThreadRhinoAdapter(new RhinoArcGIS.Rhino.RhinoAdapter(), _uiDispatcher);
            var schema = arcgis.GetSchema(arcgisLayer);
            if (schema == null)
                throw new InvalidOperationException($"ArcGIS layer '{arcgisLayer}' is not available in the active map or scene.");

            return new ProfileDraft(schema,
                BuildProfile(schema, effectiveRhinoLayer, rhino.GetUnits(), profileJson));
        }

        /// <summary>
        /// For the test bridge: the objects whose geometry hash disagrees between the two sides,
        /// with both geometries laid side by side in GIS coordinates, so a false "modified" can be
        /// traced to the vertices that differ rather than guessed at.
        /// </summary>
        internal static Task<object> GeometryDiffAsync(string arcgisLayer, string rhinoLayer, int take)
        {
            Initialize();
            var rhino = string.IsNullOrWhiteSpace(rhinoLayer) ? arcgisLayer : rhinoLayer;
            return RunGatedAsync(() => Task.Run(() => GeometryDiffCore(arcgisLayer, rhino, take)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static object GeometryDiffCore(string arcgisLayer, string rhinoLayer, int take)
        {
            EnsureEarthAnchor();

            var arcgis = new RhinoArcGIS.ArcGIS.ArcGISAdapter { CrsLayer = arcgisLayer };
            var rhino = new UiThreadRhinoAdapter(new RhinoArcGIS.Rhino.RhinoAdapter(), _uiDispatcher);
            var schema = arcgis.GetSchema(arcgisLayer);
            if (schema == null)
                throw new InvalidOperationException($"ArcGIS layer '{arcgisLayer}' is not available in the active map or scene.");
            var profile = BuildProfile(schema, rhinoLayer, rhino.GetUnits(), null);
            var map = RhinoArcGIS.Core.Spatial.GeoReferenceFactory.Create(rhino, arcgis, profile);

            var byOid = new Dictionary<long, RhinoArcGIS.Core.Adapters.FeatureRecord>();
            foreach (var f in arcgis.ReadFeatures(arcgisLayer))
                if (f.Identity?.ArcGisObjectId != null) byOid[f.Identity.ArcGisObjectId.Value] = f;

            var diffs = new List<object>();
            var checkedCount = 0;
            foreach (var snap in rhino.ReadObjects(rhinoLayer))
            {
                if (!snap.UserStrings.TryGetValue(RhinoArcGIS.Core.Identity.GisKeys.ArcGisObjectId, out var oidText) ||
                    !long.TryParse(oidText, out var oid) || !byOid.TryGetValue(oid, out var feature)) continue;

                checkedCount++;
                var rhinoGis = RhinoArcGIS.Core.Spatial.NeutralGeometryTransform.RhinoToGis(snap.Geometry, map);
                var rhinoHash = RhinoArcGIS.Core.Change.Hashing.HashGeometry(rhinoGis);
                var arcHash = RhinoArcGIS.Core.Change.Hashing.HashGeometry(feature.Geometry);
                snap.UserStrings.TryGetValue(RhinoArcGIS.Core.Identity.GisKeys.GeometryHash, out var baseline);
                if (rhinoHash == arcHash && rhinoHash == baseline) continue;

                if (diffs.Count < take)
                    diffs.Add(new
                    {
                        oid,
                        rhinoObject = snap.RhinoGuid,
                        rhinoMatchesBaseline = rhinoHash == baseline,
                        arcgisMatchesBaseline = arcHash == baseline,
                        rhino = Describe(rhinoGis),
                        arcgis = Describe(feature.Geometry)
                    });
            }

            return new { checkedCount, differing = diffs.Count, diffs };
        }

        static object Describe(RhinoArcGIS.Core.Geometry.NeutralGeometry g)
        {
            if (g == null) return null;
            System.Func<IEnumerable<RhinoArcGIS.Core.Spatial.Xyz>, object> pts = run =>
            {
                var list = new List<string>();
                foreach (var p in run) list.Add($"{p.X:F4},{p.Y:F4},{p.Z:F4}");
                return list;
            };
            var runs = new List<object>();
            if (g.Rings != null) foreach (var r in g.Rings) runs.Add(pts(r));
            if (g.Parts != null && g.Parts.Count > 0) foreach (var r in g.Parts) runs.Add(pts(r));
            else if (g.Points != null && g.Points.Count > 0 && (g.Rings == null || g.Rings.Count == 0)) runs.Add(pts(g.Points));
            return new { kind = g.Kind.ToString(), runs };
        }

        /// <summary>
        /// Pulls a layer's features into Rhino through <see cref="PullService"/>.
        /// </summary>
        /// <remarks>
        /// This goes through the interop stack rather than a direct conversion so that every created
        /// object carries its sync identity: the gis.* user strings holding the sync guid, the
        /// originating ArcGIS ObjectID and layer, and baseline geometry/attribute hashes. Without
        /// them a later sync cannot tell a pulled object from something newly drawn in Rhino, and
        /// treats the whole pull as new work to push straight back to ArcGIS.
        /// </remarks>
        internal static Task<SyncReport> PullAsync(string arcgisLayer, string rhinoLayer, bool selectedOnly,
                                                   string profileJson = null,
                                                   string expectedArcGisSource = null)
        {
            Initialize();

            if (!RhinoHost.IsStarted)
                throw new InvalidOperationException("Start Rhino before pulling.");

            if (string.IsNullOrWhiteSpace(arcgisLayer))
                throw new ArgumentException("Choose an ArcGIS layer first.", nameof(arcgisLayer));

            var rhinoLayerName = string.IsNullOrWhiteSpace(rhinoLayer) ? arcgisLayer : rhinoLayer;
            return RunGatedAsync(() => Task.Run(() =>
                PullCore(arcgisLayer, rhinoLayerName, selectedOnly, profileJson, expectedArcGisSource)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static SyncReport PullCore(string arcgisLayer, string rhinoLayer, bool selectedOnly,
                                   string profileJson, string expectedArcGisSource)
        {
            EnsureEarthAnchor();

            var arcgis = new RhinoArcGIS.ArcGIS.ArcGISAdapter
            { CrsLayer = arcgisLayer, ExpectedSource = expectedArcGisSource };
            var rhino = new UiThreadRhinoAdapter(new RhinoArcGIS.Rhino.RhinoAdapter(), _uiDispatcher);

            var schema = arcgis.GetSchema(arcgisLayer);
            if (schema == null)
                throw new InvalidOperationException($"ArcGIS layer '{arcgisLayer}' is not available in the active map or scene.");
            var profile = BuildProfile(schema, rhinoLayer, rhino.GetUnits(), profileJson);

            var options = selectedOnly
                ? new PullOptions { Mode = PullMode.Context, SelectedOnly = true }
                : PullOptions.Context;

            BeginRun();
            var report = new PullService(arcgis, rhino)
            {
                Parallelism = GeodesyThreads, Progress = ReportProgress, Notice = ReportNotice
            }.Pull(profile, profile.Layers[0], options);

            OnUi(() => { RhinoHost.RedrawViews(); return true; });
            return report;
        }

        /// <summary>
        /// Builds the mapping profile used by this run, reconciling a saved per-link profile with
        /// the current schema or producing the safe default when the link has no custom profile.
        /// </summary>
        /// <remarks>
        /// DefaultProfileFactory marks every field ArcGIS-owned and read-only in Rhino, which is a
        /// safe default for a context pull but means an attribute edited in Rhino is silently
        /// ignored by a sync -- nothing to push, so nothing happens. For authoring in both
        /// directions the design fields need to be Shared, where either side may edit and a
        /// both-sides change is raised as a conflict rather than being overwritten.
        ///
        /// Identity and geometry-derived fields stay locked: they belong to ArcGIS, and writing
        /// them back would either be rejected or would overwrite values ArcGIS maintains itself.
        /// </remarks>
        static LayerMappingProfile BuildProfile(RhinoArcGIS.Core.Adapters.LayerSchema schema, string rhinoLayer,
                                                RhinoArcGIS.Core.Spatial.UnitSystem units, string profileJson)
        {
            LayerMappingProfile saved = null;
            if (!string.IsNullOrWhiteSpace(profileJson))
            {
                try
                {
                    saved = ProfileJson.Deserialize(profileJson);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("The saved synchronization profile is not valid JSON.", ex);
                }
            }

            var profile = ProfileAuthoring.Reconcile(
                schema, rhinoLayer, units, saved, AttributesEditableInRhino);
            var validation = ProfileValidation.Validate(profile);
            if (validation.IsBlocking)
                throw new InvalidOperationException("The saved synchronization profile is invalid: " +
                    string.Join("; ", System.Linq.Enumerable.Select(validation.Issues, issue => issue.Message)));
            return profile;
        }

        /// <summary>Feature layer names in the active map, for the layer picker.</summary>
        internal static IReadOnlyList<string> GetArcGisLayerNames() =>
            new RhinoArcGIS.ArcGIS.ArcGISAdapter().GetLayerNames();

        /// <summary>Feature layers in the active map with their data sources, for re-binding links.</summary>
        internal static IReadOnlyList<KeyValuePair<string, string>> GetArcGisLayerSources() =>
            new RhinoArcGIS.ArcGIS.ArcGISAdapter().GetLayerSources();
    }

    internal sealed class NewLayerCreation
    {
        internal NewLayerCreation(string layerName, string geodatabasePath, NewLayerPlan plan)
        {
            LayerName = layerName;
            GeodatabasePath = geodatabasePath;
            Plan = plan;
        }

        internal string LayerName { get; }
        internal string GeodatabasePath { get; }
        internal NewLayerPlan Plan { get; }
    }

    /// <summary>Profile plus live schema used to populate the dockpane editor.</summary>
    internal sealed class ProfileDraft
    {
        internal ProfileDraft(RhinoArcGIS.Core.Adapters.LayerSchema schema, LayerMappingProfile profile)
        {
            Schema = schema;
            Profile = profile;
        }

        internal RhinoArcGIS.Core.Adapters.LayerSchema Schema { get; }
        internal LayerMappingProfile Profile { get; }
    }
}
