using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RhinoArcGIS.Core.Reporting;
using RhinoArcGIS.Core.Sync;

namespace RhinoInside.ArcGISPro
{
    /// <summary>Opt-in, same-user, PID-addressed typed control. Never executes arbitrary scripts.</summary>
    internal static class McpControlBridge
    {
        static CancellationTokenSource _stopping;
        static Dispatcher _dispatcher;
        static Task _listener;

        internal static void StartIfEnabled()
        {
            if (Environment.GetEnvironmentVariable("RHINOINSIDE_MCP_ENABLED") != "1" || _stopping != null) return;
            _dispatcher = Application.Current?.Dispatcher;
            if (_dispatcher == null) return;
            _stopping = new CancellationTokenSource();
            _listener = Task.Run(() => Listen(_stopping.Token));
        }

        internal static void Stop()
        {
            // Shutdown must never block the ArcGIS UI waiting for work dispatched to that UI.
            _stopping?.Cancel();
        }

        static async Task Listen(CancellationToken stopping)
        {
            string name = "RhinoInside.ArcGIS.Mcp.v1." + System.Diagnostics.Process.GetCurrentProcess().Id;
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    object reply;
                    try
                    {
                        var line = await ReadBoundedLine(reader, timeout.Token).ConfigureAwait(false);
                        var request = JObject.Parse(line);
                        if (request.Properties().Any(p => p.Name != "operation" && p.Name != "arguments"))
                            throw new ArgumentException("Unexpected request property.");
                        var operation = (string)request["operation"];
                        var arguments = request["arguments"] as JObject ?? throw new ArgumentException("Arguments must be an object.");
                        var result = await Execute(operation, arguments, stopping).ConfigureAwait(false);
                        reply = new { ok = true, result };
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && stopping.IsCancellationRequested))
                    {
                        reply = new { ok = false, error = ex.Message };
                    }
                    await writer.WriteLineAsync(JsonConvert.SerializeObject(reply)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (IOException) { /* disconnected caller: do not retry its mutation */ }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("Rhino MCP listener stopped: {0}", ex.Message);
                    break;
                }
            }
        }

        static async Task<string> ReadBoundedLine(StreamReader reader, CancellationToken token)
        {
            var text = new StringBuilder();
            var buffer = new char[1];
            while (text.Length < 65536)
            {
                if (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) == 0)
                    throw new IOException("Incomplete request.");
                if (buffer[0] == '\n') return text.ToString();
                text.Append(buffer[0]);
            }
            throw new ArgumentException("Request exceeds 64 KiB.");
        }

        static T OnUi<T>(Func<T> action) => _dispatcher.CheckAccess() ? action() : _dispatcher.Invoke(action);

        static bool Approve(string operation, string detail) => OnUi(() =>
            ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                "An MCP client requests " + operation + ".\n\n" + detail + "\n\nAllow this exact operation?",
                "Rhino.Inside MCP review", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

        static void RequireApproval(string operation, string detail)
        {
            if (!Approve(operation, detail)) throw new InvalidOperationException("Local review denied or cancelled the operation.");
        }

        static void Validate(JObject args, params string[] names)
        {
            if (args.Properties().Any(p => !names.Contains(p.Name, StringComparer.Ordinal)))
                throw new ArgumentException("Unexpected argument.");
        }

        static string Required(JObject args, string key)
        {
            var value = args[key];
            if (value?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value) || ((string)value).Length > 4096)
                throw new ArgumentException("Missing or invalid string: " + key);
            return (string)value;
        }

        static LayerLink GetLink(JObject args)
        {
            var gis = Required(args, "arcgisLayer");
            var rhino = Required(args, "rhinoLayer");
            var source = Required(args, "expectedSource");
            return OnUi(() =>
            {
                if (!RhinoHost.IsStarted) throw new InvalidOperationException("Launch Rhino first.");
                var links = LayerLinkStore.Load().Where(l =>
                    string.Equals(l.ArcGisLayer, gis, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(l.EffectiveRhinoLayer, rhino, StringComparison.Ordinal) &&
                    string.Equals(l.ArcGisSource, source, StringComparison.OrdinalIgnoreCase)).ToList();
                if (links.Count != 1) throw new InvalidOperationException("Saved link not found or ambiguous. Refresh rhino_links; configure or repair the link in the dockpane.");
                return links[0];
            });
        }

        sealed class LinkContext
        {
            internal LayerLink Link;
            internal uint DocumentSerial;
        }

        static LinkContext CaptureLinkContext(JObject args) => OnUi(() => new LinkContext
        {
            Link = GetLink(args), DocumentSerial = RhinoHost.GetActiveDocumentSerial()
        });

        static void RequireLinkContext(LinkContext context, JObject args)
        {
            if (context.DocumentSerial == 0 || RhinoHost.GetActiveDocumentSerial() != context.DocumentSerial)
                throw new InvalidOperationException("The active Rhino document changed after the request; nothing was written. Refresh rhino_links and review again.");
            var current = GetLink(args);
            if (current.Direction != context.Link.Direction ||
                !string.Equals(current.ProfileJson, context.Link.ProfileJson, StringComparison.Ordinal))
                throw new InvalidOperationException("The saved link settings changed after the request; nothing was written. Refresh rhino_links and review again.");
        }

        static object Report(SyncReport report) => new
        {
            operation = report.Operation.ToString(),
            total = report.Entries.Count,
            outcomes = report.Entries.GroupBy(e => e.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            entries = report.Entries.Take(100).Select(e => new
            {
                outcome = e.Outcome.ToString(), e.Message, e.Detail, e.SyncGuid, e.RhinoGuid, e.ArcGisObjectId
            }).ToArray(),
            truncated = report.Entries.Count > 100
        };

        static async Task<object> Execute(string operation, JObject args, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            switch (operation)
            {
                case "rhino_status":
                    Validate(args);
                    return OnUi(() => new { pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                        started = RhinoHost.IsStarted, version = RhinoHost.RhinoVersion,
                        busy = SyncCoordinator.IsBusy, closing = SyncCoordinator.IsShutdownRequested,
                        integration = "development-candidate" });
                case "rhino_links":
                    Validate(args);
                    return OnUi(() => new { links = RhinoHost.IsStarted ? LayerLinkStore.Load().Select(l => new
                    {
                        arcgisLayer = l.ArcGisLayer, rhinoLayer = l.EffectiveRhinoLayer,
                        expectedSource = l.ArcGisSource, direction = l.Direction.ToString(),
                        profile = l.ProfileLabel
                    }).ToArray() : null });
                case "rhino_launch":
                    Validate(args);
                    return await SyncCoordinator.RunHostActionAsync<object>(() =>
                    {
                        stopping.ThrowIfCancellationRequested();
                        RequireApproval("launch embedded Rhino", "The active ArcGIS project may reopen its saved Rhino document.");
                        stopping.ThrowIfCancellationRequested();
                        RhinoHost.Start();
                        return new { started = RhinoHost.IsStarted };
                    }, stopping).ConfigureAwait(false);
                case "rhino_save":
                    Validate(args);
                    return await SyncCoordinator.RunHostActionAsync<object>(() =>
                    {
                        stopping.ThrowIfCancellationRequested();
                        var serial = RhinoHost.GetActiveDocumentSerial();
                        RequireApproval("save the active Rhino document", "Writes the current .3dm. An unnamed document opens the host Save dialog.");
                        stopping.ThrowIfCancellationRequested();
                        if (serial == 0 || RhinoHost.GetActiveDocumentSerial() != serial)
                            throw new InvalidOperationException("The active Rhino document changed during local review; nothing was saved. Review again.");
                        string path = RhinoHost.SaveActiveDocument();
                        if (path == null) throw new InvalidOperationException("Save was cancelled; no document was saved.");
                        return new { savedPath = path };
                    }, stopping).ConfigureAwait(false);
                case "rhino_profile":
                case "rhino_preview":
                case "rhino_pull":
                case "rhino_apply":
                    Validate(args, "arcgisLayer", "rhinoLayer", "expectedSource",
                        operation == "rhino_pull" ? "selectedOnly" : operation == "rhino_apply" ? "conflicts" : "expectedSource");
                    var context = CaptureLinkContext(args);
                    var link = context.Link;
                    string target = link.ArcGisLayer + " ↔ " + link.EffectiveRhinoLayer + "\nSource: " + link.ArcGisSource + "\nDirection: " + link.Direction;
                    if (operation == "rhino_profile")
                    {
                        var draft = await SyncCoordinator.GetProfileDraftAsync(link.ArcGisLayer, link.EffectiveRhinoLayer,
                            link.ProfileJson, link.ArcGisSource, () => RequireLinkContext(context, args),
                            context.DocumentSerial, stopping).ConfigureAwait(false);
                        return new { draft.Schema, draft.Profile };
                    }
                    if (operation == "rhino_preview")
                        return Report(await SyncCoordinator.PreviewAsync(link.ArcGisLayer, link.EffectiveRhinoLayer,
                            link.Direction, link.ProfileJson, link.ArcGisSource, () => RequireLinkContext(context, args),
                            context.DocumentSerial, stopping).ConfigureAwait(false));
                    if (operation == "rhino_pull")
                    {
                        if (args["selectedOnly"] != null && args["selectedOnly"].Type != JTokenType.Boolean)
                            throw new ArgumentException("selectedOnly must be a boolean.");
                        bool selected = (bool?)args["selectedOnly"] ?? false;
                        return Report(await SyncCoordinator.ReviewAndPullAsync(link.ArcGisLayer, link.EffectiveRhinoLayer,
                            selected, link.ProfileJson, link.ArcGisSource,
                            () => Approve("pull ArcGIS features into Rhino", target + "\nSelected only: " + selected),
                            () => RequireLinkContext(context, args), stopping).ConfigureAwait(false));
                    }
                    var conflicts = ConflictResolution.Manual;
                    if (args["conflicts"] != null && (args["conflicts"].Type != JTokenType.String ||
                        !Enum.TryParse((string)args["conflicts"], false, out conflicts) || !Enum.IsDefined(typeof(ConflictResolution), conflicts)))
                        throw new ArgumentException("Unknown conflict policy.");
                    return Report(await SyncCoordinator.ReviewAndApplyAsync(link.ArcGisLayer, link.EffectiveRhinoLayer,
                        conflicts, link.Direction, link.ProfileJson, link.ArcGisSource, preview =>
                        Approve("apply synchronization", target + "\nConflict policy: " + conflicts + "\n\n" +
                            string.Join("\n", preview.Entries.GroupBy(e => e.Outcome).Select(g => g.Key + ": " + g.Count())) +
                            "\n\n" + string.Join("\n", preview.Entries.Where(e => e.Message != "Clean").Take(20)
                                .Select(e => e.Outcome + ": " + e.Message + " " + e.Detail))),
                        () => RequireLinkContext(context, args), stopping).ConfigureAwait(false));
                default:
                    throw new ArgumentException("Unknown typed operation.");
            }
        }
    }
}
