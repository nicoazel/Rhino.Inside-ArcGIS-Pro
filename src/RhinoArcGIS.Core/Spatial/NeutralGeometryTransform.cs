using System.Collections.Generic;
using RhinoArcGIS.Core.Geometry;

namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// Applies a <see cref="CoordinateTransform"/> to a whole <see cref="NeutralGeometry"/>,
    /// producing a new geometry in the other coordinate space. Pure and unit-tested; this is why
    /// the transform lives in the core rather than in the SDK adapters.
    /// </summary>
    public static class NeutralGeometryTransform
    {
        public static NeutralGeometry GisToRhino(NeutralGeometry g, ICoordinateMap t)
            => Map(g, t.GisToModel, t.GisLengthToModel);

        public static NeutralGeometry RhinoToGis(NeutralGeometry g, ICoordinateMap t)
            => Map(g, t.ModelToGis, t.ModelLengthToGis);

        private static NeutralGeometry Map(NeutralGeometry g, System.Func<Xyz, Xyz> point, System.Func<double, double> length)
        {
            if (g == null) return null;

            var result = new NeutralGeometry
            {
                Kind = g.Kind,
                Height = g.Height.HasValue ? length(g.Height.Value) : (double?)null,
                BaseElevation = g.BaseElevation
            };

            if (g.Points != null)
            {
                result.Points = new List<Xyz>(g.Points.Count);
                foreach (var p in g.Points) result.Points.Add(point(p));
            }

            if (g.Parts != null)
            {
                result.Parts = new List<List<Xyz>>(g.Parts.Count);
                foreach (var part in g.Parts)
                {
                    var newPart = new List<Xyz>(part.Count);
                    foreach (var p in part) newPart.Add(point(p));
                    result.Parts.Add(newPart);
                }
            }

            if (g.Rings != null)
            {
                result.Rings = new List<List<Xyz>>(g.Rings.Count);
                foreach (var ring in g.Rings)
                {
                    var newRing = new List<Xyz>(ring.Count);
                    foreach (var p in ring) newRing.Add(point(p));
                    result.Rings.Add(newRing);
                }
            }

            if (g.Mesh != null)
            {
                var mesh = new NeutralMesh { IsClosed = g.Mesh.IsClosed };
                if (g.Mesh.Vertices != null)
                {
                    mesh.Vertices = new List<Xyz>(g.Mesh.Vertices.Count);
                    foreach (var v in g.Mesh.Vertices) mesh.Vertices.Add(point(v));
                }
                if (g.Mesh.Faces != null)
                {
                    mesh.Faces = new List<int[]>(g.Mesh.Faces.Count);
                    foreach (var f in g.Mesh.Faces) mesh.Faces.Add((int[])f.Clone());
                }
                result.Mesh = mesh;
            }

            return result;
        }
    }
}
