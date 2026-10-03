using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RhinoArcGIS.Core.Reporting
{
    public enum SyncOperation
    {
        Pull = 0,
        Push,
        Validate,
        Sync
    }

    public enum SyncOutcome
    {
        Created = 0,
        Updated,
        Skipped,
        Conflict,
        Warning,
        Failed,

        /// <summary>
        /// A change the run saw but deliberately did not write, because the sync direction
        /// forbids it. Its baseline is untouched, so it is still a change next time.
        /// </summary>
        Held
    }

    /// <summary>One line in a sync report (spec 02 — "sync report generated").</summary>
    public sealed class SyncReportEntry
    {
        public Guid SyncGuid { get; set; }
        public SyncOperation Operation { get; set; }
        public string Target { get; set; }
        public SyncOutcome Outcome { get; set; }
        public string Message { get; set; }

        /// <summary>The ArcGIS ObjectID concerned, when there is one; for the review list.</summary>
        public long? ArcGisObjectId { get; set; }

        /// <summary>The Rhino object concerned, when there is one; lets the pane select it.</summary>
        public Guid? RhinoGuid { get; set; }

        /// <summary>What changed ("geometry", field names) or why, in a few words.</summary>
        public string Detail { get; set; }
    }

    /// <summary>
    /// Accumulates the result of a push/pull/validate run and serializes it to CSV or JSON
    /// (spec 06 §10 "CSV/JSON sync report").
    /// </summary>
    public sealed class SyncReport
    {
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public SyncOperation Operation { get; set; }
        public List<SyncReportEntry> Entries { get; set; } = new List<SyncReportEntry>();

        public SyncReport() { }

        public SyncReport(SyncOperation operation)
        {
            Operation = operation;
        }

        /// <summary>
        /// Wall-clock milliseconds spent in each phase of the run (reading each side, planning,
        /// writing), accumulated by phase name. What the scale benchmark reads to say where a
        /// large layer's time goes.
        /// </summary>
        public Dictionary<string, long> PhaseMs { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Runs <paramref name="work"/> and adds its duration to <paramref name="phase"/>.</summary>
        public T Time<T>(string phase, Func<T> work)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { return work(); }
            finally { AddPhase(phase, sw.ElapsedMilliseconds); }
        }

        /// <summary>Runs <paramref name="work"/> and adds its duration to <paramref name="phase"/>.</summary>
        public void Time(string phase, Action work)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { work(); }
            finally { AddPhase(phase, sw.ElapsedMilliseconds); }
        }

        public void AddPhase(string phase, long milliseconds)
        {
            PhaseMs.TryGetValue(phase, out long soFar);
            PhaseMs[phase] = soFar + milliseconds;
        }

        public SyncReportEntry Add(SyncReportEntry entry)
        {
            Entries.Add(entry);
            return entry;
        }

        public SyncReportEntry Add(Guid syncGuid, string target, SyncOutcome outcome, string message = null)
        {
            var e = new SyncReportEntry
            {
                SyncGuid = syncGuid,
                Operation = Operation,
                Target = target,
                Outcome = outcome,
                Message = message
            };
            Entries.Add(e);
            return e;
        }

        public int CountOf(SyncOutcome outcome)
        {
            int n = 0;
            foreach (var e in Entries)
                if (e.Outcome == outcome) n++;
            return n;
        }

        public string ToJson()
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
                Converters = { new Newtonsoft.Json.Converters.StringEnumConverter(new SnakeCaseNamingStrategy()) },
                Formatting = Formatting.Indented
            };
            return JsonConvert.SerializeObject(this, settings);
        }

        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.Append("sync_guid,operation,target,outcome,message,object_id,detail\n");
            foreach (var e in Entries)
            {
                sb.Append(e.SyncGuid.ToString()).Append(',');
                sb.Append(e.Operation).Append(',');
                sb.Append(Csv(e.Target)).Append(',');
                sb.Append(e.Outcome).Append(',');
                sb.Append(Csv(e.Message)).Append(',');
                sb.Append(e.ArcGisObjectId.HasValue
                    ? e.ArcGisObjectId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty).Append(',');
                sb.Append(Csv(e.Detail)).Append('\n');
            }
            return sb.ToString();
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!mustQuote) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
