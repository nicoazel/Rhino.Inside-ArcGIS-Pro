# Embedded Rhino MCP control

This source candidate includes an optional stdio MCP gateway for the Rhino document hosted inside ArcGIS Pro. It complements the separately installed [ArcGISPro.MCP](https://github.com/nicoazel/ArcGISPro.MCP) gateway, which controls maps, layers, layouts and geoprocessing. Configure both servers in the MCP client and select the same ArcGIS Pro PID. The Rhino operations are exposed by this gateway, not registered in the companion's operation catalog.

**Validation status:** RC1 passed isolated live control checks with actual local approval/denial dialogs and a fresh-process reopen. RC2 adds document/link revalidation and cancellable gate waits. Exact package installation, companion coexistence and the full visual matrix remain acceptance gates. This candidate is not a certified public installer.

## Setup

1. Install a release of this add-in containing `McpControlBridge`, with ArcGIS Pro closed. The current source candidate has not been packaged for public distribution.
2. Set `RHINOINSIDE_MCP_ENABLED=1` in the environment used to start the disposable ArcGIS Pro process. The endpoint is off by default. Set up and save the layer link in the Rhino.Inside dockpane, including its data-source identity.
3. Use Node.js 22 or later to run `tools/mcp/server.mjs`. Set `RHINOINSIDE_MCP_HOST_PID` to that ArcGIS Pro process ID. Each PID has a separate same-user Windows named pipe; there is no automatic selection or network listener.

Example MCP client configuration (replace the path and PID with your installation):

```json
{
  "mcpServers": {
    "rhino-inside-arcgis": {
      "command": "node",
      "args": ["C:\\RhinoInsideArcGIS\\tools\\mcp\\server.mjs"],
      "env": { "RHINOINSIDE_MCP_HOST_PID": "1234" }
    }
  }
}
```

Configure the companion gateway separately using its documentation and `ARCGIS_PRO_MCP_HOST_PID=1234`. Enabling the Rhino endpoint does not enable or configure the companion server. Node is needed for this optional gateway, not for ordinary dockpane use.

## Operations

| Tool | Behavior |
|---|---|
| `rhino_status` | Host PID, started/version, synchronization busy state and closing reservation. |
| `rhino_links` | Saved link names, direction, profile label and expected source identity. |
| `rhino_profile` | Current schema and reconciled profile for one saved link. |
| `rhino_preview` | Read-only sync report; requires initialized georeferencing. |
| `rhino_launch` | Launch embedded Rhino after a local confirmation. |
| `rhino_pull` | Review and pull under the shared gate; rechecks the bound document and saved link before writes. |
| `rhino_apply` | Fresh preview and local review followed by Apply under the shared synchronization gate. |
| `rhino_save` | Save the current Rhino document after local confirmation. |

Link operations require `arcgisLayer`, `rhinoLayer` and `expectedSource` exactly as returned by `rhino_links`. Missing/ambiguous links and changed sources fail before writes. A same-label layer never replaces a saved source automatically; remove and recreate the link after making the intended layer unique in the candidate maps. Repairs and new link/profile authoring are performed in the dockpane. `rhino_apply` accepts `Manual` (default), `PreferRhino` or `PreferArcGis` conflict policy. Its local review shows the target, source, direction, policy and fresh report. Denial/cancellation returns a tool error and does not apply. A result includes full outcome counts and up to 100 report rows, with an explicit truncation flag.

There is no arbitrary-script, deletion, unsaved-work-discard or remote approval tool. Approval cannot be granted through the MCP client. Preview of an uninitialized document fails with an actionable message rather than changing document metadata; first initialize through a locally approved Pull.

Pull and Apply bind the request to its Rhino document and saved link settings. Changing the document,
profile or direction while queued or during review rejects the mutation. Each UI batch also checks
the bound document, so a document switch cannot redirect later chunks into a different file.
Bridge shutdown cancels queued mutation waits and is checked again after review, before writes.
An operation that already started writing retains the gate until it finishes; cancellation does
not roll back those writes.

Client cancellation cannot undo a dispatched host operation. Requests are processed sequentially; a transport disconnect or timeout does not prove a mutation failed. Inspect state before retrying. Keep other clients from editing the same linked data during review/apply. Coexistence with the companion, edits made outside the shared coordinator and shutdown during control are deferred host acceptance cases.

The coordinator refuses shutdown while synchronization or an approved host action is active, and
rejects new work while the close request is reserved. A canceled closing event releases that
reservation. Another add-in can also veto `CanUnload` after the event without exposing a cancellation
signal here; this multi-add-in case remains a release acceptance gate. A subsequent close attempt
can renew the idle reservation. Check `rhino_status.closing` when diagnosing blocked operations.

## Offline verification and remaining acceptance

```powershell
node --test tests/mcp/protocol.test.mjs
dotnet test tests/RhinoArcGIS.Core.Tests/RhinoArcGIS.Core.Tests.csproj -c Release
dotnet build src/RhinoInside.ArcGISPro.AddIn.sln -c Release -p:SkipAddinPackaging=true
```

`SkipAddinPackaging=true` removes the Esri packaging/deploy import; it compiles against installed SDK references without registering an add-in. It produces no installable package and establishes no live compatibility. A normal build still packages/registers locally and must wait until host work is finished.

After both projects settle, freeze the source and package hashes and test: same-PID discovery; wrong PID and missing Rhino errors; independent multiple hosts; schema/profile inspection; metadata-preserving first Preview; source change/ambiguity rejection; approval denial; pull, preview, apply and conflicts across geometry families; busy state; timeout recovery; document save/cancel; shutdown/reopen; and companion writes during a Rhino operation. Run the main embedded matrix twice on independent disposable hosts and inspect the actual UI. Record both source commits, both packages and exact host versions.

The gateway implements stateless MCP 2026-07-28 discovery and per-request protocol metadata. It also supports the legacy initialize/initialized handshake for 2025-11-25, 2025-06-18, 2025-03-26 and 2024-11-05 clients. The offline Windows transport test uses a fake pipe with a synthetic PID; it never connects to ArcGIS Pro.

Protocol references: [versioning](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning), [discovery](https://modelcontextprotocol.io/specification/2026-07-28/server/discover), [stdio transport](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio) and [legacy tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).
