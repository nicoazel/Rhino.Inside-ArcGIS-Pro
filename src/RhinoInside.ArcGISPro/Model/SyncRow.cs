using System;
using System.Collections.Generic;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Reporting;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// One line of a sync result, shaped for display: which side changed, shown as a glyph.
    /// </summary>
    public sealed class SyncRow
    {
        public string Glyph { get; set; }
        public string GlyphColor { get; set; }
        public string State { get; set; }
        public string Side { get; set; }
        public string Detail { get; set; }
        public string Target { get; set; }
        public string Identifier { get; set; }
        public string Context { get; set; }

        /// <summary>
        /// The second line of a review row: which feature, and what changed or why --
        /// "OID 12 · geometry, height_m". Never a bare sync guid, which means nothing to a person.
        /// </summary>
        public string Summary { get; set; }

        /// <summary>The Rhino object this row is about, when it has one.</summary>
        public Guid? RhinoGuid { get; set; }

        /// <summary>The ArcGIS feature this row is about, when it has one.</summary>
        public long? ObjectId { get; set; }

        /// <summary>False for rows that need no action, which the list hides by default.</summary>
        public bool IsChanged { get; set; }

        const string Grey = "#9AA0A6";
        const string RhinoGreen = "#3FA45B";
        const string ArcGisBlue = "#2E86C1";
        const string ConflictRed = "#D64550";
        const string HeldAmber = "#D9A53A";

        /// <summary>
        /// Builds a row from a report entry. A preview writes the object's
        /// <see cref="SyncState"/> into the message, so that is preferred when it parses; an apply
        /// reports what it did instead, and falls back to the outcome.
        /// </summary>
        public static SyncRow From(SyncReportEntry entry)
        {
            if (Enum.TryParse(entry.Message, out SyncState state))
                return FromState(state, entry);

            return FromOutcome(entry);
        }

        static SyncRow FromState(SyncState state, SyncReportEntry entry)
        {
            string glyph, colour, side;
            var changed = true;

            switch (state)
            {
                case SyncState.Clean:
                    glyph = "\uE73E"; colour = Grey; side = "in sync"; changed = false; break;

                case SyncState.NewInRhino:
                    glyph = "\uE710"; colour = RhinoGreen; side = "Rhino"; break;
                case SyncState.ModifiedInRhino:
                    glyph = "\uE70F"; colour = RhinoGreen; side = "Rhino"; break;
                case SyncState.DeletedInRhino:
                    glyph = "\uE74D"; colour = RhinoGreen; side = "Rhino"; break;

                case SyncState.NewInArcGis:
                    glyph = "\uE710"; colour = ArcGisBlue; side = "ArcGIS"; break;
                case SyncState.ModifiedInArcGis:
                    glyph = "\uE70F"; colour = ArcGisBlue; side = "ArcGIS"; break;
                case SyncState.DeletedInArcGis:
                    glyph = "\uE74D"; colour = ArcGisBlue; side = "ArcGIS"; break;

                case SyncState.ModifiedInBoth:
                case SyncState.Conflict:
                    glyph = "\uE7BA"; colour = ConflictRed; side = "both"; break;

                case SyncState.Orphaned:
                    glyph = "\uE897"; colour = ConflictRed; side = "orphaned"; break;

                default:
                    glyph = "\uE783"; colour = Grey; side = "unsupported"; break;
            }

            return new SyncRow
            {
                Glyph = glyph,
                GlyphColor = colour,
                State = Humanise(state.ToString()),
                Side = side,
                Detail = entry.Target,
                Target = entry.Target,
                Identifier = IdentifierOf(entry),
                Context = ContextOf(entry),
                // The number a Rhino object remembers for a feature that is gone may already belong
                // to another row (a shapefile renumbers), so say it is the old one.
                Summary = state == SyncState.DeletedInArcGis && entry.ArcGisObjectId.HasValue
                    ? "was OID " + entry.ArcGisObjectId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · no longer in ArcGIS"
                    : SummaryOf(entry, null),
                RhinoGuid = entry.RhinoGuid,
                ObjectId = entry.ArcGisObjectId,
                IsChanged = changed
            };
        }

        static SyncRow FromOutcome(SyncReportEntry entry)
        {
            string glyph, colour;
            var changed = true;

            switch (entry.Outcome)
            {
                case SyncOutcome.Created: glyph = "\uE710"; colour = RhinoGreen; break;
                case SyncOutcome.Updated: glyph = "\uE895"; colour = ArcGisBlue; break;
                case SyncOutcome.Conflict: glyph = "\uE7BA"; colour = ConflictRed; break;
                case SyncOutcome.Failed: glyph = "\uEA39"; colour = ConflictRed; break;
                case SyncOutcome.Warning: glyph = "\uE7BA"; colour = ConflictRed; break;
                // Seen but deliberately not written: still needs the user's attention.
                case SyncOutcome.Held: glyph = "\uE769"; colour = HeldAmber; break;
                default: glyph = "\uE73E"; colour = Grey; changed = false; break;
            }

            return new SyncRow
            {
                Glyph = glyph,
                GlyphColor = colour,
                State = entry.Outcome.ToString(),
                Side = string.Empty,
                Detail = string.IsNullOrEmpty(entry.Message) ? entry.Target : entry.Message,
                Target = entry.Target,
                Identifier = IdentifierOf(entry),
                Context = ContextOf(entry),
                Summary = SummaryOf(entry, entry.Message),
                RhinoGuid = entry.RhinoGuid,
                ObjectId = entry.ArcGisObjectId,
                IsChanged = changed
            };
        }

        static string SummaryOf(SyncReportEntry entry, string message)
        {
            string who = entry.ArcGisObjectId.HasValue
                ? "OID " + entry.ArcGisObjectId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : entry.SyncGuid == Guid.Empty ? entry.Target
                : entry.RhinoGuid.HasValue ? "Rhino object, not yet in ArcGIS"
                : "Object " + entry.SyncGuid.ToString("N").Substring(0, 8);
            string what = !string.IsNullOrWhiteSpace(entry.Detail) ? entry.Detail
                : !string.IsNullOrWhiteSpace(message) && !string.Equals(message, entry.Target, StringComparison.Ordinal) ? message
                : null;
            return what == null ? who : who + " · " + what;
        }

        static string IdentifierOf(SyncReportEntry entry) => entry.SyncGuid == Guid.Empty
            ? "Layer"
            : "Object " + entry.SyncGuid.ToString("N").Substring(0, 8);

        static string ContextOf(SyncReportEntry entry)
        {
            string context = (entry.Target ?? "Unknown target") + " · " + IdentifierOf(entry);
            if (!string.IsNullOrWhiteSpace(entry.Message) &&
                !Enum.TryParse(entry.Message, out SyncState _) &&
                !string.Equals(entry.Message, entry.Target, StringComparison.Ordinal))
                context += " — " + entry.Message;
            return context;
        }

        /// <summary>"ModifiedInArcGis" -> "Modified in ArcGIS".</summary>
        static string Humanise(string state)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(state, "(?<!^)([A-Z])", " $1");
            return text.Replace("Arc Gis", "ArcGIS").Replace("In ", "in ");
        }
    }

    /// <summary>One tile of the roll-up: a category and how many objects are in it.</summary>
    public sealed class SyncRollupItem
    {
        public string Glyph { get; set; }
        public string GlyphColor { get; set; }
        public string Label { get; set; }
        public int Count { get; set; }

        /// <summary>Groups rows into an ordered set of tiles, dropping empty categories.</summary>
        public static IReadOnlyList<SyncRollupItem> From(IEnumerable<SyncRow> rows)
        {
            var byLabel = new Dictionary<string, SyncRollupItem>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var row in rows)
            {
                if (!byLabel.TryGetValue(row.State, out var item))
                {
                    item = new SyncRollupItem { Glyph = row.Glyph, GlyphColor = row.GlyphColor, Label = row.State };
                    byLabel[row.State] = item;
                    order.Add(row.State);
                }
                item.Count++;
            }

            var result = new List<SyncRollupItem>();
            foreach (var key in order) result.Add(byLabel[key]);
            return result;
        }
    }
}
