# Development

The add-in targets ArcGIS Pro 3.7 and Rhino 8 on Windows 11 x64. Building requires
the .NET 10 SDK, the ArcGIS Pro SDK build targets, and the installed ArcGIS Pro assemblies.
The Core tests target .NET 8 and can run without either desktop application.

For a compile check while ArcGIS Pro is running, use
`dotnet build src/RhinoInside.ArcGISPro.AddIn.sln -c Release -p:SkipAddinPackaging=true`.
This skips the Esri package/deploy targets and does not register an add-in. Run the offline MCP
protocol tests with `node --test tests/mcp/protocol.test.mjs`. See [MCP control](MCP_CONTROL.md).

## Projects

| Project | Responsibility |
|---|---|
| `RhinoArcGIS.Core` | SDK-independent geometry descriptions, identity, transforms, field ownership, validation and synchronization rules; `netstandard2.0`. |
| `RhinoArcGIS.ArcGIS` | ArcGIS schema, geometry and feature operations; `net10.0-windows`. |
| `RhinoArcGIS.Rhino` | Rhino geometry, attributes and document metadata; `net10.0-windows`. |
| `RhinoInside.ArcGISPro` | Embedded host, coordinator and ArcGIS dockpane; `net10.0-windows10.0.22621.0`. |

The dockpane routes synchronization through `SyncCoordinator` and the shared Core services.
Rhino operations run on the host UI thread; ArcGIS work uses its queued task where required.
Keep RhinoCommon access behind the host resolver and `NoInlining` boundary.

RhinoCommon and Grasshopper compile references are pinned to 8.23.25251.13001. Runtime assemblies
come from the licensed Rhino 8 installation. Do not copy Rhino or ArcGIS host DLLs into the add-in.

## Checks

```powershell
dotnet test tests/RhinoArcGIS.Core.Tests/RhinoArcGIS.Core.Tests.csproj -c Release
dotnet build src/RhinoInside.ArcGISPro.AddIn.sln -c Release
node site/build.mjs
```

The Release build registers the add-in on the local machine. Close ArcGIS Pro before building.

For a live run, create a disposable ArcGIS project with a map named `Map`, then run:

```powershell
./tools/e2e.ps1 -Project 'C:\scratch\Review.aprx' -ResetRhinoDocument -Scratch 'C:\scratch\run-1'
```

`-ResetRhinoDocument` discards the test host's current Rhino document. Use it only with a disposable
host. The matrix copies the synthetic fixtures before mutation and uses temporary geodatabases.
Run twice with independent scratch directories and fresh host processes for release acceptance.

The matrix covers all supported geometry families, selection-only pull, identity and baselines,
two-way attribute and geometry updates, conflict policies, deletion holds, source-change warnings,
bulk failure isolation, saved links, profile authoring and Rhino-first feature-class creation.
It also constructs the real dockpane and opens Profile, so view-binding failures are exercised.
Portable tests and standalone Rhino checks do not establish embedded-host compatibility.

It then runs what a real back-and-forth session does: edits on both sides before one preview
(move, retype, delete, copy and draw in Rhino; attribute and geometry edits in ArcGIS), repeated
preview/apply rounds, a repeat Pull, back-and-forth edits on one object, a preview with an
attribute table focused, a renamed layer, and a shapefile delete that renumbers FIDs.

The run works on a copy of `-Project` in the scratch folder and ends by closing Pro through the
add-in (`Close-Pro` in `tools/bridge.ps1`), which saves or discards the project and the Rhino
document and lets Pro dispose the hosted Rhino. The session phase closes and restarts Pro twice
and fails on any Project Recovery or Rhino Autosave Recovery prompt, a Rhino document that does
not reopen by itself, or links that compare differently after the restart. Use `-SkipSession` to
skip the restarts and `-LeavePro` to keep Pro open afterwards. Never end a test host with a
force-kill: that is what produces the recovery prompts on the next start.

## Scale benchmark

`tools/benchmark.ps1` times pull, preview and apply on synthetic street networks and parcel
fabrics (`tools/generate-benchmark-data.py`) in a live Pro, smallest size first. It skips any step
whose extrapolated time exceeds `-StepBudgetMin`. Each step records the phase breakdown the services
report (`SyncReport.PhaseMs`) and Pro's memory, in `benchmark.json` and `benchmark.csv`.
`ScaleTests` runs the same steps over in-memory adapters to time the engine alone; set
`RHINOINSIDE_SCALE_N=100000` to run it at full size.

```powershell
./tools/benchmark.ps1 -Project 'C:\scratch\Review.aprx' -Scratch 'C:\scratch\bench-1' -Sizes 1000,20000,100000
```

