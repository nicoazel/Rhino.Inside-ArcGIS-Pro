using RhinoArcGIS.Core.Profiles;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>Options controlling a single push run (spec 02 §1–§3, 06 §9).</summary>
    public sealed class PushOptions
    {
        public PushMode Mode { get; set; } = PushMode.GeometryAndAttributes;

        /// <summary>If true, push only the current Rhino selection (spec 02 §2).</summary>
        public bool SelectedOnly { get; set; }

        /// <summary>Only create new features; never modify existing ones.</summary>
        public bool CreateOnly { get; set; }

        /// <summary>Only update existing features; never create new ones.</summary>
        public bool UpdateOnly { get; set; }

        public static PushOptions Default => new PushOptions();
    }
}
