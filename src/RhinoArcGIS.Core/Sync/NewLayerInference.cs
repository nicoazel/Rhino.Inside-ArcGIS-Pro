using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>What a Rhino layer would become as a new ArcGIS feature class.</summary>
    public sealed class NewLayerPlan
    {
        /// <summary>Feature class name, made safe for a geodatabase.</summary>
        public string Name { get; set; }

        /// <summary>Geometry the majority of the layer's objects classify as; Unsupported when none do.</summary>
        public GeometryTarget Target { get; set; } = GeometryTarget.Unsupported;

        /// <summary>One field per user-text key the objects carry, typed by what the values parse as.</summary>
        public List<FieldDefinition> Fields { get; } = new List<FieldDefinition>();

        public int ObjectCount { get; set; }

        /// <summary>How many objects classify as <see cref="Target"/>; the rest a push will skip.</summary>
        public int MatchingCount { get; set; }
    }

    /// <summary>
    /// Works out the feature class a Rhino layer should get when it is pushed somewhere no ArcGIS
    /// layer exists yet: the geometry type from what is on the layer, and a field per user-text key.
    /// Sync bookkeeping (<c>gis.*</c>) is not data and gets no field.
    /// </summary>
    public static class NewLayerInference
    {
        const int MaxNameLength = 60;
        const int MaxFieldNameLength = 64;
        static readonly HashSet<string> SystemFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OBJECTID", "OID", "FID", "GLOBALID", "SHAPE",
            "SHAPE_LENGTH", "SHAPE_AREA", "SHAPE_LEN", "SHAPE_LENG"
        };

        public static NewLayerPlan Plan(IReadOnlyList<RhinoObjectSnapshot> objects, string name)
        {
            var plan = new NewLayerPlan { Name = SanitizeName(name, "RhinoLayer", MaxNameLength) };
            if (objects == null) return plan;
            plan.ObjectCount = objects.Count;

            var byTarget = new Dictionary<GeometryTarget, int>();
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var keyOrder = new List<string>();

            foreach (var snap in objects)
            {
                if (snap == null) continue;

                var target = TargetOf(snap);
                if (target != GeometryTarget.Unsupported)
                    byTarget[target] = (byTarget.TryGetValue(target, out var n) ? n : 0) + 1;

                if (snap.UserStrings == null) continue;
                foreach (var kv in snap.UserStrings)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Key.StartsWith(GisKeys.Namespace, StringComparison.Ordinal)) continue;
                    if (!values.TryGetValue(kv.Key, out var list))
                    {
                        list = new List<string>();
                        values[kv.Key] = list;
                        keyOrder.Add(kv.Key);
                    }
                    if (kv.Value != null) list.Add(kv.Value);
                }
            }

            foreach (var kv in byTarget)
                if (kv.Value > plan.MatchingCount) { plan.Target = kv.Key; plan.MatchingCount = kv.Value; }

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keyOrder)
            {
                var fieldName = SanitizeName(key, "field", MaxFieldNameLength);
                if (SystemFieldNames.Contains(fieldName)) continue;
                if (!taken.Add(fieldName)) continue;   // two keys that sanitise the same: first wins
                plan.Fields.Add(Describe(fieldName, values[key]));
            }
            return plan;
        }

        /// <summary>
        /// Point, polyline, polygon or multipatch. Prefer the converted neutral geometry because
        /// it is the payload the writer will actually receive: surfaces, Breps and extrusions are
        /// tessellated by the Rhino adapter and therefore need a Multipatch target.
        /// </summary>
        static GeometryTarget TargetOf(RhinoObjectSnapshot snap)
        {
            if (snap.Geometry != null && !snap.Geometry.IsEmpty())
            {
                switch (snap.Geometry.Kind)
                {
                    case NeutralGeometryKind.Point: return GeometryTarget.PointZ;
                    case NeutralGeometryKind.Polyline: return GeometryTarget.PolylineZ;
                    case NeutralGeometryKind.Polygon: return GeometryTarget.PolygonZ;
                    case NeutralGeometryKind.Multipatch: return GeometryTarget.Multipatch;
                }
            }

            // These descriptors intentionally have no neutral payload today. Letting them vote
            // would create an empty feature class followed by a wholly skipped initial push.
            if (snap.Descriptor != null &&
                (snap.Descriptor.Kind == RhinoGeometryKind.SubD ||
                 snap.Descriptor.Kind == RhinoGeometryKind.Hatch ||
                 snap.Descriptor.Kind == RhinoGeometryKind.BlockInstance))
                return GeometryTarget.Unsupported;

            var target = GeometryClassifier.Classify(snap.Descriptor).Default;
            switch (target)
            {
                case GeometryTarget.PointZ:
                case GeometryTarget.PolylineZ:
                case GeometryTarget.PolygonZ:
                    return target;
                case GeometryTarget.Multipatch:
                    return GeometryTarget.Multipatch;
                case GeometryTarget.PolygonExtrusion:
                    return GeometryTarget.PolygonZ;
                default:
                    return GeometryTarget.Unsupported;
            }
        }

        /// <summary>
        /// Integer when every value is a whole number a 32-bit field holds, Double when every value
        /// is a number, Text otherwise; text fields are sized to the longest value with headroom.
        /// </summary>
        static FieldDefinition Describe(string name, List<string> samples)
        {
            bool anyValue = false, allInteger = true, allDouble = true;
            int longest = 0;

            foreach (var raw in samples)
            {
                var v = raw?.Trim();
                if (string.IsNullOrEmpty(v)) continue;
                anyValue = true;
                longest = Math.Max(longest, raw.Length);
                if (allInteger && !(long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l >= int.MinValue && l <= int.MaxValue))
                    allInteger = false;
                if (allDouble && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    allDouble = false;
            }

            var type = !anyValue ? FieldType.Text : allInteger ? FieldType.Integer : allDouble ? FieldType.Double : FieldType.Text;
            return new FieldDefinition
            {
                Name = name,
                Type = type,
                Length = type == FieldType.Text ? Math.Max(50, Math.Min(4000, longest * 2)) : 0,
                Nullable = true
            };
        }

        /// <summary>
        /// A geodatabase name: letters, digits and underscores, starting with a letter. Anything
        /// else becomes an underscore, so "Site Plan::Trees" reads as "Site_Plan_Trees".
        /// </summary>
        public static string SanitizeName(string raw, string fallback, int maxLength)
        {
            var sb = new StringBuilder();
            foreach (var c in raw ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
            }
            var text = sb.ToString().Trim('_');
            if (text.Length == 0) text = fallback;
            if (char.IsDigit(text[0])) text = "L_" + text;
            if (text.Length > maxLength) text = text.Substring(0, maxLength).TrimEnd('_');
            return text;
        }
    }
}
