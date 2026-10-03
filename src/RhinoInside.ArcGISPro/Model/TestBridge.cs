using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Mapping;   // for the PaneCollection.CreateMapPaneAsync extension
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Drives the add-in from files, so it can be exercised without clicking through the UI.
    /// </summary>
    /// <remarks>
    /// Opt-in: does nothing unless the RHINOINSIDE_TESTBRIDGE environment variable names a
    /// directory. It then watches "in" for request files and writes a reply per request into "out",
    /// which is enough to script launch/pull/preview/apply and read the results back.
    ///
    /// A request is {"id":"...","command":"...", ...}; the reply is
    /// {"id":..., "ok":true/false, "result":{...}|"error":"..."}. Replies are written to a temporary
    /// name and moved into place so a reader never sees a half-written file.
    ///
    /// Rhino work is marshalled to the UI thread; ArcGIS work is left on the worker, because the
    /// ArcGIS adapter marshals itself and blocking the UI thread on it deadlocks -- the same
    /// constraint <see cref="UiThreadRhinoAdapter"/> exists for.
    /// </remarks>
    internal static class TestBridge
    {
        static FileSystemWatcher _watcher;
        static string _inbox, _outbox, _logPath;
        static Dispatcher _dispatcher;

        internal static void StartIfEnabled()
        {
            var root = Environment.GetEnvironmentVariable("RHINOINSIDE_TESTBRIDGE");
            if (string.IsNullOrWhiteSpace(root)) return;

            try
            {
                _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
                // What the pane would show during a long run, so a scripted run can check it.
                SyncCoordinator.ProgressChanged += message => Log("progress: " + message);
                SyncCoordinator.NoticeChanged += notice => { if (notice != null) Log("notice: " + notice); };

                _inbox = Path.Combine(root, "in");
                _outbox = Path.Combine(root, "out");
                _logPath = Path.Combine(root, "bridge.log");
                Directory.CreateDirectory(_inbox);
                Directory.CreateDirectory(_outbox);
                File.WriteAllText(Path.Combine(root, "process.id"),
                    System.Diagnostics.Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

                // Anything dropped before Pro finished starting still gets served.
                foreach (var existing in Directory.GetFiles(_inbox, "*.json")) Handle(existing);

                _watcher = new FileSystemWatcher(_inbox, "*.json") { EnableRaisingEvents = true };
                _watcher.Created += (s, e) => Handle(e.FullPath);

                Log($"bridge listening on {root}");
            }
            catch (Exception ex)
            {
                Log("bridge failed to start: " + ex);
            }
        }

        static void Handle(string path)
        {
            string id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var text = ReadWithRetry(path);
                var request = JObject.Parse(text);
                id = (string)request["id"] ?? id;
                var command = ((string)request["command"] ?? string.Empty).ToLowerInvariant();

                Log($"-> {id} {command}");
                var result = Execute(command, request);
                Write(id, new { id, ok = true, result });
                Log($"<- {id} ok");
            }
            catch (Exception ex)
            {
                Write(id, new { id, ok = false, error = ex.ToString() });
                Log($"<- {id} error: {ex.Message}");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        static object Execute(string command, JObject request)
        {
            string ArcGisLayer() => (string)request["arcgisLayer"];
            string RhinoLayer() => (string)request["rhinoLayer"];
            RhinoArcGIS.Core.Sync.SyncDirectionMode Direction() =>
                Enum.TryParse((string)request["direction"], true, out RhinoArcGIS.Core.Sync.SyncDirectionMode dir)
                    ? dir : RhinoArcGIS.Core.Sync.SyncDirectionMode.TwoWay;

            switch (command)
            {
                case "status":
                    return OnUi(() => new
                    {
                        rhinoStarted = RhinoHost.IsStarted,
                        rhinoVersion = RhinoHost.RhinoVersion,
                        rhinoWindowStyle = RhinoHost.RhinoWindowStyle,
                        rhinoCommon = RhinoHost.LoadedRhinoCommon,
                        document = Describe(RhinoHost.GetActiveDocument()),
                        anchor = Describe(RhinoHost.GetEarthAnchor())
                    });

                case "launch":
                    return OnUi(() =>
                    {
                        RhinoHost.Start();
                        return new
                        {
                            started = RhinoHost.IsStarted,
                            version = RhinoHost.RhinoVersion,
                            windowStyle = RhinoHost.RhinoWindowStyle
                        };
                    });

                case "showpane":
                    return OnUi(() => { DockpaneViewModel.Show(); return new { shown = true }; });

                case "theme":
                    return OnUi(() =>
                    {
                        var requested = (string)request["value"];
                        if (!string.IsNullOrWhiteSpace(requested))
                        {
                            if (!Enum.TryParse(requested, true, out ApplicationTheme theme))
                                throw new ArgumentException($"Unknown ArcGIS Pro theme '{requested}'.");
                            FrameworkApplication.ApplicationTheme = theme;
                        }
                        return new { value = FrameworkApplication.ApplicationTheme.ToString() };
                    });

                case "subtab":
                    return OnUi(() =>
                    {
                        var requested = (string)request["value"];
                        if (!string.IsNullOrWhiteSpace(requested)) Pane().OpenSubTab(requested);
                        var pane = Pane();
                        return new
                        {
                            value = pane.SubTab,
                            pane.IsLinkSubTab,
                            pane.IsProfileSubTab,
                            pane.IsPullSubTab,
                            pane.IsSyncSubTab
                        };
                    });

                case "uistate":
                    return OnUi(() => DockpaneView.Current?.DescribeForTest()
                        ?? throw new InvalidOperationException("The Rhino.Inside dockpane view is not loaded."));

                case "maintab":
                    return OnUi(() =>
                    {
                        var view = DockpaneView.Current ?? throw new InvalidOperationException("The Rhino.Inside dockpane view is not loaded.");
                        view.SelectMainTabForTest(request["index"]?.Value<int>() ?? 1);
                        return new { index = request["index"]?.Value<int>() ?? 1 };
                    });

                case "gridlayout":
                    return OnUi(() => DockpaneView.Current?.DescribeGridsForTest()
                        ?? throw new InvalidOperationException("The Rhino.Inside dockpane view is not loaded."));

                case "keyboardfocus":
                    return OnUi(() => DockpaneView.Current?.DescribeKeyboardFocusForTest()
                        ?? throw new InvalidOperationException("The Rhino.Inside dockpane view is not loaded."));

                // ---- the link table, through the pane's own view model so its save path is exercised
                case "links":
                    return OnUi(DescribeLinks);

                case "setlink":
                    return OnUi(() => { Pane().SetLink(ArcGisLayer(), RhinoLayer(), Direction()); return DescribeLinks(); });

                case "removelink":
                    return OnUi(() => { var removed = Pane().RemoveLink(ArcGisLayer()); return new { removed, links = DescribeLinks() }; });

                case "newlayer":
                {
                    var linkedChoice = Enum.TryParse((string)request["linked"], true, out DockpaneViewModel.LinkedObjectsChoice parsedChoice)
                        ? parsedChoice : DockpaneViewModel.LinkedObjectsChoice.Copy;
                    var task = OnUi(() => Pane().NewArcGisLayerAsync(RhinoLayer(), (string)request["name"], linkedChoice));
                    var result = task.GetAwaiter().GetResult();
                    return new
                    {
                        layer = result.Creation.LayerName,
                        rhinoLayer = result.RhinoLayer,
                        geodatabase = result.Creation.GeodatabasePath,
                        target = result.Creation.Plan.Target.ToString(),
                        result.Creation.Plan.ObjectCount,
                        result.Creation.Plan.MatchingCount,
                        fields = result.Creation.Plan.Fields.Select(field => new
                        {
                            field.Name,
                            type = field.Type.ToString(),
                            field.Length
                        }),
                        report = Summarise(result.Report)
                    };
                }

                case "configureprofile":
                {
                    // Await away from the dispatcher: loading a profile crosses both ArcGIS's
                    // queued task and Rhino's UI-thread adapter, exactly as the pane does.
                    var task = OnUi(() => Pane().ConfigureProfileFieldForTestAsync(
                        (string)request["field"],
                        (string)request["rhinoKey"],
                        (string)request["owner"],
                        request["included"]?.Value<bool>() ?? true));
                    return task.GetAwaiter().GetResult();
                }

                case "droplayer":
                {
                    var layerName = ArcGisLayer();
                    var dropped = new RhinoArcGIS.ArcGIS.ArcGISAdapter()
                        .DeleteCreatedFeatureClass(layerName);
                    OnUi(() => Pane().RemoveLink(layerName));
                    return new { dropped, layer = layerName };
                }

                case "runall":
                {
                    // The run is awaited on the worker; the view model marshals its Rhino half back
                    // to the UI thread itself, which must stay free for that.
                    var apply = request["apply"]?.Value<bool>() ?? false;
                    OnUi(() => Pane().RunAllAsync(apply)).GetAwaiter().GetResult();
                    return OnUi(DescribeLinks);
                }

                case "trackedlayers":
                    return OnUi(() => new
                    {
                        layers = RhinoHost.GetTrackedLayers()
                            .Select(t => new { t.Layer, t.ArcGisLayer, t.Tracked, t.Parts, t.Total }).ToList()
                    });

                // ---- document round trip, for what the .3dm is supposed to carry
                case "savedoc":
                    return OnUi(() => { RhinoHost.SaveDocumentAs((string)request["path"]); return Describe(RhinoHost.GetActiveDocument()); });

                case "opendoc":
                    return OnUi(() => { RhinoHost.OpenDocument((string)request["path"]); return Describe(RhinoHost.GetActiveDocument()); });

                case "newdoc":
                    return OnUi(() => { RhinoHost.NewDocument(); return Describe(RhinoHost.GetActiveDocument()); });

                case "setunits":
                    return OnUi(() => { RhinoHost.SetModelUnits((string)request["units"]); return Describe(RhinoHost.GetActiveDocument()); });

                case "setattribute":
                    return SetAttribute(ArcGisLayer(), request["objectId"].Value<long>(),
                                        (string)request["field"], (string)request["value"]);

                case "editfeaturegeometry":
                    return EditFeatureGeometry(
                        ArcGisLayer(),
                        request["objectId"].Value<long>(),
                        request["dx"]?.Value<double>() ?? 0.0,
                        request["dy"]?.Value<double>() ?? 0.0,
                        request["dz"]?.Value<double>() ?? 0.0,
                        request["singlePart"]?.Value<bool>() ?? false,
                        request["partIndex"]?.Value<int>() ?? 0);

                case "selectfeatures":
                    return SelectFeatures(ArcGisLayer(),
                        request["objectIds"]?.Select(t => t.Value<long>()).ToList() ?? new List<long>());

                case "clearselection":
                    return SelectFeatures(ArcGisLayer(), Array.Empty<long>());

                case "nativeprobe":
                    return NativeProbe((string)request["library"] ?? "resvg_rhino");

                case "ensuremapview":
                    return EnsureMapView((string)request["map"]).GetAwaiter().GetResult();

                case "addlayer":
                    return AddLayer((string)request["path"]);

                case "prepareglobalidfixture":
                    return PrepareGlobalIdFixture(
                        (string)request["sourcePath"],
                        (string)request["gdbPath"],
                        (string)request["featureClass"]).GetAwaiter().GetResult();

                case "addlayertomap":
                    return AddLayerToMap((string)request["path"], (string)request["map"]);

                case "removelayer":
                    return RemoveLayer(ArcGisLayer());

                case "saveedits":
                    return new { saved = ArcGIS.Desktop.Core.Project.Current.SaveEditsAsync().GetAwaiter().GetResult() };

                case "defaultgdb":
                    return new { path = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath };

                case "setdefaultgdb":
                    return SetDefaultGeodatabase((string)request["path"]);

                case "openproject":
                    return new
                    {
                        opened = OnUi(() => ArcGIS.Desktop.Core.Project.OpenAsync((string)request["path"]))
                            .GetAwaiter().GetResult()
                    };

                case "saveproject":
                    return new
                    {
                        // Start on the UI thread, wait off it: blocking the UI thread on SaveAsync deadlocks.
                        saved = OnUi(() => ArcGIS.Desktop.Core.Project.Current.SaveAsync()).GetAwaiter().GetResult()
                    };

                case "deletefeatures":
                    return DeleteFeatures(ArcGisLayer(), request["objectIds"]?.Select(t => t.Value<long>()).ToList() ?? new List<long>());

                case "arcgislayers":
                    return new { layers = SyncCoordinator.GetArcGisLayerNames() };

                case "objectcounts":
                    return OnUi(() => new { counts = RhinoHost.GetObjectCounts((string)request["layer"]) });

                case "features":
                    return ReadFeatures(ArcGisLayer(), request["take"]?.Value<int>() ?? 5);

                case "geomdiff":
                    return SyncCoordinator.GeometryDiffAsync(ArcGisLayer(), RhinoLayer(), request["take"]?.Value<int>() ?? 3)
                                          .GetAwaiter().GetResult();

                case "schema":
                    var schema = new RhinoArcGIS.ArcGIS.ArcGISAdapter().GetSchema(ArcGisLayer());
                    return schema == null ? null : new
                    {
                        schema.LayerName,
                        geometry = schema.GeometryType.ToString(),
                        schema.ZEnabled,
                        schema.HasGlobalIds,
                        schema.Editable,
                        fields = schema.Fields.Select(f => new { f.Name, type = f.Type.ToString(), f.Length, f.Nullable })
                    };

                case "addobject":
                    return OnUi(() => new
                    {
                        id = RhinoHost.AddTestObject(
                            (string)request["layer"],
                            (string)request["kind"],
                            ParsePoints(request["points"])).ToString()
                    });

                case "addsetback":
                    return OnUi(() => new
                    {
                        id = RhinoHost.AddSetbackBuilding(
                            (string)request["layer"],
                            request["x"].Value<double>(), request["y"].Value<double>(),
                            ((JArray)request["tiers"]).Select(t => (
                                width: t["width"].Value<double>(),
                                depth: t["depth"].Value<double>(),
                                height: t["height"].Value<double>()
                            )).ToList()).ToString()
                    });

                case "addcomplex":
                    return OnUi(() => new
                    {
                        id = RhinoHost.AddComplexTestObject(
                            (string)request["layer"],
                            (string)request["kind"],
                            request["x"].Value<double>(), request["y"].Value<double>(),
                            ((JObject)request["params"])?.Properties()
                                .ToDictionary(prop => prop.Name, prop => prop.Value.Value<double>())
                        ).ToString()
                    });

                case "setuserstring":
                    return OnUi(() =>
                    {
                        RhinoHost.SetUserString(Guid.Parse((string)request["objectId"]),
                                                (string)request["key"], (string)request["value"]);
                        return new { set = true };
                    });

                case "moveobject":
                    return OnUi(() =>
                    {
                        var id = Guid.Parse((string)request["objectId"]);
                        RhinoHost.MoveTestObject(
                            id,
                            request["x"]?.Value<double>() ?? 0.0,
                            request["y"]?.Value<double>() ?? 0.0,
                            request["z"]?.Value<double>() ?? 0.0);
                        return new { moved = true, objectId = id };
                    });

                case "setobjectlocked":
                    return OnUi(() =>
                    {
                        var id = Guid.Parse((string)request["objectId"]);
                        var locked = request["locked"]?.Value<bool>() ?? true;
                        return new
                        {
                            objectId = id,
                            locked = RhinoHost.SetTestObjectLocked(id, locked)
                        };
                    });

                case "userstrings":
                    return OnUi(() => new
                    {
                        objects = RhinoHost.GetUserStrings(
                            string.IsNullOrEmpty((string)request["objectId"]) ? (Guid?)null : Guid.Parse((string)request["objectId"]),
                            (string)request["layer"])
                    });

                case "objectgeometry":
                    return OnUi(() =>
                    {
                        var info = RhinoHost.GetGeometryInfo(Guid.Parse((string)request["objectId"]));
                        return info == null ? null : new
                        {
                            info.Kind,
                            info.XMin,
                            info.XMax,
                            info.YMin,
                            info.YMax,
                            info.ZMin,
                            info.ZMax,
                            info.VertexCount,
                            info.TopologyVertexCount,
                            info.FaceCount,
                            info.FaceCornerCount,
                            info.SharedVertices,
                            info.IsClosed
                        };
                    });

                case "attributeseditable":
                    // Through the pane, so the choice is saved with the Rhino document like a click is.
                    return OnUi(() =>
                    {
                        Pane().AttributesEditableInRhino = request["value"]?.Value<bool>() ?? false;
                        return new { editable = SyncCoordinator.AttributesEditableInRhino };
                    });

                case "addfeature":
                {
                    var attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (request["attributes"] is JObject values)
                        foreach (var property in values.Properties()) attributes[property.Name] = (string)property.Value;
                    return AddFeature(ArcGisLayer(), request["copyOf"].Value<long>(),
                        request["dx"]?.Value<double>() ?? 0.0, request["dy"]?.Value<double>() ?? 0.0, attributes);
                }

                case "rhinolayers":
                    return OnUi(() => new { layers = RhinoHost.GetLayerNames() });

                case "setanchor":
                    return OnUi(() =>
                    {
                        RhinoHost.SetEarthAnchor(
                            request["latitude"].Value<double>(),
                            request["longitude"].Value<double>(),
                            request["modelX"]?.Value<double>() ?? 0.0,
                            request["modelY"]?.Value<double>() ?? 0.0);
                        return Describe(RhinoHost.GetEarthAnchor());
                    });

                case "pull":
                    return Summarise(SyncCoordinator.PullAsync(
                        ArcGisLayer(), RhinoLayer(), request["selectedOnly"]?.Value<bool>() ?? false,
                        ProfileJsonFor(ArcGisLayer()))
                        .GetAwaiter().GetResult());

                case "preview":
                    return Summarise(SyncCoordinator.PreviewAsync(
                        ArcGisLayer(), RhinoLayer(), Direction(), ProfileJsonFor(ArcGisLayer()))
                        .GetAwaiter().GetResult());

                case "apply":
                    var policy = Enum.TryParse((string)request["conflicts"], true,
                                               out RhinoArcGIS.Core.Sync.ConflictResolution parsed)
                        ? parsed
                        : RhinoArcGIS.Core.Sync.ConflictResolution.Manual;
                    return Summarise(SyncCoordinator.ApplyAsync(
                        ArcGisLayer(), RhinoLayer(), policy, Direction(), ProfileJsonFor(ArcGisLayer()))
                        .GetAwaiter().GetResult());

                case "addline":
                    return OnUi(() => new
                    {
                        id = RhinoHost.AddTestLine(
                            (string)request["layer"],
                            request["x1"].Value<double>(), request["y1"].Value<double>(),
                            request["x2"].Value<double>(), request["y2"].Value<double>()).ToString()
                    });

                case "script":
                    return OnUi(() => { RhinoHost.RunScript((string)request["script"]); return new { ran = true }; });

                // ---- session lifecycle: what a person does between two sittings
                case "unsavedpolicy":
                {
                    var value = (string)request["value"];
                    if (!Enum.TryParse(value, true, out RhinoHost.UnsavedWorkPolicy unsaved))
                        throw new ArgumentException($"Unknown policy '{value}'. Use Prompt, Save or Discard.");
                    RhinoHost.UnsavedWork = unsaved;
                    return new { policy = unsaved.ToString() };
                }

                case "saverhino":
                    return OnUi(() => new { path = RhinoHost.SaveActiveDocument(), document = Describe(RhinoHost.GetActiveDocument()) });

                case "capturepane":
                    return OnUi(() => DockpaneView.Current?.CaptureForTest((string)request["path"],
                        request["width"]?.Value<int>() ?? 420, request["height"]?.Value<int>() ?? 900,
                        request["mainTab"]?.Value<int>() ?? -1)
                        ?? throw new InvalidOperationException("The Rhino.Inside dockpane view is not loaded."));

                case "closepro":
                {
                    // Settle the project first, the way a person answers Pro's prompts: save the
                    // project and its edits (the default for a scripted run on a disposable copy),
                    // or discard edits and let the dismisser answer "No" to Pro's save prompt.
                    var saveProject = request["saveProject"]?.Value<bool>() ?? true;
                    var project = ArcGIS.Desktop.Core.Project.Current;
                    if (project != null)
                    {
                        if (saveProject)
                        {
                            project.SaveEditsAsync().GetAwaiter().GetResult();
                            OnUi(() => project.SaveAsync()).GetAwaiter().GetResult();
                        }
                        else if (project.HasEdits)
                            project.DiscardEditsAsync().GetAwaiter().GetResult();
                    }

                    // Reply first, then close: the reply is how the caller knows the request landed.
                    // FrameworkApplication.Close runs the normal shutdown -- ApplicationClosingEvent,
                    // Pro's own save prompts, then Module.Uninitialize -- exactly as the window's X.
                    Log($"closepro requested (saveProject={saveProject})");
                    // Watchdog for an intermittent hang where the close never began: says whether
                    // the UI thread was blocked, busy without ever idling, or idle.
                    var closeStarted = false;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        for (int i = 0; i < 6 && !closeStarted; i++)
                        {
                            System.Threading.Thread.Sleep(5000);
                            if (closeStarted) break;
                            var send = _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send);
                            var background = _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                            bool sendOk = send.Wait(TimeSpan.FromSeconds(2)) == DispatcherOperationStatus.Completed;
                            bool backgroundOk = background.Wait(TimeSpan.FromSeconds(2)) == DispatcherOperationStatus.Completed;
                            Log($"closepro watchdog: +{(i + 1) * 5}s close not started; UI answers Send={sendOk}, Background={backgroundOk}" +
                                $"; sync gate busy={SyncCoordinator.IsBusy}");
                        }
                    });
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        closeStarted = true;
                        Log("closepro: FrameworkApplication.Close starting");
                        try { FrameworkApplication.Close(); Log("closepro: FrameworkApplication.Close returned"); }
                        catch (Exception ex) { Log("closepro failed: " + ex); }
                    }), DispatcherPriority.ApplicationIdle);
                    return new { closing = true };
                }

                case "duplicateobject":
                    return OnUi(() => new
                    {
                        id = RhinoHost.DuplicateTestObject(Guid.Parse((string)request["objectId"]),
                            request["dx"]?.Value<double>() ?? 0.0, request["dy"]?.Value<double>() ?? 0.0).ToString()
                    });

                case "deleteobject":
                    return OnUi(() => new { deleted = RhinoHost.DeleteTestObject(Guid.Parse((string)request["objectId"])) });

                case "renamelayer":
                    return RenameLayer(ArcGisLayer(), (string)request["newName"]);

                case "opentable":
                    return OpenTable(ArcGisLayer());

                case "layercolor":
                {
                    var hex = ((string)request["color"] ?? "#000000").TrimStart('#');
                    OnUi(() =>
                    {
                        RhinoHost.SetLayerColor((string)request["layer"], Convert.ToInt32(hex.Substring(0, 2), 16),
                            Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));
                        return true;
                    });
                    return new { layer = (string)request["layer"], color = "#" + hex };
                }

                case "scaleobjectz":
                    return OnUi(() =>
                    {
                        RhinoHost.ScaleTestObjectZ(Guid.Parse((string)request["objectId"]), request["factor"].Value<double>());
                        return new { scaled = true };
                    });

                case "setanchorrotated":
                    return OnUi(() =>
                    {
                        RhinoHost.SetEarthAnchorRotated(request["latitude"].Value<double>(), request["longitude"].Value<double>(),
                            request["northAngle"]?.Value<double>() ?? 0.0);
                        return Describe(RhinoHost.GetEarthAnchor());
                    });

                case "captureview":
                    return OnUi(() => new
                    {
                        path = RhinoHost.CaptureView((string)request["path"], (string)request["view"] ?? "Perspective",
                            request["width"]?.Value<int>() ?? 1600, request["height"]?.Value<int>() ?? 1000)
                    });

                default:
                    if (TestBridgeDemo.TryExecute(command, request, out var demo)) return demo;
                    if (TestBridgeBench.TryExecute(command, request, _dispatcher, out var bench)) return bench;
                    throw new ArgumentException($"Unknown command '{command}'.");
            }
        }

        /// <summary>
        /// Reports why a Rhino native library is or is not loaded in this process.
        /// </summary>
        /// <remarks>
        /// Rhino 8 rasterises every toolbar icon through resvg_rhino.dll. When that native library
        /// does not load, Rhino falls back to drawing command names as text, which is the symptom
        /// seen when hosting it inside Pro. This distinguishes the possibilities: the file missing,
        /// the load failing (and with which Win32 error), or the load never being attempted.
        /// </remarks>
        static object NativeProbe(string name)
        {
            var netcore = RhinoHost.RhinoSystemDirectory;
            var systemDir = string.IsNullOrEmpty(netcore) ? null : Path.GetDirectoryName(netcore);
            var path = systemDir == null ? null : Path.Combine(systemDir, name + ".dll");

            var alreadyLoaded = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>()
                .Where(m => m.ModuleName != null &&
                            m.ModuleName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.FileName)
                .ToList();

            string explicitLoad;
            try
            {
                var handle = System.Runtime.InteropServices.NativeLibrary.Load(path);
                explicitLoad = handle == IntPtr.Zero ? "returned null handle" : "loaded ok";
            }
            catch (Exception ex)
            {
                explicitLoad = ex.GetType().Name + ": " + ex.Message;
            }

            // Which Rhino natives did make it in, for comparison.
            var rhinoNatives = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>()
                .Where(m => m.FileName != null && systemDir != null &&
                            m.FileName.StartsWith(systemDir, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.ModuleName)
                .OrderBy(n => n)
                .ToList();

            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            return new
            {
                library = name,
                path,
                fileExists = path != null && File.Exists(path),
                alreadyLoaded,
                explicitLoad,
                systemDirOnPath = systemDir != null &&
                                  pathVariable.IndexOf(systemDir, StringComparison.OrdinalIgnoreCase) >= 0,
                nativeSearchDirectories = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")?.ToString(),
                rhinoNativesLoaded = rhinoNatives
            };
        }

        /// <summary>Removes a layer from the active map, for swapping a fixture's source under the same name.</summary>
        static object RemoveLayer(string layerName)
        {
            var removed = ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var map = RhinoArcGIS.ArcGIS.ActiveMap.Current;
                var layer = map?.GetLayersAsFlattenedList()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) return false;
                map.RemoveLayer(layer);
                return true;
            }).Result;

            return new { removed };
        }

        static DockpaneViewModel Pane() =>
            DockpaneViewModel.Instance ?? throw new InvalidOperationException("The Rhino.Inside dockpane could not be created.");

        /// <summary>
        /// The file bridge uses the profile saved on the same link row as the production pane.
        /// Commands without a row continue to exercise the coordinator's safe generated default.
        /// </summary>
        static string ProfileJsonFor(string arcGisLayer) => OnUi(() => DockpaneViewModel.Instance?.Links
            .FirstOrDefault(link => string.Equals(link.ArcGisLayer, arcGisLayer,
                                                   StringComparison.OrdinalIgnoreCase))
            ?.ProfileJson);

        /// <summary>
        /// Points the live test project at a disposable default geodatabase. This prevents the
        /// new-layer E2E from creating schema in the developer's real project geodatabase.
        /// </summary>
        static object SetDefaultGeodatabase(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required.");
            path = Path.GetFullPath(path);

            return ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                if (!Directory.Exists(path))
                {
                    var connection = new ArcGIS.Core.Data.FileGeodatabaseConnectionPath(new Uri(path));
                    using var created = ArcGIS.Core.Data.DDL.SchemaBuilder.CreateGeodatabase(connection);
                }

                var project = ArcGIS.Desktop.Core.Project.Current
                    ?? throw new InvalidOperationException("No ArcGIS project is open.");
                project.FindItem(path); // adds the connection when it is not already in the project
                project.SetDefaultGeoDatabasePath(path);
                return (object)new { path = project.DefaultGeodatabasePath };
            }).Result;
        }

        /// <summary>The link table as the pane holds it, and as the Rhino document stores it.</summary>
        static object DescribeLinks()
        {
            var pane = Pane();
            return new
            {
                active = pane.ActiveLink?.ArcGisLayer,
                table = pane.Links.Select(l => new
                {
                    arcgisLayer = l.ArcGisLayer,
                    rhinoLayer = l.RhinoLayer,
                    effectiveRhinoLayer = l.EffectiveRhinoLayer,
                    direction = l.Direction.ToString(),
                    profile = l.ProfileLabel,
                    lastResult = l.LastResult,
                    source = l.ArcGisSource,
                    missing = l.IsArcGisLayerMissing,
                    status = l.StatusLabel
                }).ToList(),
                stored = LayerLinkStore.Load().Select(l => new
                {
                    arcgisLayer = l.ArcGisLayer,
                    rhinoLayer = l.RhinoLayer,
                    direction = l.Direction.ToString(),
                    profile = l.ProfileLabel,
                    source = l.ArcGisSource
                }).ToList(),
                documentModified = RhinoHost.GetActiveDocument()?.IsModified ?? false,
                attributesEditable = SyncCoordinator.AttributesEditableInRhino
            };
        }

        /// <summary>Renames a map layer, the way a person tidies a table of contents between sessions.</summary>
        static object RenameLayer(string layerName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName is required.");
            var renamed = ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var layer = RhinoArcGIS.ArcGIS.ActiveMap.Current?.GetLayersAsFlattenedList()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) return false;
                layer.SetName(newName);
                return true;
            }).Result;
            return new { renamed, name = newName };
        }

        /// <summary>
        /// Opens a layer's attribute table and makes it the active pane, so no map view is active --
        /// what happens every time someone edits attributes in ArcGIS.
        /// </summary>
        static object OpenTable(string layerName)
        {
            var layer = ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
                RhinoArcGIS.ArcGIS.ActiveMap.Current?.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase))).Result
                ?? throw new InvalidOperationException($"Layer '{layerName}' is not in the map.");
            return OnUi(() =>
            {
                var pane = ArcGIS.Desktop.Core.FrameworkExtender.OpenTablePane(FrameworkApplication.Panes, layer, TableViewMode.eAllRecords);
                (pane as ArcGIS.Desktop.Framework.Contracts.Pane)?.Activate();
                return new { opened = pane != null, mapViewActive = MapView.Active != null };
            });
        }

        /// <summary>Edits one attribute on the ArcGIS side, to stage the modified-in-ArcGIS case.</summary>
        static object SetAttribute(string layerName, long objectId, string field, string value)
        {
            if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("field is required.");

            ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var layer = RhinoArcGIS.ArcGIS.ActiveMap.Current?.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) throw new ArgumentException($"No feature layer named '{layerName}'.");

                var op = new ArcGIS.Desktop.Editing.EditOperation { Name = "Rhino.Inside test attribute edit" };
                op.Modify(layer, objectId, new Dictionary<string, object> { { field, value } });
                if (!op.Execute()) throw new InvalidOperationException("Modify failed: " + op.ErrorMessage);
            }).Wait();

            return new { set = true, objectId, field, value };
        }

        /// <summary>
        /// Creates a new feature in ArcGIS -- what a GIS editor does with the Create Features pane --
        /// shaped like an existing one and moved aside, with the given attribute values.
        /// </summary>
        static object AddFeature(string layerName, long copyOf, double dx, double dy,
                                 IReadOnlyDictionary<string, object> attributes)
        {
            return ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var layer = RhinoArcGIS.ArcGIS.ActiveMap.Current?.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No feature layer named '{layerName}'.");

                ArcGIS.Core.Geometry.Geometry shape;
                using (var table = layer.GetTable())
                {
                    var filter = new ArcGIS.Core.Data.QueryFilter
                    {
                        WhereClause = $"{table.GetDefinition().GetObjectIDField()} = {copyOf}"
                    };
                    using var cursor = table.Search(filter, false);
                    if (!cursor.MoveNext() || !(cursor.Current is ArcGIS.Core.Data.Feature source))
                        throw new ArgumentException($"No feature with ObjectID {copyOf} exists in '{layerName}'.");
                    using (source) shape = source.GetShape();
                }
                shape = ArcGIS.Core.Geometry.GeometryEngine.Instance.Move(shape, dx, dy);

                var values = new Dictionary<string, object>(attributes, StringComparer.OrdinalIgnoreCase)
                {
                    [layer.GetFeatureClass().GetDefinition().GetShapeField()] = shape
                };
                var operation = new ArcGIS.Desktop.Editing.EditOperation { Name = "Rhino.Inside test create" };
                var token = operation.Create(layer, values);
                if (!operation.Execute())
                    throw new InvalidOperationException("Create failed: " + operation.ErrorMessage);
                return new { created = true, objectId = token.ObjectID };
            }).Result;
        }

        /// <summary>
        /// Translates an existing feature and can reduce multipart geometry to one selected part.
        /// It edits the same row, preserving its ObjectID, GlobalID, and non-shape attributes. This
        /// command is available only through the opt-in test bridge and must target disposable data.
        /// </summary>
        static object EditFeatureGeometry(string layerName, long objectId,
                                          double dx, double dy, double dz,
                                          bool singlePart, int partIndex)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                throw new ArgumentException("arcgisLayer is required.", nameof(layerName));
            if (new[] { dx, dy, dz }.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new ArgumentException("Geometry offsets must be finite numbers.");
            if (partIndex < 0) throw new ArgumentOutOfRangeException(nameof(partIndex));

            return ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var layer = RhinoArcGIS.ArcGIS.ActiveMap.Current?.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) throw new ArgumentException($"No feature layer named '{layerName}'.");

                using var table = layer.GetTable();
                var definition = table.GetDefinition();
                var filter = new ArcGIS.Core.Data.QueryFilter
                {
                    WhereClause = $"{definition.GetObjectIDField()} = {objectId}"
                };
                using var cursor = table.Search(filter, false);
                if (!cursor.MoveNext() || !(cursor.Current is ArcGIS.Core.Data.Feature feature))
                    throw new ArgumentException($"No feature with ObjectID {objectId} exists in '{layerName}'.");

                using (feature)
                {
                    var original = feature.GetShape()
                        ?? throw new InvalidOperationException($"Feature {objectId} has no geometry.");
                    var beforeParts = PartCount(original);
                    ArcGIS.Core.Geometry.Geometry edited = original;

                    if (singlePart)
                    {
                        var partCount = PartCount(original);
                        if (partCount == 0)
                            throw new InvalidOperationException($"Feature {objectId} has no selectable parts.");
                        if (partIndex >= partCount)
                            throw new ArgumentOutOfRangeException(
                                nameof(partIndex),
                                $"Feature {objectId} has {partCount} part(s); part {partIndex} does not exist.");
                        edited = SelectSinglePart(original, partIndex);
                    }

                    // ArcGIS rejects the 3D overload for XY-only feature classes even when dz is
                    // zero. Preserve the source geometry's awareness and use the matching overload.
                    edited = edited.HasZ
                        ? ArcGIS.Core.Geometry.GeometryEngine.Instance.Move(edited, dx, dy, dz)
                        : ArcGIS.Core.Geometry.GeometryEngine.Instance.Move(edited, dx, dy);
                    var operation = new ArcGIS.Desktop.Editing.EditOperation
                    {
                        Name = "Rhino.Inside test geometry edit"
                    };
                    operation.Modify(layer, objectId, edited, null);
                    if (!operation.Execute())
                        throw new InvalidOperationException("Geometry modify failed: " + operation.ErrorMessage);

                    return new
                    {
                        edited = true,
                        objectId,
                        beforePartCount = beforeParts,
                        afterPartCount = PartCount(edited),
                        selectedPart = singlePart,
                        dx,
                        dy,
                        dz
                    };
                }
            }).Result;
        }

        static ArcGIS.Core.Geometry.Geometry SelectSinglePart(
            ArcGIS.Core.Geometry.Geometry geometry,
            int partIndex)
        {
            var attributes = ArcGIS.Core.Geometry.AttributeFlags.None;
            if (geometry.HasZ) attributes |= ArcGIS.Core.Geometry.AttributeFlags.HasZ;
            if (geometry.HasM) attributes |= ArcGIS.Core.Geometry.AttributeFlags.HasM;
            if (geometry.HasID) attributes |= ArcGIS.Core.Geometry.AttributeFlags.HasID;

            // MultipartToSinglePart preserves an exterior polygon ring together with its holes,
            // so it cannot guarantee that a selected raw part produces a geometry change. The
            // bridge's partIndex is deliberately literal: retain exactly that ring/path.
            if (geometry is ArcGIS.Core.Geometry.Polygon polygon)
                return new ArcGIS.Core.Geometry.PolygonBuilderEx(
                    polygon.Parts[partIndex], attributes, geometry.SpatialReference).ToGeometry();
            if (geometry is ArcGIS.Core.Geometry.Polyline polyline)
                return new ArcGIS.Core.Geometry.PolylineBuilderEx(
                    polyline.Parts[partIndex], attributes, geometry.SpatialReference).ToGeometry();

            var parts = ArcGIS.Core.Geometry.GeometryEngine.Instance.MultipartToSinglePart(geometry);
            if (parts == null || partIndex >= parts.Count)
                throw new ArgumentOutOfRangeException(nameof(partIndex));
            return parts[partIndex];
        }

        static int PartCount(ArcGIS.Core.Geometry.Geometry geometry)
        {
            if (geometry == null) return 0;
            if (geometry is ArcGIS.Core.Geometry.Multipart multipart) return multipart.PartCount;
            if (geometry is ArcGIS.Core.Geometry.Multipatch multipatch) return multipatch.PartCount;
            return 1;
        }

        /// <summary>
        /// Replaces the active selection for one feature layer. An empty id list clears it. This
        /// exercises the same ArcGIS selection that the production selected-only pull reads.
        /// </summary>
        static object SelectFeatures(string layerName, IReadOnlyList<long> objectIds)
        {
            var selected = ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var layer = RhinoArcGIS.ArcGIS.ActiveMap.Current?.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) throw new ArgumentException($"No feature layer named '{layerName}'.");

                layer.ClearSelection();
                if (objectIds.Count > 0)
                {
                    var oidField = layer.GetTable().GetDefinition().GetObjectIDField();
                    var filter = new ArcGIS.Core.Data.QueryFilter
                    {
                        WhereClause = $"{oidField} IN ({string.Join(",", objectIds)})"
                    };
                    layer.Select(filter, SelectionCombinationMethod.New);
                }

                return layer.GetSelection().GetCount();
            }).Result;

            return new { selected };
        }

        /// <summary>Deletes features by object id, to stage the deleted-in-ArcGIS case.</summary>
        static object DeleteFeatures(string layerName, IReadOnlyList<long> objectIds)
        {
            var count = ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var layer = RhinoArcGIS.ArcGIS.ActiveMap.Current?.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null || objectIds.Count == 0) return 0;

                var op = new ArcGIS.Desktop.Editing.EditOperation { Name = "Rhino.Inside test delete" };
                op.Delete(layer, objectIds);
                if (!op.Execute()) throw new InvalidOperationException("Delete failed: " + op.ErrorMessage);
                return objectIds.Count;
            }).Result;

            return new { deleted = count };
        }

        /// <summary>Adds a dataset to the active map, so a scripted run can set up its own fixture.</summary>
        static object AddLayer(string path)
        {
            // A shapefile is a file; a geodatabase feature class is "<gdb-folder>\<fc-name>", so its
            // path itself never exists -- its parent .gdb folder does.
            bool exists = !string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path) || Directory.Exists(Path.GetDirectoryName(path)));
            if (!exists)
                throw new FileNotFoundException("No dataset at that path.", path);

            var name = ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var map = RhinoArcGIS.ArcGIS.ActiveMap.Current;
                if (map == null) return null;

                var layer = LayerFactory.Instance.CreateLayer(new Uri(path), map);
                SetOnGroundElevation(layer);
                return layer?.Name;
            }).Result;

            return new { added = name != null, name };
        }

        /// <summary>
        /// Builds a disposable file-geodatabase feature class with GlobalIDs from a source fixture
        /// and adds it to the active map.  This lets the E2E suite exercise the production GlobalID
        /// identity path; shapefiles have only volatile FIDs/ObjectIDs.
        /// </summary>
        static async Task<object> PrepareGlobalIdFixture(string sourcePath, string gdbPath, string featureClass)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("No source fixture at that path.", sourcePath);
            if (string.IsNullOrWhiteSpace(gdbPath))
                throw new ArgumentException("gdbPath is required.", nameof(gdbPath));
            if (string.IsNullOrWhiteSpace(featureClass))
                throw new ArgumentException("featureClass is required.", nameof(featureClass));

            var folder = Path.GetDirectoryName(gdbPath);
            var gdbName = Path.GetFileName(gdbPath);
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(gdbName))
                throw new ArgumentException("gdbPath must include a parent folder and geodatabase name.", nameof(gdbPath));
            Directory.CreateDirectory(folder);

            var environment = Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);
            if (!Directory.Exists(gdbPath))
            {
                var create = await Geoprocessing.ExecuteToolAsync(
                    "management.CreateFileGDB",
                    Geoprocessing.MakeValueArray(folder, gdbName),
                    environment, null, null, GPExecuteToolFlags.None);
                ThrowIfFailed("CreateFileGDB", create);
            }

            var output = Path.Combine(gdbPath, featureClass);
            var copy = await Geoprocessing.ExecuteToolAsync(
                "management.CopyFeatures",
                Geoprocessing.MakeValueArray(sourcePath, output),
                environment, null, null, GPExecuteToolFlags.None);
            ThrowIfFailed("CopyFeatures", copy);

            var addGlobalIds = await Geoprocessing.ExecuteToolAsync(
                "management.AddGlobalIDs",
                Geoprocessing.MakeValueArray(output),
                environment, null, null, GPExecuteToolFlags.None);
            ThrowIfFailed("AddGlobalIDs", addGlobalIds);

            var added = AddLayer(output);
            return new { path = output, layer = added };
        }

        static void ThrowIfFailed(string operation, IGPResult result)
        {
            if (result != null && !result.IsFailed) return;
            var messages = result?.Messages == null
                ? "No geoprocessing result was returned."
                : string.Join("; ", result.Messages.Select(message => message.Text));
            throw new InvalidOperationException($"{operation} failed: {messages}");
        }

        /// <summary>
        /// Makes a project map active before an E2E run starts.  The add-in can finish loading
        /// before ArcGIS Pro restores any document panes, leaving MapView.Active null forever on
        /// some cold starts.  OpenMapPaneAsync must be initiated on the GUI thread, but awaited by
        /// the bridge worker so the dispatcher remains free to complete the activation.
        /// </summary>
        static async Task<object> EnsureMapView(string mapName)
        {
            var activeName = OnUi(() => MapView.Active?.Map?.Name);
            if (!string.IsNullOrWhiteSpace(activeName) &&
                (string.IsNullOrWhiteSpace(mapName) ||
                 string.Equals(activeName, mapName, StringComparison.OrdinalIgnoreCase)))
                return new { active = true, name = activeName, opened = false };

            var item = OnUi(() =>
            {
                var items = ArcGIS.Desktop.Core.Project.Current?
                    .GetItems<ArcGIS.Desktop.Mapping.MapProjectItem>();
                if (items == null) return null;
                return string.IsNullOrWhiteSpace(mapName)
                    ? items.FirstOrDefault()
                    : items.FirstOrDefault(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase));
            });

            if (item == null)
                return new { active = false, name = (string)null, opened = false };

            var pane = await OnUi(() => item.OpenMapPaneAsync());
            activeName = OnUi(() => MapView.Active?.Map?.Name);
            return new { active = pane != null && !string.IsNullOrWhiteSpace(activeName), name = activeName, opened = true };
        }

        /// <summary>
        /// Adds a dataset to a named map or scene project item directly, rather than whichever view
        /// happens to be active -- so a feature class already populated through the (Map-view-bound)
        /// sync path can also be shown in a Scene without switching the active view.
        /// </summary>
        static object AddLayerToMap(string path, string mapName)
        {
            bool exists = !string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path) || Directory.Exists(Path.GetDirectoryName(path)));
            if (!exists)
                throw new FileNotFoundException("No dataset at that path.", path);
            if (string.IsNullOrWhiteSpace(mapName))
                throw new ArgumentException("map is required.", nameof(mapName));

            var name = ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() =>
            {
                var item = ArcGIS.Desktop.Core.Project.Current.GetItems<ArcGIS.Desktop.Mapping.MapProjectItem>()
                    .FirstOrDefault(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase));
                var map = item?.GetMap();
                if (map == null) return null;

                var layer = LayerFactory.Instance.CreateLayer(new Uri(path), map);
                SetOnGroundElevation(layer);
                return layer?.Name;
            }).Result;

            return new { added = name != null, name };
        }

        /// <summary>
        /// Defaults a newly added layer to "On the ground" elevation, so a multipatch's own Z=0 sits
        /// at the terrain surface instead of at whatever the CRS's Z datum happens to be. Absolute
        /// heights can bury or float footprint-relative buildings far from the visible ground.
        /// No-op for a 2D map or a layer
        /// type with no elevation concept (CanSetElevationTypeDefinition returns false).
        /// </summary>
        static void SetOnGroundElevation(Layer layer)
        {
            if (layer == null) return;
            var def = new ArcGIS.Desktop.Mapping.ElevationTypeDefinition
            {
                ElevationType = ArcGIS.Desktop.Mapping.LayerElevationType.OnGround
            };
            if (layer.CanSetElevationTypeDefinition(def)) layer.SetElevationTypeDefinition(def);
        }

        /// <summary>
        /// The ArcGIS side of a layer as the sync sees it: feature count, geometry kinds, and the
        /// attributes of the last few features -- enough to check that a push actually landed.
        /// </summary>
        static object ReadFeatures(string layerName, int take)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                throw new ArgumentException("arcgisLayer is required.");

            var adapter = new RhinoArcGIS.ArcGIS.ArcGISAdapter();
            var features = adapter.ReadFeatures(layerName);

            var kinds = features
                .GroupBy(f => f.Geometry == null ? "None" : f.Geometry.Kind.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            var last = features
                .OrderBy(f => f.Identity?.ArcGisObjectId ?? 0)
                .Skip(Math.Max(0, features.Count - take))
                .Select(f =>
                {
                    var points = f.Geometry?.AllPoints().ToList() ?? new List<RhinoArcGIS.Core.Spatial.Xyz>();
                    var mesh = f.Geometry?.Mesh;
                    int faceCorners = mesh?.Faces?.Sum(face => face?.Length ?? 0) ?? 0;
                    return new
                    {
                        objectId = f.Identity?.ArcGisObjectId,
                        globalId = f.Identity?.ArcGisGlobalId,
                        kind = f.Geometry?.Kind.ToString(),
                        pointCount = points.Count,
                        zMin = points.Count == 0 ? (double?)null : points.Min(p => p.Z),
                        zMax = points.Count == 0 ? (double?)null : points.Max(p => p.Z),
                        vertexCount = mesh?.Vertices?.Count ?? 0,
                        faceCount = mesh?.Faces?.Count ?? 0,
                        faceCornerCount = faceCorners,
                        sharedVertices = mesh != null && mesh.Vertices.Count < faceCorners,
                        attributes = f.Attributes
                    };
                })
                .ToList();

            var source = adapter.GetSchema(layerName)?.Source;
            return new { total = features.Count, source, kinds, last };
        }

        static IReadOnlyList<double[]> ParsePoints(JToken token)
        {
            var list = new List<double[]>();
            if (token == null) return list;

            foreach (var p in token)
            {
                if (p is JArray arr)
                    list.Add(arr.Select(v => v.Value<double>()).ToArray());
                else if (p is JObject obj)
                    list.Add(new[] { obj["x"]?.Value<double>() ?? 0, obj["y"]?.Value<double>() ?? 0, obj["z"]?.Value<double>() ?? 0 });
            }
            return list;
        }

        /// <summary>Report reduced to counts plus the rows that need action.</summary>
        static object Summarise(RhinoArcGIS.Core.Reporting.SyncReport report)
        {
            var rows = report.Entries.Select(SyncRow.From).ToList();

            return new
            {
                total = rows.Count,
                changed = rows.Count(r => r.IsChanged),
                rollup = SyncRollupItem.From(rows).Select(r => new { r.Label, r.Count }),
                changedRows = rows.Where(r => r.IsChanged).Take(50)
                                  .Select(r => new { r.State, r.Side, r.Detail, r.Summary, r.ObjectId }),
                phaseMs = report.PhaseMs
            };
        }

        static object Describe(RhinoDocumentInfo doc) => doc == null ? null : new
        {
            doc.Name, doc.Path, doc.ObjectCount, doc.LayerCount, doc.ModelUnits, doc.IsModified
        };

        static object Describe(EarthAnchorInfo anchor) => anchor == null ? null : new
        {
            anchor.IsSet, anchor.Latitude, anchor.Longitude, anchor.ModelBaseX, anchor.ModelBaseY
        };

        static T OnUi<T>(Func<T> func) =>
            _dispatcher == null || _dispatcher.CheckAccess() ? func() : _dispatcher.Invoke(func);

        static string ReadWithRetry(string path)
        {
            // The writer may still hold the handle when the created event fires.
            for (var attempt = 0; ; attempt++)
            {
                try { return File.ReadAllText(path); }
                catch (IOException) when (attempt < 20) { System.Threading.Thread.Sleep(50); }
            }
        }

        static void Write(string id, object payload)
        {
            try
            {
                var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                var final = Path.Combine(_outbox, id + ".json");
                var temp = final + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(final)) File.Delete(final);
                File.Move(temp, final);
            }
            catch (Exception ex)
            {
                Log("could not write reply: " + ex.Message);
            }
        }

        internal static void Log(string message)
        {
            if (_logPath == null) return;
            try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
