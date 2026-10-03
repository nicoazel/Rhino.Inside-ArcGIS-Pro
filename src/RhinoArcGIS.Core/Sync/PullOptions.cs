using RhinoArcGIS.Core.Profiles;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>Options controlling a single pull run (spec 02 §4, 06 §9).</summary>
    public sealed class PullOptions
    {
        /// <summary>Which pull mode to run. Defaults to the spec's safe default, Context.</summary>
        public PullMode Mode { get; set; } = PullMode.Context;

        /// <summary>If true, read only the layer's current ArcGIS selection (spec 02 §5).</summary>
        public bool SelectedOnly { get; set; }

        public static PullOptions Context => new PullOptions { Mode = PullMode.Context };

        /// <summary>True for modes that create Rhino geometry (vs. update existing objects).</summary>
        public bool IsCreateMode => Mode == PullMode.Context || Mode == PullMode.EditableLinked;
    }
}
