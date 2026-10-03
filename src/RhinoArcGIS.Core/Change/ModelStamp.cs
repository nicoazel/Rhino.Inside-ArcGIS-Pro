using System.Globalization;
using System.Text;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Change
{
    /// <summary>
    /// Proof that a Rhino object's geometry is exactly what it was when its baseline was taken,
    /// without mapping it into the layer's CRS (see <see cref="Identity.GisKeys.RhinoModelStamp"/>).
    /// </summary>
    /// <remarks>
    /// Change detection compares a Rhino object's shape, in the layer's CRS, with the hash recorded
    /// at the last sync. Getting it into the CRS puts every vertex through the georeference -- with
    /// the geodetic map, an ArcGIS geodesic walk and datum projection per vertex -- which on a layer
    /// of 100,000 objects made each preview cost about a minute, almost all of it spent confirming
    /// that untouched objects were untouched.
    ///
    /// The stamp is the model-space geometry's hash, taken at full precision, prefixed with a
    /// fingerprint of the georeference it was baselined under. The same model geometry under the
    /// same georeference maps to the same CRS geometry, so while both match, the recorded CRS hash
    /// still stands and nothing needs mapping. An edited object, or any object after the anchor,
    /// units or CRS change, fails the check and is mapped as before: a stale stamp only costs time.
    /// </remarks>
    public static class ModelStamp
    {
        /// <summary>
        /// Model coordinates are hashed far finer than CRS ones: this only has to tell "identical"
        /// from "not", in whatever units the model uses, and Rhino returns an untouched object's
        /// coordinates bit for bit.
        /// </summary>
        const int Decimals = 10;

        /// <summary>
        /// Identifies a georeference by where it puts three probe points. Anything that moves,
        /// rotates, rescales or reprojects the mapping moves at least one of them.
        /// </summary>
        public static string Fingerprint(ICoordinateMap map)
        {
            if (map == null) return "none";
            var sb = new StringBuilder();
            foreach (var probe in new[] { new Xyz(0, 0, 0), new Xyz(1000, 0, 0), new Xyz(0, 1000, 10) })
            {
                var p = map.ModelToGis(probe);
                sb.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            }
            return Hashing.Hash(sb.ToString()).Substring(0, 16);
        }

        /// <summary>The stamp for <paramref name="model"/> (model space) under <paramref name="fingerprint"/>.</summary>
        public static string Of(NeutralGeometry model, string fingerprint) =>
            model == null ? null : fingerprint + ":" + Hashing.HashGeometry(model, Decimals);

        /// <summary>Whether <paramref name="stamp"/> vouches for <paramref name="model"/> under this georeference.</summary>
        public static bool Matches(string stamp, NeutralGeometry model, string fingerprint) =>
            stamp != null && model != null &&
            stamp.Length > fingerprint.Length && stamp.StartsWith(fingerprint + ":", System.StringComparison.Ordinal) &&
            stamp == Of(model, fingerprint);
    }
}
