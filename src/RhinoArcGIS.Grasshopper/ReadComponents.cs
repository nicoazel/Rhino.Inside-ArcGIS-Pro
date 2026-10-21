using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using RhinoArcGIS.Core.Grasshopper;
using RhinoArcGIS.Rhino;

namespace RhinoArcGIS.Grasshopper
{
    public sealed class MapComponentPicker : AsyncMapComponent
    {
        public MapComponentPicker() : base("Map", "Map", "Choose a project map or scene by name or URI. Empty lists every map.", "Context") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f001");
        protected override void RegisterInputParams(GH_InputParamManager p) => p.AddTextParameter("Name or URI", "N", "Optional exact map name or URI", GH_ParamAccess.item, "");
        protected override void RegisterOutputParams(GH_OutputParamManager p) { p.AddGenericParameter("Maps", "M", "Choose a map with List Item", GH_ParamAccess.list); p.AddTextParameter("Names", "N", "Map names", GH_ParamAccess.list); }
        protected override (object, Func<object>) Capture(IGH_DataAccess da)
        {
            string name = ""; da.GetData(0, ref name);
            return (name, () => { var maps = MapDesignConnection.RequireHost().Maps();
                var matches = maps.Where(m => name == "" || m.Uri == name || m.Name == name).ToList();
                if (name != "" && matches.Count != 1) throw new InvalidOperationException("Map name is missing or ambiguous; use its URI.");
                return matches; });
        }
        protected override void Show(IGH_DataAccess da, object result) { var maps = (IReadOnlyList<MapDescriptor>)result; Output(da, 0, maps); da.SetDataList(1, maps.Select(m => m.Name)); }
    }

    public sealed class MapLayersComponent : AsyncMapComponent
    {
        public MapLayersComponent() : base("Map Layers", "Layers", "List all layers, including groups and unavailable sources. Use Layer Info to inspect capabilities.", "Context") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f002");
        protected override void RegisterInputParams(GH_InputParamManager p) => p.AddGenericParameter("Map", "M", "Map from Map component", GH_ParamAccess.item);
        protected override void RegisterOutputParams(GH_OutputParamManager p) { p.AddGenericParameter("Layers", "L", "Layer descriptors", GH_ParamAccess.list); p.AddTextParameter("Names", "N", "Layer names", GH_ParamAccess.list); p.AddTextParameter("Capabilities", "C", "Read and edit status", GH_ParamAccess.list); }
        protected override (object, Func<object>) Capture(IGH_DataAccess da)
        { var map = Get<MapDescriptor>(da, 0); return map == null ? (null, null) : (map.Uri, () => MapDesignConnection.RequireHost().Layers(map.Uri)); }
        protected override void Show(IGH_DataAccess da, object result) { var layers = (IReadOnlyList<MapLayerDescriptor>)result; Output(da, 0, layers); da.SetDataList(1, layers.Select(l => l.Reference.Name)); da.SetDataList(2, layers.Select(l => l.Status)); }
    }

    public sealed class LayerInfoComponent : MapComponent
    {
        public LayerInfoComponent() : base("Layer Info", "Info", "Inspect schema and binding of a map layer.", "Context") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f003");
        protected override void RegisterInputParams(GH_InputParamManager p) => p.AddGenericParameter("Layer", "L", "Layer descriptor", GH_ParamAccess.item);
        protected override void RegisterOutputParams(GH_OutputParamManager p) { p.AddTextParameter("CRS", "C", "Layer CRS", GH_ParamAccess.item); p.AddTextParameter("Shape", "S", "Geometry type", GH_ParamAccess.item); p.AddGenericParameter("Fields", "F", "Field definitions", GH_ParamAccess.list); p.AddTextParameter("Status", "R", "Capabilities", GH_ParamAccess.item); p.AddTextParameter("Source", "D", "Dataset identity", GH_ParamAccess.item); }
        protected override void SolveInstance(IGH_DataAccess da)
        { var layer = Get<MapLayerDescriptor>(da, 0); if (layer == null) return; da.SetData(0, layer.Schema?.Crs); da.SetData(1, layer.Schema?.GeometryType.ToString()); if (layer.Schema != null) Output(da, 2, layer.Schema.Fields); da.SetData(3, layer.Status); da.SetData(4, layer.Reference.Source); }
    }

