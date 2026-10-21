using System;
using System.IO;
using System.Runtime.CompilerServices;
using ArcGIS.Desktop.Framework.Contracts;

namespace RhinoInside.ArcGISPro
{
    internal sealed class LaunchGrasshopper : Button
    {
        protected override void OnClick()
        {
            try
            {
                if (!RhinoHost.IsStarted) throw new InvalidOperationException("Launch Rhino first, then open Grasshopper.");
                GrasshopperBootstrap.Open();
            }
            catch (Exception ex) { ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(ex.GetBaseException().Message, "Grasshopper"); }
        }
    }

    internal sealed class LaunchGrasshopperCore : Button
    {
        protected override void OnClick()
        {
            try
            {
                if (!RhinoHost.IsStarted) throw new InvalidOperationException("Launch Rhino first.");
                GrasshopperBootstrap.Open(isolated: true);
            }
            catch (Exception ex) { ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(ex.GetBaseException().Message, "Grasshopper"); }
        }
    }

    internal static class GrasshopperBootstrap
    {
        static bool loaded;
        static bool isolatedSession;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Open(bool isolated = false)
        {
            var issue = RhinoArcGIS.Core.Grasshopper.RuntimeCompatibility.GrasshopperIssue(global::Rhino.RhinoApp.Version, Environment.Version.Major);
            if (issue != null) throw new InvalidOperationException(issue);
            TestBridge.Log("Grasshopper: loading Rhino plug-in");
            // Keep Grasshopper references out of the startup call frame until Rhino's resolver is active.
            var plugin = global::Rhino.RhinoApp.GetPlugInObject("Grasshopper");
            if (plugin == null) throw new InvalidOperationException("Grasshopper could not be loaded from the Rhino installation.");
            TestBridge.Log("Grasshopper: registering ArcGIS components");
            if (isolated || Environment.GetEnvironmentVariable("RHINOINSIDE_GRASSHOPPER_ISOLATED") == "1") ConfigureIsolatedLibraries();
            Register();
            TestBridge.Log("Grasshopper: opening editor");
            global::Rhino.RhinoApp.RunScript("_Grasshopper", false);
            TestBridge.Log("Grasshopper: editor command returned");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ConfigureIsolatedLibraries()
        {
            if (isolatedSession) return;
            if (global::Grasshopper.Instances.IsComponentServer)
                throw new InvalidOperationException("Restart ArcGIS Pro before switching Grasshopper library modes.");
            var serverType = typeof(global::Grasshopper.Kernel.GH_ComponentServer);
            var selectLibraries = serverType.GetMethod("SetExternalGHAs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(System.Collections.Generic.IEnumerable<string>) }, null)
                ?? throw new NotSupportedException("This Grasshopper build does not expose the isolated-host library selector.");
            var components = Path.Combine(Path.GetDirectoryName(typeof(global::Grasshopper.Instances).Assembly.Location), "Components");
            var own = Path.Combine(Path.GetDirectoryName(typeof(GrasshopperBootstrap).Assembly.Location), "RhinoArcGIS.Grasshopper.gha");
            var paths = new System.Collections.Generic.List<string>(Directory.GetFiles(components, "*.gha"));
            paths.Add(own);
            // Rhino's hosted-library selector is internal. Resolve its exact signature and fail closed
            // when unavailable; the selection affects this process, never the user's installed libraries.
            selectLibraries.Invoke(null, new object[] { paths });
            isolatedSession = true;
            TestBridge.Log("Grasshopper: isolated vendor + ArcGIS libraries selected");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Register()
        {
            if (loaded) return;
            var path = Path.Combine(Path.GetDirectoryName(typeof(GrasshopperBootstrap).Assembly.Location), "RhinoArcGIS.Grasshopper.gha");
            if (!File.Exists(path)) throw new FileNotFoundException("The Grasshopper component library is missing from this add-in.", path);
            var originalFolders = global::Grasshopper.Folders.CustomAssemblyFolders;
            try
            {
                var folders = new System.Collections.Generic.List<string>(originalFolders);
                folders.Add(Path.GetDirectoryName(path));
                global::Grasshopper.Folders.CustomAssemblyFolders = folders;
                // The ComponentServer getter loads libraries on first access. Calling LoadExternalFiles
                // again reparses Grasshopper.dll and raises duplicate built-in component-ID dialogs.
                var server = global::Grasshopper.Instances.ComponentServer;
                var libraryId = new Guid("911cd757-1bc0-423f-a226-a047ac120e53");
                loaded = server.FindAssembly(libraryId) != null;
                if (!loaded)
                {
                    var parse = server.GetType().GetMethod("ParseGHA", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, new[] { typeof(System.Reflection.Assembly), typeof(string) }, null)
                        ?? throw new NotSupportedException("Restart ArcGIS Pro and launch Grasshopper from the ribbon to register this library.");
                    parse.Invoke(server, new object[] { System.Reflection.Assembly.LoadFrom(path), path });
                    global::Grasshopper.Kernel.GH_ComponentServer.UpdateRibbonUI();
                    loaded = server.FindAssembly(libraryId) != null;
                }
            }
            finally { global::Grasshopper.Folders.CustomAssemblyFolders = originalFolders; }
            if (!loaded) throw new InvalidOperationException("Grasshopper did not register the ArcGIS component library.");
        }
    }
}
