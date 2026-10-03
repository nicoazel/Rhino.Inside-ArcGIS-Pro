using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Attributes;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Profiles
{
    /// <summary>
    /// Creates and reconciles the one-layer profiles authored in the ArcGIS Pro dockpane. The
    /// saved JSON is deliberately independent of the view model; this class is the shared safety
    /// boundary used both when the editor opens and immediately before a sync runs.
    /// </summary>
    public static class ProfileAuthoring
    {
        /// <summary>
        /// Creates a default profile, or updates a saved profile to the layer's current identity
        /// and schema. User choices are preserved, while ArcGIS-maintained fields remain locked.
        /// </summary>
        public static LayerMappingProfile Reconcile(
            LayerSchema schema,
            string rhinoLayer,
            UnitSystem rhinoUnits,
            LayerMappingProfile saved = null,
            bool makeDesignFieldsShared = false)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));

            var profile = saved ?? DefaultProfileFactory.ForLayer(
                schema, rhinoLayer, rhinoUnits, schema.Crs);

            if (profile.ProjectAnchor == null) profile.ProjectAnchor = new AnchorSettings();
            if (string.IsNullOrWhiteSpace(profile.ProjectName)) profile.ProjectName = schema.LayerName;
            if (rhinoUnits.IsKnown()) profile.RhinoUnits = rhinoUnits;
            if (!profile.VerticalUnits.IsKnown()) profile.VerticalUnits = profile.RhinoUnits;
            // The live layer is authoritative for its coordinate system. Keeping a saved CRS when
            // a map layer was repointed would apply the wrong coordinate transform silently.
            if (!string.IsNullOrWhiteSpace(schema.Crs)) profile.ArcGisCrs = schema.Crs;

            if (profile.Layers == null) profile.Layers = new List<LayerMapping>();
            var layer = profile.Layers.Count > 0 && profile.Layers[0] != null
                ? profile.Layers[0]
                : new LayerMapping();
            profile.Layers.Clear();
            profile.Layers.Add(layer);

            layer.Name = string.IsNullOrWhiteSpace(layer.Name) ? schema.LayerName : layer.Name;
            layer.RhinoLayer = rhinoLayer;
            layer.ArcGisLayer = schema.LayerName;
            layer.ArcGisSource = schema.Source;
            layer.GeometryTarget = schema.GeometryType;
            if (layer.Sync == null) layer.Sync = new LayerSyncSettings();
            if (layer.Geometry == null) layer.Geometry = new LayerGeometrySettings();
            if (layer.Attributes == null) layer.Attributes = new List<FieldMapping>();

            // A removed ArcGIS column must not remain in the run-time profile: attempting to push
            // it would turn an ordinary schema change into a failed edit operation.
            layer.Attributes.RemoveAll(mapping => mapping == null || schema.FindField(mapping.ArcGisField) == null);

            foreach (var mapping in layer.Attributes)
            {
                var definition = schema.FindField(mapping.ArcGisField);
                mapping.Type = definition.Type;
                mapping.Required = definition.Required;
                mapping.Domain = definition.Domain == null
                    ? new List<string>()
                    : new List<string>(definition.Domain);

                ApplyOwnershipPolicy(mapping, definition, makeDesignFieldsShared, saved == null);
            }

            return profile;
        }

        /// <summary>
        /// Applies the non-negotiable system-field policy. For a new default profile, ordinary
        /// design fields can also be promoted to Shared as a convenience; saved user choices are
        /// otherwise retained.
        /// </summary>
        public static void ApplyOwnershipPolicy(FieldMapping mapping, FieldDefinition definition,
                                                bool makeDesignFieldsShared, bool isDefaultProfile)
        {
            if (mapping == null) return;

            if (IsArcGisManaged(mapping.ArcGisField) || definition?.Editable == false)
            {
                bool editableMetric = DerivedFields.MetricOf(mapping.ArcGisField) != DerivedMetric.None
                                      && definition != null && definition.Editable
                                      && definition.Type == FieldType.Double;
                mapping.Owner = editableMetric ? FieldOwnership.Derived : FieldOwnership.Locked;
                mapping.ReadonlyInRhino = true;
                return;
            }

            if (isDefaultProfile && makeDesignFieldsShared)
            {
                mapping.Owner = FieldOwnership.Shared;
                mapping.ReadonlyInRhino = false;
                return;
            }

            mapping.ReadonlyInRhino = mapping.Owner == FieldOwnership.ArcGisOwned
                                   || mapping.Owner == FieldOwnership.Derived
                                   || mapping.Owner == FieldOwnership.Locked
                                   || mapping.Owner == FieldOwnership.LocalOnlyArcGis;
        }

        /// <summary>Fields whose values ArcGIS owns or derives from geometry.</summary>
        public static bool IsArcGisManaged(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return true;
            if (fieldName.StartsWith("Shape", StringComparison.OrdinalIgnoreCase)) return true;

            switch (fieldName.ToUpperInvariant())
            {
                case "OBJECTID":
                case "FID":
                case "OID":
                case "GLOBALID":
                case "SE_ANNO_CAD_DATA":
                    return true;
                default:
                    return false;
            }
        }
    }
}
