using System;
using System.IO;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class InteropHarnessContractTests
    {
        [Fact]
        public void Every_coordinator_operation_uses_the_shared_execution_gate()
        {
            var source = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "Model", "SyncCoordinator.cs"));

            Assert.Equal(1, Count(source, "ExecutionGate.RunAsync("));
            Assert.Equal(7, Count(source, "RunGatedAsync(() =>"));
            Assert.Contains("ReviewAndApplyAsync", source);
            Assert.Contains("RunHostActionAsync", source);
            Assert.Contains("RunCore(arcgisLayer, rhino,", source);
        }

        [Fact]
        public void Arcgis_close_and_unload_are_blocked_while_the_shared_gate_is_busy()
        {
            var source = File.ReadAllText(RepoFile("src", "RhinoInside.ArcGISPro", "RhinoInsideAddin.cs"));

            Assert.Contains("if (!SyncCoordinator.TryBeginShutdown(args))", source);
            Assert.Contains("args.Cancel = true", source);
            Assert.Contains("if (args.Cancel) SyncCoordinator.CancelShutdown()", source);
            Assert.Contains("return SyncCoordinator.IsShutdownRequested", source);
        }

        [Fact]
        public void Link_reconciliation_sees_all_layer_objects_and_does_not_rebind_on_picker_reannouncement()
        {
            var adapter = File.ReadAllText(RepoFile("src", "RhinoArcGIS.ArcGIS", "ArcGISAdapter.cs"));
            var pane = File.ReadAllText(RepoFile("src", "RhinoInside.ArcGISPro", "View", "DockpaneViewModel.cs"));

            Assert.Contains("public IReadOnlyList<KeyValuePair<string, string>> GetLayerSources()", adapter);
            Assert.Contains("foreach (var layer in AllLayersInCandidateMaps())", adapter);
            Assert.Contains("LayerSourceBinding.Resolve(layerName, null, _layerSources)", pane);
            Assert.Contains("if (!labelChanged && !sourceLess) return", pane);
        }

        [Fact]
        public void Arcgis_target_resolution_rejects_same_source_layer_object_ambiguity()
        {
            var activeMap = File.ReadAllText(RepoFile("src", "RhinoArcGIS.ArcGIS", "ActiveMap.cs"));

            Assert.Contains("candidateLayers.Where(layer =>", activeMap);
            Assert.Contains("if (sourceMatches.Count > 1)", activeMap);
            Assert.Contains("if (nameMatches.Count > 1)", activeMap);
            Assert.DoesNotContain("distinctSources", activeMap);
        }

        [Fact]
        public void Live_harness_exposes_geometry_edit_and_object_lock_controls()
        {
            var bridge = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "Model", "TestBridge.cs"));
            var host = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "Model", "RhinoHost.cs"));
            var pane = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "View", "DockpaneViewModel.cs"));

            Assert.Contains("case \"editfeaturegeometry\":", bridge);
            Assert.Contains("EditFeatureGeometry(", bridge);
            Assert.Contains("edited.HasZ", bridge);
            Assert.Contains("Move(edited, dx, dy)", bridge);
            Assert.Contains("SelectSinglePart(original, partIndex)", bridge);
            Assert.Contains("polygon.Parts[partIndex]", bridge);
            Assert.Contains("polyline.Parts[partIndex]", bridge);
            Assert.Contains("case \"newlayer\":", bridge);
            Assert.Contains("case \"configureprofile\":", bridge);
            Assert.Contains("ConfigureProfileFieldForTestAsync", bridge);
            Assert.Contains("ProfileJsonFor(ArcGisLayer())", bridge);
            Assert.Contains("case \"droplayer\":", bridge);
            Assert.Contains("NewArcGisLayerAsync", pane);
            Assert.Contains("case \"setobjectlocked\":", bridge);
            Assert.Contains("RhinoHost.SetTestObjectLocked", bridge);
            Assert.Contains("info.XMin", bridge);
            Assert.Contains("SetTestObjectLockedCore", host);
            Assert.Contains("internal double XMin", host);
        }

        [Fact]
        public void Targeted_live_runs_stage_the_cross_cutting_support_fixtures()
        {
            var script = File.ReadAllText(RepoFile("tools", "e2e.ps1"));

            Assert.Contains("$fixtureNamesToStage", script);
            Assert.Contains("foreach ($f in $fixtureNamesToStage)", script);
            Assert.Contains("'Point_Multi_Mixed'", script);
            Assert.Contains("'Boundary_Multipart_Polygon'", script);
        }

        [Fact]
        public void Dockpane_help_is_persistent_accessible_and_current()
        {
            var view = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "View", "DockpaneView.xaml"));
            var codeBehind = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "View", "DockpaneView.xaml.cs"));
            var pane = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "View", "DockpaneViewModel.cs"));

            Assert.Contains("x:Key=\"InfoButton\"", view);
            Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", view);
            Assert.Contains("AutomationProperties.Name=\"Close context help\"", view);
            Assert.Contains("AutomationProperties.HelpText", view);
            Assert.Contains("CommandParameter=\"new-layer\"", view);
            Assert.Contains("CommandParameter=\"earth-anchor\"", view);
            Assert.Contains("CommandParameter=\"conflicts\"", view);
            Assert.Contains("ShowHelpCommand = new SimpleCommand<string>(ShowHelp)", pane);
            Assert.Contains("Preview is read-only", pane);
            Assert.Contains("Pull skips features the Rhino layer already holds", pane);
            Assert.DoesNotContain("creates duplicates", pane);
            Assert.Contains("SaveRhinoDocumentCommand", pane);
            Assert.Contains("MouseDoubleClick=\"ChangeReviewGrid_MouseDoubleClick\"", view);
            Assert.Contains("meshes and three-dimensional Breps, surfaces, and extrusions create Multipatch feature classes", pane);
            Assert.Contains("case \"profile\":", pane);
            Assert.Contains("A profile travels with the selected link in the Rhino document", pane);
            Assert.Contains("Content=\"Profile\"", view);
            Assert.Contains("CommandParameter=\"profile\"", view);
            Assert.Contains("AutomationProperties.Name=\"Author synchronization profile\"", view);
            Assert.Contains("AutomationProperties.Name=\"Profile field mappings\"", view);
            Assert.Contains("AutomationProperties.Name=\"Save synchronization profile\"", view);
            Assert.Contains("d:DesignWidth=\"480\"", view);
            Assert.Contains("Esri_DockPaneClientAreaBackgroundBrush", view);
            Assert.Contains("KeyboardNavigation.TabNavigation=\"Continue\"", view);
            Assert.Contains("<Setter Property=\"IsTabStop\" Value=\"False\"/>", view);
            Assert.Contains("<Trigger Property=\"IsKeyboardFocused\" Value=\"True\">", view);
            Assert.Contains("GroupName=\"SyncSubTabs\"", view);
            Assert.Contains("PreviewKeyDown=\"MainTabs_PreviewKeyDown\"", view);
            Assert.Contains("ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, SyncTopTab)", codeBehind);
            Assert.True(Count(view, "Click=\"SubTab_Click\"") >= 4);
            Assert.True(Count(view, "ToolTip=") >= 30);
            Assert.True(Count(view, "AutomationProperties.Name=") >= 30);

            Assert.DoesNotContain("there is no way yet to update an existing Rhino object's shape in place", view);
            Assert.DoesNotContain("no shared vertices between triangles", view);
            Assert.Contains("Apply replaces the matching tracked Rhino object's geometry in place", view);
            Assert.Contains("welded into shared topology", view);
        }

        [Fact]
        public void Live_matrix_covers_Rhino_first_multipatch_and_saved_profile_rules()
        {
            var script = File.ReadAllText(RepoFile("tools", "e2e.ps1"));

            Assert.Contains("=== new Multipatch layer from Rhino ===", script);
            Assert.Contains("$newMultipatch.target -eq 'Multipatch'", script);
            Assert.Contains("new Multipatch schema is Z-enabled with inferred fields and GlobalIDs", script);
            Assert.Contains("initial Multipatch push preserves 3D mesh topology and user text", script);
            Assert.Contains("Send-Bridge configureprofile", script);
            Assert.Contains("owner = 'RhinoOwned'", script);
            Assert.Contains("profile editor saves field ownership on the active link", script);
            Assert.Contains("saved Rhino-owned field rule writes the existing ArcGIS row and re-baselines", script);
            Assert.Contains("=== mixed edits on both sides ===", script);
            Assert.Contains("=== shapefile FID renumbering ===", script);
            Assert.Contains("=== close and reopen ===", script);
            Assert.Contains("restart shows no Project Recovery or Autosave Recovery prompt", script);
            Assert.Contains("Close-Pro -SaveProject", script);
        }

        [Fact]
        public void Link_store_persists_the_custom_profile_with_each_document_row()
        {
            var source = File.ReadAllText(RepoFile(
                "src", "RhinoInside.ArcGISPro", "Model", "LayerLink.cs"));

            Assert.Contains("public string ProfileJson", source);
            Assert.Contains("strings.TryGetValue($\"{Prefix}{i}.profile\"", source);
            Assert.Contains("values[$\"{Prefix}{n}.profile\"] = link.ProfileJson", source);
            Assert.Contains("Stored with the link in Rhino document text", source);
        }

        static int Count(string value, string fragment)
        {
            int count = 0;
            for (int index = 0; (index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0;
                 index += fragment.Length)
                count++;
            return count;
        }

        static string RepoFile(params string[] parts)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory != null;
                 directory = directory.Parent)
            {
                var candidateParts = new string[parts.Length + 1];
                candidateParts[0] = directory.FullName;
                Array.Copy(parts, 0, candidateParts, 1, parts.Length);
                var candidate = Path.Combine(candidateParts);
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException("Could not locate the Rhino.Inside-ArcGIS source tree from the test output directory.");
        }
    }
}
