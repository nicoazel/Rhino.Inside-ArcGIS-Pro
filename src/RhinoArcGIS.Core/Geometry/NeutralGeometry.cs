using System.Collections.Generic;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Geometry
{
    public enum NeutralGeometryKind
    {
        Point = 0,
        Polyline,
        Polygon,
        Multipatch
    }

    /// <summary>
    /// SDK-neutral geometry payload exchanged between adapters and the core. Adapters convert
    /// RhinoCommon / ArcGIS geometry to and from this so the core never depends on either SDK.
    /// Coordinates are always in GIS space by the time they reach the core (adapters apply the
    /// <see cref="CoordinateTransform"/>).
    /// </summary>
    public sealed class NeutralGeometry
    {
        public NeutralGeometryKind Kind { get; set; }

        /// <summary>Point geometry: one coordinate. Polyline: ordered vertices.</summary>
        public List<Xyz> Points { get; set; } = new List<Xyz>();

        /// <summary>
        /// Polyline parts. A multipart line keeps one entry per part; a single-part line may leave
        /// this empty and be described by <see cref="Points"/> alone. Flattening parts into one run
        /// is what makes multipart streets arrive as a single line zig-zagging between them.
        /// </summary>
        public List<List<Xyz>> Parts { get; set; } = new List<List<Xyz>>();

        /// <summary>
        /// Polygon rings. Which are outer boundaries and which are holes is decided by nesting, not
        /// by position: a polygon may hold several islands, each with its own holes.
        /// </summary>
        public List<List<Xyz>> Rings { get; set; } = new List<List<Xyz>>();

        /// <summary>Multipatch mesh, when <see cref="Kind"/> is <see cref="NeutralGeometryKind.Multipatch"/>.</summary>
        public NeutralMesh Mesh { get; set; }

        /// <summary>Extrusion height, for polygon + height targets.</summary>
        public double? Height { get; set; }

        /// <summary>Absolute base elevation, when applicable.</summary>
        public double? BaseElevation { get; set; }

        public static NeutralGeometry Point(Xyz p) =>
            new NeutralGeometry { Kind = NeutralGeometryKind.Point, Points = new List<Xyz> { p } };

        public static NeutralGeometry Polyline(IEnumerable<Xyz> pts) =>
            new NeutralGeometry { Kind = NeutralGeometryKind.Polyline, Points = new List<Xyz>(pts) };

        public static NeutralGeometry Polygon(IEnumerable<Xyz> outer) =>
            new NeutralGeometry
            {
                Kind = NeutralGeometryKind.Polygon,
                Rings = new List<List<Xyz>> { new List<Xyz>(outer) }
            };

        /// <summary>All coordinates that make up this geometry, in a stable order (for hashing).</summary>
        public IEnumerable<Xyz> AllPoints()
        {
            // Parts supersede Points when present; for a single-part line they hold the same run,
            // and yielding both would count it twice and change the geometry hash.
            if (Parts != null && Parts.Count > 0)
            {
                foreach (var part in Parts)
                    if (part != null)
                        foreach (var p in part) yield return p;
            }
            else if (Points != null)
            {
                foreach (var p in Points) yield return p;
            }

            if (Rings != null)
                foreach (var ring in Rings)
                    if (ring != null)
                        foreach (var p in ring) yield return p;

            if (Mesh != null && Mesh.Vertices != null)
                foreach (var v in Mesh.Vertices) yield return v;
        }

        public bool IsEmpty()
        {
            foreach (var _ in AllPoints()) return false;
            return true;
        }
    }

    /// <summary>A simple triangle/polygon mesh for multipatch exchange.</summary>
    public sealed class NeutralMesh
    {
        public List<Xyz> Vertices { get; set; } = new List<Xyz>();

        /// <summary>Faces as vertex-index arrays (3 or 4 indices per face).</summary>
        public List<int[]> Faces { get; set; } = new List<int[]>();

        public bool IsClosed { get; set; }
    }
}
