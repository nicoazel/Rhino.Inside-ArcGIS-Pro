using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using RhinoArcGIS.Core.Grasshopper;

namespace RhinoArcGIS.Grasshopper
{
    public abstract class MapComponent : GH_Component
    {
        protected MapComponent(string name, string nickname, string description, string group)
            : base(name, nickname, description, "ArcGIS", group) { }
        protected override Bitmap Icon => null;
        protected static T Get<T>(IGH_DataAccess da, int index) where T : class
        {
            object value = null;
            if (!da.GetData(index, ref value)) return null;
            return (value is GH_ObjectWrapper wrapper ? wrapper.Value : value) as T;
        }
        protected static List<T> GetList<T>(IGH_DataAccess da, int index) where T : class
        {
            var values = new List<object>(); da.GetDataList(index, values);
            var result = new List<T>();
            foreach (var v in values)
            {
                var value = (v is GH_ObjectWrapper w ? w.Value : v) as T;
                if (value == null) throw new InvalidOperationException($"Input {index + 1} requires {typeof(T).Name} values.");
                result.Add(value);
            }
            return result;
        }
        protected static void Output<T>(IGH_DataAccess da, int index, IEnumerable<T> values)
        { var wrapped = new List<GH_ObjectWrapper>(); foreach (var value in values) wrapped.Add(new GH_ObjectWrapper(value)); da.SetDataList(index, wrapped); }
    }

    /// <summary>Snapshots inputs during SolveInstance, then runs SDK work outside the solve/UI thread.</summary>
    public abstract class AsyncMapComponent : MapComponent
    {
        string requested;
        int refresh;
        int generation;
        protected object Result;
        string error;
        bool busy;
        bool removed;
        protected AsyncMapComponent(string name, string nickname, string description, string group) : base(name, nickname, description, group) { }
        protected abstract (object Key, Func<object> Run) Capture(IGH_DataAccess da);
        protected abstract void Show(IGH_DataAccess da, object result);
        protected void InvalidateRequest() { requested = null; Result = null; generation++; }
        protected virtual void RequestChanged() { }
        protected override void SolveInstance(IGH_DataAccess da)
        {
            try
            {
                var work = Capture(da);
                if (work.Run == null) return;
                string key = refresh + ":" + JsonConvert.SerializeObject(work.Key);
                if (key != requested)
                {
                    RequestChanged();
                    requested = key; Result = null; error = null; busy = true;
                    var version = ++generation;
                    Run(work.Run, version);
                }
                Message = busy ? "Reading…" : "";
                if (error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                if (Result != null) Show(da, Result);
            }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
        }
        async void Run(Func<object> work, int version)
        {
            object value = null; string failure = null;
            try { value = await Task.Run(work); } catch (Exception ex) { failure = ex.GetBaseException().Message; }
            if (removed || version != generation) return;
            Result = value; error = failure; busy = false;
            OnPingDocument()?.ScheduleSolution(10, _ => ExpireSolution(false));
        }
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendItem(menu, "Refresh from ArcGIS", (_, _) => { refresh++; ExpireSolution(true); });
        }
        public override void AddedToDocument(GH_Document document) { removed = false; requested = null; base.AddedToDocument(document); }
        public override void RemovedFromDocument(GH_Document document)
        { removed = true; generation++; base.RemovedFromDocument(document); }
    }

    /// <summary>Writes only from an explicit menu action. Solutions merely prepare a frozen request.</summary>
    public abstract class ActionMapComponent : MapComponent
    {
        protected object Prepared;
        protected object Receipt;
        protected virtual Type ReceiptType => typeof(string);
        bool busy;
        string status = "Review inputs, then right-click to run.";
        bool removed;
        protected ActionMapComponent(string name, string nickname, string description, string group) : base(name, nickname, description, group) { }
        protected abstract object Prepare(IGH_DataAccess da);
        protected abstract object Execute(object request);
        protected abstract void ShowReceipt(IGH_DataAccess da, object value);
        protected abstract string ActionName { get; }
        protected override void SolveInstance(IGH_DataAccess da)
        {
            Prepared = null;
            try { Prepared = Prepare(da); } catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
            Message = busy ? "Writing…" : status;
            if (Receipt != null) ShowReceipt(da, Receipt);
        }
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendItem(menu, ActionName, async (_, _) => {
                if (busy || removed || Prepared == null) return;
                var request = Prepared; busy = true;
                try { Receipt = await Task.Run(() => Execute(request)); status = "Complete; receipt retained."; }
                catch (Exception ex) { status = ex.GetBaseException().Message; }
                finally { busy = false; OnPingDocument()?.ScheduleSolution(10, _ => ExpireSolution(false)); }
            }, true, false).Enabled = !busy && Prepared != null;
        }
        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        { if (Receipt != null) writer.SetString("ArcGISReceipt", JsonConvert.SerializeObject(Receipt)); return base.Write(writer); }
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        { if (reader.ItemExists("ArcGISReceipt")) Receipt = JsonConvert.DeserializeObject(reader.GetString("ArcGISReceipt"), ReceiptType); return base.Read(reader); }
        public override void AddedToDocument(GH_Document document) { removed = false; base.AddedToDocument(document); }
        public override void RemovedFromDocument(GH_Document document) { removed = true; Prepared = null; base.RemovedFromDocument(document); }
    }
}
