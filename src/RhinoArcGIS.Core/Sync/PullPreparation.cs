using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Change;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Profiles;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// A feature made ready to create in Rhino: its model-space geometry, the user strings it is
    /// created with, and the hashes its baseline is judged by.
    /// </summary>
    public sealed class PreparedPull
    {
        public NeutralGeometry RhinoGeometry;
        public IReadOnlyDictionary<string, string> UserStrings;
        public string ArcGisHash;

        /// <summary>
        /// Hash of <see cref="RhinoGeometry"/> mapped back into the CRS: the Rhino-side baseline
        /// whenever Rhino keeps the geometry exactly as sent, known before the object exists.
        /// </summary>
        public string RhinoHash;

        public string Error;
    }

    /// <summary>
    /// The per-feature work of a pull that touches neither document: mapping each vertex into
    /// model space, building its user strings, hashing. With the per-vertex geodetic map this is
    /// most of a pull's time, so it can run on several threads.
    /// </summary>
    /// <remarks>
    /// The work is pure except for the georeference, which for the geodetic map calls ArcGIS's
    /// geometry engine. Its geodesy and projection calls are not marked as needing the MCT in the
    /// SDK reference, geometries are immutable, and a live check (bridge <c>benchgeodesy</c>: 400,000
    /// mappings on 4 and 24 threads) returned results bit-identical to serial ones. Hosts opt in
    /// through <c>Parallelism</c>; the default of 1 runs it all on the calling thread.
    /// </remarks>
    public static class PullPreparation
    {
        public static PreparedPull[] Prepare(IReadOnlyList<(FeatureRecord feature, Guid syncGuid)> items,
            LayerMapping layer, LayerMappingProfile profile, ICoordinateMap map, PullMode mode,
            string fingerprint, int parallelism, Action<int, int> progress = null)
        {
            var prepared = new PreparedPull[items.Count];
            int done = 0;
            void One(int i)
            {
                var p = new PreparedPull();
                try
                {
                    var (feature, syncGuid) = items[i];
                    p.RhinoGeometry = NeutralGeometryTransform.GisToRhino(feature.Geometry, map);
                    p.UserStrings = PullService.BuildPullUserStrings(feature, layer, profile, syncGuid, mode,
                        ModelStamp.Of(p.RhinoGeometry, fingerprint));
                    p.ArcGisHash = p.UserStrings[GisKeys.GeometryHash];
                    p.RhinoHash = Hashing.HashGeometry(NeutralGeometryTransform.RhinoToGis(p.RhinoGeometry, map));
                }
                catch (Exception ex) { p.Error = ex.Message; }
                prepared[i] = p;

                // Called from whichever worker finished; the receiver must be thread-safe.
                int n = System.Threading.Interlocked.Increment(ref done);
                if (progress != null && (n % 2000 == 0 || n == items.Count)) progress(n, items.Count);
            }

            if (parallelism > 1 && items.Count > 1)
                Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, One);
            else
                for (int i = 0; i < items.Count; i++) One(i);
            return prepared;
        }
    }
}
