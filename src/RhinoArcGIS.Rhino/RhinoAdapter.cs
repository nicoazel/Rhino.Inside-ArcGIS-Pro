using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using CoreUnit = RhinoArcGIS.Core.Spatial.UnitSystem;
using CoreXyz = RhinoArcGIS.Core.Spatial.Xyz;
using CoreEarthAnchor = RhinoArcGIS.Core.Spatial.EarthAnchor;
using CoreAffine = RhinoArcGIS.Core.Spatial.AffineTransform;
using NeutralGeometry = RhinoArcGIS.Core.Geometry.NeutralGeometry;

namespace RhinoArcGIS.Rhino
{
    /// <summary>
    /// Rhino adapter (spec 06 §2) over the in-process RhinoDoc supplied by Rhino.Inside.
    ///
    /// Wave 2 implements the write/create path used by pull: layer resolution, neutral → Rhino
    /// geometry, user strings, and selection. The read/convert path used by push lands in Wave 3.
    /// </summary>
    public sealed class RhinoAdapter : IRhinoAdapter, IBulkRhinoAdapter, IEarthAnchorSource, IDocumentStringStore
    {
        private readonly Func<RhinoDoc> _docProvider;

        public RhinoAdapter() : this(() => RhinoDoc.ActiveDoc) { }

        public RhinoAdapter(Func<RhinoDoc> docProvider)
        {
            _docProvider = docProvider ?? (() => RhinoDoc.ActiveDoc);
        }

        /// <summary>
        /// Milliseconds spent in each step of the bulk writes, accumulated across calls; read and
        /// cleared by the scale benchmark to see inside a slow phase. Touched only on the UI thread.
        /// </summary>
        public static readonly Dictionary<string, long> StepMs = new Dictionary<string, long>(StringComparer.Ordinal);

        private static T Step<T>(string step, Func<T> work)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { return work(); }
            finally
            {
                StepMs.TryGetValue(step, out long soFar);
                StepMs[step] = soFar + sw.ElapsedMilliseconds;
            }
        }

        private static void Step(string step, Action work) => Step(step, () => { work(); return true; });

        private RhinoDoc Doc()
        {
            var doc = _docProvider();
            if (doc == null)
                throw new InvalidOperationException("No active RhinoDoc; is the in-process Rhino running?");
            return doc;
        }

        public CoreUnit GetUnits()
        {
            switch (Doc().ModelUnitSystem)
            {
                case UnitSystem.Millimeters: return CoreUnit.Millimeters;
                case UnitSystem.Centimeters: return CoreUnit.Centimeters;
                case UnitSystem.Meters: return CoreUnit.Meters;
                case UnitSystem.Kilometers: return CoreUnit.Kilometers;
                case UnitSystem.Inches: return CoreUnit.Inches;
                case UnitSystem.Feet: return CoreUnit.Feet;
                case UnitSystem.Miles: return CoreUnit.Miles;
                case UnitSystem.Microns: return CoreUnit.Microns;
                case UnitSystem.Decimeters: return CoreUnit.Decimeters;
                case UnitSystem.Yards: return CoreUnit.Yards;
                case UnitSystem.None:
                case UnitSystem.Unset: return CoreUnit.Unknown;
                // Every other Rhino unit (custom units included) has an exact length Rhino knows;
                // it travels as MetresPerModelUnit.
                default: return MetresPerModelUnit(Doc()) > 0 ? CoreUnit.Other : CoreUnit.Unknown;
            }
        }

        /// <summary>Metres in one model unit, from Rhino itself; NaN when the document has no units.</summary>
        static double MetresPerModelUnit(RhinoDoc doc)
        {
            var units = doc.ModelUnitSystem;
            if (units == UnitSystem.None || units == UnitSystem.Unset) return double.NaN;
            if (units == UnitSystem.CustomUnits)
            {
                doc.GetCustomUnitSystem(true, out _, out double metres);
                return metres > 0 ? metres : double.NaN;
            }
            double scale = RhinoMath.UnitScale(units, UnitSystem.Meters);
            return scale > 0 ? scale : double.NaN;
        }

        public IReadOnlyList<string> GetLayerNames()
        {
            var doc = Doc();
            var names = new List<string>();
            foreach (var layer in doc.Layers)
                if (!layer.IsDeleted)
                    names.Add(layer.FullPath);
            return names;
        }

