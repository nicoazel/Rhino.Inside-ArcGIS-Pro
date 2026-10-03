using System.Collections.Generic;

namespace RhinoArcGIS.Core.Attributes
{
    /// <summary>
    /// One mapping between a Rhino user-string key and an ArcGIS field (spec 04 §4, 06 §8).
    /// </summary>
    public sealed class FieldMapping
    {
        public string RhinoKey { get; set; }
        public string ArcGisField { get; set; }
        public FieldType Type { get; set; } = FieldType.Text;
        public FieldOwnership Owner { get; set; } = FieldOwnership.Shared;
        public bool Required { get; set; }

        /// <summary>Optional unit hint (e.g. "meters", "square_meters") for unit-aware validation.</summary>
        public string Units { get; set; }

        /// <summary>Coded-value domain; empty when the field is free-text.</summary>
        public List<string> Domain { get; set; } = new List<string>();

        /// <summary>Named validators (e.g. "positive_number", "range:[0,20]").</summary>
        public List<string> Validators { get; set; } = new List<string>();

        /// <summary>If true the field is pulled into Rhino as read-only context and never pushed back.</summary>
        public bool ReadonlyInRhino { get; set; }

        public bool HasDomain => Domain != null && Domain.Count > 0;
    }
}
