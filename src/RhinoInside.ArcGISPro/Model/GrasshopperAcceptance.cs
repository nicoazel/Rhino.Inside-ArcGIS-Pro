using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Grasshopper;
using RhinoArcGIS.Core.Spatial;

namespace RhinoInside.ArcGISPro
{
    /// <summary>Opt-in disposable host checks. Uses the same services and actual GHA as the product.</summary>
    internal static class GrasshopperAcceptance
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object Run(JObject request, Dispatcher ui)
        {
            T OnUi<T>(Func<T> f) => ui.Invoke(f);
            var checks = new List<string>();
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Grasshopper acceptance: " + name); checks.Add(name); TestBridge.Log("GH accepted: " + name); }
            OnUi(() => { GrasshopperBootstrap.Open(); return true; });
            Check(OnUi(() => global::Grasshopper.Instances.ComponentServer.FindAssembly(new Guid("911cd757-1bc0-423f-a226-a047ac120e53")) != null), "GHA registered in embedded host");
            var emitted = OnUi(() => Enumerable.Range(1, 15).Select(i => global::Grasshopper.Instances.ComponentServer.EmitObject(
                new Guid("f278840b-70c3-4d36-beba-f0515422f" + i.ToString("x3")))).ToList());
            Check(emitted.All(c => c != null), "all 15 components instantiate");
            if (request["examplePath"]?.Value<string>() is string examplePath)
                Check(OnUi(() => GrasshopperExample.Write(examplePath)), "runnable example definition saved");
            var definition = OnUi(() => {
                var doc = new global::Grasshopper.Kernel.GH_Document();
                global::Grasshopper.Instances.DocumentServer.AddDocument(doc);
                global::Grasshopper.Instances.ActiveCanvas.Document = doc;
                doc.AddObject(emitted[0], false);
                doc.NewSolution(false);
                return doc;
            });
            var mapComponent = (global::Grasshopper.Kernel.IGH_Component)emitted[0];
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !OnUi(() => mapComponent.Params.Output[0].VolatileDataCount > 0))
                System.Threading.Thread.Sleep(100);
            Check(OnUi(() => mapComponent.Params.Output[0].VolatileDataCount > 0), "actual asynchronous Grasshopper map solution completes");
            OnUi(() => { global::Grasshopper.Instances.DocumentServer.RemoveDocument(definition); return true; });
            var host = MapDesignConnection.RequireHost();
            var map = host.Maps().First();
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            var layer = host.Create(map.Uri, "GH_Acceptance_" + suffix, null, GeometryTarget.PointZ,
                new[] { new FieldDefinition { Name = "height_m", Type = FieldType.Double }, new FieldDefinition { Name = "label", Type = FieldType.Text, Length = 64 } });
            Check(host.Layers(map.Uri).Any(l => l.Reference.Matches(layer)), "created layer returns stable binding");
            var created = host.Append(layer, new[] { new FeatureDraft { Key = "one", Geometry = NeutralGeometry.Point(new Xyz(10, 20, 3)),
                Changes = new Dictionary<string, string> { ["height_m"] = "12.5", ["label"] = null } } });
            Check(created.Count == 1 && created[0].GlobalId.HasValue, "append returns GlobalID");
            Check(Math.Abs(created[0].Geometry.Points[0].Z - 3) < 0.001 && Math.Abs(created[0].Geometry.Points[0].X - 10) < 0.01, "model coordinates round trip");
            var read = host.Read(layer, false, 10);
            Check(read.Features.Count == 1 && !read.Truncated, "read snapshot without baking");
            Check(read.Features[0].Attributes["label"] == null, "database null preserved");
            var edit = read.Features[0].Copy(); edit.Changes["height_m"] = "24.5";
            var plan = host.Plan(layer, new[] { edit });
            Check(plan.CanCommit && plan.Patches[0].Values.Geometry == null, "attribute-only plan is read-only and omits geometry");
            Check(host.Read(layer, false, 10).Features[0].Attributes["height_m"] == "12.5", "planning makes no edits");
            host.Commit(plan);
            Check(host.Read(layer, false, 10).Features[0].Attributes["height_m"] == "24.5", "commit updates existing row");
            bool held = false; try { host.Commit(plan); } catch { held = true; }
            Check(held, "stale plan cannot be replayed");
            var current = host.Read(layer, false, 10).Features[0];
            current.ReplaceGeometry = true; current.Geometry = NeutralGeometry.Point(new Xyz(15, 25, 5));
            host.Commit(host.Plan(layer, new[] { current }));
            Check(Math.Abs(host.Read(layer, false, 10).Features[0].Geometry.Points[0].X - 15) < 0.01, "geometry update retains feature identity");
            var owner = Guid.NewGuid(); host.Preview(owner, map.Uri, new[] { NeutralGeometry.Point(new Xyz(10, 20, 3)) }, unchecked((int)0xAA00FFFF), 2);
            host.ClearPreview(owner); Check(true, "temporary point preview and clear execute");
            var box = new NeutralGeometry { Kind = NeutralGeometryKind.Multipatch, Mesh = new NeutralMesh {
                Vertices = new List<Xyz> { new(0,0,0), new(10,0,0), new(0,10,0), new(0,0,10) },
                Faces = new List<int[]> { new[] {0,2,1}, new[] {0,1,3}, new[] {1,2,3}, new[] {2,0,3} }, IsClosed = true } };
            host.Preview(owner, map.Uri, new[] { box }, unchecked((int)0xAA00FFFF), 2);
            host.ClearPreview(owner); Check(true, "temporary multipatch preview and clear execute");
            global::ArcGIS.Desktop.Core.Project.Current.SaveEditsAsync().GetAwaiter().GetResult();
            var meshLayer = host.Create(map.Uri, "GH_Mesh_" + suffix, null, GeometryTarget.Multipatch, Array.Empty<FieldDefinition>());
            host.Append(meshLayer, new[] { new FeatureDraft { Key = "mesh", Geometry = box } });
            Check(host.Read(meshLayer, false, 10).Features[0].Geometry.Mesh.Faces.Count > 0, "mesh publication and readback");
            return new { checks, pointLayer = layer, meshLayer, note = "SDK execution evidence; shaded appearance and direct input remain separate checks." };
        }
    }
}
