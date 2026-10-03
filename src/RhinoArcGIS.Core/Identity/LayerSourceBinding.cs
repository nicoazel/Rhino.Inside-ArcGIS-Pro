using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoArcGIS.Core.Identity
{
    /// <summary>Resolves a saved map-layer link without allowing a reused display name to change its data.</summary>
    public static class LayerSourceBinding
    {
        public enum Status { Resolved, Missing, Ambiguous }

        public sealed class Result
        {
            internal Result(Status status, string name = null, string source = null)
            { Status = status; Name = name; Source = source; }
            public Status Status { get; }
            public string Name { get; }
            public string Source { get; }
        }

        public static Result Resolve(string savedName, string savedSource,
                                     IEnumerable<KeyValuePair<string, string>> available)
        {
            var candidates = (available ?? Enumerable.Empty<KeyValuePair<string, string>>()).ToList();
            if (!string.IsNullOrWhiteSpace(savedSource))
            {
                var bySource = candidates.Where(item => Same(item.Value, savedSource)).ToList();
                if (bySource.Count == 0) return new Result(Status.Missing);
                if (bySource.Count > 1) return new Result(Status.Ambiguous);
                return new Result(Status.Resolved, bySource[0].Key, savedSource);
            }

            var byName = candidates.Where(item => Same(item.Key, savedName)).ToList();
            if (byName.Count == 0) return new Result(Status.Missing);
            if (byName.Count > 1) return new Result(Status.Ambiguous);
            if (string.IsNullOrWhiteSpace(byName[0].Value)) return new Result(Status.Missing);
            return new Result(Status.Resolved, byName[0].Key, byName[0].Value);
        }

        static bool Same(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
