# Roadmap

The v1.3.0 release covers the ArcGIS Pro 3.7 add-in with embedded Rhino 8. Future features below
are not release blockers. Rhino 7 support has been removed from the plan.

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

1. **Grasshopper components and preview.** Add geometry and attribute components using the shared
   core, then investigate previewing Grasshopper geometry directly in ArcGIS without first baking
   it. For v1.3.0, bake geometry into a Rhino layer and synchronize that layer.
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
