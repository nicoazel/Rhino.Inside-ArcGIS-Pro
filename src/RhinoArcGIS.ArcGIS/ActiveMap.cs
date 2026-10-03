using System;
using System.Collections.Generic;
using System.Linq;
using ArcGIS.Desktop.Mapping;

namespace RhinoArcGIS.ArcGIS
{
    /// <summary>
    /// The map the sync works against: the active map view's map, or the last one that was active.
    /// </summary>
    /// <remarks>
    /// <see cref="MapView.Active"/> is null whenever the focused pane is not a map -- an attribute
    /// table, a layout, the catalog. Editing attributes in ArcGIS means exactly that, so reading
    /// the map from the active view alone made the layer pickers go blank, syncs fail with "layer
    /// not available", and, worst, the CRS projector fall back to raw longitude/latitude, which
    /// shifts every object and reads as a geometry edit. The last map stays the target until
    /// another map view becomes active or the project closes.
    ///
    /// A project often has several maps and scenes showing the same data. A saved source identity
    /// takes priority over the display name across maps shown this session. Name-only lookup is
    /// accepted only when unambiguous, so a same-name layer cannot redirect a saved link.
    /// </remarks>
    public static class ActiveMap
    {
        static Map _last;
        static readonly List<Map> _seen = new List<Map>();

        /// <summary>The active map, or the last active one; null only before any map was shown.</summary>
        public static Map Current
        {
            get
            {
                var map = MapView.Active?.Map;
                if (map != null)
                {
                    Remember(map);
                    return map;
                }
                return _last;
            }
        }

        /// <summary>The current map first, then every other map or scene shown this session.</summary>
        public static IReadOnlyList<Map> Candidates
        {
            get
            {
                var current = Current;
                var result = new List<Map>();
                if (current != null) result.Add(current);
                lock (_seen)
                    foreach (var map in _seen)
                        if (!result.Contains(map)) result.Add(map);
                return result;
            }
        }

        /// <summary>
        /// Finds the saved data source (or an unambiguous name for an older link). Call on the MCT.
        /// </summary>
        public static FeatureLayer FindLayer(string layerName, string expectedSource = null,
                                             System.Func<FeatureLayer, string> sourceOf = null)
        {
            var candidateLayers = new List<FeatureLayer>();
            foreach (var map in Candidates)
            {
                try
                {
                    candidateLayers.AddRange(map.GetLayersAsFlattenedList().OfType<FeatureLayer>());
                }
                catch { /* a map removed from the project since it was shown */ }
            }
            var nameMatches = candidateLayers.Where(l =>
                string.Equals(l.Name, layerName, System.StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrWhiteSpace(expectedSource))
            {
                if (sourceOf == null) throw new System.ArgumentNullException(nameof(sourceOf));
                // Resolve over every candidate object at once. Even an old-name match is
                // ambiguous when another layer object points at the same source, since those
                // objects may carry distinct definition queries or selections.
                var sourceMatches = candidateLayers.Where(layer =>
                {
                    try { return string.Equals(sourceOf(layer), expectedSource, System.StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                }).ToList();
                if (sourceMatches.Count > 1)
                    throw AmbiguousSource(layerName);
                if (sourceMatches.Count == 1) return sourceMatches[0];

                throw new System.InvalidOperationException(
                    $"The saved ArcGIS data source for '{layerName}' is unavailable. Rebind this link to its original source or explicitly choose a new layer.");
            }

            if (nameMatches.Count > 1)
                throw new System.InvalidOperationException(
                    $"More than one ArcGIS layer is named '{layerName}'. Reopen the link with its saved data source or choose the intended layer explicitly.");
            return nameMatches.FirstOrDefault();
        }

        static System.InvalidOperationException AmbiguousSource(string layerName) =>
            new System.InvalidOperationException(
                $"More than one ArcGIS layer can match the saved source for '{layerName}'. Keep only the intended layer in the candidate maps or rebind the link to a unique layer.");

        /// <summary>Call when a map view becomes active, so the fallback is always the newest map.</summary>
        public static void Remember(Map map)
        {
            if (map == null) return;
            _last = map;
            lock (_seen)
            {
                _seen.Remove(map);
                _seen.Insert(0, map);
            }
        }

        /// <summary>Call when the project closes: its maps must not outlive it.</summary>
        public static void Forget()
        {
            _last = null;
            lock (_seen) _seen.Clear();
        }
    }
}
