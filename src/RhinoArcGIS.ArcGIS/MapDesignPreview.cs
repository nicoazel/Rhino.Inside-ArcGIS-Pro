using System;
using System.Collections.Generic;
using System.Linq;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using RhinoArcGIS.Core.Geometry;

namespace RhinoArcGIS.ArcGIS
{
    /// <summary>All handle mutations run on the MCT. Each component owns only its own overlays.</summary>
    public sealed class MapDesignPreview
    {
        readonly Dictionary<Guid, List<IDisposable>> handles = new();
        public void Clear(Guid owner) => QueuedTask.Run(() => ClearCore(owner)).Wait();
        void ClearCore(Guid owner)
        {
            if (!handles.Remove(owner, out var old)) return;
            foreach (var handle in old)
                try { handle?.Dispose(); } catch (ObjectDisposedException) { }
        }
        public void ClearAll() => QueuedTask.Run(() => {
            foreach (var owner in handles.Keys.ToArray()) ClearCore(owner);
        }).Wait();

        public void Show(Guid owner, string mapUri, IReadOnlyList<NeutralGeometry> geometries, int argb, double width)
        {
            if (geometries.Count > 2000 || geometries.Sum(g => g.AllPoints().Take(200001).Count()) > 200000)
                throw new InvalidOperationException("Preview limit: 2,000 geometries / 200,000 vertices. Reduce geometry upstream.");
            QueuedTask.Run(() => {
                var map = ArcGISAdapter.DesignMap(mapUri);
                var views = global::ArcGIS.Desktop.Framework.FrameworkApplication.Panes.OfType<IMapPane>()
                    .Where(p => p.MapView?.Map?.URI == mapUri).Select(p => p.MapView).ToList();
                if (views.Count == 0) throw new InvalidOperationException("Open a view of the selected map or scene.");
                var color = System.Drawing.Color.FromArgb(argb);
                var cimColor = ColorFactory.Instance.CreateRGBColor(color.R, color.G, color.B, color.A * 100.0 / 255);
                var staged = new List<IDisposable>();
                try
                {
                    foreach (var g in geometries)
                    {
                        var target = g.Kind == NeutralGeometryKind.Point ? GeometryType.Point :
                            g.Kind == NeutralGeometryKind.Polyline ? GeometryType.Polyline :
                            g.Kind == NeutralGeometryKind.Polygon ? GeometryType.Polygon : GeometryType.Multipatch;
                        var shape = ArcGISGeometryWriter.ToArcGIS(g, map.SpatialReference, target);
                        if (shape == null) throw new InvalidOperationException("Preview geometry could not be converted.");
                        var symbol = target == GeometryType.Point
                            ? SymbolFactory.Instance.ConstructPointSymbol(cimColor, Math.Max(2, width * 3)).MakeSymbolReference()
                            : target == GeometryType.Polyline
                            ? SymbolFactory.Instance.ConstructLineSymbol(cimColor, width).MakeSymbolReference()
                            : SymbolFactory.Instance.ConstructPolygonSymbol(cimColor).MakeSymbolReference();
                        foreach (var view in views) staged.Add(view.AddOverlay(shape, symbol)
                            ?? throw new InvalidOperationException("ArcGIS could not display this preview geometry in the selected view."));
                    }
                    ClearCore(owner);
                    handles[owner] = staged;
                }
                catch { foreach (var handle in staged) handle.Dispose(); throw; }
            }).Wait();
        }
    }
}
