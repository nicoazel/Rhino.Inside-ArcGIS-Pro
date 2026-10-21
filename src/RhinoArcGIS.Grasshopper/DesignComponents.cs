using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Grasshopper;
using RhinoArcGIS.Rhino;

namespace RhinoArcGIS.Grasshopper
{
    public sealed class BuildFeatureComponent : MapComponent
    {
        public BuildFeatureComponent() : base("Build Feature", "Feature", "Build a new feature from geometry and an explicit stable output key.", "Design") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f007");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGeometryParameter("Geometry", "G", "One output feature geometry", GH_ParamAccess.item); p.AddTextParameter("Key", "K", "Unique stable output key", GH_ParamAccess.item); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddGenericParameter("Feature", "F", "New feature draft", GH_ParamAccess.item);
        protected override void SolveInstance(IGH_DataAccess da)
        {
            IGH_GeometricGoo goo = null; string key = "";
            if (!da.GetData(0, ref goo) || !da.GetData(1, ref key)) return;
            try { var g = GH_Convert.ToGeometryBase(goo); var neutral = GrasshopperGeometry.Read(g);
                if (neutral == null || string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Supported geometry and a nonempty key are required.");
                da.SetData(0, new GH_ObjectWrapper(new FeatureDraft { Key = key, Geometry = neutral, ReplaceGeometry = true }));
            } catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
        }
    }

    public sealed class SetFeatureGeometryComponent : MapComponent
    {
        public SetFeatureGeometryComponent() : base("Set Feature Geometry", "Set Geometry", "Replace a feature's geometry while retaining its identity and baseline.", "Design") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f008");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Feature", "F", "Original feature; use Feature Geometry's matching feature tree", GH_ParamAccess.item); p.AddGeometryParameter("Geometry", "G", "Replacement pieces in one feature branch", GH_ParamAccess.list); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddGenericParameter("Feature", "F", "Edited copy", GH_ParamAccess.item);
        protected override void SolveInstance(IGH_DataAccess da)
        {
            var f = Get<FeatureDraft>(da, 0); var geometry = new List<IGH_GeometricGoo>(); if (f == null || !da.GetDataList(1, geometry)) return;
            try { var copy = f.Copy(); copy.Geometry = FeaturePlanning.Combine(geometry.Select(g => GrasshopperGeometry.Read(GH_Convert.ToGeometryBase(g))).ToList());
                if (copy.Geometry == null) throw new InvalidOperationException("Unsupported replacement geometry.");
                copy.ReplaceGeometry = true; da.SetData(0, new GH_ObjectWrapper(copy));
            } catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
        }
    }

    public sealed class SetAttributeComponent : MapComponent
    {
        public SetAttributeComponent() : base("Set Attribute", "Set Attribute", "Set a design field on a copy of the feature. Validation occurs against the target schema.", "Design") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f009");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddGenericParameter("Feature", "F", "Feature draft", GH_ParamAccess.item); p.AddTextParameter("Field", "N", "Field name", GH_ParamAccess.item); p.AddGenericParameter("Value", "V", "Typed value; use Null to clear a nullable field", GH_ParamAccess.item); p[2].Optional = true; }
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddGenericParameter("Feature", "F", "Edited copy", GH_ParamAccess.item);
        protected override void SolveInstance(IGH_DataAccess da)
        {
            var f = Get<FeatureDraft>(da, 0); string field = ""; object value = null;
            if (f == null || !da.GetData(1, ref field)) return; da.GetData(2, ref value);
            if (value is GH_ObjectWrapper w) value = w.Value; if (value is IGH_Goo goo) value = goo.ScriptVariable();
            string text = value is DateTime date ? date.ToString("o", CultureInfo.InvariantCulture) : value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            var copy = f.Copy(); copy.Changes[field] = text; copy.Attributes[field] = text;
            da.SetData(0, new GH_ObjectWrapper(copy));
        }
    }

    public sealed class FieldComponent : MapComponent
    {
        public FieldComponent() : base("Field", "Field", "Define a typed field for a new output layer.", "Design") { }
        public override Guid ComponentGuid => new("f278840b-70c3-4d36-beba-f0515422f00a");
        protected override void RegisterInputParams(GH_InputParamManager p) { p.AddTextParameter("Name", "N", "Field name", GH_ParamAccess.item); p.AddTextParameter("Type", "T", "Text, Integer, Double, Date or Guid", GH_ParamAccess.item, "Text"); p.AddBooleanParameter("Nullable", "?", "Allow null values", GH_ParamAccess.item, true); p.AddIntegerParameter("Length", "L", "Text length", GH_ParamAccess.item, 255); }
        protected override void RegisterOutputParams(GH_OutputParamManager p) => p.AddGenericParameter("Field", "F", "Field definition", GH_ParamAccess.item);
        protected override void SolveInstance(IGH_DataAccess da)
        {
            string name = "", type = "Text"; bool nullable = true; int length = 255;
            if (!da.GetData(0, ref name)) return; da.GetData(1, ref type); da.GetData(2, ref nullable); da.GetData(3, ref length);
            if (!Enum.TryParse<FieldType>(type, true, out var parsed) || parsed == FieldType.Boolean)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Use Text, Integer, Double, Date or Guid."); return; }
            da.SetData(0, new GH_ObjectWrapper(new FieldDefinition { Name = name, Type = parsed, Nullable = nullable, Length = length }));
        }
    }
}
