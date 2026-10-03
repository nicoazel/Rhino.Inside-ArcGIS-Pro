using System;
using System.Collections.Generic;
using System.Globalization;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Attributes
{
    /// <summary>What a derived field measures.</summary>
    public enum DerivedMetric
    {
        None = 0,
        Length,
        Area
    }

    /// <summary>
    /// Geometry metrics that ArcGIS keeps in attribute columns. A geodatabase maintains
    /// <c>Shape_Length</c> / <c>Shape_Area</c> itself; a shapefile only has whatever someone last
    /// wrote into <c>Shape_Leng</c> / <c>Shape_Area</c>, so a feature pushed from Rhino would carry
    /// zeros. These are recomputed from the pushed geometry, in the layer's CRS units, whenever
    /// geometry is written for a field the profile marks <see cref="FieldOwnership.Derived"/>.
    /// </summary>
    public static class DerivedFields
    {
        /// <summary>
        /// Which metric a field name denotes, by ArcGIS's naming: <c>Shape_Length</c>,
        /// <c>Shape_Leng</c>, <c>Shape_Le_1</c>, <c>Shape_Area</c>, <c>Shape_Ar_1</c> and the like
        /// (shapefiles truncate to ten characters and de-duplicate with a numeric suffix).
        /// </summary>
        public static DerivedMetric MetricOf(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return DerivedMetric.None;
            if (!fieldName.StartsWith("shape", StringComparison.OrdinalIgnoreCase)) return DerivedMetric.None;

            string rest = fieldName.Substring(5).TrimStart('_', '.');
            if (rest.StartsWith("le", StringComparison.OrdinalIgnoreCase)) return DerivedMetric.Length;
            if (rest.StartsWith("ar", StringComparison.OrdinalIgnoreCase)) return DerivedMetric.Area;
            return DerivedMetric.None;
        }

        /// <summary>
        /// The field's value for a geometry in GIS space, as ArcGIS would store it, or null when the
        /// field is not a metric or the geometry has none (a point has neither length nor area).
        /// Planar, in the coordinates' own units, like ArcGIS's own Shape_* columns.
        /// </summary>
        public static string Compute(string fieldName, NeutralGeometry gisGeometry)
        {
            double? value = Measure(MetricOf(fieldName), gisGeometry);
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : null;
        }

        public static double? Measure(DerivedMetric metric, NeutralGeometry g)
        {
            if (g == null) return null;
            switch (metric)
            {
                case DerivedMetric.Length:
                    if (g.Kind == NeutralGeometryKind.Polyline) return Length(LineRuns(g), closed: false);
                    if (g.Kind == NeutralGeometryKind.Polygon) return Length(g.Rings, closed: true);
                    return null;

                case DerivedMetric.Area:
                    return g.Kind == NeutralGeometryKind.Polygon ? Area(g.Rings) : (double?)null;

                default:
                    return null;
            }
        }

        static IEnumerable<List<Xyz>> LineRuns(NeutralGeometry g)
        {
            if (g.Parts != null && g.Parts.Count > 0) return g.Parts;
            return new[] { g.Points ?? new List<Xyz>() };
        }

        static double Length(IEnumerable<List<Xyz>> runs, bool closed)
        {
            double total = 0;
            foreach (var run in runs)
            {
                if (run == null || run.Count < 2) continue;
                for (int i = 1; i < run.Count; i++) total += Distance2D(run[i - 1], run[i]);
                if (closed && !Same2D(run[0], run[run.Count - 1])) total += Distance2D(run[run.Count - 1], run[0]);
            }
            return total;
        }

        /// <summary>
        /// Outer rings add, holes subtract. Which is which is decided by nesting rather than by
        /// orientation, since a ring drawn in Rhino can run either way: a ring inside an odd number
        /// of larger rings is a hole.
        /// </summary>
        static double Area(List<List<Xyz>> rings)
        {
            if (rings == null) return 0;
            var areas = new List<double>(rings.Count);
            foreach (var ring in rings) areas.Add(Math.Abs(Shoelace(ring)));

            double total = 0;
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i] == null || rings[i].Count < 3) continue;
                int depth = 0;
                for (int j = 0; j < rings.Count; j++)
                    if (j != i && areas[j] > areas[i] && Contains(rings[j], rings[i][0])) depth++;
                total += depth % 2 == 0 ? areas[i] : -areas[i];
            }
            return total;
        }

        static double Shoelace(List<Xyz> ring)
        {
            if (ring == null || ring.Count < 3) return 0;
            double sum = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2.0;
        }

        /// <summary>Ray-casting point-in-polygon on X/Y.</summary>
        static bool Contains(List<Xyz> ring, Xyz p)
        {
            if (ring == null || ring.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                bool crosses = (a.Y > p.Y) != (b.Y > p.Y);
                if (crosses && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        static double Distance2D(Xyz a, Xyz b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static bool Same2D(Xyz a, Xyz b) => a.X == b.X && a.Y == b.Y;
    }
}
