using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Reporting;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Sync;
using Xunit;
using Xunit.Abstractions;

namespace RhinoArcGIS.Core.Tests
{
    /// <summary>
    /// The core services at scale, over in-memory adapters whose every call is O(1). What these
    /// time is the engine alone -- hashing, planning, baselines, the ledger -- so a result here that
    /// grows faster than the layer is the engine's fault, not an SDK's. The live counterpart,
    /// with Rhino and ArcGIS doing real work, is tools/benchmark.ps1.
    /// </summary>
    /// <remarks>
    /// Runs 5,000 features by default so the suite stays fast; set RHINOINSIDE_SCALE_N (e.g.
    /// 100000) to measure a real-sized layer.
    /// </remarks>
    public class ScaleTests
    {
        readonly ITestOutputHelper _out;
        public ScaleTests(ITestOutputHelper output) { _out = output; }

        static int Size => int.TryParse(Environment.GetEnvironmentVariable("RHINOINSIDE_SCALE_N"), out int n) && n > 0 ? n : 5000;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Pull_preview_and_apply_stay_linear_over_a_large_street_layer(bool bulk)
        {
            int n = Size;
            var arc = new MemoryArcGis();
            for (int i = 0; i < n; i++) arc.Add(Street(i));
            var rhino = bulk ? new BulkMemoryRhino() : new MemoryRhino();
            var profile = Profile();
            var layer = profile.Layers[0];

            var pull = Timed("pull", () => new PullService(arc, rhino).Pull(profile, layer, PullOptions.Context));
            Assert.Equal(n, pull.CountOf(SyncOutcome.Created));

            var sync = new SyncService(arc, rhino);
            var clean = Timed("preview.clean", () => sync.Sync(profile, layer, new SyncOptions { Apply = false }));
            Assert.Equal(0, clean.Entries.Count(e => e.Outcome != SyncOutcome.Skipped));

            Timed("apply.noop", () => sync.Sync(profile, layer, new SyncOptions { Apply = true }));

            // Move 1% of the objects in Rhino and push them.
            int edits = Math.Max(1, n / 100);
            foreach (var snap in rhino.Objects.Take(edits))
                snap.Geometry = NeutralGeometryTransform.RhinoToGis(snap.Geometry, new GeoReference(AffineTransform.Translation(new Xyz(0.5, 0, 0))));
            var move = Timed("apply.move", () => sync.Sync(profile, layer, new SyncOptions { Apply = true }));
            Assert.Equal(edits, move.CountOf(SyncOutcome.Updated));

            var after = Timed("preview.after", () => sync.Sync(profile, layer, new SyncOptions { Apply = false }));
            Assert.Equal(0, after.Entries.Count(e => e.Outcome != SyncOutcome.Skipped));
        }

        [Fact]
        public void A_large_pull_reports_its_phases_counts_and_a_notice()
        {
            var arc = new MemoryArcGis();
            for (int i = 0; i < 25000; i++) arc.Add(Street(i));
            var profile = Profile();
            var phases = new List<string>();
            var notices = new List<string>();

            new PullService(arc, new BulkMemoryRhino()) { Progress = phases.Add, Notice = notices.Add }
                .Pull(profile, profile.Layers[0], PullOptions.Context);

            Assert.Contains(notices, n => n.Contains("25,000 features"));
            Assert.Contains(phases, p => p.StartsWith("Reading features from"));
            Assert.Contains("Converting features for Rhino 25,000 / 25,000\u2026", phases);
            Assert.Contains("Creating Rhino objects 2,000 / 25,000…", phases);
            Assert.Contains("Creating Rhino objects 25,000 / 25,000…", phases);
        }

        [Fact]
        public void A_parallel_pull_writes_the_same_baselines_as_a_serial_one()
        {
            Dictionary<string, string> PullWith(int parallelism)
            {
                var arc = new MemoryArcGis();
                for (int i = 0; i < 2000; i++) arc.Add(Street(i));
                var rhino = new BulkMemoryRhino();
                var profile = Profile();
                var pull = new PullService(arc, rhino) { Parallelism = parallelism }
                    .Pull(profile, profile.Layers[0], PullOptions.Context);
                Assert.Equal(2000, pull.CountOf(SyncOutcome.Created));
                // Everything but per-run values (sync guid, Rhino object id, pull time), keyed by feature.
                return rhino.Objects.ToDictionary(
                    o => o.UserStrings[GisKeys.ArcGisObjectId],
                    o => string.Join("|", o.UserStrings
                        .Where(kv => kv.Key != GisKeys.SyncGuid && kv.Key != GisKeys.RhinoObjectId &&
                                     kv.Key != GisKeys.LastPullTime)
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => kv.Key + "=" + kv.Value)));
            }

            var serial = PullWith(1);
            var parallel = PullWith(8);
            Assert.Equal(serial.Count, parallel.Count);
            foreach (var kv in serial) Assert.Equal(kv.Value, parallel[kv.Key]);
        }