        public Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings)
        {
            var doc = Doc();
            return CreateObject(doc, EnsureLayerPath(doc, layerName), geometry, userStrings)[0];
        }

        /// <summary>Adds one feature's geometry; returns every object made, the representative first.</summary>
        private static List<Guid> CreateObject(RhinoDoc doc, int layerIndex, NeutralGeometry geometry,
            IReadOnlyDictionary<string, string> userStrings)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            var attrs = new ObjectAttributes { LayerIndex = layerIndex };
            if (userStrings != null)
                foreach (var kv in userStrings)
                    attrs.SetUserString(kv.Key, kv.Value);

            var ids = AddGeometry(doc, geometry, attrs);
            ids.RemoveAll(i => i == Guid.Empty);
            if (ids.Count == 0)
                throw new InvalidOperationException($"Failed to add {geometry.Kind} geometry to the Rhino document.");

            try
            {
                if (ids.Count > 1)
                    MarkParts(doc, ids, userStrings);

                // Every physical object needs its own stamp, including multipoint/multipart pieces.
                // Rhino duplicates these tags during Alt-gumball along with all other attributes.
                foreach (var id in ids)
                    WriteUserStrings(doc, id, new Dictionary<string, string> { { GisKeys.RhinoObjectId, id.ToString() } });
            }
            catch
            {
                // These objects were all created by this call. Do not expose a half-marked
                // feature if its metadata or group cannot be committed.
                foreach (var id in ids)
                {
                    var created = doc.Objects.FindId(id);
                    if (created != null) doc.Objects.Delete(created, true, true);
                }
                throw;
            }
            return ids;
        }

        // ---- set-at-a-time writes (IBulkRhinoAdapter) ----

        public IReadOnlyList<CreatedRhinoObject> CreateObjects(string layerName, IReadOnlyList<NewRhinoObject> objects)
        {
            var doc = Doc();
            var results = new List<CreatedRhinoObject>(objects.Count);
            int layerIndex = EnsureLayerPath(doc, layerName);
            using (Batch(doc, "Rhino.Inside pull"))
            {
                foreach (var o in objects)
                {
                    var result = new CreatedRhinoObject();
                    try
                    {
                        var ids = Step("create.add", () => CreateObject(doc, layerIndex, o.Geometry, o.UserStrings));
                        result.Id = ids[0];
                        // The pieces are exactly the objects just made: no need to look for them.
                        result.ReadBack = Step("create.readback", () => ReadBack(doc, ids));
                    }
                    catch (Exception ex) { result.Error = ex.Message; }
                    results.Add(result);
                }
            }
            return results;
        }

        public void WriteUserStrings(IReadOnlyList<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> writes)
        {
            var doc = Doc();
            using (Batch(doc, "Rhino.Inside sync"))
                foreach (var w in writes)
                    WriteUserStrings(doc, w.Key, w.Value);
        }

        public IReadOnlyList<CreatedRhinoObject> ReplaceGeometries(IReadOnlyList<GeometryReplacement> replacements)
        {
            var doc = Doc();
            var results = new List<CreatedRhinoObject>(replacements.Count);
            var indexes = new Dictionary<int, LayerIndex>();
            using (Batch(doc, "Rhino.Inside sync"))
            {
                foreach (var r in replacements)
                {
                    var result = new CreatedRhinoObject { Id = r.Id };
                    try
                    {
                        var existing = doc.Objects.FindId(r.Id)
                            ?? throw new InvalidOperationException($"Rhino object {r.Id} was not found.");
                        int layer = existing.Attributes.LayerIndex;
                        if (!indexes.TryGetValue(layer, out var index))
                            indexes[layer] = index = Step("replace.index", () => LayerIndex.Build(doc, layer));
                        var ids = Step("replace.replace", () => ReplaceObjectGeometry(doc, r.Id, r.Geometry, index));
                        result.ReadBack = Step("replace.readback", () => ReadBack(doc, ids))
                            ?? throw new InvalidOperationException("Rhino could not read the replacement geometry back.");
                    }
                    catch (Exception ex) { result.Error = ex.Message; }
                    results.Add(result);
                }
            }
            return results;
        }

        /// <summary>The feature made of <paramref name="ids"/> (representative first), as a snapshot.</summary>
        private static RhinoObjectSnapshot ReadBack(RhinoDoc doc, List<Guid> ids)
        {
            var objects = new List<RhinoObject>(ids.Count);
            foreach (var id in ids)
            {
                var ro = doc.Objects.FindId(id);
                if (ro != null) objects.Add(ro);
            }
            foreach (var snap in Assemble(doc, objects))
                if (snap.RhinoGuid == ids[0]) return snap;
            return null;
        }

        public void Redraw() => Step("redraw", () => Doc().Views.Redraw());

        /// <summary>
        /// Makes a batch of writes one undo step and holds redraws for its length.
        /// </summary>
        /// <remarks>
        /// Both are for speed as much as for tidiness. Outside an undo record, each
        /// <c>Objects.Replace</c> cost time in proportion to the document -- measured at 20 ms per
        /// object on a 20,000-object layer against 1.4 ms inside one -- and a redraw after every
        /// batch repainted the whole growing document each time. The caller redraws once, through
        /// <see cref="Redraw"/>, when all its batches are done.
        /// </remarks>
        private static IDisposable Batch(RhinoDoc doc, string description)
        {
            bool wasEnabled = doc.Views.RedrawEnabled;
            doc.Views.RedrawEnabled = false;
            // Inside a command, or a batch of its own, the writes join the record already open.
            uint record = doc.UndoRecordingIsActive ? 0 : doc.BeginUndoRecord(description);
            return new Restore(() =>
            {
                if (record != 0) doc.EndUndoRecord(record);
                doc.Views.RedrawEnabled = wasEnabled;
            });
        }

        private sealed class Restore : IDisposable
        {
            Action _undo;
            public Restore(Action undo) { _undo = undo; }
            public void Dispose() { _undo?.Invoke(); _undo = null; }
        }

        /// <summary>
        /// One layer's objects by the markers a geometry replacement checks -- multipart pieces by
        /// the feature they belong to, representatives by sync identity -- gathered in one walk of
        /// the layer, instead of two walks of the whole document per replaced object.
        /// </summary>
        private sealed class LayerIndex
        {
            readonly Dictionary<string, List<RhinoObject>> _parts = new Dictionary<string, List<RhinoObject>>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, List<Guid>> _owners = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);

            public static LayerIndex Build(RhinoDoc doc, int layerIndex)
            {
                var index = new LayerIndex();
                foreach (var ro in doc.Objects.GetObjectList(AllOnLayer(layerIndex)))
                {
                    if (ro == null || ro.Attributes.LayerIndex != layerIndex) continue;
                    if (SyncIdentity.IsCopy(ro.Id, ro.Attributes.GetUserString(GisKeys.RhinoObjectId))) continue;
                    string partOf = ro.Attributes.GetUserString(GisKeys.PartOf);
                    if (!string.IsNullOrEmpty(partOf)) { Add(index._parts, partOf, ro); continue; }
                    string guid = ro.Attributes.GetUserString(GisKeys.SyncGuid);
                    if (!string.IsNullOrEmpty(guid)) Add(index._owners, guid, ro.Id);
                }
                return index;
            }

            static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
            {
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
                list.Add(value);
            }

            /// <summary>The pieces marked as parts of <paramref name="owner"/>, other than the representative.</summary>
            public List<RhinoObject> Parts(string owner, Guid representative)
            {
                var result = new List<RhinoObject>();
                if (_parts.TryGetValue(owner, out var parts))
                    foreach (var p in parts) if (p.Id != representative && !p.IsDeleted) result.Add(p);
                return result;
            }

            /// <summary>Whether another representative on the layer carries the same sync identity.</summary>
            public bool HasOtherOwner(string owner, Guid representative)
            {
                if (!_owners.TryGetValue(owner, out var ids)) return false;
                foreach (var id in ids) if (id != representative) return true;
                return false;
            }

            /// <summary>Records the pieces a replacement left, so a later lookup sees them.</summary>
            public void SetParts(RhinoDoc doc, string owner, IEnumerable<Guid> ids)
            {
                var list = new List<RhinoObject>();
                foreach (var id in ids)
                {
                    var ro = doc.Objects.FindId(id);
                    if (ro != null) list.Add(ro);
                }
                _parts[owner] = list;
            }
        }

        private static ObjectEnumeratorSettings AllOnLayer(int layerIndex) => new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            ActiveObjects = true,
            ReferenceObjects = true,
            IncludeLights = false,
            IncludeGrips = false,
            LayerIndexFilter = layerIndex
        };

        public void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry)
        {
            var doc = Doc();
            var existing = doc.Objects.FindId(rhinoGuid);
            ReplaceObjectGeometry(doc, rhinoGuid, geometry,
                existing == null ? null : LayerIndex.Build(doc, existing.Attributes.LayerIndex));
        }

        /// <summary>Replaces one object's geometry; returns the feature's objects, the representative first.</summary>
        private static List<Guid> ReplaceObjectGeometry(RhinoDoc doc, Guid rhinoGuid, NeutralGeometry geometry, LayerIndex index)
        {
            if (rhinoGuid == Guid.Empty) throw new ArgumentException("A Rhino object id is required.", nameof(rhinoGuid));
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));

            var existing = doc.Objects.FindId(rhinoGuid);
            if (existing == null)
                throw new InvalidOperationException($"Rhino object {rhinoGuid} was not found.");

            var prepared = Step("replace.prepare", () => PrepareGeometry(doc, geometry));
            if (prepared.Count == 0)
                throw new InvalidOperationException($"Failed to prepare {geometry.Kind} replacement geometry.");

            string owner = existing.Attributes.GetUserString(GisKeys.SyncGuid);
            if (string.IsNullOrEmpty(owner)) owner = rhinoGuid.ToString();
            int layerIndex = existing.Attributes.LayerIndex;
            var oldParts = index.Parts(owner, rhinoGuid);
            Step("replace.validate", () => ValidateReplacementTargets(doc, existing, oldParts, owner, layerIndex, index));
            var sharedGroups = Step("replace.groups", () => SharedGroups(doc, existing, oldParts));
            var originalGeometry = existing.Geometry.Duplicate();
            if (originalGeometry == null)
                throw new InvalidOperationException($"Rhino could not snapshot object {rhinoGuid} for rollback.");

            // Add all secondary pieces before replacing the representative. If any cannot be
            // created, the original feature remains completely untouched.
            var newPartIds = new List<Guid>();
            var deletedOldParts = new List<RhinoObject>();
            var primaryReplaced = false;
            int createdGroup = -1;
            var partAttributes = existing.Attributes.Duplicate();
            partAttributes.RemoveFromAllGroups();
            foreach (string key in partAttributes.GetUserStrings().AllKeys)
                if (key != null) partAttributes.SetUserString(key, null);
            partAttributes.SetUserString(GisKeys.PartOf, owner);

            try
            {
                for (int i = 1; i < prepared.Count; i++)
                {
                    partAttributes.SetUserString(
                        GisKeys.PartIndex,
                        i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Guid id = AddPreparedGeometry(doc, prepared[i], partAttributes);
                    if (id == Guid.Empty)
                        throw new InvalidOperationException($"Failed to add replacement part {i}.");
                    newPartIds.Add(id);
                    WriteUserStrings(doc, id, new Dictionary<string, string> { { GisKeys.RhinoObjectId, id.ToString() } });
                }

                // Rhino's Replace/ReplaceObject API retains the original object's id and
                // attributes, including display properties, user strings, and layer.
                if (!Step("replace.rhino", () => doc.Objects.Replace(rhinoGuid, prepared[0], false)))
                    throw new InvalidOperationException($"Rhino could not replace object {rhinoGuid}.");
                primaryReplaced = true;

                // Establish the complete new group before retiring old pieces. Marker-based
                // readback remains authoritative, but a visible Rhino group is part of the UI
                // contract and every group operation must succeed.
                if (newPartIds.Count > 0)
                {
                    if (sharedGroups.Count == 0)
                    {
                        createdGroup = doc.Groups.Add();
                        if (createdGroup < 0)
                            throw new InvalidOperationException("Rhino could not create a multipart group.");
                        sharedGroups.Add(createdGroup);
                    }

                    foreach (int group in sharedGroups)
                    {
                        EnsureGroupMember(doc, group, rhinoGuid);
                        foreach (Guid id in newPartIds) EnsureGroupMember(doc, group, id);
                    }
                }

                // Retire only same-layer pieces carrying this exact gis.part_of marker. If one
                // deletion fails after another succeeded, the catch block restores every deleted
                // piece and the representative's original geometry.
                foreach (var part in oldParts)
                {
                    if (!doc.Objects.Delete(part, true, true))
                        throw new InvalidOperationException($"Rhino could not remove obsolete multipart piece {part.Id}.");
                    deletedOldParts.Add(part);
                }
            }
            catch (Exception mutationFailure)
            {
                var rollbackFailures = RollbackReplacement(
                    doc,
                    rhinoGuid,
                    originalGeometry,
                    primaryReplaced,
                    newPartIds,
                    deletedOldParts,
                    createdGroup);
                if (rollbackFailures.Count > 0)
                {
                    throw new InvalidOperationException(
                        mutationFailure.Message + " Rollback was incomplete: " + string.Join(" ", rollbackFailures),
                        mutationFailure);
                }
                throw;
            }
            finally
            {
                originalGeometry.Dispose();
            }

            index.SetParts(doc, owner, newPartIds);
            var ids = new List<Guid> { rhinoGuid };
            ids.AddRange(newPartIds);
            return ids;
        }

        /// <summary>
        /// Makes the pieces of a multipart feature one thing to the sync: the first object keeps the
        /// feature's identity, every other piece is stripped of it and marked as a part of the first,
        /// and all of them are grouped so they move together in Rhino.
        /// </summary>
        /// <remarks>
        /// Every piece used to carry the full identity, so a two-part street read back as two objects
        /// with the same sync guid and object id, each hashing to half the feature -- and each looking
        /// modified in Rhino when nothing had been touched. <see cref="ReadObjects"/> folds the marked
        /// parts back into their representative, so the engine sees the whole feature once.
        /// </remarks>
        private static void MarkParts(RhinoDoc doc, List<Guid> ids, IReadOnlyDictionary<string, string> userStrings)
        {
            string owner = null;
            if (userStrings != null) userStrings.TryGetValue(GisKeys.SyncGuid, out owner);
            if (string.IsNullOrEmpty(owner)) owner = ids[0].ToString();

            for (int i = 1; i < ids.Count; i++)
            {
                var ro = doc.Objects.FindId(ids[i]);
                if (ro == null) continue;

                var attrs = ro.Attributes.Duplicate();
                foreach (string key in attrs.GetUserStrings().AllKeys)
                    if (key != null) attrs.SetUserString(key, null);   // null deletes the entry

                attrs.SetUserString(GisKeys.PartOf, owner);
                attrs.SetUserString(GisKeys.PartIndex, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!doc.Objects.ModifyAttributes(ro, attrs, true))
                    throw new InvalidOperationException($"Rhino could not mark multipart piece {ro.Id}.");
            }

            var groupIndex = doc.Groups.Add();
            if (groupIndex < 0)
                throw new InvalidOperationException("Rhino could not create a multipart group.");
            try
            {
                foreach (var id in ids) EnsureGroupMember(doc, groupIndex, id);
            }
            catch
            {
                doc.Groups.Delete(groupIndex);
                throw;
            }
        }

        public void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings) =>
            WriteUserStrings(Doc(), rhinoGuid, userStrings);

        private static void WriteUserStrings(RhinoDoc doc, Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings)
        {
            if (userStrings == null) return;
            var ro = doc.Objects.FindId(rhinoGuid);
            if (ro == null) throw new InvalidOperationException($"Rhino object {rhinoGuid} not found.");
            foreach (var kv in userStrings)
                ro.Attributes.SetUserString(kv.Key, kv.Value);
            ro.CommitChanges();
        }

        public void IsolateFailed(IEnumerable<Guid> rhinoGuids)
        {
            if (rhinoGuids == null) return;
            var doc = Doc();
            doc.Objects.UnselectAll();
            foreach (var id in rhinoGuids)
                doc.Objects.Select(id);
            doc.Views.Redraw();
        }

        // ---- document text (IDocumentStringStore) ----

        public string GetDocumentString(string key) => Doc().Strings.GetValue(key);

        public void SetDocumentString(string key, string value)
        {
            var doc = Doc();
            if (value == null) doc.Strings.Delete(key);
            else doc.Strings.SetString(key, value);
        }

        // ---- georeferencing (IEarthAnchorSource) ----

        public CoreEarthAnchor GetEarthAnchor()
        {
            var doc = Doc();
            var eap = doc.EarthAnchorPoint;
            var north = eap.ModelNorth;
            return new CoreEarthAnchor
            {
                Latitude = eap.EarthBasepointLatitude,
                Longitude = eap.EarthBasepointLongitude,
                Elevation = eap.EarthBasepointElevation,
                ModelBasePoint = new CoreXyz(eap.ModelBasePoint.X, eap.ModelBasePoint.Y, eap.ModelBasePoint.Z),
                ModelUnits = GetUnits(),
                MetresPerModelUnit = MetresPerModelUnit(doc),
                NorthAngleDegrees = Math.Atan2(north.X, north.Y) * 180.0 / Math.PI,
                IsSet = eap.EarthLocationIsSet()
            };
        }

        public CoreAffine GetModelToEarthMetres()
        {
            var doc = Doc();
            Transform t = doc.EarthAnchorPoint.GetModelToEarthTransform(doc.ModelUnitSystem);
            return CoreAffine.FromRowMajor4x4(new[]
            {
                t.M00, t.M01, t.M02, t.M03,
                t.M10, t.M11, t.M12, t.M13,
                t.M20, t.M21, t.M22, t.M23,
                t.M30, t.M31, t.M32, t.M33
            });
        }

        // ---- push-side read path ----

        public IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName)
        {
            var doc = Doc();
            int idx = doc.Layers.FindByFullPath(layerName, -1);
            if (idx < 0) return new List<RhinoObjectSnapshot>();

            // Every object on the layer, whatever its visibility. The default enumerator skips
            // hidden objects and objects on a layer that is switched off, which turned a layer
            // someone was inspecting into "nothing in Rhino" and re-pulled the lot.
            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = true,
                ActiveObjects = true,
                ReferenceObjects = true,
                IncludeLights = false,
                IncludeGrips = false,
                LayerIndexFilter = idx
            };
            var objects = new List<RhinoObject>();
            foreach (RhinoObject ro in doc.Objects.GetObjectList(settings))
                if (ro != null && ro.Attributes.LayerIndex == idx) objects.Add(ro);

            return Assemble(doc, objects);
        }

        public IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects()
        {
            var doc = Doc();
            var objects = new List<RhinoObject>();
            foreach (RhinoObject ro in doc.Objects.GetSelectedObjects(false, false))
                if (ro != null) objects.Add(ro);

            return Assemble(doc, objects);
        }

        public RhinoObjectSnapshot ReadObject(Guid rhinoGuid)
        {
            var doc = Doc();
            var ro = doc.Objects.FindId(rhinoGuid);
            if (ro == null) return null;

            // The gis.part_of marker is the durable relationship. Groups are a Rhino UI affordance
            // and can be edited by a person, so readback must not depend on one still existing.
            var objects = new List<RhinoObject> { ro };
            string owner = ro.Attributes.GetUserString(GisKeys.SyncGuid);
            if (string.IsNullOrEmpty(owner)) owner = rhinoGuid.ToString();
            objects.AddRange(FindParts(doc, rhinoGuid, owner, ro.Attributes.LayerIndex));

            foreach (var snap in Assemble(doc, objects))
                if (snap.RhinoGuid == rhinoGuid) return snap;
            return null;
        }

        /// <summary>
        /// Snapshots the objects, folding the pieces of a multipart feature back into the one that
        /// carries its identity so the sync sees the whole feature as it was pulled.
        /// </summary>
        /// <remarks>
        /// Pieces are the objects <see cref="MarkParts"/> tagged with <see cref="GisKeys.PartOf"/>.
        /// Their geometry is appended to the representative's -- extra parts of a polyline, extra
        /// rings of a polygon, extra points of a multipoint -- in their original order. A piece whose
        /// representative is gone (deleted by the user, or simply not in this read) is reported on
        /// its own, as new Rhino work, rather than dropped: hiding geometry would be worse than
        /// offering it.
        /// </remarks>
        private static IReadOnlyList<RhinoObjectSnapshot> Assemble(RhinoDoc doc, List<RhinoObject> objects)
        {
            var snaps = new List<RhinoObjectSnapshot>(objects.Count);
            foreach (var ro in objects) snaps.Add(ToSnapshot(doc, ro));
            return MultipartAssembly.Assemble(snaps);
        }

        // ---- chunked reads (IBulkRhinoAdapter) ----

        public IReadOnlyList<Guid> ListObjects(string layerName)
        {
            var doc = Doc();
            var ids = new List<Guid>();
            int idx = doc.Layers.FindByFullPath(layerName, -1);
            if (idx < 0) return ids;
            foreach (RhinoObject ro in doc.Objects.GetObjectList(AllOnLayer(idx)))
                if (ro != null && ro.Attributes.LayerIndex == idx) ids.Add(ro.Id);
            return ids;
        }

        public IReadOnlyList<RhinoObjectSnapshot> SnapshotObjects(IReadOnlyList<Guid> ids)
        {
            var doc = Doc();
            var snaps = new List<RhinoObjectSnapshot>(ids.Count);
            foreach (var id in ids)
            {
                var ro = doc.Objects.FindId(id);
                if (ro != null) snaps.Add(ToSnapshot(doc, ro));
            }
            return snaps;
        }

        private static RhinoObjectSnapshot ToSnapshot(RhinoDoc doc, RhinoObject ro)
        {
            var descriptor = RhinoGeometryReader.Describe(ro.Geometry);
            var geometry = RhinoGeometryReader.ToNeutral(ro.Geometry, descriptor, doc.ModelAbsoluteTolerance);

            var userStrings = new Dictionary<string, string>(StringComparer.Ordinal);
            System.Collections.Specialized.NameValueCollection coll = ro.Attributes.GetUserStrings();
            foreach (string key in coll.AllKeys)
                if (key != null) userStrings[key] = coll[key];

            return new RhinoObjectSnapshot
            {
                RhinoGuid = ro.Id,
                LayerName = doc.Layers[ro.Attributes.LayerIndex].FullPath,
                Descriptor = descriptor,
                Geometry = geometry,
                UserStrings = userStrings
            };
        }

        // ---- helpers ----

        private static Point3d P(CoreXyz p) => new Point3d(p.X, p.Y, p.Z);

        /// <summary>Adds the geometry, returning every object it produced; the first is the representative.</summary>
        private static List<Guid> AddGeometry(RhinoDoc doc, NeutralGeometry g, ObjectAttributes attrs)
        {
            var ids = new List<Guid>();
            foreach (var geometry in PrepareGeometry(doc, g))
            {
                Guid id = AddPreparedGeometry(doc, geometry, attrs);
                if (id != Guid.Empty) ids.Add(id);
            }
            return ids;
        }

        private static List<GeometryBase> PrepareGeometry(RhinoDoc doc, NeutralGeometry g)
        {
            var result = new List<GeometryBase>();
            if (g == null) return result;

            switch (g.Kind)
            {
                case NeutralGeometryKind.Point:
                    if (g.Points != null)
                        foreach (var point in g.Points) result.Add(new global::Rhino.Geometry.Point(P(point)));
                    break;

                case NeutralGeometryKind.Polyline:
                    var parts = (g.Parts != null && g.Parts.Count > 0)
                        ? g.Parts
                        : new List<List<CoreXyz>> { g.Points };
                    foreach (var part in parts)
                    {
                        var points = ToPoints(part);
                        if (points.Count >= 2) result.Add(new PolylineCurve(points));
                    }
                    break;

                case NeutralGeometryKind.Polygon:
                    result.AddRange(PreparePolygon(doc, g));
                    break;

                case NeutralGeometryKind.Multipatch:
                    if (g.Mesh != null) result.Add(ToMesh(g.Mesh));
                    break;
            }

            return result;
        }

        private static List<GeometryBase> PreparePolygon(RhinoDoc doc, NeutralGeometry g)
        {
            var result = new List<GeometryBase>();
            if (g.Rings == null || g.Rings.Count == 0)
                return result;

            // Every ring is handed to CreatePlanarBreps, which works out the nesting itself: it
            // pairs each outer boundary with the rings inside it and trims those out as holes. That
            // matters because a polygon may contain several islands, so the rings cannot be read as
            // "first is outer, the rest are holes".
            var loops = new List<Curve>();
            foreach (var ring in g.Rings)
            {
                var points = ToPoints(ring);
                if (points.Count < 3) continue;

                if (points[0].DistanceTo(points[points.Count - 1]) > RhinoMath.ZeroTolerance)
                    points.Add(points[0]);   // a planar boundary has to close

                var curve = new PolylineCurve(points);
                if (curve.IsValid) loops.Add(curve);
            }

            if (loops.Count == 0) return result;

            Brep[] breps;
            try { breps = Brep.CreatePlanarBreps(loops, doc.ModelAbsoluteTolerance); }
            catch { breps = null; }

            if (breps != null && breps.Length > 0)
            {
                foreach (var brep in breps) result.Add(brep);
            }
            else
            {
                // Rings that are not coplanar or self-intersect cannot make a surface; keep them as
                // boundary curves rather than dropping the feature.
                foreach (var loop in loops) result.Add(loop);
            }

            return result;
        }

        private static Guid AddPreparedGeometry(RhinoDoc doc, GeometryBase geometry, ObjectAttributes attrs)
        {
            switch (geometry)
            {
                case global::Rhino.Geometry.Point point:
                    return doc.Objects.AddPoint(point.Location, attrs);
                case Curve curve:
                    return doc.Objects.AddCurve(curve, attrs);
                case Brep brep:
                    return doc.Objects.AddBrep(brep, attrs);
                case Mesh mesh:
                    return doc.Objects.AddMesh(mesh, attrs);
                default:
                    return Guid.Empty;
            }
        }

        private static List<RhinoObject> FindParts(RhinoDoc doc, Guid representative, string owner, int layerIndex)
        {
            var result = new List<RhinoObject>();
            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = true,
                ActiveObjects = true,
                ReferenceObjects = true,
                IncludeLights = false,
                IncludeGrips = false
            };
            foreach (var candidate in doc.Objects.GetObjectList(settings))
                if (candidate != null && candidate.Id != representative && candidate.Attributes.LayerIndex == layerIndex &&
                    !SyncIdentity.IsCopy(candidate.Id, candidate.Attributes.GetUserString(GisKeys.RhinoObjectId)) &&
                    string.Equals(candidate.Attributes.GetUserString(GisKeys.PartOf), owner, StringComparison.OrdinalIgnoreCase))
                    result.Add(candidate);
            return result;
        }

        private static void ValidateReplacementTargets(RhinoDoc doc, RhinoObject representative,
            List<RhinoObject> parts, string owner, int layerIndex, LayerIndex index)
        {
            var layer = layerIndex >= 0 && layerIndex < doc.Layers.Count ? doc.Layers[layerIndex] : null;
            if (representative.IsReference || layer == null || layer.IsReference || layer.IsReferenceParentLayer)
                throw new InvalidOperationException("A linked/reference Rhino object cannot be replaced.");
            if (representative.IsLocked || layer.IsLocked)
                throw new InvalidOperationException("Unlock the Rhino object and its layer before replacing its geometry.");

            foreach (var part in parts)
            {
                if (part.IsReference || !part.IsDeletable)
                    throw new InvalidOperationException($"Multipart piece {part.Id} cannot be safely replaced.");
                if (part.IsLocked)
                    throw new InvalidOperationException($"Unlock multipart piece {part.Id} before replacing its geometry.");
            }

            // A copied tracked representative can carry the same gis.sync_guid. Refuse to guess
            // which set of parts belongs to which object; importantly, do this before any write.
            if (index.HasOtherOwner(owner, representative.Id))
                throw new InvalidOperationException(
                    $"More than one Rhino object on this layer carries sync identity '{owner}'. Resolve the duplicate before syncing.");
        }

        // Membership is read off the object itself. GroupTable.GroupMembers walks the whole document
        // to answer, and asking it once per multipart piece made pulling a large layer quadratic.
        private static void EnsureGroupMember(RhinoDoc doc, int groupIndex, Guid objectId)
        {
            var groups = doc.Objects.FindId(objectId)?.GetGroupList();
            if (groups != null && Array.IndexOf(groups, groupIndex) >= 0) return;
            if (!doc.Groups.AddToGroup(groupIndex, objectId))
                throw new InvalidOperationException($"Rhino could not add object {objectId} to multipart group {groupIndex}.");
        }

        private static List<string> RollbackReplacement(RhinoDoc doc, Guid representative,
            GeometryBase originalGeometry, bool primaryReplaced, List<Guid> stagedParts,
            List<RhinoObject> deletedOldParts, int createdGroup)
        {
            var failures = new List<string>();

            if (createdGroup >= 0 && !doc.Groups.Delete(createdGroup))
                failures.Add($"Could not remove staged group {createdGroup}.");

            foreach (var id in stagedParts)
            {
                var staged = doc.Objects.FindId(id);
                if (staged != null && !doc.Objects.Delete(staged, true, true))
                    failures.Add($"Could not remove staged part {id}.");
            }

            if (primaryReplaced && !doc.Objects.Replace(representative, originalGeometry, true))
                failures.Add($"Could not restore representative {representative}.");

            foreach (var part in deletedOldParts)
                if (!doc.Objects.Undelete(part))
                    failures.Add($"Could not restore multipart piece {part.Id}.");

            return failures;
        }

        private static List<int> SharedGroups(RhinoDoc doc, RhinoObject representative, List<RhinoObject> parts)
        {
            // Groups the representative shares with at least one of its pieces, read off the pieces.
            var partGroups = new HashSet<int>();
            foreach (var part in parts)
                foreach (int group in part.GetGroupList() ?? Array.Empty<int>())
                    partGroups.Add(group);

            var groups = new List<int>();
            foreach (int group in representative.GetGroupList() ?? Array.Empty<int>())
                if (partGroups.Contains(group)) groups.Add(group);
            return groups;
        }

        private static List<Point3d> ToPoints(IEnumerable<CoreXyz> pts)
        {
            var list = new List<Point3d>();
            if (pts != null)
                foreach (var p in pts) list.Add(P(p));
            return list;
        }

        private static Mesh ToMesh(NeutralMesh nm)
        {
            var mesh = new Mesh();
            nm = MeshTopology.Clean(nm);
            if (nm != null)
            {
                if (nm.Vertices != null)
                    foreach (var v in nm.Vertices) mesh.Vertices.Add(v.X, v.Y, v.Z);

                if (nm.Faces != null)
                    foreach (var f in nm.Faces)
                    {
                        if (f.Length == 3) mesh.Faces.AddFace(f[0], f[1], f[2]);
                        else if (f.Length >= 4) mesh.Faces.AddFace(f[0], f[1], f[2], f[3]);
                    }

                mesh.Normals.ComputeNormals();
                mesh.Vertices.CombineIdentical(true, true);
                mesh.Faces.CullDegenerateFaces();
                mesh.Compact();
            }
            return mesh;
        }

        private static int EnsureLayerPath(RhinoDoc doc, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return doc.Layers.CurrentLayerIndex;

            string[] parts = fullPath.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
            string accum = null;
            Layer parent = null;

            foreach (var part in parts)
            {
                accum = accum == null ? part : accum + "::" + part;
                int idx = doc.Layers.FindByFullPath(accum, -1);
                if (idx < 0)
                {
                    var layer = new Layer { Name = part };
                    if (parent != null) layer.ParentLayerId = parent.Id;
                    idx = doc.Layers.Add(layer);
                }
                parent = doc.Layers[idx];
            }

            return doc.Layers.FindByFullPath(fullPath, doc.Layers.CurrentLayerIndex);
        }
    }
}
