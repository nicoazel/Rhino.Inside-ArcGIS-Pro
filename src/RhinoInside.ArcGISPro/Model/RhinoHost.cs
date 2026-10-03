using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Boots Rhino in-process inside the ArcGIS Pro process via Rhino.Inside.
    /// </summary>
    /// <remarks>
    /// Two ordering rules drive the shape of this class:
    ///
    /// 1. The assembly resolver must be installed before the CLR is asked to load any RhinoCommon
    ///    type, so every member that touches RhinoCommon sits behind a NoInlining boundary and the
    ///    RhinoCore instance is held as <see cref="object"/> rather than in a typed field.
    ///
    /// 2. ArcGIS Pro is a .NET (Core) host. Rhino ships two builds of the managed assemblies:
    ///    "Rhino 8\System" (.NET Framework) and "Rhino 8\System\netcore" (.NET). They carry the
    ///    *same* assembly identity, so picking the wrong one fails late with a REF_DEF_MISMATCH
    ///    (0x80131040) rather than a helpful error. Rhino.Inside's own resolver serves the Framework
    ///    build, so a handler for the netcore build is registered ahead of it -- AssemblyResolve
    ///    handlers run in subscription order, so registering first is what makes it win.
    ///    Rhino.Inside's resolver is still initialised afterwards because it also sets up the native
    ///    search path used to find RhinoCore.dll and friends, which live in "System", not "netcore".
    /// </remarks>
    internal static class RhinoHost
    {
        internal const string WindowStyleEnvironmentVariable = "RHINOINSIDE_ARCGIS_WINDOW_STYLE";

        static object _core;
        static string _netcoreDirectory;

        // Resolver.Initialize throws if called twice, so a failed StartCore must not cause the
        // resolver setup to be repeated on the next attempt -- otherwise the retry reports
        // "Rhino.Inside is already initialized" and hides the failure that actually matters.
        static bool _resolverInitialized;

        internal static bool IsStarted => _core != null;

        /// <summary>Directory the managed Rhino assemblies were resolved from.</summary>
        internal static string RhinoSystemDirectory { get; private set; }

        /// <summary>Full path of the RhinoCommon actually loaded into this process.</summary>
        internal static string LoadedRhinoCommon { get; private set; }

        /// <summary>Rhino version reported by the running in-process core.</summary>
        internal static string RhinoVersion { get; private set; }

        /// <summary>The Rhino window style selected for the current start attempt.</summary>
        internal static string RhinoWindowStyle { get; private set; }

        /// <summary>
        /// Starts Rhino in-process. Safe to call repeatedly; only the first call does work.
        /// </summary>
        internal static void Start()
        {
            if (_core != null) return;

            if (!_resolverInitialized)
            {
                InitializeResolver();
                _resolverInitialized = true;
            }

            StartCore();
        }

        static void InitializeResolver()
        {
            var systemDirectory = FindRhinoSystemDirectory();
            _netcoreDirectory = Path.Combine(systemDirectory, "netcore");

            if (!File.Exists(Path.Combine(_netcoreDirectory, "RhinoCommon.dll")))
                throw new InvalidOperationException(
                    $"This host is .NET {Environment.Version}, which needs the netcore build of RhinoCommon, " +
                    $"but none was found at '{_netcoreDirectory}'.");

            // The native Rhino libraries (RhinoCore.dll and friends) sit in "System", one level up
            // from the managed netcore assemblies. Put that on the process PATH so native loads
            // resolve regardless of what the working directory happens to be.
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (path.IndexOf(systemDirectory, StringComparison.OrdinalIgnoreCase) < 0)
                Environment.SetEnvironmentVariable("PATH", systemDirectory + Path.PathSeparator + path);

            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromNetcore;

            // RhinoInside.Resolver.Initialize is deliberately NOT called. It installs a
            // DllImportResolver on RhinoCommon, and RhinoCore's constructor installs its own;
            // NativeLibrary.SetDllImportResolver permits only one per assembly, so calling both
            // fails with "A resolver is already set for the assembly". Managed resolution is covered
            // by the handler above, and RhinoCore sets up native loading itself.
            //
            // Loading RhinoCommon by path up front also means the CLR never has to raise
            // AssemblyResolve for it, so another add-in's handler cannot answer first with the
            // .NET Framework build.
            PreloadFromNetcore("RhinoCommon");

            // Rhino rasterises every toolbar icon through resvg_rhino.dll, and hosted in Pro it
            // never gets loaded -- Rhino then falls back to drawing command names as text. The file
            // loads perfectly well by full path, so the failure is in resolution by name: a .NET
            // host probes its own base directory and NATIVE_DLL_SEARCH_DIRECTORIES, both of which
            // point at Pro's bin rather than Rhino's System folder. Loading it here by full path
            // puts it in the process before Rhino asks, and Windows then satisfies the by-name
            // request from the already-loaded module.
            PreloadNativeLibrary(systemDirectory, "resvg_rhino");

            RhinoSystemDirectory = _netcoreDirectory;
        }

        /// <summary>
        /// Loads a Rhino assembly from the netcore folder by path, ahead of any other add-in's
        /// resolver getting the chance to answer for it.
        /// </summary>
        static void PreloadFromNetcore(string simpleName)
        {
            var path = Path.Combine(_netcoreDirectory, simpleName + ".dll");
            if (File.Exists(path))
                Assembly.LoadFrom(path);
        }

        /// <summary>
        /// Loads a native Rhino library by full path, so later by-name resolution finds it.
        /// </summary>
        static void PreloadNativeLibrary(string directory, string name)
        {
            var path = Path.Combine(directory, name + ".dll");
            if (!File.Exists(path)) return;

            try
            {
                System.Runtime.InteropServices.NativeLibrary.Load(path);
            }
            catch (Exception ex)
            {
                // Not fatal: Rhino still runs, it just draws text where icons should be.
                System.Diagnostics.Debug.WriteLine($"Rhino.Inside: could not preload {name}: {ex.Message}");
            }
        }

        static Assembly ResolveFromNetcore(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            var path = Path.Combine(_netcoreDirectory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        static string FindRhinoSystemDirectory()
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                       @"SOFTWARE\McNeel\Rhinoceros\8.0\Install"))
            {
                var coreDllPath = key?.GetValue("CoreDllPath") as string;
                if (!string.IsNullOrEmpty(coreDllPath) && File.Exists(coreDllPath))
                    return Path.GetDirectoryName(coreDllPath);
            }

            throw new InvalidOperationException(
                @"Could not locate Rhino 8. Expected HKLM\SOFTWARE\McNeel\Rhinoceros\8.0\Install\CoreDllPath.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void StartCore()
        {
            var windowStyle = GetConfiguredWindowStyle();
            RhinoWindowStyle = windowStyle.ToString();
            _core = new Rhino.Runtime.InProcess.RhinoCore(
                new[] { "/nosplash" },
                windowStyle);

            LoadedRhinoCommon = typeof(Rhino.RhinoApp).Assembly.Location;
            RhinoVersion = Rhino.RhinoApp.Version.ToString();

            Rhino.RhinoDoc.LayerTableEvent += (s, e) => LayersChanged?.Invoke(null, EventArgs.Empty);

            // A different document means different objects, links and anchor; whoever shows them
            // has to re-read. NewDocument and EndOpenDocument are where the new one is in place.
            Rhino.RhinoDoc.NewDocument += (s, e) => DocumentChanged?.Invoke(null, EventArgs.Empty);
            Rhino.RhinoDoc.EndOpenDocument += (s, e) => { if (!e.Merge && !e.Reference) DocumentChanged?.Invoke(null, EventArgs.Empty); };

            // Between these, ActiveDoc is already the incoming document while whoever holds the old
            // one's state (the link table) has not reloaded yet. Anything saved in that window lands
            // in the wrong file, so listeners are told to hold their writes.
            Rhino.RhinoDoc.BeginOpenDocument += (s, e) => { if (!e.Merge && !e.Reference) DocumentChanging?.Invoke(null, EventArgs.Empty); };
            Rhino.RhinoDoc.CloseDocument += (s, e) => DocumentChanging?.Invoke(null, EventArgs.Empty);

            // Selection and object-count changes, so the pane can show what is selected and keep
            // its document figures current. Hosted, Rhino's own Properties panel does not show a
            // selected object's user text (its idle-driven refresh never runs), so the pane does.
            Rhino.RhinoDoc.SelectObjects += (s, e) => SelectionChanged?.Invoke(null, EventArgs.Empty);
            Rhino.RhinoDoc.DeselectObjects += (s, e) => SelectionChanged?.Invoke(null, EventArgs.Empty);
            Rhino.RhinoDoc.DeselectAllObjects += (s, e) => SelectionChanged?.Invoke(null, EventArgs.Empty);
            Rhino.RhinoDoc.AddRhinoObject += (s, e) => ObjectsChanged?.Invoke(null, EventArgs.Empty);
            Rhino.RhinoDoc.DeleteRhinoObject += (s, e) => ObjectsChanged?.Invoke(null, EventArgs.Empty);
            Rhino.RhinoDoc.UserStringChanged += (s, e) => ObjectsChanged?.Invoke(null, EventArgs.Empty);

            Started?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Keeps the shipped Normal behavior by default while allowing one-factor native-crash
        /// isolation runs. Hidden still creates Rhino UI; NoWindow is the headless comparison and
        /// may not support workflows that require Rhino panels or other UI-dependent plug-ins.
        /// </summary>
        static Rhino.Runtime.InProcess.WindowStyle GetConfiguredWindowStyle()
        {
            var configured = Environment.GetEnvironmentVariable(WindowStyleEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured) ||
                string.Equals(configured, "normal", StringComparison.OrdinalIgnoreCase))
                return Rhino.Runtime.InProcess.WindowStyle.Normal;
            if (string.Equals(configured, "hidden", StringComparison.OrdinalIgnoreCase))
                return Rhino.Runtime.InProcess.WindowStyle.Hidden;
            if (string.Equals(configured, "no-window", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(configured, "nowindow", StringComparison.OrdinalIgnoreCase))
                return Rhino.Runtime.InProcess.WindowStyle.NoWindow;

            throw new InvalidOperationException(
                $"Unsupported {WindowStyleEnvironmentVariable} value '{configured}'. " +
                "Use normal, hidden, or no-window.");
        }

        /// <summary>Raised once Rhino is up, whichever caller started it (a button, or the test bridge).</summary>
        internal static event EventHandler Started;

        /// <summary>Raised on every selection change in Rhino, possibly many times in a burst; coalesce before reacting.</summary>
        internal static event EventHandler SelectionChanged;

        /// <summary>Raised when objects are added, deleted or get their user text changed; bursts during a pull.</summary>
        internal static event EventHandler ObjectsChanged;

        /// <summary>The first selected object and its user text, or null when nothing is selected.</summary>
        internal static SelectedObjectInfo GetSelectedObject() => _core == null ? null : GetSelectedObjectCore();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static SelectedObjectInfo GetSelectedObjectCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return null;

            Rhino.DocObjects.RhinoObject first = null;
            int count = 0;
            foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
            {
                if (obj == null) continue;
                if (first == null) first = obj;
                count++;
            }
            if (first == null) return null;

            var info = new SelectedObjectInfo
            {
                Id = first.Id,
                Count = count,
                Kind = first.ObjectType.ToString(),
                Layer = doc.Layers[first.Attributes.LayerIndex].FullPath
            };
            var strings = first.Attributes.GetUserStrings();
            foreach (string key in strings.AllKeys)
                if (key != null) info.UserStrings[key] = strings[key];
            return info;
        }

        /// <summary>
        /// Every object in the document, hidden ones and ones on switched-off layers included: what
        /// the sync tracks does not depend on what happens to be visible.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static IEnumerable<Rhino.DocObjects.RhinoObject> AllObjects(Rhino.RhinoDoc doc)
        {
            var settings = new Rhino.DocObjects.ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = true,
                ActiveObjects = true,
                ReferenceObjects = true,
                IncludeLights = false,
                IncludeGrips = false
            };
            return doc.Objects.GetObjectList(settings);
        }

        /// <summary>Raised when Rhino's layer table changes, so pickers can follow it.</summary>
        internal static event EventHandler LayersChanged;

        /// <summary>Raised when the active document is replaced by a new or opened one.</summary>
        internal static event EventHandler DocumentChanged;

        /// <summary>Raised when the active document is about to be closed or replaced.</summary>
        internal static event EventHandler DocumentChanging;

        // ------------------------------------------------------------------ lifecycle

        /// <summary>What to do with unsaved Rhino work when ArcGIS Pro closes or switches project.</summary>
        internal enum UnsavedWorkPolicy
        {
            /// <summary>Ask the person. The default, and the only choice in normal use.</summary>
            Prompt,

            /// <summary>Save without asking (to a scratch path when the document was never saved).</summary>
            Save,

            /// <summary>Discard without asking. For disposable test hosts.</summary>
            Discard
        }

        /// <summary>Test hosts set this so a scripted close never waits on a dialog.</summary>
        internal static UnsavedWorkPolicy UnsavedWork { get; set; } = UnsavedWorkPolicy.Prompt;

        /// <summary>
        /// The active document's saved path, or null for a document that was never saved. Unlike
        /// <see cref="GetActiveDocument"/>, never a display placeholder.
        /// </summary>
        internal static string GetActiveDocumentPath() => _core == null ? null : GetActiveDocumentPathCore();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string GetActiveDocumentPathCore()
        {
            var path = Rhino.RhinoDoc.ActiveDoc?.Path;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        /// <summary>
        /// Identifies the active document for this session, so state loaded from one document is
        /// never written into another. 0 when Rhino is not running or has no document.
        /// </summary>
        internal static uint GetActiveDocumentSerial() => _core == null ? 0 : GetActiveDocumentSerialCore();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static uint GetActiveDocumentSerialCore() => Rhino.RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0;

        /// <summary>
        /// Settles unsaved Rhino work before the document goes away: asks (or follows
        /// <see cref="UnsavedWork"/>) and saves when told to. Returns false when the person chose
        /// Cancel, in which case the caller must not close or replace the document.
        /// </summary>
        /// <remarks>
        /// The layer links, per-link profiles, sync baselines and the ledger of what each layer holds
        /// all live in the .3dm. Losing an unsaved document loses the pairing, which is what made
        /// links "forget" themselves between sessions.
        /// </remarks>
        internal static bool ResolveUnsavedWork(string reason)
        {
            if (_core == null) return true;
            return ResolveUnsavedWorkCore(reason);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ResolveUnsavedWorkCore(string reason)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null || !doc.Modified) return true;

            var policy = UnsavedWork;
            if (policy == UnsavedWorkPolicy.Prompt)
            {
                var name = string.IsNullOrWhiteSpace(doc.Name) ? "Untitled" : doc.Name;
                var answer = ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                    $"Save changes to the Rhino document '{name}' before {reason}?\n\n" +
                    "Layer links, sync baselines and field profiles are stored in the Rhino document; " +
                    "without saving, the next session cannot match what is already synced.",
                    "Rhino.Inside", System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Yes);
                if (answer == System.Windows.MessageBoxResult.Cancel) return false;
                policy = answer == System.Windows.MessageBoxResult.Yes ? UnsavedWorkPolicy.Save : UnsavedWorkPolicy.Discard;
            }

            if (policy == UnsavedWorkPolicy.Discard)
            {
                doc.Modified = false;
                return true;
            }

            var path = string.IsNullOrWhiteSpace(doc.Path) ? AskForSavePath(doc) : doc.Path;
            if (path == null) return false;
            SaveDocumentTo(doc, path);
            return true;
        }

        /// <summary>
        /// Saves the document to a path and makes that path the document's own, the way File >
        /// Save As does.
        /// </summary>
        /// <remarks>
        /// <see cref="Rhino.RhinoDoc.SaveAs(string)"/> only writes a copy: the document keeps its old
        /// path (or stays Untitled), so the project would remember the wrong file -- or none -- and
        /// the next session would reopen stale links. Overwriting the document's own file through
        /// SaveAs was also refused outright during Pro's shutdown. Its own path therefore goes
        /// through <see cref="Rhino.RhinoDoc.Save"/>, and a new path through the SaveAs command,
        /// which re-homes the document.
        /// </remarks>
        static void SaveDocumentTo(Rhino.RhinoDoc doc, string path)
        {
            var target = Path.GetFullPath(path);
            var current = string.IsNullOrWhiteSpace(doc.Path) ? null : Path.GetFullPath(doc.Path);
            if (current != null && string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                // Not RhinoDoc.Save: during ArcGIS Pro's shutdown it intermittently never returned
                // (about one close in three in the acceptance matrix), stuck inside Rhino. The
                // same write without the viewport preview image and with every prompt suppressed
                // does not wait on UI that Pro is tearing down; the file loses only its thumbnail.
                TestBridge.Log("save: WriteFile " + target);
                var options = new Rhino.FileIO.FileWriteOptions
                {
                    IncludePreviewImage = false,
                    SuppressDialogBoxes = true,
                    SuppressAllInput = true,
                    UpdateDocumentPath = true
                };
                if (!doc.WriteFile(target, options)) throw new InvalidOperationException($"Rhino could not save '{target}'.");
                doc.Modified = false;
                TestBridge.Log("save: done");
                return;
            }

            // The command asks before replacing a file; the caller already decided (a Save dialog
            // confirms overwrites), so the old file is set aside and restored if the save fails.
            string backup = null;
            if (File.Exists(target))
            {
                backup = target + ".rhinoinside-bak";
                File.Copy(target, backup, true);
                File.Delete(target);
            }
            try
            {
                TestBridge.Log("save: -SaveAs " + target);
                Rhino.RhinoApp.RunScript(doc.RuntimeSerialNumber, "_-SaveAs \"" + target + "\" _Enter", false);
                TestBridge.Log("save: -SaveAs returned");
                var now = string.IsNullOrWhiteSpace(doc.Path) ? null : Path.GetFullPath(doc.Path);
                if (!File.Exists(target) || !string.Equals(now, target, StringComparison.OrdinalIgnoreCase))
                {
                    // The command was unavailable (another command running): still write the file.
                    if (!doc.SaveAs(target)) throw new InvalidOperationException($"Rhino could not save '{target}'.");
                }
                if (backup != null) File.Delete(backup);
            }
            catch
            {
                if (backup != null && !File.Exists(target)) File.Move(backup, target);
                throw;
            }
        }

        static string AskForSavePath(Rhino.RhinoDoc doc)
        {
            if (UnsavedWork == UnsavedWorkPolicy.Save)
            {
                var folder = Path.Combine(Path.GetTempPath(), "RhinoInside-ArcGIS");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, $"Untitled-{DateTime.Now:yyyyMMdd-HHmmss}.3dm");
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Rhino document",
                Filter = "Rhino 3D Models (*.3dm)|*.3dm",
                DefaultExt = ".3dm",
                FileName = "Untitled.3dm",
                InitialDirectory = ArcGIS.Desktop.Core.Project.Current?.HomeFolderPath ?? string.Empty
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        /// <summary>
        /// Opens the .3dm a project was saved with. Does nothing when it is already the active
        /// document, and never discards unsaved work without asking.
        /// </summary>
        internal static bool OpenProjectDocument(string path)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return OpenProjectDocumentCore(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool OpenProjectDocumentCore(string path)
        {
            var current = Rhino.RhinoDoc.ActiveDoc;
            if (current != null && !string.IsNullOrWhiteSpace(current.Path) &&
                string.Equals(Path.GetFullPath(current.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                return true;

            if (!ResolveUnsavedWork("opening this project's Rhino document")) return false;
            if (current != null) current.Modified = false;
            var doc = Rhino.RhinoDoc.Open(path, out _);
            if (doc == null) throw new InvalidOperationException($"Rhino could not open '{path}'.");
            return true;
        }

        /// <summary>Saves the active document to its own path (asking for one if it has none).</summary>
        internal static string SaveActiveDocument()
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return SaveActiveDocumentCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string SaveActiveDocumentCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var path = string.IsNullOrWhiteSpace(doc.Path) ? AskForSavePath(doc) : doc.Path;
            if (path == null) return null;
            SaveDocumentTo(doc, path);
            return path;
        }

        /// <summary>
        /// Shuts the in-process Rhino down. Called once, as ArcGIS Pro unloads the add-in: a Rhino
        /// that is never disposed leaves its autosave file behind -- the "recover?" prompt the next
        /// time Rhino starts -- and tears down its native threads mid-flight when Pro exits.
        /// </summary>
        internal static void Shutdown()
        {
            if (_core == null) return;
            ShutdownCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ShutdownCore()
        {
            var core = _core as IDisposable;
            try
            {
                // Settled by the closing prompt already; a still-modified document here would make
                // Rhino raise its own save prompt during disposal, which nothing can answer.
                var doc = Rhino.RhinoDoc.ActiveDoc;
                if (doc != null) doc.Modified = false;
            }
            catch { }
            _core = null;
            core?.Dispose();
        }

        /// <summary>Writes the active document to a .3dm, for round-tripping what it stores.</summary>
        internal static void SaveDocumentAs(string path)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            SaveDocumentAsCore(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SaveDocumentAsCore(string path)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            SaveDocumentTo(doc, path);
        }

        /// <summary>Opens a .3dm as the active document, replacing the current one.</summary>
        internal static void OpenDocument(string path)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            OpenDocumentCore(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void OpenDocumentCore(string path)
        {
            // Replacing a modified document would raise Rhino's save prompt, which nothing scripted
            // answers; this is a test helper, and the caller saved first if it cared.
            var current = Rhino.RhinoDoc.ActiveDoc;
            if (current != null) current.Modified = false;
            var doc = Rhino.RhinoDoc.Open(path, out _);
            if (doc == null) throw new InvalidOperationException($"Rhino could not open '{path}'.");
        }

        /// <summary>Replaces the active document with an empty one.</summary>
        internal static void NewDocument()
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            NewDocumentCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void NewDocumentCore()
        {
            var current = Rhino.RhinoDoc.ActiveDoc;
            if (current != null) current.Modified = false;
            var doc = Rhino.RhinoDoc.Create(null);
            if (doc == null) throw new InvalidOperationException("Rhino could not create a new document.");
        }

        /// <summary>
        /// Sets the active document's model unit system. <see cref="NewDocument"/> creates a document
        /// from Rhino's factory default template (millimeters), so a script that draws in another
        /// unit needs to set it explicitly -- otherwise every coordinate is read at 1/1000th scale
        /// once the georeference transform converts it.
        /// </summary>
        internal static void SetModelUnits(string unitSystem)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            SetModelUnitsCore(unitSystem);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetModelUnitsCore(string unitSystem)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");
            // "Custom:<name>:<metres per unit>" sets a custom unit system.
            var custom = unitSystem?.Split(':');
            if (custom?.Length == 3 && custom[0].Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                doc.SetCustomUnitSystem(true, custom[1],
                    double.Parse(custom[2], System.Globalization.CultureInfo.InvariantCulture), false);
                return;
            }
            if (!Enum.TryParse(unitSystem, true, out Rhino.UnitSystem units))
                throw new ArgumentException($"Unknown unit system '{unitSystem}'.", nameof(unitSystem));
            doc.AdjustModelUnitSystem(units, scale: false);
        }

        /// <summary>
        /// Redraws Rhino's views. Objects added through the adapter do not trigger this themselves,
        /// so without it new geometry only appears once the user happens to touch a viewport.
        /// </summary>
        internal static void RedrawViews()
        {
            if (_core == null) return;
            RedrawViewsCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RedrawViewsCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return;

            doc.Views.Redraw();
            Rhino.RhinoApp.Wait();   // let Rhino service the redraw before we hand control back
        }

        /// <summary>
        /// Draws a line on a layer, for exercising the push direction without hand-modelling.
        /// </summary>
        internal static Guid AddTestLine(string layerName, double x1, double y1, double x2, double y2)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return AddTestLineCore(layerName, x1, y1, x2, y2);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Guid AddTestLineCore(string layerName, double x1, double y1, double x2, double y2)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");

            var attributes = new Rhino.DocObjects.ObjectAttributes();
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var existing = doc.Layers.FindName(layerName);
                attributes.LayerIndex = existing?.Index
                                        ?? doc.Layers.Add(layerName, System.Drawing.Color.Black);
            }

            var id = doc.Objects.AddLine(
                new Rhino.Geometry.Point3d(x1, y1, 0.0),
                new Rhino.Geometry.Point3d(x2, y2, 0.0),
                attributes);

            doc.Views.Redraw();
            return id;
        }

        /// <summary>
        /// Adds a point, polyline, closed planar polygon, or mesh box on a layer, for exercising
        /// the push direction with each geometry class the sync supports without hand-modelling.
        /// </summary>
        /// <param name="kind">"point", "line"/"polyline", "polygon", or "mesh".</param>
        /// <param name="points">
        /// Model-space vertices; a polygon is closed automatically. A mesh uses the vertices' planar
        /// bounds as a box footprint, extruded from z=0 up to the first point's Z (or 10 units).
        /// </param>
        /// <summary>
        /// Copies an object the way Copy/Paste or the Copy command does: same geometry, same layer,
        /// and every user string, sync identity included. For the test bridge.
        /// </summary>
        internal static Guid DuplicateTestObject(Guid id, double dx, double dy)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return DuplicateTestObjectCore(id, dx, dy);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Guid DuplicateTestObjectCore(Guid id, double dx, double dy)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var source = doc.Objects.FindId(id) ?? throw new InvalidOperationException($"Object {id} not found.");
            var geometry = source.Geometry.Duplicate();
            if (dx != 0 || dy != 0) geometry.Translate(new Rhino.Geometry.Vector3d(dx, dy, 0));
            var attributes = source.Attributes.Duplicate();
            attributes.ObjectId = Guid.Empty;
            var copy = doc.Objects.Add(geometry, attributes);
            if (copy == Guid.Empty) throw new InvalidOperationException("Rhino could not add the copy.");
            return copy;
        }

        /// <summary>Selects one object in Rhino, zooms the active view to it, and brings Rhino forward.</summary>
        internal static void SelectAndZoom(Guid id)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            SelectAndZoomCore(id);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SelectAndZoomCore(Guid id)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var obj = doc.Objects.FindId(id);
            if (obj == null) return;
            doc.Objects.UnselectAll();
            doc.Objects.Select(id);
            var box = obj.Geometry.GetBoundingBox(true);
            if (box.IsValid)
            {
                box.Inflate(Math.Max(box.Diagonal.Length * 0.5, 1.0));
                doc.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(box);
            }
            doc.Views.Redraw();
            ShowRhinoWindowCore();
        }

        /// <summary>Sets a layer's display colour. For scripted demonstrations.</summary>
        internal static void SetLayerColor(string layerName, int r, int g, int b)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            SetLayerColorCore(layerName, r, g, b);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetLayerColorCore(string layerName, int r, int g, int b)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var index = doc.Layers.FindByFullPath(layerName, -1);
            if (index < 0) index = doc.Layers.Add(layerName, System.Drawing.Color.FromArgb(r, g, b));
            var layer = doc.Layers[index];
            layer.Color = System.Drawing.Color.FromArgb(r, g, b);
            doc.Views.Redraw();
        }

        /// <summary>
        /// Stretches an object vertically about its base, the way a designer raises a massing
        /// block with Scale1D. Keeps the object's id and user text. For the test bridge.
        /// </summary>
        internal static void ScaleTestObjectZ(Guid id, double factor)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            ScaleTestObjectZCore(id, factor);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ScaleTestObjectZCore(Guid id, double factor)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var obj = doc.Objects.FindId(id) ?? throw new InvalidOperationException($"Object {id} not found.");
            var box = obj.Geometry.GetBoundingBox(true);
            var basePlane = new Rhino.Geometry.Plane(new Rhino.Geometry.Point3d(box.Center.X, box.Center.Y, box.Min.Z),
                Rhino.Geometry.Vector3d.ZAxis);
            var xform = Rhino.Geometry.Transform.Scale(basePlane, 1.0, 1.0, factor);
            if (doc.Objects.Transform(id, xform, true) == Guid.Empty)
                throw new InvalidOperationException($"Rhino could not scale object {id}.");
            doc.Views.Redraw();
        }

        /// <summary>
        /// Sets the earth anchor with the model rotated: <paramref name="northAngleDegrees"/> is
        /// the bearing, clockwise from true north, that the model's +Y axis points to.
        /// </summary>
        internal static void SetEarthAnchorRotated(double latitude, double longitude, double northAngleDegrees)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            SetEarthAnchorRotatedCore(latitude, longitude, northAngleDegrees);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetEarthAnchorRotatedCore(double latitude, double longitude, double northAngleDegrees)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var anchor = doc.EarthAnchorPoint;
            anchor.EarthBasepointLatitude = latitude;
            anchor.EarthBasepointLongitude = longitude;
            anchor.EarthBasepointElevation = 0.0;
            anchor.ModelBasePoint = Rhino.Geometry.Point3d.Origin;
            // True north, expressed in model coordinates: model +Y turned back by the bearing.
            var a = -northAngleDegrees * Math.PI / 180.0;
            anchor.ModelNorth = new Rhino.Geometry.Vector3d(-Math.Sin(a), Math.Cos(a), 0);
            anchor.ModelEast = new Rhino.Geometry.Vector3d(Math.Cos(a), Math.Sin(a), 0);
            doc.EarthAnchorPoint = anchor;
        }

        /// <summary>
        /// Renders a viewport to a PNG: "Top" for plan or "Perspective" for an aerial view from
        /// the south-east, shaded, zoomed to everything. For demonstrations.
        /// </summary>
        internal static string CaptureView(string path, string view, int width, int height)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return CaptureViewCore(path, view, width, height);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CaptureViewCore(string path, string viewName, int width, int height)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var view = doc.Views.Find(viewName ?? "Perspective", false)
                       ?? doc.Views.ActiveView ?? throw new InvalidOperationException("No Rhino view.");
            var viewport = view.ActiveViewport;
            var shaded = Rhino.Display.DisplayModeDescription.FindByName("Shaded");
            if (shaded != null) viewport.DisplayMode = shaded;
            var box = Rhino.Geometry.BoundingBox.Empty;
            foreach (var obj in doc.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject))
                if (obj.Visible) box.Union(obj.Geometry.GetBoundingBox(true));
            if (viewport.IsPerspectiveProjection && box.IsValid)
            {
                var target = box.Center;
                var reach = box.Diagonal.Length;
                viewport.SetCameraLocations(target, target + new Rhino.Geometry.Vector3d(0.55 * reach, -0.75 * reach, 0.55 * reach));
            }
            if (box.IsValid) viewport.ZoomBoundingBox(box);
            else viewport.ZoomExtents();
            view.Redraw();
            // Rhino.Display.ViewCapture throws a native SEHException inside Pro's process; the
            // ViewCaptureToFile command renders through the view itself and works. Maximised, the
            // viewport (and so the image) is as large as Rhino's window allows.
            doc.Views.ActiveView = view;
            if (!view.Maximized) view.Maximized = true;
            view.Redraw();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Delete(path);
            Rhino.RhinoApp.RunScript(doc.RuntimeSerialNumber,
                $"_-ViewCaptureToFile \"{path}\" _Width={width} _Height={height} _Scale=1 _DrawGrid=_No " +
                "_DrawWorldAxes=_No _DrawCPlaneAxes=_No _TransparentBackground=_No _Enter", false);
            if (!File.Exists(path)) throw new InvalidOperationException("Rhino did not write the view capture.");
            return path;
        }

        // ------------------------------------------------------------------ links on a layer

        /// <summary>
        /// The ArcGIS layers that objects on a Rhino layer are linked to (recorded layer name),
        /// with how many objects each; empty when nothing on the layer is linked.
        /// </summary>
        internal static IReadOnlyDictionary<string, int> GetLinkedLayers(string rhinoLayer) =>
            _core == null ? new Dictionary<string, int>() : GetLinkedLayersCore(rhinoLayer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyDictionary<string, int> GetLinkedLayersCore(string rhinoLayer)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in ObjectsOn(rhinoLayer))
            {
                var attrs = obj.Attributes;
                if (string.IsNullOrEmpty(attrs.GetUserString(RhinoArcGIS.Core.Identity.GisKeys.ArcGisObjectId)) &&
                    string.IsNullOrEmpty(attrs.GetUserString(RhinoArcGIS.Core.Identity.GisKeys.ArcGisGlobalId))) continue;
                var name = attrs.GetUserString(RhinoArcGIS.Core.Identity.GisKeys.ArcGisLayer) ?? "(unnamed)";
                result[name] = result.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            return result;
        }

        /// <summary>
        /// Removes every object's link on a Rhino layer -- identity, baselines, source -- keeping
        /// its geometry, its design attributes and the grouping of multipart pieces. The objects
        /// then read as new Rhino work to whatever layer they are synced with next.
        /// </summary>
        internal static int StripLinks(string rhinoLayer)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return StripLinksCore(rhinoLayer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int StripLinksCore(string rhinoLayer)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            int stripped = 0;
            foreach (var obj in ObjectsOn(rhinoLayer).ToList())
            {
                var attrs = obj.Attributes.Duplicate();
                if (RemoveLinkKeys(attrs)) { doc.Objects.ModifyAttributes(obj, attrs, true); stripped++; }
            }
            ClearLedger(doc, rhinoLayer);
            return stripped;
        }

        /// <summary>
        /// Copies every object on a Rhino layer to a new layer without links, for publishing as new
        /// features while the originals keep their own link. Multipart pieces stay grouped under
        /// fresh sync ids. Returns the number of objects copied.
        /// </summary>
        internal static int CopyLayerUnlinked(string sourceLayer, string targetLayer)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return CopyLayerUnlinkedCore(sourceLayer, targetLayer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int CopyLayerUnlinkedCore(string sourceLayer, string targetLayer)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            var source = doc.Layers.FindByFullPath(sourceLayer, -1);
            if (source < 0) throw new InvalidOperationException($"Rhino layer '{sourceLayer}' does not exist.");
            var target = doc.Layers.FindByFullPath(targetLayer, -1);
            if (target < 0) target = doc.Layers.Add(targetLayer, doc.Layers[source].Color);

            // Representatives first get fresh sync ids; their pieces are re-pointed at them.
            var objects = ObjectsOn(sourceLayer).ToList();
            var newIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in objects)
            {
                var id = obj.Attributes.GetUserString(RhinoArcGIS.Core.Identity.GisKeys.SyncGuid);
                if (!string.IsNullOrEmpty(id) && !newIds.ContainsKey(id)) newIds[id] = Guid.NewGuid().ToString();
            }
            int copied = 0;
            foreach (var obj in objects)
            {
                var attrs = obj.Attributes.Duplicate();
                attrs.ObjectId = Guid.Empty;
                attrs.LayerIndex = target;
                var syncId = attrs.GetUserString(RhinoArcGIS.Core.Identity.GisKeys.SyncGuid);
                var partOf = attrs.GetUserString(RhinoArcGIS.Core.Identity.GisKeys.PartOf);
                RemoveLinkKeys(attrs);
                if (!string.IsNullOrEmpty(syncId) && newIds.TryGetValue(syncId, out var fresh))
                    attrs.SetUserString(RhinoArcGIS.Core.Identity.GisKeys.SyncGuid, fresh);
                if (!string.IsNullOrEmpty(partOf) && newIds.TryGetValue(partOf, out var owner))
                    attrs.SetUserString(RhinoArcGIS.Core.Identity.GisKeys.PartOf, owner);
                if (doc.Objects.Add(obj.Geometry.Duplicate(), attrs) != Guid.Empty) copied++;
            }
            doc.Views.Redraw();
            return copied;
        }

        static IEnumerable<Rhino.DocObjects.RhinoObject> ObjectsOn(string rhinoLayer)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) yield break;
            var index = doc.Layers.FindByFullPath(rhinoLayer, -1);
            if (index < 0) yield break;
            foreach (var obj in AllObjects(doc))
                if (obj.Attributes.LayerIndex == index) yield return obj;
        }

        /// <summary>
        /// Drops the link keys (every gis.* key except the sync id and multipart grouping). Design
        /// attributes are not gis.* and stay.
        /// </summary>
        static bool RemoveLinkKeys(Rhino.DocObjects.ObjectAttributes attrs)
        {
            var keys = attrs.GetUserStrings();
            bool removed = false;
            foreach (string key in keys.AllKeys)
            {
                if (!RhinoArcGIS.Core.Identity.GisKeys.IsSystemKey(key)) continue;
                if (key == RhinoArcGIS.Core.Identity.GisKeys.SyncGuid || key == RhinoArcGIS.Core.Identity.GisKeys.PartOf ||
                    key == RhinoArcGIS.Core.Identity.GisKeys.PartIndex) continue;
                attrs.DeleteUserString(key);
                removed = true;
            }
            return removed;
        }

        static void ClearLedger(Rhino.RhinoDoc doc, string rhinoLayer) =>
            doc.Strings.Delete(RhinoArcGIS.Core.Sync.SyncLedger.DocumentKey(rhinoLayer));

        /// <summary>Deletes an object the way the Delete command does. For the test bridge.</summary>
        internal static bool DeleteTestObject(Guid id)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return DeleteTestObjectCore(id);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool DeleteTestObjectCore(Guid id)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document.");
            return doc.Objects.Delete(id, true);
        }

        internal static Guid AddTestObject(string layerName, string kind, IReadOnlyList<double[]> points)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return AddTestObjectCore(layerName, kind, points);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Guid AddTestObjectCore(string layerName, string kind, IReadOnlyList<double[]> points)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");
            if (points == null || points.Count == 0) throw new ArgumentException("No points given.", nameof(points));

            var attributes = new Rhino.DocObjects.ObjectAttributes();
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var existing = doc.Layers.FindByFullPath(layerName, -1);
                attributes.LayerIndex = existing >= 0
                    ? existing
                    : doc.Layers.Add(layerName, System.Drawing.Color.Black);
            }

            var pts = new List<Rhino.Geometry.Point3d>();
            foreach (var p in points)
                pts.Add(new Rhino.Geometry.Point3d(p[0], p[1], p.Length > 2 ? p[2] : 0.0));

            Guid id;
            switch ((kind ?? "line").ToLowerInvariant())
            {
                case "point":
                    id = doc.Objects.AddPoint(pts[0], attributes);
                    break;

                case "polygon":
                    if (pts.Count < 3) throw new ArgumentException("A polygon needs at least 3 points.");
                    if (pts[0].DistanceTo(pts[pts.Count - 1]) > Rhino.RhinoMath.ZeroTolerance) pts.Add(pts[0]);
                    var loop = new Rhino.Geometry.PolylineCurve(pts);
                    var breps = Rhino.Geometry.Brep.CreatePlanarBreps(loop, doc.ModelAbsoluteTolerance);
                    id = breps != null && breps.Length > 0
                        ? doc.Objects.AddBrep(breps[0], attributes)
                        : doc.Objects.AddCurve(loop, attributes);
                    break;

                case "mesh":
                    if (pts.Count < 3) throw new ArgumentException("A mesh footprint needs at least 3 points.");
                    double height = pts[0].Z > Rhino.RhinoMath.ZeroTolerance ? pts[0].Z : 10.0;
                    var box = new Rhino.Geometry.BoundingBox(
                        new Rhino.Geometry.Point3d(pts.Min(p => p.X), pts.Min(p => p.Y), 0.0),
                        new Rhino.Geometry.Point3d(pts.Max(p => p.X), pts.Max(p => p.Y), height));
                    var boxMesh = Rhino.Geometry.Mesh.CreateFromBox(box, 1, 1, 1);
                    id = doc.Objects.AddMesh(boxMesh, attributes);
                    break;

                default:
                    if (pts.Count < 2) throw new ArgumentException("A line needs at least 2 points.");
                    id = doc.Objects.AddPolyline(pts, attributes);
                    break;
            }

            doc.Views.Redraw();
            return id;
        }

        /// <summary>
        /// Moves an existing Rhino object while preserving its id and all gis.* baseline strings,
        /// so the live matrix can prove that an edit becomes an update rather than a new feature.
        /// </summary>
        internal static void MoveTestObject(Guid id, double x, double y, double z)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            MoveTestObjectCore(id, x, y, z);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void MoveTestObjectCore(Guid id, double x, double y, double z)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");

            var obj = doc.Objects.FindId(id);
            if (obj == null) throw new ArgumentException($"No Rhino object with id '{id}'.", nameof(id));

            var geometry = obj.Geometry.Duplicate();
            if (!geometry.Transform(Rhino.Geometry.Transform.Translation(x, y, z)))
                throw new InvalidOperationException($"Could not transform Rhino object '{id}'.");
            if (!doc.Objects.Replace(id, geometry, false))
                throw new InvalidOperationException($"Could not replace Rhino object '{id}' after transforming it.");

            doc.Views.Redraw();
        }

        /// <summary>
        /// Locks or unlocks one object without changing its identity, geometry, attributes, or
        /// baseline strings. The opt-in live harness uses this to prove failed replacements do not
        /// partially modify a protected Rhino object.
        /// </summary>
        internal static bool SetTestObjectLocked(Guid id, bool locked)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return SetTestObjectLockedCore(id, locked);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool SetTestObjectLockedCore(Guid id, bool locked)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");

            var obj = doc.Objects.FindId(id);
            if (obj == null) throw new ArgumentException($"No Rhino object with id '{id}'.", nameof(id));

            bool changed = locked
                ? doc.Objects.Lock(id, true)
                : doc.Objects.Unlock(id, true);
            var actual = doc.Objects.FindId(id)?.IsLocked ?? false;
            if (!changed && actual != locked)
                throw new InvalidOperationException($"Could not {(locked ? "lock" : "unlock")} Rhino object '{id}'.");

            doc.Views.Redraw();
            return actual;
        }

        /// <summary>
        /// Adds a stepped-massing ("setback") building: one mesh per tier, stacked in Z from the
        /// ground up and centered on (x, y), merged into a single mesh object.
        /// </summary>
        /// <param name="tiers">Each tier's footprint (width along X, depth along Y) and height, in
        /// document units, ordered from the ground up.</param>
        internal static Guid AddSetbackBuilding(string layerName, double x, double y,
            IReadOnlyList<(double width, double depth, double height)> tiers)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return AddSetbackBuildingCore(layerName, x, y, tiers);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Guid AddSetbackBuildingCore(string layerName, double x, double y,
            IReadOnlyList<(double width, double depth, double height)> tiers)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");
            if (tiers == null || tiers.Count == 0) throw new ArgumentException("A setback building needs at least one tier.", nameof(tiers));

            var attributes = new Rhino.DocObjects.ObjectAttributes();
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var existing = doc.Layers.FindByFullPath(layerName, -1);
                attributes.LayerIndex = existing >= 0
                    ? existing
                    : doc.Layers.Add(layerName, System.Drawing.Color.Black);
            }

            // Each tier is a closed box, so a bare stack puts tier N+1's floor exactly on tier N's
            // roof: two identical coplanar faces at the same Z, which z-fights and flickers in a
            // scene. Sinking every tier after the first slightly into the one below hides the seam
            // inside solid volume instead. Trivial at building scale, invisible at city scale.
            const double overlap = 0.1;

            var merged = new Rhino.Geometry.Mesh();
            double z = 0.0;
            for (int i = 0; i < tiers.Count; i++)
            {
                var tier = tiers[i];
                if (tier.width <= 0 || tier.depth <= 0 || tier.height <= 0)
                    throw new ArgumentException("Tier width, depth, and height must all be positive.");

                double bottom = i == 0 ? z : z - overlap;
                var box = new Rhino.Geometry.BoundingBox(
                    new Rhino.Geometry.Point3d(x - tier.width / 2.0, y - tier.depth / 2.0, bottom),
                    new Rhino.Geometry.Point3d(x + tier.width / 2.0, y + tier.depth / 2.0, z + tier.height));
                merged.Append(Rhino.Geometry.Mesh.CreateFromBox(box, 1, 1, 1));
                z += tier.height;
            }

            Guid id = doc.Objects.AddMesh(merged, attributes);
            doc.Views.Redraw();
            return id;
        }

        /// <summary>
        /// Adds a geometrically harder test object than a box stack: a curved frustum, a domed
        /// building (planar + curved faces via boolean union), a twisted tower (loft between
        /// rotated profiles, non-planar ruled faces), an intentionally open Brep, or a SubD -- to
        /// exercise the parts of the push pipeline a box mesh never touches (Brep tessellation,
        /// booleans, open-Brep and SubD classification).
        /// </summary>
        /// <param name="kind">"frustum", "dome", "twisted", "openbrep", or "subd".</param>
        /// <param name="p">Kind-specific numeric parameters (see each builder below).</param>
        internal static Guid AddComplexTestObject(string layerName, string kind, double x, double y,
            IReadOnlyDictionary<string, double> p)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return AddComplexTestObjectCore(layerName, kind, x, y, p);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Guid AddComplexTestObjectCore(string layerName, string kind, double x, double y,
            IReadOnlyDictionary<string, double> p)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");

            var attributes = new Rhino.DocObjects.ObjectAttributes();
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var existing = doc.Layers.FindByFullPath(layerName, -1);
                attributes.LayerIndex = existing >= 0
                    ? existing
                    : doc.Layers.Add(layerName, System.Drawing.Color.Black);
            }

            double V(string key, double fallback) => p != null && p.TryGetValue(key, out double v) ? v : fallback;
            double tol = doc.ModelAbsoluteTolerance;
            Guid id;

            switch ((kind ?? "").ToLowerInvariant())
            {
                case "frustum":
                    id = doc.Objects.AddBrep(BuildFrustum(x, y, V("baseRadius", 20), V("topRadius", 10), V("height", 60)), attributes);
                    break;

                case "dome":
                    id = doc.Objects.AddBrep(BuildDome(x, y, V("width", 40), V("depth", 40), V("height", 25), V("domeRadius", 22), tol), attributes);
                    break;

                case "twisted":
                    id = doc.Objects.AddBrep(BuildTwistedTower(x, y, V("width", 30), V("height", 70), V("twistDegrees", 90), V("levels", 6), tol), attributes);
                    break;

                case "openbrep":
                    id = doc.Objects.AddBrep(BuildOpenBox(x, y, V("width", 30), V("depth", 30), V("height", 40)), attributes);
                    break;

                case "subd":
                    var box = new Rhino.Geometry.BoundingBox(
                        new Rhino.Geometry.Point3d(x - V("width", 25) / 2.0, y - V("depth", 25) / 2.0, 0),
                        new Rhino.Geometry.Point3d(x + V("width", 25) / 2.0, y + V("depth", 25) / 2.0, V("height", 45)));
                    var subd = Rhino.Geometry.SubD.CreateFromMesh(Rhino.Geometry.Mesh.CreateFromBox(box, 1, 1, 1));
                    id = doc.Objects.AddSubD(subd, attributes);
                    break;

                default:
                    throw new ArgumentException($"Unknown complex kind '{kind}'.", nameof(kind));
            }

            if (id == Guid.Empty) throw new InvalidOperationException($"Failed to build '{kind}' geometry.");
            doc.Views.Redraw();
            return id;
        }

        /// <summary>A tapered cylindrical tower: a curved lateral surface loft between two circles, capped.</summary>
        static Rhino.Geometry.Brep BuildFrustum(double x, double y, double baseRadius, double topRadius, double height)
        {
            var basePlane = new Rhino.Geometry.Plane(new Rhino.Geometry.Point3d(x, y, 0), Rhino.Geometry.Vector3d.ZAxis);
            var topPlane = new Rhino.Geometry.Plane(new Rhino.Geometry.Point3d(x, y, height), Rhino.Geometry.Vector3d.ZAxis);
            var baseCrv = new Rhino.Geometry.Circle(basePlane, baseRadius).ToNurbsCurve();
            var topCrv = new Rhino.Geometry.Circle(topPlane, topRadius).ToNurbsCurve();

            var lofts = Rhino.Geometry.Brep.CreateFromLoft(
                new[] { baseCrv, topCrv }, Rhino.Geometry.Point3d.Unset, Rhino.Geometry.Point3d.Unset,
                Rhino.Geometry.LoftType.Straight, false);
            if (lofts == null || lofts.Length == 0) throw new InvalidOperationException("Loft failed.");

            Rhino.Geometry.Brep brep = lofts[0];
            Rhino.Geometry.Brep capped = brep.CapPlanarHoles(0.01);
            return capped ?? brep;
        }

        /// <summary>A box with a hemispherical dome on its roof, merged into one Brep by boolean union.</summary>
        static Rhino.Geometry.Brep BuildDome(double x, double y, double width, double depth, double height, double domeRadius, double tol)
        {
            var boxBrep = new Rhino.Geometry.Box(
                new Rhino.Geometry.Plane(new Rhino.Geometry.Point3d(x, y, 0), Rhino.Geometry.Vector3d.ZAxis),
                new Rhino.Geometry.Interval(-width / 2.0, width / 2.0),
                new Rhino.Geometry.Interval(-depth / 2.0, depth / 2.0),
                new Rhino.Geometry.Interval(0, height)).ToBrep();

            var sphereBrep = new Rhino.Geometry.Sphere(new Rhino.Geometry.Point3d(x, y, height), domeRadius).ToBrep();

            var union = Rhino.Geometry.Brep.CreateBooleanUnion(new[] { boxBrep, sphereBrep }, tol);
            if (union == null || union.Length == 0) throw new InvalidOperationException("Dome boolean union failed.");
            return union[0];
        }

        /// <summary>
        /// A tower lofted between square profiles that rotate and taper going up, giving non-planar
        /// ruled side faces rather than the flat vertical walls every other test shape has.
        /// </summary>
        static Rhino.Geometry.Brep BuildTwistedTower(double x, double y, double width, double height, double twistDegrees, double levels, double tol)
        {
            int n = Math.Max(2, (int)levels);
            var profiles = new List<Rhino.Geometry.Curve>();
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double angle = t * twistDegrees * Math.PI / 180.0;
                double half = width / 2.0 * (1.0 - 0.3 * t);
                double z = t * height;

                var corners = new[]
                {
                    new Rhino.Geometry.Point3d(-half, -half, 0),
                    new Rhino.Geometry.Point3d(half, -half, 0),
                    new Rhino.Geometry.Point3d(half, half, 0),
                    new Rhino.Geometry.Point3d(-half, half, 0),
                };
                var rotation = Rhino.Geometry.Transform.Rotation(angle, Rhino.Geometry.Vector3d.ZAxis, Rhino.Geometry.Point3d.Origin);
                var translation = Rhino.Geometry.Transform.Translation(x, y, z);
                var pts = new List<Rhino.Geometry.Point3d>();
                foreach (var c in corners)
                {
                    var pt = c;
                    pt.Transform(rotation);
                    pt.Transform(translation);
                    pts.Add(pt);
                }
                pts.Add(pts[0]);
                profiles.Add(new Rhino.Geometry.PolylineCurve(pts));
            }

            var lofts = Rhino.Geometry.Brep.CreateFromLoft(
                profiles, Rhino.Geometry.Point3d.Unset, Rhino.Geometry.Point3d.Unset,
                Rhino.Geometry.LoftType.Normal, false);
            if (lofts == null || lofts.Length == 0) throw new InvalidOperationException("Twisted-tower loft failed.");

            Rhino.Geometry.Brep brep = lofts[0];
            Rhino.Geometry.Brep capped = brep.CapPlanarHoles(tol > 0 ? tol : 0.01);
            return capped ?? brep;
        }

        /// <summary>A box missing its roof face: classifies as an open Brep (multipatch, but warned as non-watertight).</summary>
        static Rhino.Geometry.Brep BuildOpenBox(double x, double y, double width, double depth, double height)
        {
            var boxBrep = new Rhino.Geometry.Box(
                new Rhino.Geometry.Plane(new Rhino.Geometry.Point3d(x, y, 0), Rhino.Geometry.Vector3d.ZAxis),
                new Rhino.Geometry.Interval(-width / 2.0, width / 2.0),
                new Rhino.Geometry.Interval(-depth / 2.0, depth / 2.0),
                new Rhino.Geometry.Interval(0, height)).ToBrep();

            // Face index 5 of Box.ToBrep is the top (+Z) face; removing it leaves five walls/floor.
            boxBrep.Faces.RemoveAt(5);
            boxBrep.Compact();
            return boxBrep;
        }

        /// <summary>Sets a user string on an object, as a user editing attributes in Rhino would.</summary>
        internal static void SetUserString(Guid id, string key, string value)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            SetUserStringCore(id, key, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetUserStringCore(Guid id, string key, string value)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            var obj = doc?.Objects.FindId(id);
            if (obj == null) throw new InvalidOperationException($"Rhino object {id} not found.");

            obj.Attributes.SetUserString(key, value);
            obj.CommitChanges();
        }

        /// <summary>User strings of one object, or of every object on a layer keyed by object id.</summary>
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetUserStrings(Guid? id, string layerName)
        {
            if (_core == null) return new Dictionary<string, IReadOnlyDictionary<string, string>>();
            return GetUserStringsCore(id, layerName);
        }

        /// <summary>Geometry/topology facts used by the live 3D round-trip assertions.</summary>
        internal static RhinoGeometryInfo GetGeometryInfo(Guid id)
        {
            if (_core == null) return null;
            return GetGeometryInfoCore(id);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static RhinoGeometryInfo GetGeometryInfoCore(Guid id)
        {
            var obj = Rhino.RhinoDoc.ActiveDoc?.Objects.FindId(id);
            if (obj?.Geometry == null) return null;

            var bounds = obj.Geometry.GetBoundingBox(true);
            var info = new RhinoGeometryInfo
            {
                Kind = obj.Geometry.ObjectType.ToString(),
                XMin = bounds.IsValid ? bounds.Min.X : 0.0,
                XMax = bounds.IsValid ? bounds.Max.X : 0.0,
                YMin = bounds.IsValid ? bounds.Min.Y : 0.0,
                YMax = bounds.IsValid ? bounds.Max.Y : 0.0,
                ZMin = bounds.IsValid ? bounds.Min.Z : 0.0,
                ZMax = bounds.IsValid ? bounds.Max.Z : 0.0
            };

            if (obj.Geometry is Rhino.Geometry.Mesh mesh)
            {
                info.VertexCount = mesh.Vertices.Count;
                info.TopologyVertexCount = mesh.TopologyVertices.Count;
                info.FaceCount = mesh.Faces.Count;
                for (int i = 0; i < mesh.Faces.Count; i++)
                    info.FaceCornerCount += mesh.Faces[i].IsQuad ? 4 : 3;
                info.SharedVertices = info.VertexCount == info.TopologyVertexCount &&
                                      info.VertexCount < info.FaceCornerCount;
                info.IsClosed = mesh.IsClosed;
            }

            return info;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetUserStringsCore(Guid? id, string layerName)
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return result;

            int layerIndex = string.IsNullOrWhiteSpace(layerName) ? -1 : doc.Layers.FindByFullPath(layerName, -1);

            foreach (var obj in AllObjects(doc))
            {
                if (obj == null) continue;
                if (id.HasValue && obj.Id != id.Value) continue;
                if (layerIndex >= 0 && obj.Attributes.LayerIndex != layerIndex) continue;
                if (!id.HasValue && layerIndex < 0) continue;

                var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                var coll = obj.Attributes.GetUserStrings();
                foreach (string key in coll.AllKeys)
                    if (key != null) strings[key] = coll[key];

                result[obj.Id.ToString()] = strings;
            }

            return result;
        }

        /// <summary>
        /// Counts objects by geometry type and by group, for checking what a pull actually created.
        /// Restricted to one layer when <paramref name="layerName"/> is given.
        /// </summary>
        internal static IReadOnlyDictionary<string, int> GetObjectCounts(string layerName = null) =>
            _core == null ? new Dictionary<string, int>() : GetObjectCountsCore(layerName);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyDictionary<string, int> GetObjectCountsCore(string layerName)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return counts;

            int layerIndex = string.IsNullOrWhiteSpace(layerName) ? -1 : doc.Layers.FindByFullPath(layerName, -1);
            if (!string.IsNullOrWhiteSpace(layerName) && layerIndex < 0)
            {
                counts["(layer not found)"] = 1;
                return counts;
            }

            var grouped = 0;
            var total = 0;
            foreach (var obj in AllObjects(doc))
            {
                if (obj == null) continue;
                if (layerIndex >= 0 && obj.Attributes.LayerIndex != layerIndex) continue;
                total++;

                var kind = obj.Geometry?.ObjectType.ToString() ?? "Unknown";
                counts.TryGetValue(kind, out var n);
                counts[kind] = n + 1;

                if (obj.Attributes.GroupCount > 0) grouped++;
            }

            counts["(total)"] = total;
            counts["(grouped objects)"] = grouped;
            counts["(groups)"] = doc.Groups.Count;
            return counts;
        }

        /// <summary>
        /// Rhino layers that hold synced objects, with how many of their objects carry an identity.
        /// A layer with tracked objects is one a sync can pick up again after the file is reopened.
        /// </summary>
        internal static IReadOnlyList<TrackedLayerInfo> GetTrackedLayers() =>
            _core == null ? Array.Empty<TrackedLayerInfo>() : GetTrackedLayersCore();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyList<TrackedLayerInfo> GetTrackedLayersCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return Array.Empty<TrackedLayerInfo>();

            var byLayer = new Dictionary<int, TrackedLayerInfo>();
            foreach (var obj in AllObjects(doc))
            {
                if (obj == null) continue;
                int idx = obj.Attributes.LayerIndex;
                if (!byLayer.TryGetValue(idx, out var info))
                {
                    info = new TrackedLayerInfo { Layer = doc.Layers[idx].FullPath };
                    byLayer[idx] = info;
                }
                info.Total++;

                var strings = obj.Attributes.GetUserStrings();
                if (!string.IsNullOrEmpty(strings[RhinoArcGIS.Core.Identity.GisKeys.SyncGuid]))
                {
                    info.Tracked++;
                    var source = strings[RhinoArcGIS.Core.Identity.GisKeys.ArcGisLayer];
                    if (!string.IsNullOrEmpty(source)) info.ArcGisLayer = info.ArcGisLayer ?? source;
                }
                else if (!string.IsNullOrEmpty(strings[RhinoArcGIS.Core.Identity.GisKeys.PartOf]))
                {
                    info.Parts++;
                }
            }

            var result = new List<TrackedLayerInfo>();
            foreach (var info in byLayer.Values)
                if (info.Tracked > 0) result.Add(info);
            result.Sort((a, b) => string.CompareOrdinal(a.Layer, b.Layer));
            return result;
        }

        /// <summary>
        /// Document user text -- key/value pairs saved in the .3dm -- which is where the layer links
        /// live so they come back with the file.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> GetDocumentStrings(string prefix) =>
            _core == null ? new Dictionary<string, string>() : GetDocumentStringsCore(prefix);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyDictionary<string, string> GetDocumentStringsCore(string prefix)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return result;

            for (int i = 0; i < doc.Strings.Count; i++)
            {
                var key = doc.Strings.GetKey(i);
                if (key == null) continue;
                if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                result[key] = doc.Strings.GetValue(i);
            }
            return result;
        }

        /// <summary>Writes document user text; a null value deletes the key.</summary>
        internal static void SetDocumentStrings(IReadOnlyDictionary<string, string> values)
        {
            if (_core == null || values == null) return;
            SetDocumentStringsCore(values);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetDocumentStringsCore(IReadOnlyDictionary<string, string> values)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return;
            foreach (var kv in values)
            {
                if (kv.Value == null) doc.Strings.Delete(kv.Key);
                else doc.Strings.SetString(kv.Key, kv.Value);
            }
        }

        /// <summary>Names of the layers in Rhino's active document.</summary>
        internal static IReadOnlyList<string> GetLayerNames() =>
            _core == null ? Array.Empty<string>() : GetLayerNamesCore();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyList<string> GetLayerNamesCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return Array.Empty<string>();

            // Full paths, matching what the Rhino adapter reads and writes by. A nested layer's
            // bare name would not resolve through FindByFullPath, so a picker offering it would pair
            // the sync with a layer that reads as empty.
            var names = new List<string>();
            foreach (var layer in doc.Layers)
                if (!layer.IsDeleted)
                    names.Add(layer.FullPath);

            return names;
        }

        /// <summary>
        /// Captures only the geometry family and user text needed by the new-layer preview. Unlike
        /// <c>RhinoAdapter.ReadObjects</c>, this never meshes Breps, surfaces, or extrusions, so an
        /// ordinary pane refresh stays fast even on a detailed building layer. The create command
        /// separately re-plans from fully converted payloads before it writes schema.
        /// </summary>
        internal static IReadOnlyList<RhinoArcGIS.Core.Adapters.RhinoObjectSnapshot> GetLayerPlanSnapshots(
            string layerName) => _core == null
                ? Array.Empty<RhinoArcGIS.Core.Adapters.RhinoObjectSnapshot>()
                : GetLayerPlanSnapshotsCore(layerName);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyList<RhinoArcGIS.Core.Adapters.RhinoObjectSnapshot> GetLayerPlanSnapshotsCore(
            string layerName)
        {
            var result = new List<RhinoArcGIS.Core.Adapters.RhinoObjectSnapshot>();
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null || string.IsNullOrWhiteSpace(layerName)) return result;
            var layerIndex = doc.Layers.FindByFullPath(layerName, -1);
            if (layerIndex < 0) return result;

            foreach (var obj in AllObjects(doc))
            {
                if (obj?.Geometry == null || obj.Attributes.LayerIndex != layerIndex) continue;
                var descriptor = DescribeForLayerPlan(obj.Geometry);
                var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                var collection = obj.Attributes.GetUserStrings();
                foreach (string key in collection.AllKeys)
                    if (key != null) strings[key] = collection[key];

                result.Add(new RhinoArcGIS.Core.Adapters.RhinoObjectSnapshot
                {
                    RhinoGuid = obj.Id,
                    LayerName = layerName,
                    Descriptor = descriptor,
                    Geometry = MinimalPlanGeometry(descriptor),
                    UserStrings = strings
                });
            }
            return result;
        }

        static RhinoArcGIS.Core.Geometry.GeometryDescriptor DescribeForLayerPlan(Rhino.Geometry.GeometryBase geometry)
        {
            var descriptor = new RhinoArcGIS.Core.Geometry.GeometryDescriptor();
            switch (geometry)
            {
                case Rhino.Geometry.Point _:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Point;
                    break;
                case Rhino.Geometry.Curve curve:
                    descriptor.IsClosed = curve.IsClosed;
                    descriptor.IsPlanar = curve.IsPlanar();
                    descriptor.Kind = !curve.IsClosed
                        ? RhinoArcGIS.Core.Geometry.RhinoGeometryKind.OpenCurve
                        : descriptor.IsPlanar
                            ? RhinoArcGIS.Core.Geometry.RhinoGeometryKind.ClosedPlanarCurve
                            : RhinoArcGIS.Core.Geometry.RhinoGeometryKind.ClosedNonPlanarCurve;
                    break;
                case Rhino.Geometry.Extrusion _:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Extrusion;
                    descriptor.IsSolid = true;
                    break;
                case Rhino.Geometry.Brep brep:
                    descriptor.Kind = brep.IsSolid
                        ? RhinoArcGIS.Core.Geometry.RhinoGeometryKind.ClosedBrep
                        : RhinoArcGIS.Core.Geometry.RhinoGeometryKind.OpenBrep;
                    descriptor.IsSolid = brep.IsSolid;
                    descriptor.IsPlanar = brep.Faces.Count == 1 && brep.Faces[0].IsPlanar();
                    break;
                case Rhino.Geometry.Surface surface:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.PlanarSurface;
                    descriptor.IsPlanar = surface.IsPlanar();
                    break;
                case Rhino.Geometry.Mesh mesh:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Mesh;
                    descriptor.IsSolid = mesh.IsClosed;
                    descriptor.IsEmpty = mesh.Vertices.Count == 0 || mesh.Faces.Count == 0;
                    break;
                case Rhino.Geometry.SubD _:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.SubD;
                    break;
                case Rhino.Geometry.InstanceReferenceGeometry _:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.BlockInstance;
                    break;
                case Rhino.Geometry.Hatch _:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Hatch;
                    break;
                default:
                    descriptor.Kind = RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Unknown;
                    descriptor.IsEmpty = true;
                    break;
            }
            return descriptor;
        }

        static RhinoArcGIS.Core.Geometry.NeutralGeometry MinimalPlanGeometry(
            RhinoArcGIS.Core.Geometry.GeometryDescriptor descriptor)
        {
            if (descriptor == null || descriptor.IsEmpty) return null;
            var xyz = new RhinoArcGIS.Core.Spatial.Xyz(0, 0, 0);
            switch (descriptor.Kind)
            {
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Point:
                    return RhinoArcGIS.Core.Geometry.NeutralGeometry.Point(xyz);
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.OpenCurve:
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.ClosedNonPlanarCurve:
                    return RhinoArcGIS.Core.Geometry.NeutralGeometry.Polyline(new[]
                    {
                        xyz, new RhinoArcGIS.Core.Spatial.Xyz(1, 0, 0)
                    });
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.ClosedPlanarCurve:
                    return RhinoArcGIS.Core.Geometry.NeutralGeometry.Polygon(new[]
                    {
                        xyz, new RhinoArcGIS.Core.Spatial.Xyz(1, 0, 0),
                        new RhinoArcGIS.Core.Spatial.Xyz(0, 1, 0), xyz
                    });
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.PlanarSurface:
                    if (descriptor.IsPlanar)
                        return RhinoArcGIS.Core.Geometry.NeutralGeometry.Polygon(new[]
                        {
                            xyz, new RhinoArcGIS.Core.Spatial.Xyz(1, 0, 0),
                            new RhinoArcGIS.Core.Spatial.Xyz(0, 1, 0), xyz
                        });
                    goto case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Mesh;
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.ClosedBrep:
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.OpenBrep:
                    if (descriptor.IsPlanar)
                        return RhinoArcGIS.Core.Geometry.NeutralGeometry.Polygon(new[]
                        {
                            xyz, new RhinoArcGIS.Core.Spatial.Xyz(1, 0, 0),
                            new RhinoArcGIS.Core.Spatial.Xyz(0, 1, 0), xyz
                        });
                    goto case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Mesh;
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Extrusion:
                case RhinoArcGIS.Core.Geometry.RhinoGeometryKind.Mesh:
                    return new RhinoArcGIS.Core.Geometry.NeutralGeometry
                    {
                        Kind = RhinoArcGIS.Core.Geometry.NeutralGeometryKind.Multipatch,
                        Mesh = new RhinoArcGIS.Core.Geometry.NeutralMesh
                        {
                            Vertices = new List<RhinoArcGIS.Core.Spatial.Xyz> { xyz },
                            Faces = new List<int[]> { new[] { 0, 0, 0 } }
                        }
                    };
                default:
                    return null;
            }
        }

        /// <summary>
        /// Runs a Rhino command line in the in-process Rhino, e.g. to prove the core is live.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RunScript(string script)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            Rhino.RhinoApp.RunScript(script, false);
        }

        /// <summary>
        /// Snapshot of Rhino's active document, or null when Rhino is not running.
        /// </summary>
        /// <remarks>
        /// Returns plain data rather than a RhinoDoc so that callers -- the dockpane view model in
        /// particular -- never mention a RhinoCommon type. A view model that did would force the
        /// assembly to load when its own type is first touched, which can happen before Rhino has
        /// been started and the netcore resolver is in place.
        /// </remarks>
        internal static RhinoDocumentInfo GetActiveDocument()
        {
            return _core == null ? null : GetActiveDocumentCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static RhinoDocumentInfo GetActiveDocumentCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return null;

            return new RhinoDocumentInfo
            {
                Name = string.IsNullOrWhiteSpace(doc.Name) ? "Untitled" : doc.Name,
                Path = string.IsNullOrWhiteSpace(doc.Path) ? "(not saved)" : doc.Path,
                ObjectCount = doc.Objects.Count,
                LayerCount = doc.Layers.Count,
                ModelUnits = doc.ModelUnitSystem.ToString(),
                IsModified = doc.Modified
            };
        }

        /// <summary>
        /// Brings the Rhino window back to the front. Rhino shares the Pro process but has its own
        /// top-level window, so it is easy to lose behind Pro or leave minimised.
        /// </summary>
        internal static void ShowRhinoWindow()
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            ShowRhinoWindowCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ShowRhinoWindowCore()
        {
            // SetFocusToMainWindow alone does not restore a minimised window, so un-minimise it
            // first and then raise it.
            var handle = Rhino.RhinoApp.MainWindowHandle();
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }

            Rhino.RhinoApp.SetFocusToMainWindow();
        }

        const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>Reads the document's current earth anchor, or null when Rhino is not running.</summary>
        internal static EarthAnchorInfo GetEarthAnchor()
        {
            return _core == null ? null : GetEarthAnchorCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static EarthAnchorInfo GetEarthAnchorCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return null;

            var anchor = doc.EarthAnchorPoint;
            return new EarthAnchorInfo
            {
                IsSet = anchor.EarthLocationIsSet(),
                Latitude = anchor.EarthBasepointLatitude,
                Longitude = anchor.EarthBasepointLongitude,
                ModelBaseX = anchor.ModelBasePoint.X,
                ModelBaseY = anchor.ModelBasePoint.Y
            };
        }

        /// <summary>
        /// Anchors the document at a location, tying it to a chosen point in model space.
        /// </summary>
        /// <param name="modelBaseX">
        /// Model point that the location maps to. Zero puts the location at the Rhino origin, which
        /// is usually what is wanted; a non-zero point is useful when a model is already built
        /// around some other reference and should not be moved to suit the GIS data.
        /// </param>
        internal static void SetEarthAnchor(double latitude, double longitude,
                                            double modelBaseX, double modelBaseY)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            SetEarthAnchorCore(latitude, longitude, modelBaseX, modelBaseY);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetEarthAnchorCore(double latitude, double longitude,
                                       double modelBaseX, double modelBaseY)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) throw new InvalidOperationException("Rhino has no active document.");

            var anchor = doc.EarthAnchorPoint;
            anchor.EarthBasepointLatitude = latitude;
            anchor.EarthBasepointLongitude = longitude;
            anchor.EarthBasepointElevation = 0.0;
            anchor.ModelBasePoint = new Rhino.Geometry.Point3d(modelBaseX, modelBaseY, 0.0);
            anchor.ModelEast = Rhino.Geometry.Vector3d.XAxis;
            anchor.ModelNorth = Rhino.Geometry.Vector3d.YAxis;

            // The property hands back a copy; assigning it back is what persists it.
            doc.EarthAnchorPoint = anchor;
        }

        /// <summary>Clears the document's earth anchor so the next pull can re-anchor.</summary>
        internal static void ClearEarthAnchor()
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            ClearEarthAnchorCore();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ClearEarthAnchorCore()
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return;
            doc.EarthAnchorPoint = new Rhino.DocObjects.EarthAnchorPoint();
        }

        /// <summary>
        /// Creates Rhino geometry for the supplied features and returns what was made.
        /// </summary>
        internal static PullResult AddFeatures(IReadOnlyList<FeatureRecord> records)
        {
            if (_core == null) throw new InvalidOperationException("Rhino has not been started.");
            return AddFeaturesCore(records);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static PullResult AddFeaturesCore(IReadOnlyList<FeatureRecord> records)
        {
            var result = new PullResult { FeatureCount = records.Count };

            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                result.Warnings.Add("Rhino has no active document.");
                return result;
            }

            if (!TryGetEarthPlacement(doc, records, result, out var placement))
                return result;

            result.ModelUnits = doc.ModelUnitSystem.ToString();

            var layerIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var bounds = Rhino.Geometry.BoundingBox.Empty;

            foreach (var record in records)
            {
                var layerIndex = EnsureLayer(doc, record.LayerName, record.ColorArgb, layerIndexes);

                var attributes = new Rhino.DocObjects.ObjectAttributes { LayerIndex = layerIndex };
                foreach (var pair in record.Attributes)
                    attributes.SetUserString(pair.Key, pair.Value);

                foreach (var part in record.Parts)
                {
                    var points = ToPoints(part, placement);
                    var id = Guid.Empty;

                    switch (record.Kind)
                    {
                        case FeatureGeometryKind.Point:
                            if (points.Count >= 1)
                                id = doc.Objects.AddPoint(points[0], attributes);
                            break;

                        case FeatureGeometryKind.Polyline:
                            if (points.Count >= 2)
                                id = doc.Objects.AddPolyline(points, attributes);
                            break;

                        case FeatureGeometryKind.Polygon:
                            if (points.Count >= 3)
                            {
                                CloseRing(points);
                                id = doc.Objects.AddPolyline(points, attributes);
                            }
                            break;
                    }

                    if (id != Guid.Empty)
                    {
                        result.ObjectCount++;
                        foreach (var point in points) bounds.Union(point);
                    }
                    else result.SkippedCount++;
                }
            }

            if (bounds.IsValid)
            {
                result.ModelWidth = bounds.Max.X - bounds.Min.X;
                result.ModelHeight = bounds.Max.Y - bounds.Min.Y;
            }

            result.LayerCount = layerIndexes.Count;
            doc.Views.Redraw();
            return result;
        }

        /// <summary>
        /// Builds the transform that places incoming geographic coordinates into model space.
        /// </summary>
        /// <remarks>
        /// Rhino only exposes the forward direction, <c>GetModelToEarthTransform</c>, so the inverse
        /// is taken here. Routing through the document's earth anchor rather than a hand-rolled
        /// offset is what makes the result genuinely georeferenced: the same anchor drives Rhino's
        /// sun, terrain and Google Earth export, so the model agrees with them. It also takes the
        /// document unit system as a parameter, which is why nothing in this add-in converts feet to
        /// metres by hand -- Rhino does it, and the answer follows the document if its units change.
        ///
        /// When the document has no earth location yet, one is set at the centre of the incoming
        /// data. That keeps geometry near the model origin; anchoring elsewhere would place this
        /// data millions of units out and lose precision.
        /// </remarks>
        static bool TryGetEarthPlacement(Rhino.RhinoDoc doc, IReadOnlyList<FeatureRecord> records,
                                         PullResult result, out EarthPlacement placement)
        {
            placement = new EarthPlacement
            {
                EarthToModel = Rhino.Geometry.Transform.Identity,
                LatitudeIsX = true
            };

            var anchor = doc.EarthAnchorPoint;

            if (!anchor.EarthLocationIsSet())
            {
                if (!TryGetCentroid(records, out var latitude, out var longitude))
                {
                    result.Warnings.Add("No usable coordinates were found in these features.");
                    return false;
                }

                anchor.EarthBasepointLatitude = latitude;
                anchor.EarthBasepointLongitude = longitude;
                anchor.EarthBasepointElevation = 0.0;
                anchor.ModelBasePoint = Rhino.Geometry.Point3d.Origin;
                anchor.ModelEast = Rhino.Geometry.Vector3d.XAxis;
                anchor.ModelNorth = Rhino.Geometry.Vector3d.YAxis;

                // The property hands back a copy; assigning it back is what persists it.
                doc.EarthAnchorPoint = anchor;

                result.Warnings.Add($"Set the document earth anchor to {latitude:F6}, {longitude:F6}.");
            }

            result.AnchorLatitude = doc.EarthAnchorPoint.EarthBasepointLatitude;
            result.AnchorLongitude = doc.EarthAnchorPoint.EarthBasepointLongitude;

            var modelToEarth = doc.EarthAnchorPoint.GetModelToEarthTransform(doc.ModelUnitSystem);
            if (!modelToEarth.IsValid || !modelToEarth.TryGetInverse(out var earthToModel))
            {
                result.Warnings.Add("The document's earth anchor did not yield a usable transform.");
                return false;
            }

            placement.EarthToModel = earthToModel;
            placement.LatitudeIsX = DetectLatitudeIsX(modelToEarth);
            result.LatitudeIsX = placement.LatitudeIsX;

            return true;
        }

        /// <summary>
        /// Works out which component of the earth vector holds latitude, by asking the transform.
        /// </summary>
        /// <remarks>
        /// RhinoCommon documents this as "E.x = latitude, E.y = longitude", but taking that on trust
        /// produced geometry rotated about 90 degrees and squashed by roughly cos(latitude) -- the
        /// exact signature of the two axes being swapped. Rather than hard-code either convention
        /// and be wrong on some Rhino version, push a known model vector through the transform and
        /// see which earth component actually moves: stepping north in the model must change
        /// latitude, whichever slot latitude happens to live in.
        /// </remarks>
        static bool DetectLatitudeIsX(Rhino.Geometry.Transform modelToEarth)
        {
            var origin = modelToEarth * Rhino.Geometry.Point3d.Origin;
            var north = modelToEarth * new Rhino.Geometry.Point3d(0.0, 1000.0, 0.0);

            return Math.Abs(north.X - origin.X) >= Math.Abs(north.Y - origin.Y);
        }

        /// <summary>Centre of the incoming data's geographic extent, used to place a fresh anchor.</summary>
        static bool TryGetCentroid(IReadOnlyList<FeatureRecord> records, out double latitude, out double longitude)
        {
            double minLat = double.MaxValue, maxLat = double.MinValue;
            double minLon = double.MaxValue, maxLon = double.MinValue;
            var found = false;

            foreach (var record in records)
            {
                foreach (var part in record.Parts)
                {
                    for (int i = 0; i + 1 < part.Length; i += 3)
                    {
                        var lat = part[i];
                        var lon = part[i + 1];
                        if (double.IsNaN(lat) || double.IsNaN(lon)) continue;

                        if (lat < minLat) minLat = lat;
                        if (lat > maxLat) maxLat = lat;
                        if (lon < minLon) minLon = lon;
                        if (lon > maxLon) maxLon = lon;
                        found = true;
                    }
                }
            }

            latitude = found ? (minLat + maxLat) / 2.0 : 0.0;
            longitude = found ? (minLon + maxLon) / 2.0 : 0.0;
            return found;
        }

        static int EnsureLayer(Rhino.RhinoDoc doc, string name, int colorArgb,
                               Dictionary<string, int> cache)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "GIS";

            if (cache.TryGetValue(name, out var cached)) return cached;

            var existing = doc.Layers.FindName(name);
            var index = existing?.Index ?? doc.Layers.Add(name, System.Drawing.Color.FromArgb(colorArgb));

            if (index < 0) index = 0;
            cache[name] = index;
            return index;
        }

        static List<Rhino.Geometry.Point3d> ToPoints(double[] flat, EarthPlacement placement)
        {
            var points = new List<Rhino.Geometry.Point3d>(flat.Length / 3);
            for (int i = 0; i + 2 < flat.Length; i += 3)
            {
                // Records carry (latitude, longitude, elevation); feed them in whichever order this
                // Rhino's earth transform actually expects.
                var latitude = flat[i];
                var longitude = flat[i + 1];

                var point = placement.LatitudeIsX
                    ? new Rhino.Geometry.Point3d(latitude, longitude, flat[i + 2])
                    : new Rhino.Geometry.Point3d(longitude, latitude, flat[i + 2]);

                point.Transform(placement.EarthToModel);
                points.Add(point);
            }
            return points;
        }

        /// <summary>How to get geographic coordinates into this document's model space.</summary>
        struct EarthPlacement
        {
            internal Rhino.Geometry.Transform EarthToModel;
            internal bool LatitudeIsX;
        }

        static void CloseRing(List<Rhino.Geometry.Point3d> points)
        {
            if (points.Count > 0 && points[0].DistanceTo(points[points.Count - 1]) > Rhino.RhinoMath.ZeroTolerance)
                points.Add(points[0]);
        }
    }

    /// <summary>The selected Rhino object, RhinoCommon-free, for the pane.</summary>
    internal sealed class SelectedObjectInfo
    {
        internal Guid Id { get; set; }
        /// <summary>How many objects are selected; the rest of this describes the first.</summary>
        internal int Count { get; set; }
        internal string Kind { get; set; }
        internal string Layer { get; set; }
        internal Dictionary<string, string> UserStrings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>A Rhino layer that holds synced objects.</summary>
    internal sealed class TrackedLayerInfo
    {
        internal string Layer { get; set; }
        /// <summary>The ArcGIS layer the tracked objects came from, when they record one.</summary>
        internal string ArcGisLayer { get; set; }
        internal int Tracked { get; set; }
        internal int Parts { get; set; }
        internal int Total { get; set; }
    }

    /// <summary>RhinoCommon-free geometry summary returned through the opt-in test bridge.</summary>
    internal sealed class RhinoGeometryInfo
    {
        internal string Kind { get; set; }
        internal double XMin { get; set; }
        internal double XMax { get; set; }
        internal double YMin { get; set; }
        internal double YMax { get; set; }
        internal double ZMin { get; set; }
        internal double ZMax { get; set; }
        internal int VertexCount { get; set; }
        internal int TopologyVertexCount { get; set; }
        internal int FaceCount { get; set; }
        internal int FaceCornerCount { get; set; }
        internal bool SharedVertices { get; set; }
        internal bool IsClosed { get; set; }
    }

    /// <summary>
    /// RhinoCommon-free description of a Rhino document, safe to bind to.
    /// </summary>
    internal sealed class RhinoDocumentInfo
    {
        internal string Name { get; set; }
        internal string Path { get; set; }
        internal int ObjectCount { get; set; }
        internal int LayerCount { get; set; }
        internal string ModelUnits { get; set; }
        internal bool IsModified { get; set; }
    }
}