        [Fact]
        public void A_bulk_create_that_fails_one_object_reports_it_in_place_and_keeps_the_rest()
        {
            var arc = new MemoryArcGis();
            for (int i = 0; i < 6; i++) arc.Add(Street(i));
            var rhino = new BulkMemoryRhino { FailName = "Street 3" };
            var profile = Profile();

            var pull = new PullService(arc, rhino).Pull(profile, profile.Layers[0], PullOptions.Context);

            Assert.Equal(new long?[] { 0, 1, 2, null, 4, 5 },
                pull.Entries.Select(e => e.Outcome == SyncOutcome.Created ? e.ArcGisObjectId : null).ToArray());
            Assert.Equal(SyncOutcome.Failed, pull.Entries[3].Outcome);
            Assert.Contains("refused", pull.Entries[3].Message);
            Assert.Equal(5, rhino.Objects.Count);

            // The failed feature is still new in ArcGIS; everything created reads as clean.
            var preview = new SyncService(arc, rhino).Sync(profile, profile.Layers[0], new SyncOptions { Apply = false });
            Assert.Single(preview.Entries, e => e.Outcome == SyncOutcome.Created);
            Assert.Equal(5, preview.Entries.Count(e => e.Outcome == SyncOutcome.Skipped));
        }

        SyncReport Timed(string step, Func<SyncReport> run)
        {
            var sw = Stopwatch.StartNew();
            var report = run();
            var phases = string.Join(" ", report.PhaseMs.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}ms"));
            _out.WriteLine($"{Size,7} {step,-14} {sw.ElapsedMilliseconds,7} ms   {phases}");
            return report;
        }

        static LayerMappingProfile Profile() => new LayerMappingProfile
        {
            RhinoUnits = UnitSystem.Meters,
            ArcGisCrs = "EPSG:3857",
            Layers =
            {
                new LayerMapping
                {
                    Name = "streets", RhinoLayer = "streets", ArcGisLayer = "streets", ArcGisSource = "C:\\bench\\streets.shp",
                    GeometryTarget = GeometryTarget.PolylineZ,
                    Attributes =
                    {
                        new FieldMapping { RhinoKey = "name", ArcGisField = "name", Owner = FieldOwnership.Shared },
                        new FieldMapping { RhinoKey = "kind", ArcGisField = "kind", Owner = FieldOwnership.Shared },
                        new FieldMapping { RhinoKey = "rank", ArcGisField = "rank", Owner = FieldOwnership.ArcGisOwned, Type = FieldType.Integer },
                        new FieldMapping { RhinoKey = "value", ArcGisField = "value", Owner = FieldOwnership.Shared, Type = FieldType.Double },
                    }
                }
            }
        };

        static FeatureRecord Street(int i)
        {
            double x = 1000 + (i % 400) * 80.0, y = 1000 + (i / 400) * 80.0;
            var pts = new List<Xyz>();
            for (int k = 0; k < 8; k++) pts.Add(new Xyz(x + k * 10.0, y + Math.Sin(k) * 3.0, 10));
            var f = new FeatureRecord
            {
                Target = GeometryTarget.PolylineZ,
                Geometry = new NeutralGeometry { Kind = NeutralGeometryKind.Polyline, Parts = new List<List<Xyz>> { pts } },
                Identity = new SyncIdentity { ArcGisObjectId = i }
            };
            f.Attributes["name"] = "Street " + i;
            f.Attributes["kind"] = "residential";
            f.Attributes["rank"] = i.ToString();
            f.Attributes["value"] = (i * 0.25).ToString("R");
            return f;
        }

        /// <summary>Rhino document as a dictionary: every call is O(1) except reading a layer.</summary>
        class MemoryRhino : IRhinoAdapter, IDocumentStringStore
        {
            readonly Dictionary<Guid, RhinoObjectSnapshot> _byId = new Dictionary<Guid, RhinoObjectSnapshot>();
            readonly Dictionary<string, string> _strings = new Dictionary<string, string>();
            public List<RhinoObjectSnapshot> Objects { get; } = new List<RhinoObjectSnapshot>();

            public UnitSystem GetUnits() => UnitSystem.Meters;
            public IReadOnlyList<string> GetLayerNames() => new[] { "streets" };
            public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName) => Objects.Select(Copy).ToList();
            public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects() => new RhinoObjectSnapshot[0];
            public RhinoObjectSnapshot ReadObject(Guid id) => _byId.TryGetValue(id, out var s) ? Copy(s) : null;

            public void WriteUserStrings(Guid id, IReadOnlyDictionary<string, string> userStrings)
            {
                var s = _byId[id];
                foreach (var kv in userStrings)
                    if (kv.Value == null) s.UserStrings.Remove(kv.Key); else s.UserStrings[kv.Key] = kv.Value;
            }