    public sealed class ReadFeaturesComponent : AsyncMapComponent
    {
        public ReadFeaturesComponent() : base("Read Features", "Read", "Read feature snapshots into model coordinates without baking. Right-click Refresh after GIS edits.", "Read") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f004");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Layer", "L", "Feature layer", GH_ParamAccess.item); p.AddBooleanParameter("Selected only", "S", "Read selected features only", GH_ParamAccess.item, true); p.AddIntegerParameter("Limit", "N", "Maximum read count, 1–100,000", GH_ParamAccess.item, 1000); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) { p.AddGenericParameter("Features", "F", "Snapshots with identity, attributes and model geometry", GH_ParamAccess.list); p.AddBooleanParameter("Truncated", "T", "More rows exist than requested limit", GH_ParamAccess.item); }
        protected override (object, Func<object>) Capture(IGH_DataAccess da)
        {
            var layer = Get<MapLayerDescriptor>(da, 0); bool selected = true; int limit = 1000; da.GetData(1, ref selected); da.GetData(2, ref limit);
            return layer == null ? (null, null) : (new { layer.Reference, selected, limit }, () => MapDesignConnection.RequireHost().Read(layer.Reference, selected, limit));
        }
        protected override void Show(IGH_DataAccess da, object result) { var read = (FeatureReadResult)result; Output(da, 0, read.Features); da.SetData(1, read.Truncated); if (read.Truncated) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Read was truncated; increase the limit or narrow the selection."); }
    }

    public sealed class FeatureGeometryComponent : MapComponent
    {
        public FeatureGeometryComponent() : base("Feature Geometry", "Geometry", "One branch per feature; multipart geometry remains grouped by feature.", "Read") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f005");
        protected override void RegisterInputParams(GH_InputParamManager p) => p.AddGenericParameter("Features", "F", "Feature snapshots", GH_ParamAccess.list);
        protected override void RegisterOutputParams(GH_OutputParamManager p) { p.AddGeometryParameter("Geometry", "G", "Model geometry, one branch per feature", GH_ParamAccess.tree); p.AddTextParameter("Identity", "I", "Source feature identity", GH_ParamAccess.list); p.AddGenericParameter("Matching Features", "F", "One snapshot in each geometry branch; use with Set Feature Geometry", GH_ParamAccess.tree); }
        protected override void SolveInstance(IGH_DataAccess da)
        {
            try { var features = GetList<FeatureDraft>(da, 0); var tree = new DataTree<IGH_GeometricGoo>(); var refs = new DataTree<GH_ObjectWrapper>();
                for (int i = 0; i < features.Count; i++) { var path = new GH_Path(i); tree.EnsurePath(path); refs.Add(new GH_ObjectWrapper(features[i]), path); foreach (var g in GrasshopperGeometry.Display(features[i].Geometry)) tree.Add(GH_Convert.ToGeometricGoo(g), path); }
                da.SetDataTree(0, tree); da.SetDataList(1, features.Select(f => f.ToString())); da.SetDataTree(2, refs);
            } catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
        }
    }

    public sealed class FeatureAttributeComponent : MapComponent
    {
        public FeatureAttributeComponent() : base("Feature Attribute", "Attribute", "Read one typed field value. Null remains null.", "Read") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f006");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Feature", "F", "Feature snapshot", GH_ParamAccess.item); p.AddTextParameter("Field", "N", "Field name", GH_ParamAccess.item); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) { p.AddGenericParameter("Value", "V", "Typed field value", GH_ParamAccess.item); p.AddTextParameter("Fields", "N", "Available names", GH_ParamAccess.list); }
        protected override void SolveInstance(IGH_DataAccess da)
        {
            var f = Get<FeatureDraft>(da, 0); string name = ""; if (f == null || !da.GetData(1, ref name)) return;
            try { da.SetDataList(1, f.Attributes.Keys); if (!f.Attributes.TryGetValue(name, out var value)) throw new InvalidOperationException("Field does not exist.");
                var type = f.FieldTypes.TryGetValue(name, out var t) ? t : Core.Attributes.FieldType.Text;
                da.SetData(0, new GH_ObjectWrapper(FeaturePlanning.TypedValue(value, type)));
            } catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
        }
    }
}
