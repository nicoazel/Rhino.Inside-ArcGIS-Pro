using System.Collections.Generic;
using ArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Geometry;
using NeutralGeometry = RhinoArcGIS.Core.Geometry.NeutralGeometry;
using NeutralMesh = RhinoArcGIS.Core.Geometry.NeutralMesh;
using CoreXyz = RhinoArcGIS.Core.Spatial.Xyz;

namespace RhinoArcGIS.ArcGIS
{
    /// <summary>
    /// Converts SDK-neutral geometry (already in GIS coordinates) into ArcGIS geometry for writing.
    /// </summary>
    /// <remarks>
    /// The output type is chosen by the <em>target layer</em>, not by whatever the Rhino object
    /// happened to be. Deciding from the neutral kind alone means a closed curve always becomes a
    /// polygon and an open one always a polyline, so pushing either into a feature class that wants
    /// the other is refused by ArcGIS with a bare "no support for this geometry type" that names
    /// neither side of the mismatch. A closed curve is a good polygon boundary and a polygon ring is
    /// a good line, so these are converted rather than rejected.
    /// </remarks>
    internal static class ArcGISGeometryWriter
    {
        public static Geometry ToArcGIS(NeutralGeometry g, SpatialReference sr, GeometryType target)
        {
            if (g == null) return null;

            switch (target)
            {
                case GeometryType.Point:
                    return FirstPoint(g, sr);

                case GeometryType.Polyline:
                    return BuildPolyline(g, sr);

                case GeometryType.Polygon:
                    return BuildPolygon(g, sr);

                case GeometryType.Multipoint:
                    return BuildMultipoint(g, sr);

                case GeometryType.Multipatch:
                    return BuildMultipatch(g, sr);

                default:
                    return null;
            }
        }

        /// <summary>Runs of points to write: explicit parts, else the point list, else the rings.</summary>
        private static List<List<CoreXyz>> Runs(NeutralGeometry g)
        {
            if (g.Parts != null && g.Parts.Count > 0) return g.Parts;
            if (g.Points != null && g.Points.Count > 0) return new List<List<CoreXyz>> { g.Points };
            if (g.Rings != null && g.Rings.Count > 0) return g.Rings;
            return new List<List<CoreXyz>>();
        }

        private static Geometry FirstPoint(NeutralGeometry g, SpatialReference sr)
        {
            foreach (var run in Runs(g))
                foreach (var p in run)
                    return Pt(p, sr);
            return null;
        }

        private static Geometry BuildMultipoint(NeutralGeometry g, SpatialReference sr)
        {
            var builder = new MultipointBuilderEx(sr);
            foreach (var run in Runs(g))
                foreach (var p in run)
                    builder.AddPoint(Pt(p, sr));
            return builder.PointCount > 0 ? builder.ToGeometry() : null;
        }

        private static Geometry BuildPolyline(NeutralGeometry g, SpatialReference sr)
        {
            var builder = new PolylineBuilderEx(sr);
            var wrote = false;

            foreach (var run in Runs(g))
            {
                if (run == null || run.Count < 2) continue;
                builder.AddPart(Points(run, sr));
                wrote = true;
            }

            return wrote ? builder.ToGeometry() : null;
        }

        /// <summary>
        /// Builds a polygon from every ring, so inner rings go back as holes instead of only the
        /// outer boundary surviving the round trip.
        /// </summary>
        private static Geometry BuildPolygon(NeutralGeometry g, SpatialReference sr)
        {
            var rings = (g.Rings != null && g.Rings.Count > 0) ? g.Rings : Runs(g);

            var builder = new PolygonBuilderEx(sr);
            var wrote = false;

            foreach (var ring in rings)
            {
                if (ring == null || ring.Count < 3) continue;
                builder.AddPart(Points(ring, sr));
                wrote = true;
            }

            if (!wrote) return null;

            // Rings read back from a Brep can run either way round. ArcGIS decides outer from hole
            // by ring orientation, so an unsimplified polygon with a reversed ring would store the
            // hole as a second island. Simplify puts the orientation right without moving a vertex.
            var polygon = builder.ToGeometry();
            return GeometryEngine.Instance.SimplifyAsFeature(polygon, true);
        }

        /// <summary>
        /// Builds a compact multipatch. Triangular faces share one Triangles patch; each quad or
        /// polygon face is a TriangleFan, so a quad needs four coordinates instead of being split
        /// into two disconnected three-coordinate triangles. ArcGIS multipatches have per-patch
        /// coordinates rather than a global indexed vertex pool, so shared topology is restored by
        /// <see cref="MeshTopology.Clean"/> when reading back.
        /// </summary>
        private static Geometry BuildMultipatch(NeutralGeometry g, SpatialReference sr)
        {
            NeutralMesh mesh = MeshTopology.Clean(g.Mesh);
            if (mesh?.Vertices == null || mesh.Vertices.Count == 0 || mesh.Faces == null || mesh.Faces.Count == 0)
                return null;

            var builder = new MultipatchBuilderEx(sr);
            Patch triangles = builder.MakePatch(PatchType.Triangles);

            foreach (int[] face in mesh.Faces)
            {
                if (face == null || face.Length < 3) continue;

                if (face.Length == 3)
                {
                    AddTriangle(triangles, mesh.Vertices, face[0], face[1], face[2], sr);
                    continue;
                }

                Patch fan = builder.MakePatch(PatchType.TriangleFan);
                foreach (int index in face) fan.AddPoint(Pt(mesh.Vertices[index], sr));
                if (fan.Coords.Count >= 3) builder.Patches.Add(fan);
            }

            if (triangles.Coords.Count > 0) builder.Patches.Insert(0, triangles);
            if (builder.Patches.Count == 0) return null;
            return builder.ToGeometry();
        }

        private static void AddTriangle(Patch patch, List<CoreXyz> vertices, int a, int b, int c, SpatialReference sr)
        {
            patch.AddPoint(Pt(vertices[a], sr));
            patch.AddPoint(Pt(vertices[b], sr));
            patch.AddPoint(Pt(vertices[c], sr));
        }

        private static MapPoint Pt(CoreXyz p, SpatialReference sr)
            => MapPointBuilderEx.CreateMapPoint(p.X, p.Y, p.Z, sr);

        private static List<MapPoint> Points(IEnumerable<CoreXyz> pts, SpatialReference sr)
        {
            var list = new List<MapPoint>();
            foreach (var p in pts) list.Add(Pt(p, sr));
            return list;
        }
    }
}