            public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings)
            {
                var s = new RhinoObjectSnapshot
                {
                    RhinoGuid = Guid.NewGuid(), LayerName = layerName,
                    Descriptor = new GeometryDescriptor(RhinoGeometryKind.OpenCurve), Geometry = geometry,
                    UserStrings = new Dictionary<string, string>(userStrings, StringComparer.Ordinal)
                };
                _byId[s.RhinoGuid] = s;
                Objects.Add(s);
                return s.RhinoGuid;
            }

            public void ReplaceObjectGeometry(Guid id, NeutralGeometry geometry) => _byId[id].Geometry = geometry;
            public void IsolateFailed(IEnumerable<Guid> ids) { }
            public string GetDocumentString(string key) => _strings.TryGetValue(key, out var v) ? v : null;
            public void SetDocumentString(string key, string value) => _strings[key] = value;

            static RhinoObjectSnapshot Copy(RhinoObjectSnapshot s) => new RhinoObjectSnapshot
            {
                RhinoGuid = s.RhinoGuid, LayerName = s.LayerName, Descriptor = s.Descriptor, Geometry = s.Geometry,
                UserStrings = new Dictionary<string, string>(s.UserStrings, StringComparer.Ordinal)
            };
        }

        /// <summary>The same document behind the set-at-a-time interface the Rhino adapter offers.</summary>
        sealed class BulkMemoryRhino : MemoryRhino, IBulkRhinoAdapter
        {
            /// <summary>A "name" user string whose object the bulk create refuses.</summary>
            public string FailName;

            public IReadOnlyList<CreatedRhinoObject> CreateObjects(string layerName, IReadOnlyList<NewRhinoObject> objects) =>
                objects.Select(o =>
                {
                    if (FailName != null && o.UserStrings.TryGetValue("name", out var name) && name == FailName)
                        return new CreatedRhinoObject { Error = "refused by the test" };
                    var id = CreateObject(o.Geometry, layerName, o.UserStrings);
                    return new CreatedRhinoObject { Id = id, ReadBack = ReadObject(id) };
                }).ToList();

            public void WriteUserStrings(IReadOnlyList<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> writes)
            {
                foreach (var w in writes) WriteUserStrings(w.Key, w.Value);
            }

            public void Redraw() { }

            public IReadOnlyList<Guid> ListObjects(string layerName) => Objects.Select(o => o.RhinoGuid).ToList();

            public IReadOnlyList<RhinoObjectSnapshot> SnapshotObjects(IReadOnlyList<Guid> ids) =>
                ids.Select(ReadObject).Where(s => s != null).ToList();

            public IReadOnlyList<CreatedRhinoObject> ReplaceGeometries(IReadOnlyList<GeometryReplacement> replacements) =>
                replacements.Select(r =>
                {
                    ReplaceObjectGeometry(r.Id, r.Geometry);
                    return new CreatedRhinoObject { Id = r.Id, ReadBack = ReadObject(r.Id) };
                }).ToList();
        }

        /// <summary>A feature class as a dictionary keyed by ObjectID.</summary>
        sealed class MemoryArcGis : IArcGISAdapter
        {
            readonly Dictionary<long, FeatureRecord> _byOid = new Dictionary<long, FeatureRecord>();
            readonly List<long> _order = new List<long>();
            long _next = 10_000_000;

            public void Add(FeatureRecord f) { _byOid[f.Identity.ArcGisObjectId.Value] = f; _order.Add(f.Identity.ArcGisObjectId.Value); }
            public IReadOnlyList<string> GetLayerNames() => new[] { "streets" };
            public LayerSchema GetSchema(string layerName) => new LayerSchema
            {
                LayerName = layerName, GeometryType = GeometryTarget.PolylineZ,
                Fields =
                {
                    new FieldDefinition { Name = "name", Type = FieldType.Text },
                    new FieldDefinition { Name = "kind", Type = FieldType.Text },
                    new FieldDefinition { Name = "rank", Type = FieldType.Integer },
                    new FieldDefinition { Name = "value", Type = FieldType.Double },
                }
            };
            public IReadOnlyList<FeatureRecord> ReadFeatures(string layerName) => _order.Select(o => _byOid[o]).ToList();
            public IReadOnlyList<FeatureRecord> ReadSelectedFeatures(string layerName) => new FeatureRecord[0];

            public IReadOnlyList<long> CreateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                var oids = new List<long>();
                foreach (var f in features)
                {
                    long oid = _next++;
                    var stored = new FeatureRecord { Target = f.Target, Geometry = f.Geometry, Identity = new SyncIdentity { ArcGisObjectId = oid } };
                    foreach (var kv in f.Attributes) stored.Attributes[kv.Key] = kv.Value;
                    Add(stored);
                    oids.Add(oid);
                }
                return oids;
            }

            public void UpdateFeatures(string layerName, IReadOnlyList<FeatureRecord> features)
            {
                foreach (var f in features)
                {
                    var stored = _byOid[f.Identity.ArcGisObjectId.Value];
                    if (f.Geometry != null) stored.Geometry = f.Geometry;
                    foreach (var kv in f.Attributes) stored.Attributes[kv.Key] = kv.Value;
                }
            }

            public void RefreshScene() { }
        }
    }
}
