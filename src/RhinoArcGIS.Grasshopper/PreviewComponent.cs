using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Grasshopper;
using RhinoArcGIS.Rhino;

namespace RhinoArcGIS.Grasshopper
{
    public sealed class MapPreviewComponent : AsyncMapComponent
    {
        readonly SemaphoreSlim gate = new(1, 1);
        int revision;
        public MapPreviewComponent() : base("Map Preview", "Preview", "Temporary geometry in every open view of the chosen map/scene. Geometry uses absolute elevations.", "Preview")
        { ObjectChanged += (_, _) => { if (Hidden || Locked) Clear(); }; }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f00f");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Map", "M", "Preview map or scene", GH_ParamAccess.item); p.AddGeometryParameter("Geometry", "G", "Model geometry", GH_ParamAccess.list); p.AddColourParameter("Color", "C", "Preview color and alpha", GH_ParamAccess.item, Color.FromArgb(160, 30, 180, 220)); p.AddNumberParameter("Width", "W", "Line width in points", GH_ParamAccess.item, 2); p.AddBooleanParameter("Enabled", "E", "Show preview", GH_ParamAccess.item, true); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddTextParameter("Status", "S", "Preview result", GH_ParamAccess.item);
        protected override (object, Func<object>) Capture(IGH_DataAccess da)
        {
            var map = Get<MapDescriptor>(da, 0); var geometry = new List<IGH_GeometricGoo>(); da.GetDataList(1, geometry);
            Color color = Color.Cyan; double width = 2; bool enabled = true; da.GetData(2, ref color); da.GetData(3, ref width); da.GetData(4, ref enabled);
            if (!enabled || Hidden || Locked || map == null) { Clear(); return (null, null); }
            if (!double.IsFinite(width) || width <= 0 || width > 100) throw new InvalidOperationException("Width must be between 0 and 100 points.");
            var shapes = geometry.Select(g => GrasshopperGeometry.Read(GH_Convert.ToGeometryBase(g))).ToList();
            if (shapes.Any(g => g == null)) throw new InvalidOperationException("Unsupported preview geometry.");
            int stamp = revision + 1;
            return (new { map.Uri, shapes, Color = color.ToArgb(), width }, () => {
                gate.Wait();
                try { if (stamp != revision) return "Superseded"; MapDesignConnection.RequireHost().Preview(InstanceGuid, map.Uri, shapes, color.ToArgb(), width); return $"{shapes.Count} temporary geometries"; }
                finally { gate.Release(); }
            });
        }
        protected override void Show(IGH_DataAccess da, object result) => da.SetData(0, result);
        protected override void RequestChanged() => Interlocked.Increment(ref revision);
        void Clear()
        {
            InvalidateRequest();
            int clearAt = Interlocked.Increment(ref revision);
            _ = Task.Run(() => { gate.Wait(); try { if (clearAt == revision) MapDesignConnection.Host?.ClearPreview(InstanceGuid); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("ArcGIS preview cleanup: " + ex.Message); }
                finally { gate.Release(); } });
        }
        public override void RemovedFromDocument(GH_Document document) { Clear(); base.RemovedFromDocument(document); }
        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        { Clear(); base.DocumentContextChanged(document, context); }
    }
}
