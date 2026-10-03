using System;
using System.IO;
using System.Threading.Tasks;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace RhinoInside.ArcGISPro
{
    internal class RhinoInsideAddin : Module
    {
        private static RhinoInsideAddin _this = null;
        const string RhinoDocumentPathKey = "RhinoInside.RhinoDocumentPath";

        /// <summary>
        /// The Rhino document path this project was saved with, read back on load. Rhino is usually
        /// not running yet at that point (Launch Rhino is a user action), so it is just held here
        /// until <see cref="RhinoHost.Started"/> fires and it can actually be opened.
        /// </summary>
        string _pendingRhinoDocumentPath;

        /// <summary>
        /// Retrieve the singleton instance to this module here
        /// </summary>
        public static RhinoInsideAddin Current => _this ??= (RhinoInsideAddin)FrameworkApplication.FindModule("RhinoInsideAddin_module");

        #region Overrides

        /// <summary>
        /// Called by the Framework when the module is first loaded.
        /// </summary>
        protected override bool Initialize()
        {
            SyncCoordinator.Initialize();
            McpControlBridge.StartIfEnabled();

            // Opt-in, and inert unless RHINOINSIDE_TESTBRIDGE names a directory.
            TestBridge.StartIfEnabled();

            // A hard crash in the in-process Rhino takes ArcGIS Pro down with it and leaves nothing
            // behind, so record what we can before the process dies.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                TestBridge.Log("UNHANDLED: " + (e.ExceptionObject?.ToString() ?? "(none)"));

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
                TestBridge.Log("UNOBSERVED TASK: " + e.Exception);

            // The link table already travels with the .3dm (RhinoHost/LayerLinkStore save it into
            // the document's own user text); what was missing was the project remembering *which*
            // .3dm goes with it, so reopening the project can reopen the right file and its links
            // come back too, instead of starting from a blank Rhino document every time.
            RhinoHost.Started += OnRhinoStarted;

            // Unsaved Rhino work is settled while Pro can still be told to stay open; Rhino itself
            // is shut down in Uninitialize, once Pro is really going.
            ArcGIS.Desktop.Framework.Events.ApplicationClosingEvent.Subscribe(OnApplicationClosing);

            // The sync targets the last active map when a table or layout pane has focus; that
            // memory must follow map views and must not outlive the project.
            ArcGIS.Desktop.Mapping.Events.ActiveMapViewChangedEvent.Subscribe(e =>
                RhinoArcGIS.ArcGIS.ActiveMap.Remember(e.IncomingView?.Map));
            ArcGIS.Desktop.Core.Events.ProjectClosedEvent.Subscribe(_ => RhinoArcGIS.ArcGIS.ActiveMap.Forget());

            return base.Initialize();
        }

        Task OnApplicationClosing(System.ComponentModel.CancelEventArgs args)
        {
            if (!SyncCoordinator.TryBeginShutdown(args))
            {
                args.Cancel = true;
                TestBridge.Log("closing: cancelled while synchronization or host work is active, or shutdown is already reserved");
                return Task.CompletedTask;
            }

            try
            {
                TestBridge.Log("closing: resolving unsaved Rhino work");
                if (!RhinoHost.ResolveUnsavedWork("closing ArcGIS Pro")) args.Cancel = true;
                TestBridge.Log("closing: resolved" + (args.Cancel ? " (close cancelled)" : ""));
            }
            catch (Exception ex)
            {
                TestBridge.Log("Could not save the Rhino document on close: " + ex);
                // A scripted host chose its policy up front and has nobody to answer a dialog.
                if (RhinoHost.UnsavedWork != RhinoHost.UnsavedWorkPolicy.Prompt)
                {
                    args.Cancel = true;
                    return Task.CompletedTask;
                }
                var answer = ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                    "The Rhino document could not be saved:\n\n" + ex.Message + "\n\nClose ArcGIS Pro anyway?",
                    "Rhino.Inside", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Error);
                if (answer != System.Windows.MessageBoxResult.Yes) args.Cancel = true;
            }
            finally
            {
                // Keep the lease through Uninitialize after an accepted close. If our own close
                // path cancels, allow the user to continue using the pane.
                if (args.Cancel) SyncCoordinator.CancelShutdown();
            }
            return Task.CompletedTask;
        }

        /// <summary>Called by the Framework as ArcGIS Pro shuts down, after closing was confirmed.</summary>
        protected override void Uninitialize()
        {
            McpControlBridge.Stop();
            try { RhinoHost.Shutdown(); }
            catch (Exception ex) { TestBridge.Log("Rhino shutdown failed: " + ex); }
            TestBridge.Log("add-in uninitialized");
            base.Uninitialize();
        }

        /// <summary>
        /// Called by Framework when ArcGIS Pro is closing
        /// </summary>
        /// <returns>False to prevent Pro from closing, otherwise True</returns>
        protected override bool CanUnload()
        {
            // Uninitialize shuts Rhino down synchronously; do not enter it while a sync or
            // approved host action is still using the document.
            if (SyncCoordinator.IsBusy)
            {
                SyncCoordinator.CancelShutdown();
                return false;
            }
            return SyncCoordinator.IsShutdownRequested;
        }

        protected override Task OnReadSettingsAsync(ModuleSettingsReader settings)
        {
            _pendingRhinoDocumentPath = settings?.Get(RhinoDocumentPathKey) as string;
            if (RhinoHost.IsStarted) OnRhinoStarted(null, EventArgs.Empty);
            return Task.FromResult(true);
        }

        protected override Task OnWriteSettingsAsync(ModuleSettingsWriter settings)
        {
            // Only a real file: an unsaved document has nothing to reopen.
            string path = RhinoHost.GetActiveDocumentPath();
            if (!string.IsNullOrEmpty(path)) settings.Add(RhinoDocumentPathKey, path);
            return Task.FromResult(true);
        }

        /// <summary>Opens the project's saved Rhino document once Rhino is actually up.</summary>
        void OnRhinoStarted(object sender, EventArgs e)
        {
            string path = _pendingRhinoDocumentPath;
            _pendingRhinoDocumentPath = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try { RhinoHost.OpenProjectDocument(path); }
            catch (Exception ex) { TestBridge.Log($"Could not reopen saved Rhino document '{path}': {ex}"); }
        }

        #endregion Overrides

    }
}
