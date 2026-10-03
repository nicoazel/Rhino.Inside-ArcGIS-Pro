using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;

namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>One field in an ArcGIS feature class, as reported by the ArcGIS adapter.</summary>
    public sealed class FieldDefinition
    {
        public string Name { get; set; }
        public FieldType Type { get; set; } = FieldType.Text;
        public int Length { get; set; }
        public bool Nullable { get; set; } = true;
        public bool Required { get; set; }

        /// <summary>
        /// False for a column the data source maintains itself and rejects writes to -- a
        /// geodatabase's OBJECTID, GlobalID, Shape_Length and Shape_Area. A shapefile's Shape_Leng
        /// / Shape_Area are ordinary editable columns that nothing keeps current.
        /// </summary>
        public bool Editable { get; set; } = true;
        public List<string> Domain { get; set; } = new List<string>();
    }

    /// <summary>The schema of an ArcGIS target layer (spec 04 §13).</summary>
    public sealed class LayerSchema
    {
        public string LayerName { get; set; }

        /// <summary>
        /// What the layer actually reads from -- workspace path plus feature class name -- as
        /// opposed to <see cref="LayerName"/>, which is only the label shown in the map. Two maps can
        /// carry the same label over different data; this is what tells them apart across sessions.
        /// </summary>
        public string Source { get; set; }
        /// <summary>Explicit coordinate-system identifier, normally EPSG:&lt;WKID&gt; or its name.</summary>
        public string Crs { get; set; }
        public GeometryTarget GeometryType { get; set; } = GeometryTarget.Unsupported;
        public bool ZEnabled { get; set; }
        public bool HasGlobalIds { get; set; }
        public bool Editable { get; set; }
        public List<FieldDefinition> Fields { get; set; } = new List<FieldDefinition>();

        public FieldDefinition FindField(string name)
        {
            if (Fields == null || name == null) return null;
            foreach (var f in Fields)
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }
    }
}
