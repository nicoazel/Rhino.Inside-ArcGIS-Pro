using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;
using RhinoArcGIS.Core.Geometry;

namespace RhinoArcGIS.Rhino
{
    /// <summary>Uses the same conversion routines as document synchronization, without baking objects.</summary>
    public static class GrasshopperGeometry
    {
        public static NeutralGeometry Read(GeometryBase geometry) => RhinoGeometryReader.ToNeutral(geometry,
            RhinoGeometryReader.Describe(geometry), RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.01);
        public static IReadOnlyList<GeometryBase> Display(NeutralGeometry geometry) =>
            RhinoAdapter.PrepareGeometry(RhinoDoc.ActiveDoc, geometry);
    }
}
