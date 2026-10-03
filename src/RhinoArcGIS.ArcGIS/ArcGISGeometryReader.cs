using System.Collections.Generic;
using ArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Geometry;
using CoreXyz = RhinoArcGIS.Core.Spatial.Xyz;
using NeutralGeometry = RhinoArcGIS.Core.Geometry.NeutralGeometry;
using NeutralMesh = RhinoArcGIS.Core.Geometry.NeutralMesh;

namespace RhinoArcGIS.ArcGIS
{
    /// <summary>
    /// Converts ArcGIS geometry into the SDK-neutral <see cref="NeutralGeometry"/> the core uses.
    /// Coordinates stay in GIS space; the core applies the project transform afterwards.
    /// </summary>
    internal static class ArcGISGeometryReader
    {
        public static NeutralGeometry ToNeutral(Geometry shape)
        {
            if (shape == null || shape.IsEmpty) return null;

            switch (shape.GeometryType)
            {
                case GeometryType.Point:
                    return NeutralGeometry.Point(ToXyz((MapPoint)shape));

                case GeometryType.Polyline:
                    return ReadPolyline((Polyline)shape);

                case GeometryType.Polygon:
                    return ReadPolygon((Polygon)shape);

                case GeometryType.Multipoint:
                    return ReadMultipoint((Multipoint)shape);

                case GeometryType.Multipatch:
                    return ReadMultipatch((Multipatch)shape);

                default:
                    return null;
            }
        }

        private static CoreXyz ToXyz(MapPoint p)
            => new CoreXyz(p.X, p.Y, p.HasZ ? p.Z : 0.0);

        /// <summary>A multipoint is a point geometry with one entry per point.</summary>
        private static NeutralGeometry ReadMultipoint(Multipoint multipoint)
        {
            var geometry = new NeutralGeometry { Kind = NeutralGeometryKind.Point, Points = new List<CoreXyz>() };
            foreach (MapPoint p in multipoint.Points) geometry.Points.Add(ToXyz(p));
            return geometry.Points.Count == 0 ? null : geometry;
        }

        private static NeutralGeometry ReadPolyline(Polyline polyline)
        {
            // Read part by part. polyline.Points flattens them, which turns a multipart street into
            // one line that jumps between the pieces.
            var parts = new List<List<CoreXyz>>();
            foreach (ReadOnlySegmentCollection part in polyline.Parts)
            {
                var run = ReadRun(part);
                if (run.Count > 1) parts.Add(run);
            }

            if (parts.Count == 0) return null;

            var geometry = NeutralGeometry.Polyline(parts[0]);
            geometry.Parts = parts;
            return geometry;
        }

        /// <summary>Points of one part: each segment's start, plus the last segment's end.</summary>
        private static List<CoreXyz> ReadRun(ReadOnlySegmentCollection part)
        {
            var run = new List<CoreXyz>();
            MapPoint last = null;

            foreach (Segment seg in part)
            {
                run.Add(ToXyz(seg.StartPoint));
                last = seg.EndPoint;
            }

            if (last != null) run.Add(ToXyz(last));
            return run;
        }

        private static NeutralGeometry ReadPolygon(Polygon polygon)
        {
            var geom = new NeutralGeometry { Kind = NeutralGeometryKind.Polygon, Rings = new List<List<CoreXyz>>() };

            // Every part is a ring. Which are outer boundaries and which are holes is left to the
            // consumer to work out from nesting: a polygon may hold several islands, so "first is
            // outer, rest are holes" does not hold.
            foreach (ReadOnlySegmentCollection part in polygon.Parts)
            {
                var ring = ReadRun(part);
                if (ring.Count > 2) geom.Rings.Add(ring);
            }

            if (geom.Rings.Count == 0) return null;
            return geom;
        }

        /// <summary>
        /// Expands every patch back into indexed faces, then welds coincident patch coordinates so
        /// a mesh round-tripped through a multipatch feature class has shared Rhino vertices rather
        /// than carrying ArcGIS's per-patch coordinate storage through as triangle soup.
        /// A lone FirstRing is ear-clipped as a simple planar polygon. ArcGIS defines following
        /// Ring patches as belonging to that same polygon, so those groups are rejected until hole
        /// tessellation is implemented instead of being silently filled or discarded.
        /// </summary>
        private static NeutralGeometry ReadMultipatch(Multipatch multipatch)
        {
            ReadOnlyPointCollection points = multipatch.Points;
            var vertices = new List<CoreXyz>(points.Count);
            foreach (MapPoint p in points) vertices.Add(ToXyz(p));

            var faces = new List<int[]>();
            for (int patchIndex = 0; patchIndex < multipatch.PartCount; patchIndex++)
            {
                int start = multipatch.GetPatchStartPointIndex(patchIndex);
                int count = multipatch.GetPatchPointCount(patchIndex);
                PatchType type = multipatch.GetPatchType(patchIndex);

                switch (type)
                {
                    case PatchType.Triangles:
                        for (int i = 0; i + 2 < count; i += 3)
                            faces.Add(new[] { start + i, start + i + 1, start + i + 2 });
                        break;

                    case PatchType.TriangleFan:
                        for (int i = 1; i + 1 < count; i++)
                            faces.Add(new[] { start, start + i, start + i + 1 });
                        break;

                    case PatchType.TriangleStrip:
                        for (int i = 0; i + 2 < count; i++)
                            faces.Add(i % 2 == 0
                                ? new[] { start + i, start + i + 1, start + i + 2 }
                                : new[] { start + i + 1, start + i, start + i + 2 });
                        break;

                    case PatchType.FirstRing:
                    {
                        if (patchIndex + 1 < multipatch.PartCount &&
                            multipatch.GetPatchType(patchIndex + 1) == PatchType.Ring)
                            throw new System.NotSupportedException(
                                "Multipatch FirstRing/Ring groups may contain holes; this input is not supported until hole tessellation is available.");
                        var ring = new List<CoreXyz>(count);
                        for (int i = 0; i < count; i++) ring.Add(vertices[start + i]);
                        var triangles = PlanarRingTriangulation.TriangulateRingGroup(
                            new IReadOnlyList<CoreXyz>[] { ring });
                        foreach (int[] triangle in triangles)
                            faces.Add(new[] { start + triangle[0], start + triangle[1], start + triangle[2] });
                        break;
                    }

                    case PatchType.Ring:
                        throw new System.NotSupportedException(
                            "Multipatch Ring patches belong to a ring group that may contain holes; this input is not supported until hole tessellation is available.");

                    default:
                        throw new System.NotSupportedException($"Multipatch patch type '{type}' is not supported.");
                }
            }

            if (vertices.Count == 0 || faces.Count == 0) return null;

            var mesh = MeshTopology.Clean(new NeutralMesh { Vertices = vertices, Faces = faces });
            return new NeutralGeometry { Kind = NeutralGeometryKind.Multipatch, Mesh = mesh };
        }
    }
}