At 100,000 features, a pull takes about 1.5 minutes, and a preview or an apply of 1% edits takes 15 to 25 s.
A pull is mostly per-vertex ArcGIS geodesy, prepared on several threads (`PullPreparation`; set
`RHINOINSIDE_ARCGIS_GEODESY_THREADS=1` for serial). After each pull the benchmark re-prepares a
sample serially and fails on any difference. A preview reads both sides in full: about 7 s for
Rhino (in UI-thread chunks), 4 s for ArcGIS, and 3.5 s to plan. Keep large-layer Rhino writes in
the bulk path (`IBulkRhinoAdapter`):

- each batch runs inside one undo record, because `Objects.Replace` without one costs time in
  proportion to the document;
- redraw happens once per batch set;
- multipart pieces come from a per-layer index, never from a document walk or
  `GroupTable.GroupMembers`.

Planning skips the georeference for untouched objects by using their model stamp
(`gis.rhino_model_stamp`).

## Sync identity

`SyncEngine.BuildPlan` claims each ArcGIS feature for at most one Rhino object. It tries GlobalID
first, then object ID, then the geometry baseline, which re-matches shapefile FIDs that renumber
after a delete. A second Rhino object with the same identity is a copy and becomes a new feature.
Newly linked objects record their own Rhino ID in `gis.rhino_objectid`. An Alt-gumball copy
inherits the tags but has a different ID, so it is new work even if the original moved or is
outside the current read. Each physical piece of a newly pulled multipart feature has its own
stamp; copied pieces are offered separately rather than merged into the original feature.
Apply assigns a copy a new sync identity and removes inherited multipart membership.

Older objects without the stamp retain the geometry-based fallback. Apply stamps a matched
legacy representative; preview remains read-only. Existing copies and multipart pieces made
before owner stamps were recorded cannot always be distinguished from their originals. This
change does not retroactively repair those ambiguous identities or already-applied GIS edits.

A per-layer `SyncLedger` (`gis.ledger.<layer>` document string) records what was last synced, as
GlobalIDs or, for object-ID-only layers, geometry hashes. The ledger lets a feature deleted in Rhino
be held instead of re-pulled, even when its FID is reused. Objects linked to another ArcGIS layer
are left alone, and publishing them offers Copy or Move.

## Georeferencing

Rhino's earth anchor is WGS84. The core does no geodesy of its own: ArcGIS and Rhino supply it.

- **Per-vertex placement.** For documents marked `gis.georef.mode = local-frame` (every document
  with nothing synced yet), `GeodeticCoordinateMap` in `RhinoArcGIS.ArcGIS` maps each vertex on its
  own. Model coordinates become metres east and north of the anchor. `GeometryEngine.GeodeticMove`
  finds that ground point on WGS84, and it is projected into the synced layer's CRS. The way back
  projects to WGS84 and uses `GeodeticDistanceAndAzimuth`. That call returns the azimuth in
  **degrees** in Pro 3.7, although the SDK reference says radians. `GeodeticMove` takes radians.
- **Datums.** `DatumTransforms` picks the transformation: the environment's preference, else
  `ProjectionTransformation.Create` for the area of interest, given in the source CRS. It always
  resolves the WGS84-side direction and uses `GetInverse()` for the other, so both directions
  agree exactly.
- **Planar documents.** Documents already synced with the older planar frame are marked `planar`
  and keep it. There is no migration command yet. `ILocalFrameProjector` (a linear frame measured
  at the anchor) remains as a fallback when no per-vertex map is available.
- **Units.** Model units come from Rhino itself: `RhinoMath.UnitScale`, or the document's custom
  unit length. They travel as `EarthAnchor.MetresPerModelUnit`, so every Rhino unit system works.
  Documents without units are refused. The CRS unit length comes from ArcGIS's `LinearUnit`.
  The planar path keeps its historical reading of US survey feet as international feet, so older
  documents do not move by 2 ppm.

Checked live across 12 Rhino unit systems and State Plane (NAD83 and NAD83(2011)) → Rhino →
new maps in Web Mercator, UTM and WGS84, and into a new project. Every vertex landed within
0.1 mm on the ground.

## Local test bridge

`tools/bridge.ps1` uses an opt-in file interface enabled by `RHINOINSIDE_TESTBRIDGE` in the ArcGIS Pro
process environment. It is disabled by default and opens no network listener. Use it only for a
controlled local test host. `tools/e2e.ps1` handles setup and cleanup for the acceptance matrix.
Commands used only by demos and georeferencing checks live in `Model/TestBridgeDemo.cs`. They cover
map setup, layouts, point projection and geodesy. `capturepane` renders the real dockpane to PNG.

## Data and release evidence

See [test data](../tests/data/README.md) for the synthetic fixture generator and provenance.
Keep logs, crash dumps, scratch projects, build outputs and historical investigation notes outside
the public source tree. Release assets should include only the installer, checksums, license notices,
manifest and a sanitized validation record.

See [production readiness](PRODUCTION_READINESS.md), [publishing](PUBLISHING.md) and the
[roadmap](ROADMAP.md) for the supported release boundary and future work.
