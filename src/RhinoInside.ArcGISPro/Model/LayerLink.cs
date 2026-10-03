using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RhinoArcGIS.Core.Sync;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// One row of the link table: an ArcGIS layer paired with a Rhino layer and how the pair may
    /// sync. The selected row is what the Pull and Sync areas act on.
    /// </summary>
    public sealed class LayerLink : INotifyPropertyChanged
    {
        string _arcGisLayer, _rhinoLayer;
        SyncDirectionMode _direction = SyncDirectionMode.TwoWay;
        string _lastResult, _profileJson, _arcGisSource;
        bool _isArcGisLayerMissing;

        public string ArcGisLayer
        {
            get => _arcGisLayer;
            set
            {
                // A picker pushes null when its item list is rebuilt (every time the map's layers
                // change); that is not the user clearing the pair, so the row keeps what it had.
                if (value == null) return;
                if (Set(ref _arcGisLayer, value) && string.IsNullOrWhiteSpace(_rhinoLayer)) RhinoLayer = value;
            }
        }

        /// <summary>
        /// Re-announces every property, so pickers bound to this row re-select after their item
        /// lists were rebuilt underneath them.
        /// </summary>
        internal void Reannounce()
        {
            foreach (var name in new[]
            {
                nameof(ArcGisLayer), nameof(RhinoLayer), nameof(Direction), nameof(ProfileJson),
                nameof(ProfileLabel), nameof(EffectiveRhinoLayer), nameof(Title)
            })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Rhino layer to pair with; defaults to the ArcGIS layer's name, which is what a pull creates.</summary>
        public string RhinoLayer
        {
            get => _rhinoLayer;
            // Same reason as ArcGisLayer: a picker whose list was rebuilt pushes null. An empty
            // string is still a deliberate "use the ArcGIS layer's name".
            set { if (value != null) Set(ref _rhinoLayer, value); }
        }

        /// <summary>
        /// The data behind the ArcGIS layer (workspace path and feature class) when the link was last
        /// seen in the map. Layer names can change while their source stays stable, so this lets a
        /// link follow a rename. A reused name never replaces the saved source; changing data needs
        /// a deliberate new/repaired link to one unambiguous layer object.
        /// </summary>
        public string ArcGisSource
        {
            get => _arcGisSource;
            set => Set(ref _arcGisSource, value);
        }

        /// <summary>True when no layer in the current map has this link's name or data source.</summary>
        public bool IsArcGisLayerMissing
        {
            get => _isArcGisLayerMissing;
            set => Set(ref _isArcGisLayerMissing, value);
        }

        public SyncDirectionMode Direction
        {
            get => _direction;
            set => Set(ref _direction, value);
        }

        /// <summary>One line from the last preview or apply on this row.</summary>
        public string LastResult
        {
            get => _lastResult;
            set => Set(ref _lastResult, value);
        }

        /// <summary>
        /// The user-authored one-layer mapping profile. Null means the safe generated default.
        /// Stored with the link in Rhino document text so the .3dm remains the portable source of
        /// truth for both pairing and field ownership.
        /// </summary>
        public string ProfileJson
        {
            get => _profileJson;
            set => Set(ref _profileJson, value);
        }

        public bool HasCustomProfile => !string.IsNullOrWhiteSpace(ProfileJson);
        public string ProfileLabel => HasCustomProfile ? "Custom" : "Default";

        /// <summary>Compact status shown in the multi-link grid; the full result stays in the tooltip.</summary>
        public string StatusGlyph
        {
            get
            {
                if (IsArcGisLayerMissing) return "\uE7BA";
                if (string.IsNullOrWhiteSpace(LastResult)) return "\uE823"; // clock
                if (LastResult.StartsWith("failed", StringComparison.OrdinalIgnoreCase)) return "\uEA39";
                if (LastResult.IndexOf("attention", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    LastResult.IndexOf("held", StringComparison.OrdinalIgnoreCase) >= 0) return "\uE7BA";
                if (LastResult.StartsWith("in sync", StringComparison.OrdinalIgnoreCase) ||
                    LastResult.StartsWith("applied", StringComparison.OrdinalIgnoreCase)) return "\uE73E";
                return "\uE946";
            }
        }

        public string StatusColor
        {
            get
            {
                if (IsArcGisLayerMissing) return "#D9A53A";
                if (string.IsNullOrWhiteSpace(LastResult)) return "#9AA0A6";
                if (LastResult.StartsWith("failed", StringComparison.OrdinalIgnoreCase)) return "#D64550";
                if (LastResult.IndexOf("attention", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    LastResult.IndexOf("held", StringComparison.OrdinalIgnoreCase) >= 0) return "#D9A53A";
                return "#3FA45B";
            }
        }

        public string StatusText => IsArcGisLayerMissing
            ? $"'{ArcGisLayer}' is not in the active map. Add its data to the map, or pick another layer."
            : string.IsNullOrWhiteSpace(LastResult) ? "Not previewed in this session" : LastResult;

        /// <summary>A short, scannable label for the grid; <see cref="StatusText"/> remains the detail tooltip.</summary>
        public string StatusLabel
        {
            get
            {
                if (IsArcGisLayerMissing) return "Not in map";
                if (string.IsNullOrWhiteSpace(LastResult)) return "Not run";
                if (LastResult.StartsWith("failed", StringComparison.OrdinalIgnoreCase)) return "Failed";
                if (LastResult.IndexOf("attention", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    LastResult.IndexOf("held", StringComparison.OrdinalIgnoreCase) >= 0) return "Review";
                if (LastResult.StartsWith("in sync", StringComparison.OrdinalIgnoreCase)) return "In sync";
                if (LastResult.StartsWith("applied", StringComparison.OrdinalIgnoreCase)) return "Applied";
                if (LastResult.StartsWith("pulled", StringComparison.OrdinalIgnoreCase)) return "Pulled";
                return "Updated";
            }
        }

        /// <summary>
        /// The rows of the last preview or apply on this pair, so selecting the row brings its own
        /// result back into the Sync area rather than leaving whichever pair ran last.
        /// </summary>
        internal IReadOnlyList<SyncRow> LastRows { get; set; }

        /// <summary>The Rhino layer a sync actually uses: the pair's, or the ArcGIS layer's name.</summary>
        public string EffectiveRhinoLayer => string.IsNullOrWhiteSpace(RhinoLayer) ? ArcGisLayer : RhinoLayer;

        public bool IsComplete => !string.IsNullOrWhiteSpace(ArcGisLayer);

        /// <summary>One line naming the pair and how it syncs, for the pair picker.</summary>
        public string Title => !IsComplete
            ? "(row without an ArcGIS layer)"
            : $"{ArcGisLayer}  ⇄  {EffectiveRhinoLayer}  ({DescribeDirection(Direction)})";

        public static string DescribeDirection(SyncDirectionMode d)
        {
            switch (d)
            {
                case SyncDirectionMode.PullOnly: return "pull only";
                case SyncDirectionMode.PushOnly: return "push only";
                default: return "two-way";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(LastResult) || name == nameof(IsArcGisLayerMissing))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
            }
            if (name == nameof(ProfileJson))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomProfile)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileLabel)));
            }
            if (name == nameof(RhinoLayer) || name == nameof(ArcGisLayer))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveRhinoLayer)));
            if (name != nameof(LastResult) && name != nameof(IsArcGisLayerMissing) && name != nameof(ArcGisSource))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            return true;
        }
    }

    /// <summary>
    /// Saves and loads the link table in the Rhino document's user text, so the pairings come back
    /// with the .3dm rather than having to be re-picked every session.
    /// </summary>
    /// <remarks>
    /// Keys are <c>gis.link.N.arcgis</c>, <c>gis.link.N.rhino</c>, <c>gis.link.N.direction</c>, and
    /// optional <c>gis.link.N.profile</c> and <c>gis.link.N.source</c>, with N a 0-based row index;
    /// <c>gis.link.count</c> says how many. Rows are rewritten whole on save and stale rows past the
    /// new count are deleted, so removing a row removes its keys. Only keys whose value actually
    /// changes are written, so selecting or previewing a row does not mark the .3dm modified.
    /// </remarks>
    internal static class LayerLinkStore
    {
        const string Prefix = "gis.link.";

        internal static List<LayerLink> Load()
        {
            var links = new List<LayerLink>();
            var strings = RhinoHost.GetDocumentStrings(Prefix);
            if (!strings.TryGetValue(Prefix + "count", out var countText) ||
                !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                return links;

            for (int i = 0; i < count; i++)
            {
                strings.TryGetValue($"{Prefix}{i}.arcgis", out var arcgis);
                if (string.IsNullOrWhiteSpace(arcgis)) continue;
                strings.TryGetValue($"{Prefix}{i}.rhino", out var rhino);
                strings.TryGetValue($"{Prefix}{i}.direction", out var directionText);
                strings.TryGetValue($"{Prefix}{i}.profile", out var profileJson);
                strings.TryGetValue($"{Prefix}{i}.source", out var source);
                Enum.TryParse(directionText, true, out SyncDirectionMode direction);

                links.Add(new LayerLink
                {
                    ArcGisLayer = arcgis,
                    RhinoLayer = rhino ?? string.Empty,
                    Direction = direction,
                    ProfileJson = string.IsNullOrWhiteSpace(profileJson) ? null : profileJson,
                    ArcGisSource = string.IsNullOrWhiteSpace(source) ? null : source
                });
            }
            return links;
        }

        internal static void Save(IReadOnlyList<LayerLink> links)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            // Clear whatever is there first, so a removed row does not linger.
            var existing = RhinoHost.GetDocumentStrings(Prefix);
            foreach (var key in existing.Keys) values[key] = null;

            int n = 0;
            foreach (var link in links)
            {
                if (link == null || !link.IsComplete) continue;
                values[$"{Prefix}{n}.arcgis"] = link.ArcGisLayer;
                values[$"{Prefix}{n}.rhino"] = link.RhinoLayer ?? string.Empty;
                values[$"{Prefix}{n}.direction"] = link.Direction.ToString();
                if (!string.IsNullOrWhiteSpace(link.ProfileJson))
                    values[$"{Prefix}{n}.profile"] = link.ProfileJson;
                if (!string.IsNullOrWhiteSpace(link.ArcGisSource))
                    values[$"{Prefix}{n}.source"] = link.ArcGisSource;
                n++;
            }
            values[Prefix + "count"] = n.ToString(CultureInfo.InvariantCulture);

            var changes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in values)
            {
                existing.TryGetValue(kv.Key, out var current);
                if (!string.Equals(current, kv.Value, StringComparison.Ordinal)) changes[kv.Key] = kv.Value;
            }
            if (changes.Count > 0) RhinoHost.SetDocumentStrings(changes);
        }
    }
}
