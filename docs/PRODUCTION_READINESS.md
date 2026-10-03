# Production readiness — v1.3.0

## Supported scope

ArcGIS Pro 3.7 with embedded Rhino 8 on Windows 11 x64. The release includes the geometry,
layer-linking, profile and synchronization features listed in the [README](../README.md).
Grasshopper geometry can be baked into a Rhino layer for exchange. Dedicated Grasshopper
components, direct Grasshopper preview and the other [roadmap](ROADMAP.md) items are future work.

## Required release checks

| Check | Acceptance |
|---|---|
| Portable contracts | All Core tests pass with no skips. |
| Clean-source build | Release build succeeds from a clean commit with no warnings or errors. |
| Installer boundary | Version matches the release; license notices included; no ArcGIS or Rhino host DLLs or private integration code. |
| Embedded-host matrix | Full matrix passes twice against the exact package in fresh hosts with independent scratch data. |
| Rendered Profile view | The live matrix constructs the dockpane and selects Profile, covering the read-only WPF binding. |
| Visual acceptance | Inspect 320, 360 and 480 px layouts in light/dark themes, including long names, 10+ links, empty/disabled states, warnings, conflicts, failures and keyboard navigation. |
| Public source | Synthetic fixtures only; original development history and local artifacts stay private. |
| Website | Four pages build; relative links and anchors resolve; public installer download matches its checksum. |
| MCP control | Explicit PID routing, saved source identity, read-only preview, local review/denial, busy state, save/cancel, shutdown/reopen and companion coexistence pass against both frozen packages. |

Historical desktop test versions: ArcGIS Pro 3.7.1.1904 and embedded Rhino 8.34.26223.11001.
Compile references remain pinned to RhinoCommon/Grasshopper 8.23.25251.13001.

Each published release must include a sanitized validation record tied to the source revision and
the exact installer's SHA-256. The release record reports the completed checks; this document
defines their requirements. Historical logs and scratch projects are retained privately.

## Operational limits

- Multipatch conversion is mesh-based and does not preserve NURBS parameters, analytic surfaces
  or materials.
- Simple planar FirstRing multipatch faces support concavity. Multi-ring groups, including holes,
  are rejected; tessellate them to triangle patches in ArcGIS before exchange.
- Empty or unsupported Rhino layers cannot infer a new GIS feature-class type.
- Detected deletions are held; they are not automatically mirrored.
- Manual conflict policy holds conflicts. Per-object decisions are not available in this release.
- Heights pass through unchanged; vertical datums (NAVD88, ellipsoidal heights) are not transformed.
- ArcGIS must provide an appropriate datum transformation. Missing transformations produce a
  warning and may leave a metre-scale datum shift; inspect that warning before Apply.
- Rhino documents synced by earlier versions keep planar placement, and there is no command to move
  them to per-vertex placement. On that path, US survey feet are read as international feet (2 ppm).
- Rhino documents set to no units cannot be georeferenced and are refused.
- The installer is unsigned. It requires an ArcGIS Pro configuration that permits unsigned add-ins.

See [development](DEVELOPMENT.md) for test commands and [publishing](PUBLISHING.md) for packaging.
