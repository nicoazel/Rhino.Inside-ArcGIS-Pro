using System.Collections.Generic;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Profiles
{
    /// <summary>
    /// Serializable project mapping profile (spec 06 §8). Plain POCOs with public get/set so
    /// they round-trip cleanly through JSON; <see cref="AnchorSettings.ToProjectAnchor"/> bridges
    /// to the immutable domain types.
    /// </summary>
    public sealed class LayerMappingProfile
    {
        public string ProjectName { get; set; }
        public UnitSystem RhinoUnits { get; set; } = UnitSystem.Unknown;
        public string ArcGisCrs { get; set; }
        public UnitSystem VerticalUnits { get; set; } = UnitSystem.Unknown;

        public AnchorSettings ProjectAnchor { get; set; } = new AnchorSettings();
        public List<LayerMapping> Layers { get; set; } = new List<LayerMapping>();
    }

    /// <summary>Serializable form of <see cref="Spatial.ProjectAnchor"/>.</summary>
    public sealed class AnchorSettings
    {
        public double[] RhinoPoint { get; set; } = new[] { 0.0, 0.0, 0.0 };
        public double[] GisPoint { get; set; } = new[] { 0.0, 0.0, 0.0 };
        public double RotationDegrees { get; set; }
        public double Scale { get; set; } = 1.0;

        private static Xyz ToXyz(double[] a)
        {
            if (a == null) return new Xyz(0, 0, 0);
            double x = a.Length > 0 ? a[0] : 0.0;
            double y = a.Length > 1 ? a[1] : 0.0;
            double z = a.Length > 2 ? a[2] : 0.0;
            return new Xyz(x, y, z);
        }

        public ProjectAnchor ToProjectAnchor()
            => new ProjectAnchor(ToXyz(RhinoPoint), ToXyz(GisPoint), RotationDegrees, Scale == 0.0 ? 1.0 : Scale);
    }

    public sealed class LayerMapping
    {
        public string Name { get; set; }
        public string RhinoLayer { get; set; }
        public string ArcGisLayer { get; set; }

        /// <summary>The ArcGIS layer's data source (see <see cref="Adapters.LayerSchema.Source"/>).</summary>
        public string ArcGisSource { get; set; }
        public GeometryTarget GeometryTarget { get; set; } = GeometryTarget.Unsupported;

        public LayerSyncSettings Sync { get; set; } = new LayerSyncSettings();
        public LayerGeometrySettings Geometry { get; set; } = new LayerGeometrySettings();
        public List<FieldMapping> Attributes { get; set; } = new List<FieldMapping>();
    }

    public sealed class LayerSyncSettings
    {
        public PullMode DefaultPullMode { get; set; } = PullMode.Context;
        public PushMode DefaultPushMode { get; set; } = PushMode.GeometryAndAttributes;
        public GeometryOwner GeometryOwner { get; set; } = GeometryOwner.Rhino;
        public bool AllowAttributeOnlyPull { get; set; } = true;
        public bool AllowAttributeOnlyPush { get; set; } = true;
    }

    public sealed class LayerGeometrySettings
    {
        public ElevationMode ElevationMode { get; set; } = ElevationMode.Absolute;
        public string BaseElevationField { get; set; }
        public string HeightField { get; set; }
        public ExtrusionMode ExtrusionMode { get; set; } = ExtrusionMode.None;
    }
}
