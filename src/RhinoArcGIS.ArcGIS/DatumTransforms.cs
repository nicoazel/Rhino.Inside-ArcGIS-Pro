using System;
using System.Collections.Concurrent;
using System.Linq;
using ArcGIS.Core.Geometry;

namespace RhinoArcGIS.ArcGIS
{
    /// <summary>
    /// The datum transformation between two coordinate systems, chosen by ArcGIS.
    /// </summary>
    /// <remarks>
    /// Rhino's earth anchor is WGS84 latitude/longitude by definition. Most US survey data is on
    /// NAD83 (State Plane, NAD83 UTM, NAD83(2011)), which today sits about a metre and a half from
    /// WGS84 in New York. <see cref="IGeometryEngine.Project"/> alone ignores that difference, so
    /// anything anchored in WGS84 lands that far off in a NAD83 layer.
    ///
    /// ArcGIS decides the transformation: first the environment's preferred one
    /// (<see cref="ProjectionTransformation.CreateFromEnvironment"/>), then, when that brings no
    /// datum transformation although the datums differ, the one ArcGIS selects for the area of
    /// interest (<see cref="ProjectionTransformation.Create(SpatialReference, SpatialReference, Envelope)"/>).
    /// Choices are cached per coordinate-system pair and area, and described so they can be shown.
    /// Call on the MCT.
    /// </remarks>
    public static class DatumTransforms
    {
        public sealed class Choice
        {
            internal Choice(ProjectionTransformation transformation, string description)
            {
                Transformation = transformation;
                Description = description;
            }

            /// <summary>Null when the two systems share a datum and plain projection is exact.</summary>
            public ProjectionTransformation Transformation { get; }

            /// <summary>The transformation's name(s), or "none (same datum)".</summary>
            public string Description { get; }

            /// <summary>Projects a geometry into the target system, through the datum transformation.</summary>
            public Geometry Project(Geometry geometry, SpatialReference target) =>
                Transformation != null
                    ? GeometryEngine.Instance.ProjectEx(geometry, Transformation)
                    : GeometryEngine.Instance.Project(geometry, target);
        }

        static readonly ConcurrentDictionary<string, Choice> Cache = new ConcurrentDictionary<string, Choice>();

        /// <summary>
        /// The transformation from <paramref name="from"/> to <paramref name="to"/> for data around
        /// a WGS84 longitude/latitude (the area of interest steers ArcGIS's choice); NaN for anywhere.
        /// </summary>
        public static Choice Between(SpatialReference from, SpatialReference to, double longitude, double latitude)
        {
            if (from == null) throw new ArgumentNullException(nameof(from));
            if (to == null) throw new ArgumentNullException(nameof(to));
            // ArcGIS may pick different transformations for the two directions of a pair, and then
            // a round trip does not close (about 5 mm between WGS84 and NAD83(2011) in New York).
            // Choose once, from the WGS84 side, and invert that exact transformation for the way back.
            if (Canonical(to, from))
            {
                var forward = Between(to, from, longitude, latitude);
                return forward.Transformation == null
                    ? forward
                    : new Choice(forward.Transformation.GetInverse(), forward.Description + " (inverse)");
            }
            var key = $"{Key(from)}>{Key(to)}@{Math.Round(longitude, 1)},{Math.Round(latitude, 1)}";
            return Cache.GetOrAdd(key, _ => Choose(from, to, longitude, latitude));
        }

        /// <summary>
        /// Whether (a, b) is the direction a pair's transformation is chosen in: from WGS84's
        /// datum when one side is on it, otherwise a fixed order of the two systems.
        /// </summary>
        static bool Canonical(SpatialReference a, SpatialReference b)
        {
            bool aWgs = (a.Gcs?.Wkid ?? a.Wkid) == 4326, bWgs = (b.Gcs?.Wkid ?? b.Wkid) == 4326;
            if (aWgs != bWgs) return aWgs;
            return string.CompareOrdinal(Key(a), Key(b)) < 0;
        }

        static string Key(SpatialReference sr) => sr.Wkid != 0 ? sr.Wkid.ToString() : sr.Wkt.GetHashCode().ToString();

        static Choice Choose(SpatialReference from, SpatialReference to, double longitude, double latitude)
        {
            if (!DatumsDiffer(from, to)) return new Choice(null, "none (same datum)");

            ProjectionTransformation chosen = null;
            try { chosen = ProjectionTransformation.CreateFromEnvironment(from, to); }
            catch { /* no environment preference */ }

            if (chosen?.Transformation == null)
            {
                // NaN: no particular place, let ArcGIS choose for the systems' whole extent.
                // The area of interest is expected in the source system, not in WGS84.
                Envelope area = null;
                if (!double.IsNaN(longitude) && !double.IsNaN(latitude))
                {
                    var wgs84Area = EnvelopeBuilderEx.CreateEnvelope(longitude - 0.05, latitude - 0.05,
                        longitude + 0.05, latitude + 0.05, SpatialReferences.WGS84);
                    area = GeometryEngine.Instance.Project(wgs84Area, from) as Envelope;
                }
                chosen = ProjectionTransformation.Create(from, to, area);
            }

            return chosen?.Transformation == null
                ? new Choice(null, "none available (projected without a datum transformation)")
                : new Choice(chosen, Describe(chosen.Transformation));
        }

        static bool DatumsDiffer(SpatialReference a, SpatialReference b)
        {
            var ga = a.Gcs ?? a;
            var gb = b.Gcs ?? b;
            return ga.Wkid != 0 && gb.Wkid != 0 ? ga.Wkid != gb.Wkid : !string.Equals(ga.Wkt, gb.Wkt, StringComparison.Ordinal);
        }

        static string Describe(DatumTransformation transformation)
        {
            if (transformation is CompositeGeographicTransformation composite)
                return string.Join(" + ", composite.Transformations.Select(t => t.Name + (t.IsForward ? string.Empty : " (reversed)")));
            if (transformation is GeographicTransformation single)
                return single.Name + (single.IsForward ? string.Empty : " (reversed)");
            return transformation.ToString();
        }
    }
}
