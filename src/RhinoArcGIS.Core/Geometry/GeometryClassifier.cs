using System.Collections.Generic;

namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>
    /// Maps a <see cref="GeometryDescriptor"/> to a default GIS target plus alternates and a
    /// status, implementing the classification table in spec 03 §2. Pure and unit-tested.
    /// </summary>
    public static class GeometryClassifier
    {
        private static IReadOnlyList<GeometryTarget> List(params GeometryTarget[] targets) => targets;

        public static ClassificationResult Classify(GeometryDescriptor d)
        {
            if (d == null)
                return new ClassificationResult(GeometryTarget.Unsupported, ClassificationStatus.Failed, null, "No geometry descriptor.");

            if (d.IsEmpty)
                return new ClassificationResult(GeometryTarget.Unsupported, ClassificationStatus.Failed, null, "Empty geometry.");

            switch (d.Kind)
            {
                case RhinoGeometryKind.Point:
                    return new ClassificationResult(GeometryTarget.PointZ, ClassificationStatus.Ready);

                case RhinoGeometryKind.BlockInstance:
                    return new ClassificationResult(
                        GeometryTarget.PointZ, ClassificationStatus.Ready,
                        List(GeometryTarget.Multipatch),
                        "Block instance: default to point asset with block metadata; can explode to multipatch.");

                case RhinoGeometryKind.OpenCurve:
                    return new ClassificationResult(
                        GeometryTarget.PolylineZ, ClassificationStatus.Ready,
                        List(GeometryTarget.PolygonZ));

                case RhinoGeometryKind.ClosedPlanarCurve:
                    return new ClassificationResult(
                        GeometryTarget.PolygonZ, ClassificationStatus.Ready,
                        List(GeometryTarget.PolylineZ));

                case RhinoGeometryKind.ClosedNonPlanarCurve:
                    return new ClassificationResult(
                        GeometryTarget.PolylineZ, ClassificationStatus.ReadyWithWarnings,
                        List(GeometryTarget.PolygonZ),
                        "Closed but non-planar: polygon target requires projection.");

                case RhinoGeometryKind.PlanarSurface:
                    return new ClassificationResult(
                        GeometryTarget.PolygonZ, ClassificationStatus.Ready,
                        List(GeometryTarget.Multipatch));

                case RhinoGeometryKind.Extrusion:
                    return new ClassificationResult(
                        GeometryTarget.PolygonExtrusion, ClassificationStatus.Ready,
                        List(GeometryTarget.Multipatch));

                case RhinoGeometryKind.ClosedBrep:
                    return new ClassificationResult(
                        GeometryTarget.Multipatch, ClassificationStatus.Ready,
                        List(GeometryTarget.PolygonExtrusion));

                case RhinoGeometryKind.OpenBrep:
                    return new ClassificationResult(
                        GeometryTarget.Multipatch, ClassificationStatus.ReadyWithWarnings,
                        null,
                        "Open Brep: multipatch will not be watertight.");

                case RhinoGeometryKind.Mesh:
                    return new ClassificationResult(GeometryTarget.Multipatch, ClassificationStatus.Ready);

                case RhinoGeometryKind.SubD:
                    return new ClassificationResult(
                        GeometryTarget.Multipatch, ClassificationStatus.NeedsUserDecision,
                        null,
                        "SubD must be converted to mesh before export.");

                case RhinoGeometryKind.Hatch:
                    return new ClassificationResult(
                        GeometryTarget.PolygonZ, ClassificationStatus.NeedsUserDecision,
                        null,
                        "Hatch boundary becomes a polygon; pattern-only hatches are unsupported.");

                default:
                    return new ClassificationResult(
                        GeometryTarget.Unsupported, ClassificationStatus.Unsupported,
                        null, "Unrecognized geometry kind.");
            }
        }
    }
}
