using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Change
{
    /// <summary>
    /// Stable hashing for change detection (spec 06 §7). All hashes are lower-case hex SHA-256
    /// of a canonical, culture-invariant string so the same input always yields the same hash.
    /// </summary>
    public static class Hashing
    {
        /// <summary>Hash an arbitrary canonical string.</summary>
        public static string Hash(string canonical)
        {
            if (canonical == null) canonical = string.Empty;
            // One hasher per thread: creating one per call dominated hashing a 100k-feature layer.
            var sha = _sha ?? (_sha = SHA256.Create());
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            var hex = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                hex[2 * i] = HexDigits[bytes[i] >> 4];
                hex[2 * i + 1] = HexDigits[bytes[i] & 0xF];
            }
            return new string(hex);
        }

        [ThreadStatic] static SHA256 _sha;
        const string HexDigits = "0123456789abcdef";

        /// <summary>Hash a single field value (null and empty hash identically).</summary>
        public static string HashField(string value) => Hash(value ?? string.Empty);

        /// <summary>
        /// Hash an attribute set independent of key ordering. Keys are sorted ordinally and
        /// system (<c>gis.*</c>) keys are excluded so sync bookkeeping never changes the hash.
        /// </summary>
        public static string HashAttributes(IEnumerable<KeyValuePair<string, string>> attributes)
        {
            var keys = new List<string>();
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (attributes != null)
            {
                foreach (var kv in attributes)
                {
                    if (kv.Key != null && kv.Key.StartsWith(Identity.GisKeys.Namespace, StringComparison.Ordinal))
                        continue;
                    map[kv.Key] = kv.Value ?? string.Empty;
                    keys.Add(kv.Key);
                }
            }
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            foreach (string k in keys)
            {
                sb.Append(k).Append('=').Append(map[k]).Append('\n');
            }
            return Hash(sb.ToString());
        }

        /// <summary>
        /// Hash an ordered point sequence (polyline / point geometry). Coordinates are rounded
        /// to <paramref name="decimals"/> places to keep the hash stable against float noise.
        /// </summary>
        /// <summary>
        /// Decimal places geometry is rounded to before hashing, in CRS units. Coordinates make a
        /// round trip through two affine transforms between hashes, which perturbs them at around
        /// 1e-9; the coarser this rounding, the rarer a boundary flip that reads as a change. Four
        /// decimals is 0.1 mm for metre or foot CRSs, well under any survey tolerance.
        /// </summary>
        public const int GeometryDecimals = 4;

        /// <summary>
        /// Hash of a geometry's shape that is indifferent to how the shape happens to be
        /// represented: which vertex a closed ring starts at, which way round it runs, and the order
        /// of parts and rings.
        /// </summary>
        /// <remarks>
        /// The same feature comes back from Rhino and from ArcGIS in different representations. A
        /// polygon boundary trimmed into a Brep can restart at a different vertex or run the other
        /// way; a multipart feature reassembled from grouped Rhino objects can list its parts in a
        /// different order; ArcGIS repeats a ring's first point at the end where Rhino may not. None
        /// of that is a change the user made, and a hash that treated it as one would push
        /// untouched geometry back over the original. So each ring is opened, rotated to start at its
        /// smallest vertex and oriented so its second vertex is the smaller neighbour; each open part
        /// is oriented to run from its smaller end; and parts and rings are sorted. Meshes are hashed
        /// as they are, since Rhino does not reorder them.
        /// </remarks>
        public static string HashGeometry(RhinoArcGIS.Core.Geometry.NeutralGeometry g, int decimals = GeometryDecimals)
        {
            if (g == null) return Hash(string.Empty);

            string fmt = "F" + decimals.ToString(CultureInfo.InvariantCulture);
            var runs = new List<string>();

            switch (g.Kind)
            {
                case RhinoArcGIS.Core.Geometry.NeutralGeometryKind.Point:
                    // Every point of a (multi)point is its own run, so ordering falls out of the sort.
                    foreach (var p in g.AllPoints())
                        runs.Add(Format(new List<Vertex> { new Vertex(p, fmt) }));
                    break;

                case RhinoArcGIS.Core.Geometry.NeutralGeometryKind.Polyline:
                    if (g.Parts != null && g.Parts.Count > 0)
                        foreach (var part in g.Parts) { if (part != null) runs.Add(Format(CanonicalOpen(Vertices(part, fmt)))); }
                    else if (g.Points != null)
                        runs.Add(Format(CanonicalOpen(Vertices(g.Points, fmt))));
                    break;

                case RhinoArcGIS.Core.Geometry.NeutralGeometryKind.Polygon:
                    if (g.Rings != null)
                        foreach (var ring in g.Rings) { if (ring != null) runs.Add(Format(CanonicalRing(Vertices(ring, fmt)))); }
                    break;

                default:
                    return HashPoints(g.AllPoints(), decimals);
            }

            runs.Sort(StringComparer.Ordinal);
            return Hash(string.Join("|", runs));
        }

        /// <summary>
        /// A vertex as it is hashed: each coordinate rounded and formatted once. Ordering compares
        /// these strings, so canonicalising a ring never formats the same coordinate twice.
        /// </summary>
        readonly struct Vertex
        {
            public readonly string X, Y, Z;
            public Vertex(Xyz p, string fmt) { X = Round(p.X, fmt); Y = Round(p.Y, fmt); Z = Round(p.Z, fmt); }
        }

        static List<Vertex> Vertices(IList<Xyz> points, string fmt)
        {
            var list = new List<Vertex>(points.Count);
            foreach (var p in points) list.Add(new Vertex(p, fmt));
            return list;
        }

        static string Format(List<Vertex> points)
        {
            var sb = new StringBuilder(points.Count * 36);
            foreach (var p in points)
                sb.Append(p.X).Append(',').Append(p.Y).Append(',').Append(p.Z).Append(';');
            return sb.ToString();
        }

        /// <summary>Formats a coordinate, folding "-0.0000" into "0.0000".</summary>
        static string Round(double v, string fmt)
        {
            var s = v.ToString(fmt, CultureInfo.InvariantCulture);
            return s.StartsWith("-", StringComparison.Ordinal) && s.Trim('-', '0', '.').Length == 0 ? s.Substring(1) : s;
        }

        static int Compare(Vertex a, Vertex b)
        {
            int c = string.CompareOrdinal(a.X, b.X);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Y, b.Y);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Z, b.Z);
        }

        /// <summary>An open run, oriented to start from its smaller end.</summary>
        static List<Vertex> CanonicalOpen(List<Vertex> list)
        {
            if (list.Count > 1 && Compare(list[list.Count - 1], list[0]) < 0) list.Reverse();
            return list;
        }

        /// <summary>
        /// A closed ring with the closing duplicate removed, rotated to start at its smallest vertex
        /// and oriented so the vertex after it is the smaller of its two neighbours.
        /// </summary>
        static List<Vertex> CanonicalRing(List<Vertex> list)
        {
            if (list.Count > 1 && Compare(list[0], list[list.Count - 1]) == 0)
                list.RemoveAt(list.Count - 1);
            if (list.Count < 3) return list;

            int start = 0;
            for (int i = 1; i < list.Count; i++)
                if (Compare(list[i], list[start]) < 0) start = i;

            int n = list.Count;
            var next = list[(start + 1) % n];
            var prev = list[(start - 1 + n) % n];
            bool forward = Compare(next, prev) <= 0;

            var result = new List<Vertex>(n);
            for (int k = 0; k < n; k++)
                result.Add(forward ? list[(start + k) % n] : list[(start - k + n) % n]);
            return result;
        }

        public static string HashPoints(IEnumerable<Xyz> points, int decimals = 6)
        {
            var sb = new StringBuilder();
            if (points != null)
            {
                string fmt = "F" + decimals.ToString(CultureInfo.InvariantCulture);
                foreach (Xyz p in points)
                {
                    sb.Append(p.X.ToString(fmt, CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(p.Y.ToString(fmt, CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(p.Z.ToString(fmt, CultureInfo.InvariantCulture)).Append(';');
                }
            }
            return Hash(sb.ToString());
        }
    }
}
