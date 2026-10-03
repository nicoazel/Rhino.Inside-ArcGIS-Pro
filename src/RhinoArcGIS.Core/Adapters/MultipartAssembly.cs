using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Adapters
{
    /// <summary>
    /// Folds the pieces of multipart features back into the object that carries each feature's
    /// identity, so the sync sees every feature once, whole.
    /// </summary>
    /// <remarks>
    /// A pulled multipart feature becomes several Rhino objects: the first keeps the feature's
    /// identity and every other piece is marked <see cref="GisKeys.PartOf"/> it (with its
    /// <see cref="GisKeys.PartIndex"/>). A piece's geometry is appended to its representative's --
    /// extra points of a multipoint, extra parts of a polyline, extra rings of a polygon -- in
    /// part order. A piece whose representative is not among the snapshots, or whose kind does not
    /// fit it, is reported on its own, without its stale markers, as new Rhino work: hiding
    /// geometry would be worse than offering it.
    ///
    /// Pure, over snapshots taken one object at a time, so a layer can be read in chunks and
    /// assembled once every chunk is in.
    /// </remarks>
    public static class MultipartAssembly
    {
        public static List<RhinoObjectSnapshot> Assemble(IEnumerable<RhinoObjectSnapshot> objects)
        {
            var list = new List<RhinoObjectSnapshot>();
            var byGuid = new Dictionary<string, RhinoObjectSnapshot>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<(string owner, int index, int order, RhinoObjectSnapshot snap)>();

            foreach (var snap in objects)
            {
                var us = snap.UserStrings;
                bool isCopy = SyncIdentity.IsCopy(snap.RhinoGuid, us);
                if (us != null && us.TryGetValue(GisKeys.PartOf, out var owner) && !string.IsNullOrEmpty(owner))
                {
                    // A cloned point/part belongs to neither the original feature nor its copied
                    // representative. Offer it as new geometry instead of changing the original.
                    if (isCopy)
                    {
                        us.Remove(GisKeys.PartOf);
                        us.Remove(GisKeys.PartIndex);
                        list.Add(snap);
                        continue;
                    }
                    us.TryGetValue(GisKeys.PartIndex, out var indexText);
                    int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index);
                    parts.Add((owner, index, parts.Count, snap));
                    continue;
                }

                list.Add(snap);
                if (!isCopy && us != null && us.TryGetValue(GisKeys.SyncGuid, out var guid) && !string.IsNullOrEmpty(guid) &&
                    !byGuid.ContainsKey(guid))
                    byGuid[guid] = snap;
            }

            foreach (var part in parts.OrderBy(p => p.index).ThenBy(p => p.order))
            {
                if (byGuid.TryGetValue(part.owner, out var owner) && Merge(owner.Geometry, part.snap.Geometry))
                    continue;

                part.snap.UserStrings.Remove(GisKeys.PartOf);
                part.snap.UserStrings.Remove(GisKeys.PartIndex);
                list.Add(part.snap);
            }
            return list;
        }

        /// <summary>Appends a piece's geometry to its representative's; false if the kinds do not fit.</summary>
        static bool Merge(NeutralGeometry target, NeutralGeometry geometry)
        {
            if (geometry == null || target == null || geometry.Kind != target.Kind) return false;

            switch (target.Kind)
            {
                case NeutralGeometryKind.Point:
                    if (target.Points == null) target.Points = new List<Xyz>();
                    if (geometry.Points != null) target.Points.AddRange(geometry.Points);
                    return true;

                case NeutralGeometryKind.Polyline:
                    if (target.Parts == null || target.Parts.Count == 0)
                        target.Parts = new List<List<Xyz>> { target.Points ?? new List<Xyz>() };
                    if (geometry.Parts != null && geometry.Parts.Count > 0) target.Parts.AddRange(geometry.Parts);
                    else if (geometry.Points != null) target.Parts.Add(geometry.Points);
                    return true;

                case NeutralGeometryKind.Polygon:
                    if (target.Rings == null) target.Rings = new List<List<Xyz>>();
                    if (geometry.Rings != null) target.Rings.AddRange(geometry.Rings);
                    return true;

                default:
                    return false;
            }
        }
    }
}
