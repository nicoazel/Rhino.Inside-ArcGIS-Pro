using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>
    /// The Rhino side of the interop (spec 06 §2). Implemented by <c>RhinoArcGIS.Rhino</c> against
    /// the in-process RhinoDoc. The core depends only on this abstraction.
    /// </summary>
    public interface IRhinoAdapter
    {
        /// <summary>The active document's unit system.</summary>
        UnitSystem GetUnits();

        /// <summary>All layer names in the active document.</summary>
        IReadOnlyList<string> GetLayerNames();

        /// <summary>Read every object on a layer as a neutral snapshot (coordinates in Rhino space).</summary>
        IReadOnlyList<RhinoObjectSnapshot> ReadObjects(string layerName);

        /// <summary>Read the current Rhino selection as neutral snapshots.</summary>
        IReadOnlyList<RhinoObjectSnapshot> ReadSelectedObjects();

        /// <summary>
        /// Read one object -- with the pieces of its multipart feature folded in, as
        /// <see cref="ReadObjects"/> would -- or null if it does not exist. Used straight after a
        /// create to record how Rhino itself represents what was just written.
        /// </summary>
        RhinoObjectSnapshot ReadObject(Guid rhinoGuid);

        /// <summary>Write/merge user strings onto an existing Rhino object.</summary>
        void WriteUserStrings(Guid rhinoGuid, IReadOnlyDictionary<string, string> userStrings);

        /// <summary>Create a Rhino object from neutral geometry (Rhino-space) on the named layer.</summary>
        Guid CreateObject(NeutralGeometry geometry, string layerName, IReadOnlyDictionary<string, string> userStrings);

        /// <summary>
        /// Replace an existing object's geometry in place, retaining its Rhino GUID, attributes,
        /// layer, and user strings. Implementations throw if the replacement cannot be completed.
        /// </summary>
        void ReplaceObjectGeometry(Guid rhinoGuid, NeutralGeometry geometry);

        /// <summary>Select/isolate objects that failed validation so the user can repair them (spec 03 §7).</summary>
        void IsolateFailed(IEnumerable<Guid> rhinoGuids);
    }
}
