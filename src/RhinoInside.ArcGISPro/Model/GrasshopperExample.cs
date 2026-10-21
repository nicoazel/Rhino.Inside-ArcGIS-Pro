using System;
using System.Drawing;
using System.IO;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace RhinoInside.ArcGISPro
{
    internal static class GrasshopperExample
    {
        internal static object Open(string path)
        {
            var io = new GH_DocumentIO();
            if (!io.Open(path)) throw new IOException("Could not open the example definition.");
            global::Grasshopper.Instances.DocumentServer.AddDocument(io.Document);
            global::Grasshopper.Instances.ActiveCanvas.Document = io.Document;
            io.Document.NewSolution(false);
            return new { opened = true, components = io.Document.ObjectCount };
        }
        internal static bool Write(string path)
        {
            if (File.Exists(path)) throw new IOException("Example destination already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using var doc = new GH_Document();
            var server = global::Grasshopper.Instances.ComponentServer;
            IGH_Component Add(int index, float x, float y)
            {
                var component = (IGH_Component)server.EmitObject(new Guid("f278840b-70c3-4d36-beba-f0515422f" + index.ToString("x3")));
                component.CreateAttributes(); component.Attributes.Pivot = new PointF(x, y); doc.AddObject(component, false); return component;
            }
            IGH_Component Native(string name, float x, float y)
            {
                var component = server.FindObjectByName(name, true, true)?.CreateInstance() as IGH_Component
                    ?? throw new InvalidOperationException("Bundled component not found: " + name);
                component.CreateAttributes(); component.Attributes.Pivot = new PointF(x, y); doc.AddObject(component, false); return component;
            }
            void Text(IGH_Component c, int index, string value)
            { var p = (Param_String)c.Params.Input[index]; p.PersistentData.Clear(); p.PersistentData.Append(new GH_String(value)); }
            void Wire(IGH_Component a, int output, IGH_Component b, int input) => b.Params.Input[input].AddSource(a.Params.Output[output]);
            var map = Add(1, 100, 100);
            var layers = Add(2, 320, 100); Wire(map, 0, layers, 0);
            var item = Native("List Item", 530, 100); Wire(layers, 0, item, 0);
            var read = Add(4, 730, 100); Wire(item, 0, read, 0);
            var geometry = Add(5, 950, 100); Wire(read, 0, geometry, 0);
            var field = Add(10, 100, 360); Text(field, 0, "height_m"); Text(field, 1, "Double");
            var create = Add(13, 350, 340); Wire(map, 0, create, 0); Wire(field, 0, create, 3);
            Text(create, 1, "GH_Design_Example"); Text(create, 2, "Multipatch");
            double units = global::Rhino.RhinoMath.UnitScale(global::Rhino.UnitSystem.Meters, global::Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem);
            var brep = new global::Rhino.Geometry.BoundingBox(new global::Rhino.Geometry.Point3d(0, 0, 0),
                new global::Rhino.Geometry.Point3d(20 * units, 15 * units, 30 * units)).ToBrep();
            var lift = new GH_NumberSlider(); lift.CreateAttributes(); lift.NickName = "Lift (model units)";
            lift.Attributes.Pivot = new PointF(100, 650); lift.Slider.Minimum = 0; lift.Slider.Maximum = (decimal)(50 * units); lift.SetSliderValue(0);
            doc.AddObject(lift, false);
            var z = Native("Unit Z", 350, 650); z.Params.Input[0].AddSource(lift);
            var move = Native("Move", 540, 570);
            ((Param_Geometry)move.Params.Input[0]).PersistentData.Append(new GH_Brep(brep)); Wire(z, 0, move, 1);
            var build = Add(7, 730, 450); Wire(move, 0, build, 0); Text(build, 1, "massing-001");
            var attribute = Add(9, 930, 450); Wire(build, 0, attribute, 0); Text(attribute, 1, "height_m");
            ((Param_GenericObject)attribute.Params.Input[2]).PersistentData.Append(new GH_Number(30));
            var append = Add(14, 1160, 340); Wire(create, 0, append, 0); Wire(attribute, 0, append, 1);
            var preview = Add(15, 730, 700); Wire(map, 0, preview, 0); Wire(move, 0, preview, 1);
            return new GH_DocumentIO(doc).SaveQuiet(path);
        }
    }
}
