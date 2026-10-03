using System;
using System.Collections.Generic;
using System.Windows.Threading;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Wraps the Rhino adapter so every call runs on the thread Rhino was started on.
    /// </summary>
    /// <remarks>
    /// The two SDKs want opposite things. Rhino's document is affine to the UI thread, while the
    /// ArcGIS adapter marshals its own work onto the ArcGIS main CIM thread and blocks on the
    /// result. Running a sync on the UI thread therefore deadlocks: the UI thread waits on the CIM
    /// thread, and an edit operation on the CIM thread needs the UI thread back. That showed up as
    /// Pro hanging when applying a sync after editing in Rhino.
    ///
    /// So the orchestration runs on a background thread -- which suits ArcGIS, since it marshals
    /// anyway -- and this decorator puts the Rhino half back where it belongs. Nothing in
    /// <see cref="IRhinoAdapter"/> mentions a RhinoCommon type, so this can sit in the add-in
    /// without dragging that assembly in early.
    /// </remarks>
    internal sealed class UiThreadRhinoAdapter : IRhinoAdapter, IBulkRhinoAdapter, IEarthAnchorSource, IDocumentStringStore
    {
        readonly IRhinoAdapter _inner;
        readonly Dispatcher _dispatcher;

        internal UiThreadRhinoAdapter(IRhinoAdapter inner, Dispatcher dispatcher)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        // IEarthAnchorSource must be forwarded too: GeoReferenceFactory tests the adapter for it,
        // and a decorator that dropped it would silently fall back to an identity georeference --
        // geometry would land at the model origin instead of its mapped position.

        public EarthAnchor GetEarthAnchor() =>
            OnUi(() => (_inner as IEarthAnchorSource)?.GetEarthAnchor());

        public AffineTransform GetModelToEarthMetres() =>
            OnUi(() => (_inner as IEarthAnchorSource)?.GetModelToEarthMetres());

        T OnUi<T>(Func<T> func) => _dispatcher.CheckAccess() ? func() : _dispatcher.Invoke(func);

        void OnUi(Action action)
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.Invoke(action);
        }

        public UnitSystem GetUnits() => OnUi(() => _inner.GetUnits());

        public IReadOnlyList<string> GetLayerNames() => OnUi(() => _inner.GetLayerNames());

        public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName) =>
            OnUi(() => _inner.ReadObjects(layerName));

        public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() =>
            OnUi(() => _inner.ReadSelectedObjects());

        public RhinoObjectSnapshot ReadObject(Guid rhinoGuid) =>
            OnUi(() => _inner.ReadObject(rhinoGuid));

        public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings) =>
            OnUi(() => _inner.WriteUserStrings(rhinoGuid, userStrings));

        public Guid CreateObject(NeutralGeometry geometry, string layerName,
                                 IReadOnlyDictionary<string, string> userStrings) =>
            OnUi(() => _inner.CreateObject(geometry, layerName, userStrings));

        public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry) =>
            OnUi(() => _inner.ReplaceObjectGeometry(rhinoGuid, geometry));

        public void IsolateFailed(IEnumerable<Guid> rhinoGuids) =>
            OnUi(() => _inner.IsolateFailed(rhinoGuids));

        // Forwarded for the same reason as IEarthAnchorSource: SyncLedger tests the adapter for it,
        // and without it every object deleted in Rhino would read as new in ArcGIS again.
        // Bulk writes go over in chunks: one hop per chunk rather than per object, while the UI
        // thread still gets back between chunks to paint and to answer ArcGIS.
        const int Chunk = 500;

        public IReadOnlyList<CreatedRhinoObject> CreateObjects(string layerName, IReadOnlyList<NewRhinoObject> objects) =>
            Chunked(objects, chunk => (_inner as IBulkRhinoAdapter)?.CreateObjects(layerName, chunk)
                                      ?? throw new NotSupportedException("The Rhino adapter has no bulk create."));

        public void WriteUserStrings(IReadOnlyList<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> writes) =>
            Chunked(writes, chunk =>
            {
                if (_inner is IBulkRhinoAdapter bulk) bulk.WriteUserStrings(chunk);
                else foreach (var w in chunk) _inner.WriteUserStrings(w.Key, w.Value);
                return Array.Empty<object>();
            });

        public IReadOnlyList<CreatedRhinoObject> ReplaceGeometries(IReadOnlyList<GeometryReplacement> replacements) =>
            Chunked(replacements, chunk => (_inner as IBulkRhinoAdapter)?.ReplaceGeometries(chunk)
                                           ?? throw new NotSupportedException("The Rhino adapter has no bulk replace."));

        public void Redraw() => OnUi(() => (_inner as IBulkRhinoAdapter)?.Redraw());

        public IReadOnlyList<Guid> ListObjects(string layerName) =>
            OnUi(() => (_inner as IBulkRhinoAdapter)?.ListObjects(layerName)
                       ?? throw new NotSupportedException("The Rhino adapter has no chunked read."));

        // Reading a 100,000-object layer in one hop held the UI thread for about 7 s; in chunks,
        // Pro keeps painting and responding between them.
        public IReadOnlyList<RhinoObjectSnapshot> SnapshotObjects(IReadOnlyList<Guid> ids) =>
            Chunked(ids, chunk => (_inner as IBulkRhinoAdapter)?.SnapshotObjects(chunk)
                                  ?? throw new NotSupportedException("The Rhino adapter has no chunked read."));

        List<TOut> Chunked<TIn, TOut>(IReadOnlyList<TIn> items, Func<IReadOnlyList<TIn>, IReadOnlyList<TOut>> run)
        {
            var results = new List<TOut>(items.Count);
            for (int start = 0; start < items.Count; start += Chunk)
            {
                var chunk = new List<TIn>(Math.Min(Chunk, items.Count - start));
                for (int i = start; i < items.Count && i < start + Chunk; i++) chunk.Add(items[i]);
                results.AddRange(OnUi(() => run(chunk)));
            }
            return results;
        }

        public string GetDocumentString(string key) =>
            OnUi(() => (_inner as IDocumentStringStore)?.GetDocumentString(key));

        public void SetDocumentString(string key, string value) =>
            OnUi(() => (_inner as IDocumentStringStore)?.SetDocumentString(key, value));
    }
}
