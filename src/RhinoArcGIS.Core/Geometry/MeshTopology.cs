using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>
    /// Normalises adapter-neutral meshes into indexed topology: coincident coordinates share one
    /// vertex, invalid/degenerate faces are removed, and unused vertices are dropped.
    /// </summary>
    /// <remarks>
    /// ArcGIS multipatches store coordinates per patch rather than in a global indexed pool. A
    /// box can therefore arrive as the same eight corners repeated across six patches. Welding at
    /// the neutral boundary keeps that storage detail from becoming a disconnected Rhino mesh.
    /// Eight decimal places is well below the geometry hash precision while still absorbing the
    /// exact or near-exact duplicates produced by SDK conversions.
    /// </remarks>
    public static class MeshTopology
    {
        public const int WeldDecimalPlaces = 8;

        public static NeutralMesh Clean(NeutralMesh source)
        {
            var result = new NeutralMesh { IsClosed = source?.IsClosed ?? false };
            if (source?.Vertices == null || source.Faces == null) return result;

            var remap = new int[source.Vertices.Count];
            var byCoordinate = new Dictionary<VertexKey, int>();

            for (int i = 0; i < source.Vertices.Count; i++)
            {
                Xyz vertex = source.Vertices[i];
                var key = new VertexKey(vertex);
                if (!byCoordinate.TryGetValue(key, out int target))
                {
                    target = result.Vertices.Count;
                    byCoordinate.Add(key, target);
                    result.Vertices.Add(vertex);
                }
                remap[i] = target;
            }

            foreach (int[] sourceFace in source.Faces)
            {
                if (sourceFace == null || sourceFace.Length < 3) continue;

                var face = new List<int>(sourceFace.Length);
                bool invalid = false;
                foreach (int sourceIndex in sourceFace)
                {
                    if (sourceIndex < 0 || sourceIndex >= remap.Length)
                    {
                        invalid = true;
                        break;
                    }

                    int index = remap[sourceIndex];
                    if (face.Count == 0 || face[face.Count - 1] != index) face.Add(index);
                }
                if (invalid) continue;
                if (face.Count > 1 && face[0] == face[face.Count - 1]) face.RemoveAt(face.Count - 1);
                if (DistinctCount(face) < 3) continue;

                result.Faces.Add(face.ToArray());
            }

            // A malformed source can contain vertices used by no surviving face. Compacting here
            // makes topology counts meaningful and avoids carrying dead points through both SDKs.
            var used = new bool[result.Vertices.Count];
            foreach (int[] face in result.Faces)
                foreach (int index in face) used[index] = true;

            var compact = new List<Xyz>();
            var compactMap = new int[result.Vertices.Count];
            for (int i = 0; i < result.Vertices.Count; i++)
            {
                compactMap[i] = compact.Count;
                if (used[i]) compact.Add(result.Vertices[i]);
            }
            foreach (int[] face in result.Faces)
                for (int i = 0; i < face.Length; i++) face[i] = compactMap[face[i]];
            result.Vertices = compact;

            return result;
        }

        static int DistinctCount(List<int> values)
        {
            var set = new HashSet<int>();
            foreach (int value in values) set.Add(value);
            return set.Count;
        }

        readonly struct VertexKey : IEquatable<VertexKey>
        {
            readonly double _x, _y, _z;

            internal VertexKey(Xyz value)
            {
                _x = Math.Round(value.X, WeldDecimalPlaces, MidpointRounding.AwayFromZero);
                _y = Math.Round(value.Y, WeldDecimalPlaces, MidpointRounding.AwayFromZero);
                _z = Math.Round(value.Z, WeldDecimalPlaces, MidpointRounding.AwayFromZero);
            }

            public bool Equals(VertexKey other) => _x.Equals(other._x) && _y.Equals(other._y) && _z.Equals(other._z);
            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + _x.GetHashCode();
                    hash = hash * 31 + _y.GetHashCode();
                    hash = hash * 31 + _z.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
