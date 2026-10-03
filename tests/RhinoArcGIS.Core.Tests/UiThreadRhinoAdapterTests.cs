using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using RhinoInside.ArcGISPro;
using Xunit;

// This shim executes inline. Tests cover the actual production adapter's batching/guard logic,
// not WPF scheduling or SDK affinity; those remain live-host acceptance checks.
namespace System.Windows.Threading
{
    public sealed class Dispatcher
    {
        public bool CheckAccess() => true;
        public T Invoke<T>(Func<T> action) => action();
        public void Invoke(Action action) => action();
    }
}

namespace RhinoArcGIS.Core.Tests
{
    public class UiThreadRhinoAdapterTests
    {
        [Theory]
        [InlineData("create")]
        [InlineData("replace")]
        [InlineData("metadata")]
        [InlineData("read")]
        public void Switching_documents_between_chunks_never_touches_the_second_document(string operation)
        {
            uint document = 1;
            var inner = new SwitchingAdapter(() => document, () => document = 2);
            var adapter = new UiThreadRhinoAdapter(inner, new System.Windows.Threading.Dispatcher(), () => document);
            var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
            Action run = operation switch
            {
                "create" => () => adapter.CreateObjects("Target", ids.Select(_ => new NewRhinoObject()).ToArray()),
                "replace" => () => adapter.ReplaceGeometries(ids.Select(id => new GeometryReplacement { Id = id }).ToArray()),
                "metadata" => () => adapter.WriteUserStrings(ids.Select(id =>
                    new KeyValuePair<Guid, IReadOnlyDictionary<string, string>>(id, new Dictionary<string, string>())).ToArray()),
                _ => () => adapter.SnapshotObjects(ids)
            };

            Assert.Contains("document changed", Assert.Throws<InvalidOperationException>(run).Message);
            Assert.Equal(500, inner.Touched[1]);
            Assert.False(inner.Touched.ContainsKey(2));
        }

        [Fact]
        public void Changed_document_is_rejected_before_a_single_metadata_write()
        {
            uint document = 1;
            var inner = new SwitchingAdapter(() => document, () => { });
            var adapter = new UiThreadRhinoAdapter(inner, new System.Windows.Threading.Dispatcher(), () => document);
            document = 2;
            Assert.Throws<InvalidOperationException>(() => adapter.WriteUserStrings(Guid.NewGuid(), new Dictionary<string, string>()));
            Assert.Empty(inner.Touched);
        }

        [Fact]
        public void Adapter_refuses_to_bind_without_an_active_document()
        {
            Assert.Throws<InvalidOperationException>(() => new UiThreadRhinoAdapter(
                new SwitchingAdapter(() => 0, () => { }), new System.Windows.Threading.Dispatcher(), () => 0));
        }

        sealed class SwitchingAdapter : IRhinoAdapter, IBulkRhinoAdapter
        {
            readonly Func<uint> _document;
            readonly Action _afterChunk;
            internal readonly Dictionary<uint, int> Touched = new Dictionary<uint, int>();
            internal SwitchingAdapter(Func<uint> document, Action afterChunk) { _document = document; _afterChunk = afterChunk; }
            void Touch(int count)
            {
                var serial = _document();
                Touched.TryGetValue(serial, out var prior);
                Touched[serial] = prior + count;
                _afterChunk();
            }
            public IReadOnlyList<CreatedRhinoObject> CreateObjects(string layer, IReadOnlyList<NewRhinoObject> items)
            { Touch(items.Count); return items.Select(_ => new CreatedRhinoObject { Id = Guid.NewGuid() }).ToArray(); }
            public IReadOnlyList<CreatedRhinoObject> ReplaceGeometries(IReadOnlyList<GeometryReplacement> items)
            { Touch(items.Count); return items.Select(item => new CreatedRhinoObject { Id = item.Id }).ToArray(); }
            public void WriteUserStrings(IReadOnlyList<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> items) => Touch(items.Count);
            public IReadOnlyList<RhinoObjectSnapshot> SnapshotObjects(IReadOnlyList<Guid> ids)
            { Touch(ids.Count); return ids.Select(id => new RhinoObjectSnapshot { RhinoGuid = id }).ToArray(); }
            public void WriteUserStrings(Guid id, IReadOnlyDictionary<string, string> values) => Touch(1);
            public UnitSystem GetUnits() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => Array.Empty<string>();
            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layer) => Array.Empty<RhinoObjectSnapshot>();
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => Array.Empty<RhinoObjectSnapshot>();
            public RhinoObjectSnapshot ReadObject(Guid id) => null;
            public Guid CreateObject(NeutralGeometry geometry, string layer, IReadOnlyDictionary<string, string> values) => throw new NotSupportedException();
            public void ReplaceObjectGeometry(Guid id, NeutralGeometry geometry) => throw new NotSupportedException();
            public void IsolateFailed(IEnumerable<Guid> ids) { }
            public void Redraw() { }
            public IReadOnlyList<Guid> ListObjects(string layer) => Array.Empty<Guid>();
        }
    }
}
