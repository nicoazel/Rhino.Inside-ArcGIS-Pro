using System;
using System.Globalization;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// The heads-up given before a long run on a large layer: how many features, and roughly how
    /// long it will take.
    /// </summary>
    /// <remarks>
    /// Rates come from tools/benchmark.ps1 on a 24-core workstation with eight geodesy threads
    /// (100,000 street or parcel features: pull about 100 s, preview about 15 s, apply of 1% edits
    /// about 20 s), rounded up, so the estimate errs long. They scale with the machine; the notice
    /// says "about" for that reason.
    /// </remarks>
    public static class LargeLayerNotice
    {
        /// <summary>Layers below this size finish in seconds and get no notice.</summary>
        public const int Threshold = 20000;

        const double PullSecondsPerThousand = 1.2;
        const double CompareSecondsPerThousand = 0.2;

        /// <summary>The notice for pulling <paramref name="features"/>, or null for a small layer.</summary>
        public static string ForPull(int features) =>
            features < Threshold ? null
                : $"Large layer: {features.ToString("N0", CultureInfo.InvariantCulture)} features. " +
                  $"Pulling takes {About(features * PullSecondsPerThousand / 1000.0)}. " +
                  "ArcGIS Pro stays usable; the Rhino view fills in when the pull finishes.";

        /// <summary>The notice for comparing (preview or apply) this many objects, or null.</summary>
        public static string ForSync(int objects, bool apply) =>
            objects < Threshold ? null
                : $"Large layer: {objects.ToString("N0", CultureInfo.InvariantCulture)} objects. " +
                  $"{(apply ? "Applying" : "Previewing")} takes {About(objects * CompareSecondsPerThousand / 1000.0)}" +
                  (apply ? ", longer if many objects changed." : ".");

        /// <summary>"about 20 s", "about 1 min", "about 2.5 min".</summary>
        public static string About(double seconds)
        {
            if (seconds < 60) return "about " + (Math.Ceiling(seconds / 5.0) * 5).ToString("0", CultureInfo.InvariantCulture) + " s";
            double minutes = Math.Ceiling(seconds / 30.0) / 2.0;
            return "about " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " min";
        }
    }
}
