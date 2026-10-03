using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Newtonsoft.Json.Linq;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Test-bridge commands for the scale benchmark (tools/benchmark.ps1): bulk edits on either
    /// side in one call, so a benchmark can dirty 1% of a 100k layer without 1,000 bridge round
    /// trips, and a memory probe. Like the rest of the bridge, inert unless RHINOINSIDE_TESTBRIDGE
    /// is set, and meant only for disposable test projects.
    /// </summary>
    internal static class TestBridgeBench
    {
        internal static bool TryExecute(string command, JObject request, Dispatcher ui, out object result)
        {
            switch (command)
            {
                case "benchrhinoedit": result = OnUi(ui, () => RhinoEdit(request)); return true;
                case "benchgisedit": result = GisEdit(request); return true;
                case "benchmem": result = Memory(request["collect"]?.Value<bool>() ?? false); return true;
                case "benchdiag": result = OnUi(ui, Diagnostics); return true;
                case "benchgeodesy": result = Geodesy(request, ui); return true;
                case "benchverify": result = Verify(request, ui); return true;
                case "benchpanerun": result = OnUi(ui, () => PaneRun(request)); return true;
                case "benchpanestate": result = OnUi(ui, PaneState); return true;
                default: result = null; return false;
            }
        }

        static T OnUi<T>(Dispatcher ui, Func<T> func) =>
            ui == null || ui.CheckAccess() ? func() : ui.Invoke(func);

        /// <summary>
        /// Moves, or sets a user string on, <c>count</c> tracked objects spread evenly across the
        /// Rhino layer (every k-th one), in a single undo record.
        /// </summary>
        static object RhinoEdit(JObject request)
        {
            string layer = (string)request["layer"] ?? throw new ArgumentException("layer is required.");
            int count = request["count"]?.Value<int>() ?? 1;
            string mode = (string)request["mode"] ?? "move";
            var sw = Stopwatch.StartNew();
            int edited = RhinoEditCore(layer, count, mode, (string)request["key"] ?? "note",
                (string)request["value"] ?? "bench edit", request["dx"]?.Value<double>() ?? 0.5,
                request["undo"]?.Value<bool>() ?? true, request["ignoreModes"]?.Value<bool>() ?? true,
                request["quiet"]?.Value<bool>() ?? false);
            return new { edited, ms = sw.ElapsedMilliseconds };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RhinoEditCore(string layerName, int count, string mode, string key, string value, double dx,
            bool undo, bool ignoreModes, bool quiet)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Rhino has no active document.");
            int idx = doc.Layers.FindByFullPath(layerName, -1);
            if (idx < 0) throw new ArgumentException($"No Rhino layer '{layerName}'.");

            var settings = new Rhino.DocObjects.ObjectEnumeratorSettings
            {
                NormalObjects = true, LockedObjects = true, HiddenObjects = true,
                IncludeLights = false, IncludeGrips = false, LayerIndexFilter = idx
            };
            var tracked = doc.Objects.GetObjectList(settings)
                .Where(o => !string.IsNullOrEmpty(o.Attributes.GetUserString(RhinoArcGIS.Core.Identity.GisKeys.SyncGuid)))
                .ToList();
            if (tracked.Count == 0 || count <= 0) return 0;
            int stride = Math.Max(1, tracked.Count / count);

            uint record = undo ? doc.BeginUndoRecord("Rhino.Inside bench edit") : 0;
            bool redraw = doc.Views.RedrawEnabled;
            if (quiet) doc.Views.RedrawEnabled = false;
            int edited = 0;
            try
            {
                for (int i = 0; i < tracked.Count && edited < count; i += stride)
                {
                    var obj = tracked[i];
                    if (mode == "attr")
                    {
                        var attrs = obj.Attributes.Duplicate();
                        attrs.SetUserString(key, value + " " + edited);
                        doc.Objects.ModifyAttributes(obj, attrs, true);
                    }
                    else
                    {
                        var g = obj.Geometry.Duplicate();
                        g.Transform(Rhino.Geometry.Transform.Translation(dx, 0, 0));
                        doc.Objects.Replace(obj.Id, g, ignoreModes);
                    }
                    edited++;
                }
            }
            finally
            {
                if (undo) doc.EndUndoRecord(record);
                doc.Views.RedrawEnabled = redraw;
            }
            doc.Views.Redraw();
            return edited;
        }

        /// <summary>
        /// Sets a field on (or, with mode "move", shifts the geometry of) <c>count</c> features spread
        /// evenly across the layer, in one edit operation, the way a field calculation would.
        /// </summary>
        static object GisEdit(JObject request)
        {
            string layerName = (string)request["arcgisLayer"] ?? throw new ArgumentException("arcgisLayer is required.");
            int count = request["count"]?.Value<int>() ?? 1;
            string field = (string)request["field"] ?? "name";
            string value = (string)request["value"] ?? "gis bench edit";
            bool move = (string)request["mode"] == "move";
            double dy = request["dy"]?.Value<double>() ?? 0.5;
            var sw = Stopwatch.StartNew();

            int edited = QueuedTask.Run(() =>
            {
                var layer = RhinoArcGIS.ArcGIS.ActiveMap.FindLayer(layerName)
                            ?? throw new ArgumentException($"No feature layer named '{layerName}'.");
                var oids = new List<long>();
                using (var cursor = layer.Search(null))
                    while (cursor.MoveNext())
                        using (var row = cursor.Current) oids.Add(row.GetObjectID());
                if (oids.Count == 0 || count <= 0) return 0;

                int stride = Math.Max(1, oids.Count / count);
                var op = new ArcGIS.Desktop.Editing.EditOperation { Name = "Rhino.Inside bench edit" };
                int n = 0;
                for (int i = 0; i < oids.Count && n < count; i += stride, n++)
                {
                    if (move) op.Modify(layer, oids[i], ShiftedShape(layer, oids[i], dy));
                    else op.Modify(layer, oids[i], new Dictionary<string, object> { { field, value + " " + n } });
                }
                if (!op.Execute()) throw new InvalidOperationException("Bench edit failed: " + op.ErrorMessage);
                return n;
            }).Result;

            return new { edited, ms = sw.ElapsedMilliseconds };
        }

        static ArcGIS.Core.Geometry.Geometry ShiftedShape(FeatureLayer layer, long oid, double dy)
        {
            using var cursor = layer.Search(new ArcGIS.Core.Data.QueryFilter { ObjectIDs = new[] { oid } });
            if (!cursor.MoveNext()) throw new ArgumentException($"No feature {oid}.");
            using var feature = (ArcGIS.Core.Data.Feature)cursor.Current;
            return ArcGIS.Core.Geometry.GeometryEngine.Instance.Move(feature.GetShape(), 0, dy);
        }

        /// <summary>
        /// Whether the per-vertex geodetic map gives the same answers when called from many threads
        /// at once, and how much faster that is. Maps the same model points (a grid over +-span
        /// metres) one at a time and then in parallel, both directions, several rounds, comparing
        /// every coordinate bit for bit. Runs off the MCT and the UI thread, as a parallel pull would.
        /// </summary>
        static object Geodesy(JObject request, Dispatcher ui)
        {
            string layerName = (string)request["arcgisLayer"] ?? throw new ArgumentException("arcgisLayer is required.");
            int count = request["points"]?.Value<int>() ?? 20000;
            double span = request["span"]?.Value<double>() ?? 15000;
            int rounds = request["rounds"]?.Value<int>() ?? 3;
            int threads = request["threads"]?.Value<int>() ?? Environment.ProcessorCount;

            var anchor = OnUi(ui, () => ((RhinoArcGIS.Core.Adapters.IEarthAnchorSource)new RhinoArcGIS.Rhino.RhinoAdapter()).GetEarthAnchor());
            if (anchor == null || !anchor.IsValid) throw new InvalidOperationException("The Rhino document has no earth anchor.");

            return System.Threading.Tasks.Task.Run(() =>
            {
                var map = new RhinoArcGIS.ArcGIS.ArcGISAdapter { CrsLayer = layerName }.CreateGeodeticMap(anchor)
                          ?? throw new InvalidOperationException("No geodetic map for that layer.");
                var rng = new Random(5);
                var model = new RhinoArcGIS.Core.Spatial.Xyz[count];
                for (int i = 0; i < count; i++)
                    model[i] = new RhinoArcGIS.Core.Spatial.Xyz((rng.NextDouble() * 2 - 1) * span, (rng.NextDouble() * 2 - 1) * span, rng.NextDouble() * 20);

                var sw = Stopwatch.StartNew();
                var gisSeq = model.Select(map.ModelToGis).ToArray();
                long toGisSeq = sw.ElapsedMilliseconds; sw.Restart();
                var backSeq = gisSeq.Select(map.GisToModel).ToArray();
                long toModelSeq = sw.ElapsedMilliseconds;

                var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads };
                long toGisPar = 0, toModelPar = 0;
                int mismatches = 0, errors = 0;
                string firstError = null;
                for (int round = 0; round < rounds; round++)
                {
                    var gis = new RhinoArcGIS.Core.Spatial.Xyz[count];
                    var back = new RhinoArcGIS.Core.Spatial.Xyz[count];
                    sw.Restart();
                    System.Threading.Tasks.Parallel.For(0, count, options, i =>
                    {
                        try { gis[i] = map.ModelToGis(model[i]); }
                        catch (Exception ex) { System.Threading.Interlocked.Increment(ref errors); firstError = firstError ?? ex.Message; }
                    });
                    toGisPar += sw.ElapsedMilliseconds; sw.Restart();
                    System.Threading.Tasks.Parallel.For(0, count, options, i =>
                    {
                        try { back[i] = map.GisToModel(gisSeq[i]); }
                        catch (Exception ex) { System.Threading.Interlocked.Increment(ref errors); firstError = firstError ?? ex.Message; }
                    });
                    toModelPar += sw.ElapsedMilliseconds;
                    for (int i = 0; i < count; i++)
                    {
                        if (!Same(gis[i], gisSeq[i])) mismatches++;
                        if (!Same(back[i], backSeq[i])) mismatches++;
                    }
                }

                return (object)new
                {
                    points = count, rounds, threads,
                    toGisSeqMs = toGisSeq, toModelSeqMs = toModelSeq,
                    toGisParMs = toGisPar / rounds, toModelParMs = toModelPar / rounds,
                    usPerVertexSeq = (toGisSeq + toModelSeq) * 1000.0 / (2.0 * count),
                    mismatches, errors, firstError
                };
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Re-prepares a spread sample of a layer's features for pulling, once serially and once on
        /// the threads a pull uses, and counts any feature whose model stamp or hashes differ.
        /// Anything but zero means the parallel pull cannot be trusted on this machine.
        /// </summary>
        static object Verify(JObject request, Dispatcher ui)
        {
            string layerName = (string)request["arcgisLayer"] ?? throw new ArgumentException("arcgisLayer is required.");
            int sample = request["sample"]?.Value<int>() ?? 2000;
            int threads = SyncCoordinator.GeodesyThreads;

            var anchor = OnUi(ui, () => ((RhinoArcGIS.Core.Adapters.IEarthAnchorSource)new RhinoArcGIS.Rhino.RhinoAdapter()).GetEarthAnchor());
            if (anchor == null || !anchor.IsValid) throw new InvalidOperationException("The Rhino document has no earth anchor.");

            return System.Threading.Tasks.Task.Run(() =>
            {
                var arcgis = new RhinoArcGIS.ArcGIS.ArcGISAdapter { CrsLayer = layerName };
                var map = arcgis.CreateGeodeticMap(anchor) ?? throw new InvalidOperationException("No geodetic map for that layer.");
                var features = arcgis.ReadFeatures(layerName);
                int stride = Math.Max(1, features.Count / Math.Max(1, sample));
                var items = new List<(RhinoArcGIS.Core.Adapters.FeatureRecord, Guid)>();
                for (int i = 0; i < features.Count && items.Count < sample; i += stride)
                    if (features[i].Geometry != null && !features[i].Geometry.IsEmpty()) items.Add((features[i], Guid.Empty));

                var layer = new RhinoArcGIS.Core.Profiles.LayerMapping { Name = layerName, RhinoLayer = layerName, ArcGisLayer = layerName };
                var profile = new RhinoArcGIS.Core.Profiles.LayerMappingProfile();
                string fingerprint = RhinoArcGIS.Core.Change.ModelStamp.Fingerprint(map);
                var mode = RhinoArcGIS.Core.Profiles.PullMode.EditableLinked;

                var sw = Stopwatch.StartNew();
                var serial = RhinoArcGIS.Core.Sync.PullPreparation.Prepare(items, layer, profile, map, mode, fingerprint, 1);
                long serialMs = sw.ElapsedMilliseconds; sw.Restart();
                var parallel = RhinoArcGIS.Core.Sync.PullPreparation.Prepare(items, layer, profile, map, mode, fingerprint, threads);
                long parallelMs = sw.ElapsedMilliseconds;

                int mismatches = 0, errors = 0;
                const string stampKey = RhinoArcGIS.Core.Identity.GisKeys.RhinoModelStamp;
                for (int i = 0; i < items.Count; i++)
                {
                    if (serial[i].Error != null || parallel[i].Error != null) { errors++; continue; }
                    if (serial[i].RhinoHash != parallel[i].RhinoHash || serial[i].ArcGisHash != parallel[i].ArcGisHash ||
                        serial[i].UserStrings[stampKey] != parallel[i].UserStrings[stampKey])
                        mismatches++;
                }
                return (object)new { sample = items.Count, threads, serialMs, parallelMs, mismatches, errors };
            }).GetAwaiter().GetResult();
        }

        static bool Same(RhinoArcGIS.Core.Spatial.Xyz a, RhinoArcGIS.Core.Spatial.Xyz b) =>
            a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Z.Equals(b.Z);

        /// <summary>
        /// Presses the pane's own Pull, Preview or Apply button for the active link and returns at
        /// once, so the bridge stays free to watch the pane (capturepane, benchpanestate) while the
        /// run is in progress -- what a person sees, not what a script sees.
        /// </summary>
        static object PaneRun(JObject request)
        {
            var pane = DockpaneViewModel.Instance ?? throw new InvalidOperationException("The dockpane is not loaded.");
            string action = (string)request["action"] ?? "pull";
            var command = action == "preview" ? pane.PreviewSyncCommand
                        : action == "apply" ? pane.ApplySyncCommand
                        : pane.PullCommand;
            if (!command.CanExecute(null)) throw new InvalidOperationException($"The pane cannot {action} now.");
            command.Execute(null);
            return new { started = action };
        }

        static object PaneState()
        {
            var pane = DockpaneViewModel.Instance ?? throw new InvalidOperationException("The dockpane is not loaded.");
            return new { pane.IsBusy, pane.BusyPhase, pane.BusyNotice, pane.BusyElapsed, pane.PullSummary, pane.SyncSummary };
        }

        /// <summary>The Rhino adapter's per-step bulk timings since the last call, then cleared.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static object Diagnostics()
        {
            var steps = new Dictionary<string, long>(RhinoArcGIS.Rhino.RhinoAdapter.StepMs);
            RhinoArcGIS.Rhino.RhinoAdapter.StepMs.Clear();
            return steps;
        }

        /// <summary>Pro's memory, optionally after a full collection so managed growth is visible.</summary>
        static object Memory(bool collect)
        {
            if (collect)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return new
            {
                workingSetMb = p.WorkingSet64 / (1024 * 1024),
                privateMb = p.PrivateMemorySize64 / (1024 * 1024),
                managedMb = GC.GetTotalMemory(false) / (1024 * 1024)
            };
        }
    }
}
