using System.Collections.Generic;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using RhinoArcGIS.Core.Validation;

namespace RhinoArcGIS.Core.Profiles
{
    /// <summary>
    /// Validates a mapping profile's own consistency before it is used for sync (distinct from
    /// per-object validation in <see cref="Validators"/>).
    /// </summary>
    public static class ProfileValidation
    {
        public static ValidationResult Validate(LayerMappingProfile profile)
        {
            var result = new ValidationResult();
            if (profile == null)
                return result.Add(ValidationIssue.Error("profile.null", "Profile is null."));

            if (!profile.RhinoUnits.IsKnown())
                result.Add(ValidationIssue.Error("profile.units", "Profile rhino_units is unknown."));

            if (string.IsNullOrWhiteSpace(profile.ArcGisCrs))
                result.Add(ValidationIssue.Error("profile.crs", "Profile arcgis_crs is not set; CRS must be explicit."));

            if (profile.ProjectAnchor == null)
                result.Add(ValidationIssue.Error("profile.anchor", "Profile has no project_anchor."));
            else if (profile.ProjectAnchor.Scale == 0.0)
                result.Add(ValidationIssue.Error("profile.anchor.scale", "Anchor scale must be non-zero."));

            if (profile.Layers == null || profile.Layers.Count == 0)
                result.Add(ValidationIssue.Warning("profile.layers.empty", "Profile defines no layers."));
            else
                ValidateLayers(profile.Layers, result);

            return result;
        }

        private static void ValidateLayers(List<LayerMapping> layers, ValidationResult result)
        {
            var seenRhino = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var layer in layers)
            {
                if (layer == null)
                {
                    result.Add(ValidationIssue.Error("layer.null", "Profile contains an empty layer mapping."));
                    continue;
                }
                string id = layer.Name ?? layer.RhinoLayer ?? "(unnamed)";

                if (string.IsNullOrWhiteSpace(layer.RhinoLayer))
                    result.Add(ValidationIssue.Error("layer.rhino", $"Layer '{id}' has no rhino_layer."));
                else if (!seenRhino.Add(layer.RhinoLayer))
                    result.Add(ValidationIssue.Error("layer.dup", $"Rhino layer '{layer.RhinoLayer}' is mapped more than once."));

                if (string.IsNullOrWhiteSpace(layer.ArcGisLayer))
                    result.Add(ValidationIssue.Error("layer.arcgis", $"Layer '{id}' has no arcgis_layer."));

                if (layer.GeometryTarget == GeometryTarget.Unsupported)
                    result.Add(ValidationIssue.Error("layer.geomtarget", $"Layer '{id}' has no valid geometry_target."));

                ValidateAttributes(id, layer, result);
            }
        }

        private static void ValidateAttributes(string layerId, LayerMapping layer, ValidationResult result)
        {
            if (layer.Attributes == null) return;

            var seenRhinoKey = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var seenArcField = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var a in layer.Attributes)
            {
                if (a == null)
                {
                    result.Add(ValidationIssue.Error("attr.null", $"Layer '{layerId}' contains an empty attribute mapping."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(a.RhinoKey))
                    result.Add(ValidationIssue.Error("attr.rhinokey", $"Layer '{layerId}' has an attribute with no rhino_key."));
                else if (!seenRhinoKey.Add(a.RhinoKey))
                    result.Add(ValidationIssue.Error("attr.dupkey", $"Layer '{layerId}' maps rhino_key '{a.RhinoKey}' more than once."));

                if (string.IsNullOrWhiteSpace(a.ArcGisField))
                    result.Add(ValidationIssue.Error("attr.field", $"Layer '{layerId}' attribute '{a.RhinoKey}' has no arcgis_field."));
                else if (!seenArcField.Add(a.ArcGisField))
                    result.Add(ValidationIssue.Error("attr.dupfield", $"Layer '{layerId}' maps arcgis_field '{a.ArcGisField}' more than once."));

                if (a.Validators != null)
                    foreach (var validator in a.Validators)
                        if (!Attributes.FieldValueValidator.TryValidateRule(validator, out string error))
                            result.Add(ValidationIssue.Error("attr.validator",
                                $"Layer '{layerId}' field '{a.ArcGisField}': {error}"));
            }
        }
    }
}
