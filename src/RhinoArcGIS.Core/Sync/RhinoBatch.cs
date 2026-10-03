using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Adapters;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// Runs a set of Rhino writes through <see cref="IBulkRhinoAdapter"/> when the adapter offers
    /// it, and one call at a time when it does not, with the same per-item results either way.
    /// </summary>
    /// <remarks>
    /// Bulk calls go in slices of <see cref="Slice"/> so a long batch can say how far it has got
    /// through <c>progress</c> (items done, items in all).
    /// </remarks>
    internal static class RhinoBatch
    {
        const int Slice = 2000;

        public static IReadOnlyList<CreatedRhinoObject> Create(IRhinoAdapter rhino, string layerName,
            IReadOnlyList<NewRhinoObject> objects, Action<int, int> progress = null)
        {
            if (objects.Count == 0) return new CreatedRhinoObject[0];
            if (rhino is IBulkRhinoAdapter bulk)
            {
                try { return Sliced(objects, chunk => bulk.CreateObjects(layerName, chunk), progress); }
                finally { bulk.Redraw(); }
            }

            var results = new List<CreatedRhinoObject>(objects.Count);
            foreach (var o in objects)
            {
                var result = new CreatedRhinoObject();
                try
                {
                    result.Id = rhino.CreateObject(o.Geometry, layerName, o.UserStrings);
                    try { result.ReadBack = rhino.ReadObject(result.Id); }
                    catch { /* an adapter that cannot read back leaves the shared baseline in place */ }
                }
                catch (Exception ex) { result.Error = ex.Message; }
                results.Add(result);
                if (results.Count % Slice == 0) progress?.Invoke(results.Count, objects.Count);
            }
            progress?.Invoke(results.Count, objects.Count);
            return results;
        }

        /// <summary>
        /// A layer's objects, read in pieces a UI-thread adapter can hand over in chunks, then
        /// assembled; the same result as <see cref="IRhinoAdapter.ReadObjects"/>.
        /// </summary>
        public static IReadOnlyList<RhinoObjectSnapshot> ReadObjects(IRhinoAdapter rhino, string layerName,
            Action<int, int> progress = null)
        {
            if (!(rhino is IBulkRhinoAdapter bulk)) return rhino.ReadObjects(layerName);
            var ids = bulk.ListObjects(layerName);
            return MultipartAssembly.Assemble(Sliced(ids, chunk => bulk.SnapshotObjects(chunk), progress));
        }

        public static void WriteUserStrings(IRhinoAdapter rhino,
            IReadOnlyList<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> writes,
            Action<int, int> progress = null)
        {
            if (writes.Count == 0) return;
            if (rhino is IBulkRhinoAdapter bulk)
            {
                Sliced(writes, chunk => { bulk.WriteUserStrings(chunk); return new object[0]; }, progress);
                return;
            }
            foreach (var w in writes) rhino.WriteUserStrings(w.Key, w.Value);
        }

        public static IReadOnlyList<CreatedRhinoObject> ReplaceGeometries(IRhinoAdapter rhino,
            IReadOnlyList<GeometryReplacement> replacements, Action<int, int> progress = null)
        {
            if (replacements.Count == 0) return new CreatedRhinoObject[0];
            if (rhino is IBulkRhinoAdapter bulk)
            {
                try { return Sliced(replacements, chunk => bulk.ReplaceGeometries(chunk), progress); }
                finally { bulk.Redraw(); }
            }

            var results = new List<CreatedRhinoObject>(replacements.Count);
            foreach (var r in replacements)
            {
                var result = new CreatedRhinoObject { Id = r.Id };
                try
                {
                    rhino.ReplaceObjectGeometry(r.Id, r.Geometry);
                    result.ReadBack = rhino.ReadObject(r.Id)
                        ?? throw new InvalidOperationException("Rhino could not read the replacement geometry back.");
                }
                catch (Exception ex) { result.Error = ex.Message; }
                results.Add(result);
            }
            progress?.Invoke(results.Count, replacements.Count);
            return results;
        }

        static List<TOut> Sliced<TIn, TOut>(IReadOnlyList<TIn> items, Func<IReadOnlyList<TIn>, IReadOnlyList<TOut>> run,
            Action<int, int> progress)
        {
            var results = new List<TOut>(items.Count);
            for (int start = 0; start < items.Count; start += Slice)
            {
                var chunk = new List<TIn>(Math.Min(Slice, items.Count - start));
                for (int i = start; i < items.Count && i < start + Slice; i++) chunk.Add(items[i]);
                results.AddRange(run(chunk));
                progress?.Invoke(Math.Min(start + Slice, items.Count), items.Count);
            }
            return results;
        }
    }
}
