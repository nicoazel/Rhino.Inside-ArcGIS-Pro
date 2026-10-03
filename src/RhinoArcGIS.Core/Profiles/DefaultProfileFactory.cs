using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Profiles
{
    /// <summary>
    /// Builds a sensible default mapping profile for a single ArcGIS layer when the user has not
    /// authored one yet. Every readable field is mapped 1:1 and treated as ArcGIS-owned (pull
    /// brings them in as read-only context), with an identity anchor. This makes a first pull
    /// possible before a custom per-link profile is saved; a real project should supply an anchor.
    /// </summary>
    public static class DefaultProfileFactory
    {
        public static LayerMappingProfile ForLayer(LayerSchema schema, string rhinoLayer, UnitSystem rhinoUnits, string crs)
        {
            var profile = new LayerMappingProfile
            {
                ProjectName = "(default)",
                RhinoUnits = rhinoUnits,
                ArcGisCrs = crs,
                VerticalUnits = rhinoUnits,
                ProjectAnchor = new AnchorSettings() // identity
            };

            var mapping = new LayerMapping
            {
                Name = schema != null ? schema.LayerName : rhinoLayer,
                RhinoLayer = rhinoLayer,
                ArcGisLayer = schema != null ? schema.LayerName : rhinoLayer,
                ArcGisSource = schema != null ? schema.Source : null,
                GeometryTarget = schema != null ? schema.GeometryType : Geometry.GeometryTarget.Unsupported
            };

            if (schema != null && schema.Fields != null)
            {
                foreach (FieldDefinition f in schema.Fields)
                {
                    mapping.Attributes.Add(new FieldMapping
                    {
                        RhinoKey = f.Name,
                        ArcGisField = f.Name,
                        Type = f.Type,
                        Owner = FieldOwnership.ArcGisOwned,
                        Required = f.Required,
                        Domain = f.Domain == null ? new System.Collections.Generic.List<string>()
                                                  : new System.Collections.Generic.List<string>(f.Domain),
                        ReadonlyInRhino = true
                    });
                }
            }

            profile.Layers.Add(mapping);
            return profile;
        }
    }
}
