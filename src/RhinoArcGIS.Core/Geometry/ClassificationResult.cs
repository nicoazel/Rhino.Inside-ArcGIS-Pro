using System.Collections.Generic;

namespace RhinoArcGIS.Core.Geometry
{
    /// <summary>
    /// Outcome of classifying a single Rhino object: the default GIS target, any acceptable
    /// alternates the user can choose, a status, and an optional note (spec 03 §2–§3).
    /// </summary>
    public sealed class ClassificationResult
    {
        public GeometryTarget Default { get; }
        public IReadOnlyList<GeometryTarget> Alternates { get; }
        public ClassificationStatus Status { get; }
        public string Note { get; }

        public ClassificationResult(
            GeometryTarget @default,
            ClassificationStatus status,
            IReadOnlyList<GeometryTarget> alternates = null,
            string note = null)
        {
            Default = @default;
            Status = status;
            Alternates = alternates ?? new List<GeometryTarget>();
            Note = note;
        }
    }
}
