# Roadmap

The 1.3 synchronization baseline covers the ArcGIS Pro 3.7 add-in with embedded Rhino 8.
The next major development wave is 2.0.0-alpha.1 on `codex/v2-grasshopper-map-workflows`.
Rhino 7 support has been removed from the plan.

## Grasshopper 2.0 development wave

The first implementation adds a packaged `RhinoArcGIS.Grasshopper.gha` and a Grasshopper ribbon
launcher. The GHA depends on neutral Core contracts and the Rhino geometry adapter; ArcGIS SDK
access remains in the existing ArcGIS adapter and an embedded host service.

| Milestone | Implementation | Acceptance |
| --- | --- | --- |
| Discover and read | Map/layer URI and verified source/CRS binding, schema/capabilities, selected/all bounded reads, typed attribute output, multipart grouping | Core and embedded read/identity checks passed |
| Temporary preview | Component-owned overlays, absolute elevation, color/alpha, width, geometry/vertex limits, asynchronous replacement and cleanup | Point and mesh SDK execution passed; cyan massing overlay visually observed in a 2D map with zero Rhino objects; broader scene appearance remains a gate |
| Create and publish | Explicit schema, default/chosen file geodatabase, new layer added to chosen map, keyed publishing with retained receipts | Fresh host creation and field/geometry round trips passed; broader repeat-action/reopening qualification pending |
| Update existing | Frozen review plans, full read-time baseline and schema, GlobalID-first matching, geometry/attribute patches, atomic edit operation | Core contracts and embedded attribute/geometry updates plus stale-plan rejection passed; live undo qualification pending |
| Package and examples | GHA included in add-in, compile-only host SDK references, component documentation, disposable acceptance harness, runnable `examples/Grasshopper/MapDesign.ghx` | Builds and functional hosts passed; clean-close gate failed at the native project-save prompt |

The alpha has **322 passing Core tests**. Disposable hosts D and E recorded 16 and 17 functional
checks respectively (E also generated the example): actual GHA registration and component
instantiation, an asynchronous Grasshopper solution, feature-class creation, GlobalID readback,
model-coordinate and null preservation, read-only planning, attribute and geometry updates,
stale-plan rejection, point/multipatch overlay execution and mesh publication/readback.
Host F repeated the 17 functional checks on the final CRS/schema-protected build and opened the
runnable example. A fresh visual observation showed its cyan box overlay in the map while the
Rhino dockpane reported zero objects. This establishes basic 2D preview appearance without baking;
slider-driven motion, scene styling and full release qualification remain separate checks.

During the first run, Rhino 8.34 displayed the known IronPython/.NET 10 `scriptcontext` dialog.
With explicit user authorization the machine was updated to McNeel-signed Rhino 8.36.26282.15001.
The launcher now checks that compatibility boundary. Grasshopper Core offers a process-scoped
vendor-plus-ArcGIS library mode for older installed add-ons. A duplicate library-load bug found
during testing was corrected before the passing functional runs.

The hosts passed their functional checks but did not close unattended: ArcGIS displayed its
native project-save confirmation despite the harness's pre-close save. The runner records
`cleanExit: false`, terminates only its verified disposable process, restores the previous add-in,
and fails the clean-close gate. Two clean full-matrix hosts are still required for release.

Core rules have portable tests in `GrasshopperPlanningTests`. Run the installed SDK build with:

```powershell
dotnet test tests/RhinoArcGIS.Core.Tests/RhinoArcGIS.Core.Tests.csproj -c Release
dotnet build src/RhinoInside.ArcGISPro.AddIn.sln -c Release -p:ArcGISFolder=
```

Use `tools/grasshopper-acceptance.ps1 -SourceProject <disposable-template.aprx> -Scratch <fresh-folder> -Install`
for the new embedded acceptance checks. The harness refuses an existing Pro process, copies the
project into a fresh folder with its own geodatabase, backs up the installed add-in, and restores
it after its owned host exits. A stuck disposable host is recorded as a clean-close failure and
terminated by its verified process identity before restoring the backup. Run the full existing `tools/e2e.ps1` matrix twice
before promoting the major version.

The starting checkout contained unfinished copy-identity and shutdown work, preserved in checkpoint
commit `2f06600`. Its exact pre-wave
patch and status are recorded locally under `artifacts/grasshopper-v2/`; they are not release
source. The initial Core suite was 284/291; the seven failures were stale fake-adapter/ownership
expectations. Those tests now honor follow-up user-string writes and exclude per-object owner
GUIDs when comparing independent pull runs.

## Current release

- Optional embedded Rhino MCP gateway and typed, locally reviewed control. Portable validation is
  available; embedded-host/coexistence acceptance remains a release gate. See [MCP control](MCP_CONTROL.md).

- Point Z, Polyline Z, Polygon Z, multipart polygon and Multipatch exchange.
- Initial GIS pull, selection-only pull and Rhino-first feature-class creation.
- Preview and apply with two-way, pull-only and push-only directions.
- GlobalID-first identity, change tracking, deletion holds and explicit conflict policies.
- Saved layer links and field profiles with inclusion, ownership, units and validators.
- Multi-link status, bulk actions and contextual help in the ArcGIS dockpane.
- Per-vertex georeferencing with ArcGIS datum transformations, for every Rhino unit system.
- Copy and move publishing of already-linked objects, and clean shutdown with unsaved-work handling.

## Future work

1. **Grasshopper extensions.** Automatic source watching, additional query/extent filters,
   service-backed editing qualification and additional export formats follow the 2.0 alpha.
2. **Advanced filters.** Extent, attribute-query and delta filtering, plus geometry-only and
   attribute-only exchange modes.
3. **Change-review detail.** Per-object conflict decisions. Selecting and zooming to a row's object
   is available by double-clicking the row.
4. **Rhino commands.** Optional command front ends over the same core and adapters.
5. **Georeferencing.** Vertical datum transformation, and a command that moves documents synced with
   the older planar placement to per-vertex placement.

These are planned directions, not supported features or delivery commitments.

## Acceptance for changes

Preserve one synchronization path through Core and the adapters. Add meaningful portable tests
for changed rules, then validate affected behavior in ArcGIS Pro with embedded Rhino.
Before a release, run the complete live matrix twice in independent disposable hosts and inspect
the dockpane at 320, 360 and 480 px in light and dark themes, with empty and disabled states,
long names, 10 or more links, warnings, conflicts, failures and keyboard navigation.

See [development](DEVELOPMENT.md) and [production readiness](PRODUCTION_READINESS.md).
