using System;
using System.Collections.Generic;
using Rhino.Geometry;
using RhinoArcGIS.Core.Geometry;
using CoreXyz = RhinoArcGIS.Core.Spatial.Xyz;
using NeutralGeometry = RhinoArcGIS.Core.Geometry.NeutralGeometry;

namespace RhinoArcGIS.Rhino
{
    /// <summary>
    /// Inspects RhinoCommon geometry and produces neutral descriptors/geometry for the core
    /// (spec 06 §2). Coordinates stay in Rhino space; the core applies the project transform.
    ///
    /// Wave 3 reads Point, Curve (open → polyline, closed → polygon ring), and meshes Breps/
    /// Extrusions/Meshes into a neutral mesh. SubD/blocks/hatches are described but not yet
    /// converted (returned as null geometry so the push reports them rather than crashing).
    /// </summary>
    internal static class RhinoGeometryReader
    {
        public static GeometryDescriptor Describe(GeometryBase g)
        {
            var d = new GeometryDescriptor { IsEmpty = g == null };
            if (g == null) return d;

            switch (g)
            {
                case Point _:
                    d.Kind = RhinoGeometryKind.Point;
                    break;

                case Curve c:
                    d.IsClosed = c.IsClosed;
                    d.IsPlanar = c.IsPlanar();
                    d.Kind = c.IsClosed
                        ? (c.IsPlanar() ? RhinoGeometryKind.ClosedPlanarCurve : RhinoGeometryKind.ClosedNonPlanarCurve)
                        : RhinoGeometryKind.OpenCurve;
                    break;

                case Extrusion _:
                    d.Kind = RhinoGeometryKind.Extrusion;
                    d.IsSolid = true;
                    break;

                case Brep b:
                    d.IsSolid = b.IsSolid;
                    d.Kind = b.IsSolid ? RhinoGeometryKind.ClosedBrep : RhinoGeometryKind.OpenBrep;
                    break;

                case Surface s:
                    d.IsPlanar = s.IsPlanar();
                    d.Kind = RhinoGeometryKind.PlanarSurface;
                    break;

                case Mesh _:
                    d.Kind = RhinoGeometryKind.Mesh;
                    break;

                case SubD _:
                    d.Kind = RhinoGeometryKind.SubD;
                    break;

                case InstanceReferenceGeometry _:
                    d.Kind = RhinoGeometryKind.BlockInstance;
                    break;

                case Hatch _:
                    d.Kind = RhinoGeometryKind.Hatch;
                    break;

                default:
                    d.Kind = RhinoGeometryKind.Unknown;
                    break;
            }
            return d;
        }

        public static NeutralGeometry ToNeutral(GeometryBase g, GeometryDescriptor d, double tolerance)
        {
            if (g == null) return null;

            switch (d.Kind)
            {
                case RhinoGeometryKind.Point:
                    return NeutralGeometry.Point(X(((Point)g).Location));

                case RhinoGeometryKind.OpenCurve:
                    return NeutralGeometry.Polyline(SampleCurve((Curve)g, tolerance));

                case RhinoGeometryKind.ClosedPlanarCurve:
                    return NeutralGeometry.Polygon(SampleCurve((Curve)g, tolerance));

                case RhinoGeometryKind.ClosedNonPlanarCurve:
                    return NeutralGeometry.Polyline(SampleCurve((Curve)g, tolerance));

                case RhinoGeometryKind.Mesh:
                    return MeshToNeutral((Mesh)g);

                case RhinoGeometryKind.ClosedBrep:
                case RhinoGeometryKind.OpenBrep:
                    return BrepToNeutral((Brep)g);

                case RhinoGeometryKind.Extrusion:
                    return BrepToNeutral(((Extrusion)g).ToBrep(false));

                case RhinoGeometryKind.PlanarSurface:
                    return BrepToNeutral(((Surface)g).ToBrep());

                default:
                    // SubD, blocks, hatches: conversion is a later wave.
                    return null;
            }
        }

        private static CoreXyz X(Point3d p) => new CoreXyz(p.X, p.Y, p.Z);

        private static List<CoreXyz> SampleCurve(Curve c, double tolerance)
        {
            var pts = new List<CoreXyz>();

            if (c.TryGetPolyline(out Polyline pl))
            {
                for (int i = 0; i < pl.Count; i++) pts.Add(X(pl[i]));
                return pts;
            }

            double tol = tolerance > 0 ? tolerance : 0.01;
            PolylineCurve pc = c.ToPolyline(tol, Math.PI / 36.0, 0.0, 0.0);
            if (pc != null)
                for (int i = 0; i < pc.PointCount; i++)
                    pts.Add(X(pc.Point(i)));

            return pts;
        }

        private static NeutralGeometry BrepToNeutral(Brep brep)
        {
            if (brep == null) return null;

            // A single planar face reads back as a polygon with rings: the outer loop plus any
            // inner loops as holes. Meshing it instead would produce a multipatch, which the ArcGIS
            // writer does not handle, so a pulled polygon edited in Rhino could never be pushed back
            // -- it would fail as "no support for this geometry type".
            if (brep.Faces.Count == 1 && brep.Faces[0].IsPlanar())
            {
                var polygon = new NeutralGeometry { Kind = NeutralGeometryKind.Polygon };

                foreach (BrepLoop loop in brep.Faces[0].Loops)
                {
                    Curve curve = loop.To3dCurve();
                    if (curve == null) continue;

                    var ring = SampleCurve(curve, 0.0);
                    if (ring.Count > 2) polygon.Rings.Add(ring);
                }

                if (polygon.Rings.Count > 0) return polygon;
            }

            Mesh[] meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
            if (meshes == null || meshes.Length == 0) return null;

            var merged = new Mesh();
            foreach (var m in meshes) if (m != null) merged.Append(m);
            return MeshToNeutral(merged);
        }

        private static NeutralGeometry MeshToNeutral(Mesh mesh)
        {
            if (mesh == null) return null;

            var nm = new NeutralMesh { IsClosed = mesh.IsClosed };
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                Point3d v = mesh.Vertices.Point3dAt(i);
                nm.Vertices.Add(new CoreXyz(v.X, v.Y, v.Z));
            }
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                MeshFace f = mesh.Faces[i];
                nm.Faces.Add(f.IsQuad ? new[] { f.A, f.B, f.C, f.D } : new[] { f.A, f.B, f.C });
            }

            return new NeutralGeometry { Kind = NeutralGeometryKind.Multipatch, Mesh = MeshTopology.Clean(nm) };
        }
    }
}
