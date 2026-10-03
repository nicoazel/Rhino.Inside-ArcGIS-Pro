using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Geometry;

namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>One object for <see cref="IBulkRhinoAdapter.CreateObjects"/> to add.</summary>
    public sealed class NewRhinoObject
    {
        public NeutralGeometry Geometry { get; set; }
        public IReadOnlyDictionary<string, string> UserStrings { get; set; }
    }

    /// <summary>What became of one <see cref="NewRhinoObject"/>.</summary>
    public sealed class CreatedRhinoObject
    {
        /// <summary>The representative object's id; <see cref="Guid.Empty"/> when it failed.</summary>
        public Guid Id { get; set; }

        /// <summary>The object as Rhino holds it after the add, parts folded in; null if unreadable.</summary>
        public RhinoObjectSnapshot ReadBack { get; set; }

        /// <summary>Why the object could not be created, when it could not.</summary>
        public string Error { get; set; }
    }

    /// <summary>One object's geometry for <see cref="IBulkRhinoAdapter.ReplaceGeometries"/>.</summary>
    public sealed class GeometryReplacement
    {
        public Guid Id { get; set; }
        public NeutralGeometry Geometry { get; set; }
    }

    /// <summary>
    /// Set-at-a-time counterparts of the per-object <see cref="IRhinoAdapter"/> writes, for layers
    /// of tens of thousands of objects.
    /// </summary>
    /// <remarks>
    /// Per object, a hosted Rhino pays for a hop to its UI thread, an undo record, a redraw request
    /// and -- to find a multipart feature's pieces -- a walk of the whole document; the last of these
    /// made pulling a layer quadratic. A bulk call pays for each once. Optional: the services use
    /// it when an adapter offers it (see <see cref="Sync.RhinoBatch"/>) and fall back to the
    /// per-object calls otherwise, so each item still succeeds or fails on its own.
    /// </remarks>
    public interface IBulkRhinoAdapter
    {
        /// <summary>
        /// Adds every object to <paramref name="layerName"/>, returning one result per input in
        /// order. A failed item does not stop the rest.
        /// </summary>
        IReadOnlyList<CreatedRhinoObject> CreateObjects(string layerName, IReadOnlyList<NewRhinoObject> objects);

        /// <summary>Writes each object's user strings (a null value deletes the key).</summary>
        void WriteUserStrings(IReadOnlyList<KeyValuePair<Guid, IReadOnlyDictionary<string, string>>> writes);

        /// <summary>
        /// Replaces each object's geometry in place, as <see cref="IRhinoAdapter.ReplaceObjectGeometry"/>
        /// does, returning the object as read back (<see cref="CreatedRhinoObject.Id"/> is the
        /// replaced object) or the error for that one item.
        /// </summary>
        IReadOnlyList<CreatedRhinoObject> ReplaceGeometries(IReadOnlyList<GeometryReplacement> replacements);

        /// <summary>
        /// Repaints the views. Bulk writes hold redraws while they run, and a caller making several
        /// (the UI-thread decorator splits one into chunks) repaints once at the end.
        /// </summary>
        void Redraw();

        /// <summary>The ids of every object on the layer, hidden and locked ones included.</summary>
        IReadOnlyList<Guid> ListObjects(string layerName);

        /// <summary>
        /// One snapshot per object, as it is: multipart pieces are not folded into their
        /// representatives (<see cref="MultipartAssembly"/> does that once all are read). Ids no
        /// longer in the document are left out.
        /// </summary>
        IReadOnlyList<RhinoObjectSnapshot> SnapshotObjects(IReadOnlyList<Guid> ids);
    }
}
