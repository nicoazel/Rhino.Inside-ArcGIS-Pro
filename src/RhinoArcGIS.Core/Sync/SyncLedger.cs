using System;
using System.Collections.Generic;
using System.Globalization;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Identity;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// The set of ArcGIS features a Rhino layer holds a linked object for, as of the last pull or
    /// apply. Stored in the Rhino document, one entry per Rhino layer.
    /// </summary>
    /// <remarks>
    /// Without it, a feature whose Rhino object was deleted cannot be told from one that was never
    /// pulled, so a sync reads it as "new in ArcGIS" and quietly brings the deleted object back.
    /// With it, the engine reports <see cref="SyncState.DeletedInRhino"/> and the apply holds the
    /// feature instead. The ledger names the data source it was recorded against: a ledger made
    /// for a different source is ignored rather than trusted.
    ///
    /// A feature is recorded by its GlobalID when it has one. Without one, an ObjectID is not an
    /// identity: saving a shapefile delete renumbers every later FID, and the next feature created
    /// takes a number the ledger still holds -- a brand-new feature then read as deleted in Rhino
    /// and was never pulled. Such features are recorded by their geometry hash instead ("h:"),
    /// which survives renumbering and never matches a new feature. Ledgers written before that
    /// (only "o:" entries) are still read by ObjectID.
    /// </remarks>
    public sealed class SyncLedger
    {
        public const string KeyPrefix = "gis.ledger.";
        const string SourceLine = "source=";

        readonly HashSet<string> _keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Source { get; private set; }
        public int Count => _keys.Count;

        public static string DocumentKey(string rhinoLayer) => KeyPrefix + (rhinoLayer ?? string.Empty);

        /// <summary>A feature's ledger key: its GlobalID when it has one, otherwise its ObjectID.</summary>
        public static string KeyOf(Guid? globalId, long? objectId)
        {
            if (globalId.HasValue && globalId.Value != Guid.Empty) return "g:" + globalId.Value.ToString("D");
            if (objectId.HasValue) return "o:" + objectId.Value.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        public static string KeyOf(FeatureRecord feature) =>
            feature?.Identity == null ? null : KeyOf(feature.Identity.ArcGisGlobalId, feature.Identity.ArcGisObjectId);

        public bool Contains(string key) => key != null && _keys.Contains(key);

        /// <summary>The entry to record for a feature; see the remarks on the class.</summary>
        public static string EntryOf(FeatureRecord feature, string geometryHash = null)
        {
            if (feature?.Identity?.ArcGisGlobalId is Guid g && g != Guid.Empty) return KeyOf(g, null);
            if (feature?.Geometry != null && !feature.Geometry.IsEmpty())
                return "h:" + (geometryHash ?? Change.Hashing.HashGeometry(feature.Geometry));
            return KeyOf(feature);
        }

        /// <summary>Whether the Rhino layer held this feature at the last sync.</summary>
        public bool ContainsFeature(FeatureRecord feature, string geometryHash = null)
        {
            if (feature == null) return false;
            if (feature.Identity?.ArcGisGlobalId is Guid g && g != Guid.Empty) return Contains(KeyOf(g, null));
            if (Contains(EntryOf(feature, geometryHash))) return true;
            // A ledger written before geometry entries existed can only answer by ObjectID.
            return !HasGeometryEntries && Contains(KeyOf(feature));
        }

        // Kept as keys are added: asked for every feature the ledger does not hold, a scan here made
        // planning a large new layer quadratic.
        bool HasGeometryEntries;

        public void Add(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _keys.Add(key);
            if (key.StartsWith("h:", StringComparison.Ordinal)) HasGeometryEntries = true;
        }

        public static SyncLedger Empty(string source) => new SyncLedger { Source = source };

        public static SyncLedger Load(IRhinoAdapter rhino, string rhinoLayer, string source)
        {
            var ledger = new SyncLedger { Source = source };
            if (!(rhino is IDocumentStringStore store)) return ledger;

            string text;
            try { text = store.GetDocumentString(DocumentKey(rhinoLayer)); }
            catch { return ledger; }
            if (string.IsNullOrEmpty(text)) return ledger;

            var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string recordedSource = lines.Length > 0 && lines[0].StartsWith(SourceLine, StringComparison.Ordinal)
                ? lines[0].Substring(SourceLine.Length).Trim()
                : string.Empty;

            // Recorded against other data: its ids mean nothing here.
            if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(recordedSource) &&
                !string.Equals(source, recordedSource, StringComparison.OrdinalIgnoreCase))
                return ledger;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(SourceLine, StringComparison.Ordinal)) continue;
                ledger.Add(line);
            }
            return ledger;
        }

        public void Save(IRhinoAdapter rhino, string rhinoLayer)
        {
            if (!(rhino is IDocumentStringStore store)) return;
            var keys = new List<string>(_keys);
            keys.Sort(StringComparer.Ordinal);
            var text = SourceLine + (Source ?? string.Empty) + "\n" + string.Join("\n", keys);
            store.SetDocumentString(DocumentKey(rhinoLayer), text);
        }
    }
}
