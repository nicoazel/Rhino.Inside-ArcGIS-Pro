using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;

namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>
    /// A neutral record for one feature/object as seen by the core, used in both directions.
    /// Attributes are un-namespaced design fields; sync bookkeeping lives in <see cref="Identity"/>.
    /// </summary>
    public sealed class FeatureRecord
    {
        public SyncIdentity Identity { get; set; } = new SyncIdentity();
        public GeometryTarget Target { get; set; } = GeometryTarget.Unsupported;
        public NeutralGeometry Geometry { get; set; }
        public Dictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// A Rhino object as read by the Rhino adapter: its descriptor (for classification), its
    /// current geometry, and its raw user strings (system + design).
    /// </summary>
    public sealed class RhinoObjectSnapshot
    {
        public Guid RhinoGuid { get; set; }
        public string LayerName { get; set; }
        public GeometryDescriptor Descriptor { get; set; }
        public NeutralGeometry Geometry { get; set; }
        public Dictionary<string, string> UserStrings { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
