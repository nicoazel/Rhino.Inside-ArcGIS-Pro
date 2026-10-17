# Public source candidate

## 2026-10-17 save/shutdown investigation

Stable installer publication is blocked. Windows recorded repeated ArcGIS Pro access violations
(`0xc0000005`) naming `VRayForRhino.rhp_unloaded` version 6.10.0.0 after saving and closing an
embedded Rhino session. A process disappearing is insufficient evidence of a clean shutdown;
the harness now retains the process handle and requires exit code 0 with no matching crash event.
Earlier October 17 runs that counted a crashed exit as clean do not establish release acceptance.
A narrow startup check now refuses to load embedded Rhino when this exact V-Ray build is
configured to autoload. It reads settings only and explains the required compatibility correction.

New-file saves now use `RhinoDoc.WriteFile` with dialogs/input/preview-image rendering suppressed
and `UpdateDocumentPath` enabled. A focused live probe successfully saved a new file and three
modified versions. This addresses an interactive Save As stall; it does not establish that the
native shutdown crash is fixed. Embedded Rhino uses a separate settings scheme, but V-Ray still
autoloaded under that scheme. Two focused safe-mode hosts completed saving and exited with code 0;
one also reopened the saved four-object document. Normal-mode document creation/reopen probes
stalled. Diagnostic safe mode is not a supported replacement for normal-mode acceptance.
See [save/shutdown diagnostics](SAVE_SHUTDOWN_DIAGNOSTICS.md).

The About page, user installation guide, checked PowerShell installer, optional MCP launcher,
`install.json` and `llms.txt` are prepared. Candidate metadata advertises no installer URL.
The stable installer and gateway remain unpublished until two exact-package normal-mode lifecycle
runs, companion coexistence, and full visual/keyboard/local-review acceptance pass.

Portable checks for the diagnostic candidate: 327 Core tests, 17 offline MCP/release tests,
30 synthetic fixture hashes, and a Release package build with 0 warnings and 0 errors.
These checks do not certify embedded-host lifecycle behavior.

## Earlier source preparation

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

RC1 passed an isolated live six-fixture matrix (284 checks), actual stdio MCP control with local
review dialogs (23 checks), and persistence in a second process (6 checks); both owned hosts closed
cleanly. RC2 hardens review-to-write document binding, batch document guards, project switching,
queued shutdown cancellation, successful source adoption and concave multipatch ring tessellation.
Portable tests and compilation do not establish exact installer compatibility. Two full matrices
against the final installer, companion coexistence and the full visual/keyboard matrix remain pending.

Checks completed for this source candidate on 2026-10-03:

- Core: 327 passed, 0 failed, 0 skipped.
- Offline MCP and release contracts: 17 passed, including a synthetic Windows pipe transport.
- Compile-only Release build: 0 warnings, 0 errors, with Esri packaging/registration disabled.
- Fixtures: 30 manifest hashes verified; repeated generation produced identical bytes.
- Website: four pages built and local links/anchors checked; candidate has no download links.
- Source secrets scan: no findings. Dependency audit: no known vulnerable packages from the configured sources.

Release attachments record the exact source revision, loaded DLL hashes and live evidence for each
candidate. The optional MCP shutdown behavior also needs a multi-add-in check when another module
vetoes unload after the closing event; see [MCP control](MCP_CONTROL.md).

The new GitHub destination is configured in `site/release.json`. Changing that value or
`SITE_REPOSITORY` updates generated site links; both must agree for deployment. Do not change the
original private repository's visibility or push its history into this destination.
