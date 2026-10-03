# Public source candidate

This tree starts with fresh Git history and reproducibly generated synthetic shapefiles. It has no
parent commits, old GIS objects, private repository tags or copied release binaries. The original
development repository remains private and separate.

This preparation fixes saved-link target resolution, rejected field baselines and read-only Preview,
adds optional typed embedded Rhino MCP control, and hardens public export and Pages release checks.
The public site can show this source candidate with no installer download; a stable installer requires
the complete sanitized release record defined in [publishing](PUBLISHING.md).

The current optional MCP gateway runs alongside ArcGISPro.MCP with explicit host-PID routing.
It does not register its tools in the companion registry. See [MCP control](MCP_CONTROL.md) for
the tool boundary and local-review policy. Dedicated Grasshopper components, advanced sync filters,
new-link/profile authoring through MCP and vertical datum transformation remain outside this candidate.

Portable test, offline protocol, source compilation, dependency metadata and secrets checks are
prepublication checks. They do not establish installed-package compatibility. The exact-package
matrix in two independent clean hosts, UI/keyboard inspection, MCP local dialogs, companion
coexistence and shutdown/reopen acceptance are pending. Live tests were intentionally deferred while
another agent was working in ArcGIS Pro.

Checks completed for this source candidate on 2026-10-03:

- Core: 307 passed, 0 failed, 0 skipped.
- Offline MCP and release contracts: 16 passed, including a synthetic Windows pipe transport.
- Compile-only Release build: 0 warnings, 0 errors, with Esri packaging/registration disabled.
- Fixtures: 30 manifest hashes verified; repeated generation produced identical bytes.
- Website: four pages built and local links/anchors checked; candidate has no download links.
- Source secrets scan: no findings. Dependency audit: no known vulnerable packages from the configured sources.

These are source preparation results, not an installed-package validation record. The optional
MCP shutdown behavior also needs a multi-add-in check when another module vetoes unload after the
closing event; see [MCP control](MCP_CONTROL.md).

The new GitHub destination is configured in `site/release.json`. Changing that value or
`SITE_REPOSITORY` updates generated site links; both must agree for deployment. Do not change the
original private repository's visibility or push its history into this destination.
