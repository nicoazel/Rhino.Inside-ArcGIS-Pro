using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Grasshopper;

namespace RhinoArcGIS.Grasshopper
{
    public sealed class PlanUpdatesComponent : AsyncMapComponent
    {
        public PlanUpdatesComponent() : base("Plan Updates", "Plan", "Review detached feature edits against the live target. This component writes nothing.", "Write") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f00b");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Layer", "L", "Target layer", GH_ParamAccess.item); p.AddGenericParameter("Features", "F", "Edited source snapshots", GH_ParamAccess.list); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) { p.AddGenericParameter("Plan", "P", "Frozen reviewed updates", GH_ParamAccess.item); p.AddTextParameter("Review", "R", "Counts and validation issues", GH_ParamAccess.list); }
        protected override (object, Func<object>) Capture(IGH_DataAccess da)
        { var layer = Get<MapLayerDescriptor>(da, 0); var features = GetList<FeatureDraft>(da, 1).Select(f => f.Copy()).ToList();
            return layer == null ? (null, null) : (new { layer.Reference, features }, () => MapDesignConnection.RequireHost().Plan(layer.Reference, features)); }
        protected override void Show(IGH_DataAccess da, object result)
        {
            var plan = (MapUpdatePlan)result; da.SetData(0, new GH_ObjectWrapper(plan));
            da.SetDataList(1, new[] { plan.ToString() }.Concat(plan.Issues).Concat(plan.Patches.Select(p =>
                $"Feature {p.GlobalId?.ToString() ?? p.ObjectId.ToString()}: geometry={p.Values.Geometry != null}; fields={Newtonsoft.Json.JsonConvert.SerializeObject(p.Values.Attributes)}")));
        }
    }

    public sealed class CommitUpdatesComponent : ActionMapComponent
    {
        public CommitUpdatesComponent() : base("Commit Updates", "Commit", "Right-click Commit reviewed updates. Rechecks source and baselines before one ArcGIS edit operation.", "Write") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f00c");
        protected override string ActionName => "Commit reviewed updates";
        protected override void RegisterInputParams(GH_InputParamManager p) => p.AddGenericParameter("Plan", "P", "Reviewed update plan", GH_ParamAccess.item);
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddTextParameter("Receipt", "R", "Last explicit commit result", GH_ParamAccess.item);
        protected override object Prepare(IGH_DataAccess da) { var plan = Get<MapUpdatePlan>(da, 0); if (plan != null && !plan.CanCommit) throw new InvalidOperationException("Plan has no updates or contains issues."); return plan; }
        protected override object Execute(object request) => MapDesignConnection.RequireHost().Commit((MapUpdatePlan)request);
        protected override void ShowReceipt(IGH_DataAccess da, object value) => da.SetData(0, value);
    }

    public sealed class CreateLayerComponent : ActionMapComponent
    {
        sealed class Request { public MapDescriptor Map; public string Name, Geodatabase; public GeometryTarget Target; public List<FieldDefinition> Fields; }
        public CreateLayerComponent() : base("Create Layer", "Create", "Review the schema inputs, then right-click Create layer. Retains its output receipt in the definition.", "Write") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f00d");
        protected override string ActionName => "Create layer once";
        protected override Type ReceiptType => typeof(MapLayerDescriptor);
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Map", "M", "Output map", GH_ParamAccess.item); p.AddTextParameter("Name", "N", "New feature-class name", GH_ParamAccess.item); p.AddTextParameter("Shape", "S", "PointZ, PolylineZ, PolygonZ or Multipatch", GH_ParamAccess.item, "PolygonZ"); p.AddGenericParameter("Fields", "F", "Field definitions", GH_ParamAccess.list); p[3].Optional = true; p.AddTextParameter("Geodatabase", "G", "Blank uses project default file geodatabase", GH_ParamAccess.item, ""); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddGenericParameter("Layer", "L", "Created layer; reconnect after reopening a project", GH_ParamAccess.item);
        protected override object Prepare(IGH_DataAccess da)
        {
            var map = Get<MapDescriptor>(da, 0); string name = "", shape = "PolygonZ", path = "";
            if (map == null || !da.GetData(1, ref name)) return null; da.GetData(2, ref shape); da.GetData(4, ref path);
            if (!Enum.TryParse<GeometryTarget>(shape, true, out var target) || target == GeometryTarget.Unsupported || target == GeometryTarget.PolygonExtrusion || !Enum.IsDefined(target)) throw new InvalidOperationException("Choose PointZ, PolylineZ, PolygonZ or Multipatch.");
            var fields = GetList<FieldDefinition>(da, 3);
            return new Request { Map = map, Name = name, Target = target, Geodatabase = path, Fields = fields };
        }
        protected override object Execute(object request)
        {
            var r = (Request)request; var host = MapDesignConnection.RequireHost();
            if (Receipt is MapLayerDescriptor previous)
                return host.Layers(r.Map.Uri).Single(l => l.Reference.LayerUri == previous.Reference.LayerUri &&
                    l.Reference.Source == previous.Reference.Source);
            var layer = host.Create(r.Map.Uri, r.Name, r.Geodatabase, r.Target, r.Fields);
            return host.Layers(r.Map.Uri).Single(l => l.Reference.LayerUri == layer.LayerUri);
        }
        protected override void ShowReceipt(IGH_DataAccess da, object value) => da.SetData(0, new GH_ObjectWrapper(value));
    }

    public sealed class AppendFeaturesComponent : ActionMapComponent
    {
        sealed class Request { public MapLayerReference Layer; public List<FeatureDraft> Features; }
        public AppendFeaturesComponent() : base("Append Features", "Append", "Right-click to publish keyed features. Later actions update the same keyed output set; solves never write.", "Write") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f00e");
        protected override string ActionName => "Publish keyed features";
        protected override Type ReceiptType => typeof(List<FeatureDraft>);
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Layer", "L", "Writable target layer", GH_ParamAccess.item); p.AddGenericParameter("Features", "F", "New feature drafts with unique stable keys", GH_ParamAccess.list); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddGenericParameter("Published Features", "F", "Retained identities for further edits", GH_ParamAccess.list);
        protected override object Prepare(IGH_DataAccess da)
        { var layer = Get<MapLayerDescriptor>(da, 0); return layer == null ? null : new Request { Layer = layer.Reference, Features = GetList<FeatureDraft>(da, 1).Select(f => f.Copy()).ToList() }; }
        protected override object Execute(object request)
        {
            var r = (Request)request; var host = MapDesignConnection.RequireHost();
            if (Receipt == null) return host.Append(r.Layer, r.Features).ToList();
            var previous = ((List<FeatureDraft>)Receipt).ToDictionary(f => f.Key);
            if (r.Features.Count != previous.Count || r.Features.Select(f => f.Key).Distinct().Count() != r.Features.Count || r.Features.Any(f => !previous.ContainsKey(f.Key)))
                throw new InvalidOperationException("Published key set changed. Use a new Append component for additional output.");
            var rebound = host.Read(r.Layer, false, 100000);
            var updates = r.Features.Select(f => { var copy = previous[f.Key].Copy();
                if (copy.Source.MapUri != r.Layer.MapUri || copy.Source.LayerUri != r.Layer.LayerUri || copy.Source.Source != r.Layer.Source || copy.Source.CrsDefinition != r.Layer.CrsDefinition)
                    throw new InvalidOperationException("Reconnect to the original published layer before updating.");
                var current = rebound.Features.SingleOrDefault(v => copy.GlobalId.HasValue ? v.GlobalId == copy.GlobalId : v.ObjectId == copy.ObjectId);
                if (current == null || current.Baseline != copy.Baseline) throw new InvalidOperationException("Published features changed or were removed. Read and review them before further updates.");
                copy.Source = current.Source; copy.ModelContext = current.ModelContext;
                copy.Geometry = f.Geometry; copy.ReplaceGeometry = true; copy.Changes = f.Changes; return copy; }).ToList();
            var plan = host.Plan(r.Layer, updates); host.Commit(plan);
            var read = host.Read(r.Layer, false, 100000);
            return read.Features.Where(f => updates.Any(u => u.GlobalId.HasValue ? u.GlobalId == f.GlobalId : u.ObjectId == f.ObjectId))
                .Select(f => { f.Key = updates.Single(u => u.GlobalId.HasValue ? u.GlobalId == f.GlobalId : u.ObjectId == f.ObjectId).Key; return f; }).ToList();
        }
        protected override void ShowReceipt(IGH_DataAccess da, object value) => Output(da, 0, (List<FeatureDraft>)value);
    }
}
