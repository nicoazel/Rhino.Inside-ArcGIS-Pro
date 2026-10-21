# Rhino.Inside-ArcGIS

Rhino.Inside-ArcGIS hosts Rhino 8 inside ArcGIS Pro 3.7 and keeps ArcGIS feature layers paired with
Rhino layers. It supports preview-before-write synchronization for Point Z, Polyline Z, Polygon Z,
multipart polygons and Multipatch geometry, including attributes and durable GlobalID identity.

**Latest experimental download: [v2.0.0-alpha.1](https://github.com/nicoazel/Rhino.Inside-ArcGIS-Pro/releases/tag/v2.0.0-alpha.1)** includes the Grasshopper library and
[runnable MapDesign.ghx example](examples/Grasshopper/MapDesign.ghx). The `.esriAddinX` installs
our Grasshopper components automatically; no separate GHA installer or Yak package is required.
Rhino itself must already be installed and licensed. This is an alpha with known shutdown issues;
stable certification and full interactive acceptance remain pending.

The [public website](https://nicoazel.github.io/Rhino.Inside-ArcGIS-Pro/) is generated from [`site/`](site/). See the
[installation and release preparation](docs/PUBLISHING.md) and
[acknowledgements](ACKNOWLEDGEMENTS.md). This repository is licensed under [MIT](LICENSE).

## Install

Start at the [installation page](https://nicoazel.github.io/Rhino.Inside-ArcGIS-Pro/install.html).
Users can download the versioned `.esriAddinX` and double-click it with Pro closed. Agents can
use the [pinned PowerShell installer](docs/AGENT_INSTALL.md), which verifies the package and prints
JSON; `-WithMcp` adds the optional gateway and a launcher that prints an exact-PID client config.
The download panel shows whether a stable installer is available. Source prereleases are not installers.

Requirements for the 2.0 alpha: Windows 11 x64, licensed ArcGIS Pro 3.7 and Rhino 8.36.26282.15001 or newer. No SDK is needed to install.
Node.js 22+ is needed only for MCP control. Read [About](https://nicoazel.github.io/Rhino.Inside-ArcGIS-Pro/about.html)
for the project's origins and acknowledgements.

## Supported feature set

The source candidate also includes optional [embedded Rhino MCP control](docs/MCP_CONTROL.md):
status, saved links, profiles and preview, plus locally reviewed launch, pull, apply and save.
It uses explicit host-PID selection. Public installer certification must include coexistence with
the separately installed ArcGISPro.MCP gateway.

### 2.0 development wave: Grasshopper

The **2.0.0-alpha.1** experimental release provides an
embedded Grasshopper component library. It is a development build; release acceptance is tracked
in [the roadmap](docs/ROADMAP.md#grasshopper-20-development-wave).

Launch Rhino, then click **Grasshopper** on the Rhino.Inside ribbon. The **ArcGIS** component tab
provides Map, Map Layers, Layer Info, Read Features, Feature Geometry, Feature Attribute, Build
Feature, Set Feature Geometry, Set Attribute, Field, Map Preview, Plan Updates, Commit Updates,
Create Layer and Append Features.

Grasshopper in ArcGIS Pro's .NET 10 process requires **Rhino 8.36.26282.15001 or newer**, including
McNeel's RH-99401 IronPython fix. Older builds can display a blocking `scriptcontext` initialization
error even when the Python module is installed. The Grasshopper launcher checks this before loading
the plug-in. See [McNeel's report](https://discourse.mcneel.com/t/rir-gh-throws-failed-loading-the-scriptcontext-module-error/222948)
and [hotfix confirmation](https://discourse.mcneel.com/t/scripcontext-module-error-in-latest-rhino-update-everything-is-broken/223554).

If older installed Grasshopper add-ons fail under the ArcGIS runtime, use **Grasshopper Core**
as the first Grasshopper launch in a fresh Pro session. This process loads Rhino's bundled
components plus the ArcGIS library. It does not move add-on files or change the saved library
folder list. This opt-in mode uses Rhino's internal hosted-library selector with an exact-signature
check; an unsupported future Grasshopper build reports that the selector is unavailable.

Use **Map → Map Layers → Read Features** to get detached snapshots without baking. Read Features
defaults to selected features and a 1,000-feature limit; its Truncated output reports incomplete
reads. Use the component menu's **Refresh from ArcGIS** after selection or data changes. Feature
Geometry returns one tree branch per feature, including multipart pieces; keep the corresponding
feature snapshot when filtering or reordering geometry.

For multipart edits, wire Feature Geometry's **Matching Features** tree and transformed geometry
branches into Set Feature Geometry. Each geometry branch is reassembled as one feature. Flatten
the edited feature output when preparing one combined update plan.

For live design, connect model geometry to **Map Preview** and choose the output map or scene.
Preview uses absolute elevations and temporary graphics. Color includes alpha; Width controls
line weight. Disable preview or remove the component to clear its overlays. No feature class is
created. Mesh appearance in scenes remains a live visual acceptance gate.

For existing data, use **Set Attribute** and/or **Set Feature Geometry**, connect the edited
snapshots to **Plan Updates**, review the report, then connect its plan to **Commit Updates** and
choose **Commit reviewed updates** from the component menu. The plan is frozen; commit rechecks
the dataset and read-time baselines. Protected fields, duplicate targets and stale edits block
the whole operation. Geometry-only and attribute-only changes preserve the other values.

For new data, define fields with **Field**, select PointZ, PolylineZ, PolygonZ or Multipatch in
**Create Layer**, then choose **Create layer once** from its menu. The default destination is the
project file geodatabase; an existing file geodatabase can be supplied. Save or discard pending
GIS edits before schema creation. Use **Build Feature** with explicit unique keys, set any design
attributes, and connect these drafts and the created layer to **Append Features**. Choose
**Publish keyed features** to publish. Later explicit actions update the same keyed output set;
recomputing or moving a slider never writes. Create and Append retain receipts in the definition.
To publish a different key set, use a new Append component. Reconnect the original layer after
reopening a project; changed or deleted GIS outputs require a fresh read and review.

This wave targets feature layers in the embedded Rhino 8 / ArcGIS Pro 3.7 host. Other layer types
are discoverable with their capabilities. Automatic source watching, feature services, arbitrary
file-format export, deletion, vertical datum transformations, and material-faithful rendering
are outside this alpha's supported boundary.

Open [the runnable example](examples/Grasshopper/MapDesign.ghx) after launching Grasshopper from
the ribbon. Its top branch reads the first listed layer's selected features. The lower branch
previews a synthetic massing box and includes explicit Create/Publish components. The lift slider
moves the box in model units; publishing still requires the component menu action. The example
was authored in metres; use a metre-based Rhino document or adapt its geometry to your units.

### Existing synchronization

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
