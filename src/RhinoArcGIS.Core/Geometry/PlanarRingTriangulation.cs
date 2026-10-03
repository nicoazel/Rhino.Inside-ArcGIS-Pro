using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>Triangulates one simple, planar 3D boundary while retaining its input winding.</summary>
    public static class PlanarRingTriangulation
    {
        /// <summary>
        /// Triangulates a single-ring surface group. More than one ring is rejected because it may
        /// describe holes, which require a constrained tessellator rather than filling the outer ring.
        /// </summary>
        public static IReadOnlyList<int[]> TriangulateRingGroup(IReadOnlyList<IReadOnlyList<Xyz>> rings)
        {
            if (rings == null) throw new ArgumentNullException(nameof(rings));
            if (rings.Count != 1)
                throw new NotSupportedException("Multipatch ring groups with multiple rings (including holes) are not supported.");
            return Triangulate(rings[0]);
        }

        /// <summary>
        /// Returns triangles as indices into <paramref name="ring"/>. One repeated closing point,
        /// consecutive duplicate points, and collinear points are omitted from the triangulation.
        /// Degenerate rings with fewer than three usable vertices or zero area return no triangles.
        /// Non-planar or self-intersecting input is rejected.
        /// </summary>
        public static IReadOnlyList<int[]> Triangulate(IReadOnlyList<Xyz> ring)
        {
            if (ring == null) throw new ArgumentNullException(nameof(ring));
            if (ring.Count < 3) return Array.Empty<int[]>();

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                Xyz p = ring[i];
                if (!IsFinite(p.X) || !IsFinite(p.Y) || !IsFinite(p.Z))
                    throw new ArgumentException("Ring coordinates must be finite.", nameof(ring));
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }

            double scale = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            if (!(scale > 0) || !IsFinite(scale)) return Array.Empty<int[]>();
            double lengthTolerance = scale * 1e-12;
            double lengthToleranceSquared = lengthTolerance * lengthTolerance;
            double areaTolerance = scale * scale * 1e-14;

            var indices = new List<int>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
            {
                if (indices.Count == 0 || DistanceSquared(ring[indices[indices.Count - 1]], ring[i]) > lengthToleranceSquared)
                    indices.Add(i);
            }
            while (indices.Count > 1 && DistanceSquared(ring[indices[0]], ring[indices[indices.Count - 1]]) <= lengthToleranceSquared)
                indices.RemoveAt(indices.Count - 1);
            if (indices.Count < 3) return Array.Empty<int[]>();

            // Find a stable plane normal from the largest cross product anchored at the first point.
            Xyz origin = ring[indices[0]];
            double nx = 0, ny = 0, nz = 0, bestNormalSquared = 0;
            for (int i = 1; i < indices.Count - 1; i++)
            {
                Vector a = Subtract(ring[indices[i]], origin);
                for (int j = i + 1; j < indices.Count; j++)
                {
                    Vector b = Subtract(ring[indices[j]], origin);
                    Cross(a, b, out double x, out double y, out double z);
                    double squared = x * x + y * y + z * z;
                    if (squared > bestNormalSquared)
                    {
                        bestNormalSquared = squared;
                        nx = x; ny = y; nz = z;
                    }
                }
            }
            if (bestNormalSquared <= areaTolerance * areaTolerance) return Array.Empty<int[]>();

            double normalLength = Math.Sqrt(bestNormalSquared);
            nx /= normalLength; ny /= normalLength; nz /= normalLength;
            double planeTolerance = scale * 1e-9;
            foreach (int index in indices)
            {
                Vector delta = Subtract(ring[index], origin);
                double distance = Math.Abs(delta.X * nx + delta.Y * ny + delta.Z * nz);
                if (distance > planeTolerance)
                    throw new ArgumentException("Ring must be planar before it can be triangulated.", nameof(ring));
            }

            // Drop the axis with the largest normal component to preserve area and avoid a
            // near-edge-on projection. Vertex indices remain in the source ring's order.
            int droppedAxis = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0
                            : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
            var projected = new Point2[ring.Count];
            foreach (int index in indices)
            {
                Xyz p = ring[index];
                projected[index] = droppedAxis == 0 ? new Point2(p.Y, p.Z)
                               : droppedAxis == 1 ? new Point2(p.X, p.Z)
                               : new Point2(p.X, p.Y);
            }

            RemoveCollinear(indices, projected, areaTolerance);
            if (indices.Count < 3) return Array.Empty<int[]>();
            EnsureSimple(indices, projected, areaTolerance, lengthTolerance);

            double signedAreaTwice = SignedAreaTwice(indices, projected);
            if (Math.Abs(signedAreaTwice) <= areaTolerance) return Array.Empty<int[]>();
            double winding = signedAreaTwice > 0 ? 1 : -1;

            var remaining = new List<int>(indices);
            var triangles = new List<int[]>(remaining.Count - 2);
            int failedPasses = 0;
            while (remaining.Count > 3)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int previous = remaining[(i + remaining.Count - 1) % remaining.Count];
                    int current = remaining[i];
                    int next = remaining[(i + 1) % remaining.Count];
                    if (winding * Cross2(projected[previous], projected[current], projected[next]) <= areaTolerance)
                        continue;

                    bool containsVertex = false;
                    foreach (int candidate in remaining)
                    {
                        if (candidate == previous || candidate == current || candidate == next) continue;
                        if (PointInTriangle(projected[candidate], projected[previous], projected[current], projected[next], winding, areaTolerance))
                        {
                            containsVertex = true;
                            break;
                        }
                    }
                    if (containsVertex) continue;

                    triangles.Add(new[] { previous, current, next });
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }

                if (!clipped)
                {
                    if (++failedPasses > 1)
                        throw new ArgumentException("Ring could not be triangulated as a simple polygon.", nameof(ring));
                    RemoveCollinear(remaining, projected, areaTolerance);
                    if (remaining.Count < 3) return Array.Empty<int[]>();
                }
                else failedPasses = 0;
            }

            if (winding * Cross2(projected[remaining[0]], projected[remaining[1]], projected[remaining[2]]) > areaTolerance)
                triangles.Add(new[] { remaining[0], remaining[1], remaining[2] });
            return triangles;
        }

        static void RemoveCollinear(List<int> indices, Point2[] points, double areaTolerance)
        {
            bool changed;
            do
            {
                changed = false;
                if (indices.Count <= 3) return;
                for (int i = 0; i < indices.Count; i++)
                {
                    Point2 a = points[indices[(i + indices.Count - 1) % indices.Count]];
                    Point2 b = points[indices[i]];
                    Point2 c = points[indices[(i + 1) % indices.Count]];
                    double dot = (b.X - a.X) * (b.X - c.X) + (b.Y - a.Y) * (b.Y - c.Y);
                    if (Math.Abs(Cross2(a, b, c)) <= areaTolerance && dot <= areaTolerance)
                    {
                        indices.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            } while (changed);
        }

        static void EnsureSimple(List<int> indices, Point2[] points, double areaTolerance, double lengthTolerance)
        {
            int count = indices.Count;
            for (int i = 0; i < count; i++)
            {
                int iNext = (i + 1) % count;
                Point2 a = points[indices[i]], b = points[indices[iNext]];
                for (int j = i + 1; j < count; j++)
                {
                    int jNext = (j + 1) % count;
                    if (j == i || j == iNext || jNext == i) continue;
                    Point2 c = points[indices[j]], d = points[indices[jNext]];
                    if (SegmentsIntersect(a, b, c, d, areaTolerance, lengthTolerance))
                        throw new ArgumentException("Ring must be a simple polygon without self-intersections.", nameof(indices));
                }
            }
        }

        static bool SegmentsIntersect(Point2 a, Point2 b, Point2 c, Point2 d, double areaTolerance, double lengthTolerance)
        {
            double abC = Cross2(a, b, c), abD = Cross2(a, b, d);
            double cdA = Cross2(c, d, a), cdB = Cross2(c, d, b);
            if (((abC > areaTolerance && abD < -areaTolerance) || (abC < -areaTolerance && abD > areaTolerance)) &&
                ((cdA > areaTolerance && cdB < -areaTolerance) || (cdA < -areaTolerance && cdB > areaTolerance))) return true;
            return Math.Abs(abC) <= areaTolerance && OnSegment(a, b, c, lengthTolerance) ||
                   Math.Abs(abD) <= areaTolerance && OnSegment(a, b, d, lengthTolerance) ||
                   Math.Abs(cdA) <= areaTolerance && OnSegment(c, d, a, lengthTolerance) ||
                   Math.Abs(cdB) <= areaTolerance && OnSegment(c, d, b, lengthTolerance);
        }

        static bool OnSegment(Point2 a, Point2 b, Point2 p, double tolerance) =>
            p.X >= Math.Min(a.X, b.X) - tolerance && p.X <= Math.Max(a.X, b.X) + tolerance &&
            p.Y >= Math.Min(a.Y, b.Y) - tolerance && p.Y <= Math.Max(a.Y, b.Y) + tolerance;

        static bool PointInTriangle(Point2 p, Point2 a, Point2 b, Point2 c, double winding, double tolerance)
        {
            double ab = winding * Cross2(a, b, p);
            double bc = winding * Cross2(b, c, p);
            double ca = winding * Cross2(c, a, p);
            return ab >= -tolerance && bc >= -tolerance && ca >= -tolerance;
        }

        static double SignedAreaTwice(List<int> indices, Point2[] points)
        {
            Point2 origin = points[indices[0]];
            double area = 0;
            for (int i = 0; i < indices.Count; i++)
            {
                Point2 a = points[indices[i]], b = points[indices[(i + 1) % indices.Count]];
                area += (a.X - origin.X) * (b.Y - origin.Y) - (b.X - origin.X) * (a.Y - origin.Y);
            }
            return area;
        }

        static double Cross2(Point2 a, Point2 b, Point2 c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        static double DistanceSquared(Xyz a, Xyz b)
        {
            double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
            return x * x + y * y + z * z;
        }

        static Vector Subtract(Xyz a, Xyz b) => new Vector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        static void Cross(Vector a, Vector b, out double x, out double y, out double z)
        {
            x = a.Y * b.Z - a.Z * b.Y;
            y = a.Z * b.X - a.X * b.Z;
            z = a.X * b.Y - a.Y * b.X;
        }

        static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        readonly struct Point2
        {
            internal readonly double X, Y;
            internal Point2(double x, double y) { X = x; Y = y; }
        }

        readonly struct Vector
        {
            internal readonly double X, Y, Z;
            internal Vector(double x, double y, double z) { X = x; Y = y; Z = z; }
        }
    }
}
