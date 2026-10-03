# Rhino.Inside-ArcGIS

Rhino.Inside-ArcGIS hosts Rhino 8 inside ArcGIS Pro 3.7 and keeps ArcGIS feature layers paired with
Rhino layers. It supports preview-before-write synchronization for Point Z, Polyline Z, Polygon Z,
multipart polygons and Multipatch geometry, including attributes and durable GlobalID identity.

**Source candidate:** portable checks and compilation do not certify the installed add-in. The
two-host runtime matrix, MCP coexistence and visual acceptance remain pending. See
[release status](docs/RELEASE_STATUS.md) for the current boundary.

The [public website](https://nicoazel.github.io/Rhino.Inside-ArcGIS-Pro/) is generated from [`site/`](site/). See the
[installation and release preparation](docs/PUBLISHING.md) and
[acknowledgements](ACKNOWLEDGEMENTS.md). This repository is licensed under [MIT](LICENSE).

## Supported feature set

The source candidate also includes optional [embedded Rhino MCP control](docs/MCP_CONTROL.md):
status, saved links, profiles and preview, plus locally reviewed launch, pull, apply and save.
It runs alongside the ArcGISPro.MCP gateway with explicit host-PID selection. Its embedded-host
acceptance is pending; public installer certification must include both projects' coexistence.

- Pull ArcGIS features into editable, tracked Rhino geometry.
- Preview object, geometry and field changes without writing either model.
- Apply two-way, pull-only or push-only links with explicit conflict policy.
- Review and run many saved links; link rows and custom profiles travel with the `.3dm`.
- Create a new Z-aware Point, Polyline, Polygon or Multipatch feature class directly from a Rhino
  layer, infer its user-text fields, add GlobalIDs, link it and perform the initial push.
- Author a per-link profile: include/exclude fields, map each ArcGIS field to a Rhino user-text key,
  and choose Rhino-owned, ArcGIS-owned, Shared, local-only, Derived or Locked ownership. ArcGIS
  identity and `Shape_*` fields stay protected.
- Place geometry exactly in any layer's coordinate system: every vertex goes through ArcGIS's own
  geodesy and datum transformation from Rhino's earth anchor, so State Plane, UTM, Web Mercator and
  geographic layers on NAD83, NAD83(2011) or WGS84 can use the installed datum transformations.
  Inspect warnings when ArcGIS cannot supply a required transformation; that case does not carry
  a sub-millimetre accuracy guarantee. Named and custom Rhino units are supported; documents
  without units are refused.
- Hold up in real back-and-forth sessions: copies, deletions and renumbered shapefile FIDs are told
  apart, repeated pulls are idempotent, links re-bind across maps, scenes and restarts, and closing
  ArcGIS Pro offers to save the Rhino document instead of leaving recovery files.
- Publish objects already linked to one layer into another as a **copy** (the original link stays)
  or a **move** (the link transfers).
- Double-click a preview row to select and zoom to that object in Rhino and ArcGIS.
- Use contextual info buttons and consequence-oriented tooltips throughout the compact dockpane.

Extent, query, delta, geometry-only and attribute-only advanced filters are not part of this release.
Selection-only pull is supported.

## Typical workflow

1. Open the Rhino.Inside dockpane and launch the embedded Rhino host.
2. In **Link**, pair an ArcGIS layer with a Rhino layer, or create a new ArcGIS layer from an
   existing Rhino layer.
3. In **Profile**, review field mappings and ownership, then save the profile.
4. Use **Pull** once to establish tracked Rhino objects for a GIS-first link.
5. Use **Preview** for every later exchange. Review changes and conflicts, then use **Apply**.

## Georeferencing

Rhino's earth anchor is a WGS84 latitude and longitude. If a document has no anchor, the first pull
or push anchors it at the map centre. Each link is then placed in its own layer's coordinate system,
vertex by vertex, using ArcGIS's geodesic and datum-transformation tools, so a Rhino metre is a
metre of ground in every projection. Rhino documents synced by earlier versions keep their original
planar placement, so nothing already linked moves. Heights pass through unchanged; vertical datums
are not transformed.

The global “Let Rhino edit attributes” option is only the fallback for links without a custom
profile. A saved profile is the authoritative per-link rule set. Preview is always read-only;
Apply writes only what the link direction, field ownership and conflict policy allow.

## Multipatch creation

Planar regions remain Polygon. Rhino meshes plus three-dimensional or nonplanar Breps, surfaces and
extrusions are converted to a neutral mesh payload and infer a Multipatch target. The majority
supported geometry type on the source Rhino layer selects the new feature-class shape; unsupported
objects are skipped and reported. The class is created in the
ArcGIS project's default file geodatabase with Z enabled and GlobalIDs, then added to the active map.

Multipatch exchange is mesh-based and therefore intentionally lossy for NURBS parametrization,
materials and analytic surfaces. The geometry, Z extent and shared mesh topology are covered by
the live matrix; that matrix must be rerun against this candidate's exact package before a stable
installer is published.

## Build and validation

Requirements: ArcGIS Pro 3.7, a licensed Rhino 8 installation, and the .NET 10 SDK.

```powershell
dotnet test tests/RhinoArcGIS.Core.Tests/RhinoArcGIS.Core.Tests.csproj -c Release
node --test tests/site-release.test.mjs tests/mcp/protocol.test.mjs
node tools/verify-fixtures.mjs
dotnet build src/RhinoInside.ArcGISPro.AddIn.sln -c Release -p:SkipAddinPackaging=true
```

This build only compiles source. Ordinary builds package and register the add-in locally; wait
until ArcGIS Pro is closed before packaging. The separate live acceptance command is
`.\tools\e2e.ps1 -Project 'C:\scratch\Review.aprx'` and requires disposable hosts and data.

`tools/e2e.ps1` is the production-path acceptance matrix. It launches a real ArcGIS Pro process,
hosts Rhino in that process and exercises the dockpane/coordinator path. Its disposable
geodatabases cover GlobalID matching, Rhino-first Polyline and Multipatch creation, saved profile
ownership, geometry replacement, attribute conflicts, bulk links and document persistence. A
standalone Rhino or unit-test pass is not evidence of embedded-host compatibility.

See [docs/PROFILE_AUTHORING.md](docs/PROFILE_AUTHORING.md) for the field-ownership guide,
[docs/PRODUCTION_READINESS.md](docs/PRODUCTION_READINESS.md) for the supported release boundary and
acceptance evidence,
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for build and test instructions, and
[docs/ROADMAP.md](docs/ROADMAP.md) for future work.
