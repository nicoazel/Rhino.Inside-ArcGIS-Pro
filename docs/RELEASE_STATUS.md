# Public source candidate

## 2026-10-17 current validation

Stable installer publication remains blocked. The public source, About page, user/agent guides,
checked PowerShell installer, optional launcher, `install.json` and `llms.txt` are available.
Candidate metadata advertises no installer or gateway download URL.

The frozen diagnostic package uses source `5dd601f97165ca8a23a3ece609de5fa0091c47dd` and installer SHA-256
`f94396760c5081c09c3af36161a279474d60a85bd275f4827fde9850c25e5805`. Its completed checks are:

- 327 Core tests, 17 offline MCP/release tests and 30 synthetic fixture hashes passed.
- Release package build: 0 warnings and 0 errors; package contents audited.
- Supported-geometry/core matrix: 298 checks passed, 0 failed. **The close/reopen phase was explicitly excluded.**
- All four loaded add-in DLL hashes matched the frozen installer.
- 21 live MCP read-only/safety cases passed across six geometry families, including real saved source identities, no-mutation Preview, wrong-source/PID rejection and concurrent clients with autonomous control disabled.
- Both gateways reached the same PID during real synchronization; Rhino busy state was observed. A separately disabled endpoint did not fall back to the enabled host.
- All 60 live scale-benchmark rows passed independent count/change checks with no skips, errors or parallel mismatches. See [measured performance](PERFORMANCE.md).
- The installer refused a real installation attempt while Pro was running, before creating its destination; the host remained running. WhatIf remains an allowed no-write preview.

V-Ray for Rhino 6.10 was removed from the validation machine, including six orphaned Rhino 7/8
registrations backed up privately. The startup guard still refuses this exact known conflicting
build when it is registered to autoload. New and subsequent saves use noninteractive `WriteFile`.
The established default Rhino profile was restored after the experimental isolated profile stalled
WPF dispatch. The fixture helper now correctly skips elevation writes in 2D maps.

After explicit SDK project/edit saves, the benchmark host closed normally with exit code 0 in
eight seconds, with no matching crash event or forced termination. A fresh full lifecycle attempt
then closed normally with exit code 0 in 113 seconds and restored the document, seven links,
source bindings, earth anchor, identical previews and both sides' edited values in its next process.
Both processes' four loaded add-in assemblies matched the frozen installer.

That attempt **failed** its next save-on-close check: pending Rhino work entered native
`RhinoDoc.WriteFile` inside the application-closing handler and did not complete within 120 seconds.
A private managed stack confirmed that call. The attempt ended with 312 checks passed and 3 failed (save-on-close, scenario completion and final clean shutdown). This is a remaining release blocker; V-Ray removal
and the earlier successful saves do not establish a complete lifecycle. The second independent
matrix has not been run while this reproducible gate is unresolved.

Actual UI/keyboard and local MCP review/denial/save/cancel checks remain pending; interactive control
tooling currently fails before initialization. Recovery-dialog absence was not visually verified.
Thirty programmatic component renders were generated but **not accepted**: the capture includes
its parent offset, and setting the theme flag did not load dark resources in that running process.
They do not establish visual acceptance.

Two complete exact-package clean embedded-host lifecycle runs and full interactive MCP/UI acceptance
are still required before publishing installer or gateway assets. See
[save/shutdown diagnostics](SAVE_SHUTDOWN_DIAGNOSTICS.md) and [release requirements](PRODUCTION_READINESS.md).

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
