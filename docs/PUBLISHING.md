# Publishing

## Website

The static site in `site/` contains the overview, installation instructions, user guide and release
information. Build it with Node.js 22 or later:

```powershell
$env:SITE_REPOSITORY = 'nicoazel/RhinoInside-ArcGIS-Pro'
node site/build.mjs
python -m http.server 8765 --bind 127.0.0.1 --directory site/dist
```

`SITE_REPOSITORY` accepts a GitHub `OWNER/NAME` path. GitHub Actions sets it from the repository.
`site/release.json` pins the release repository, version, source commit, installer filename and
SHA-256. Candidate pages state that runtime validation is pending and show no installer or gateway
download. Candidate verification rejects invalid repository paths and any mismatch between the
configured repository and `site/release.json`. All local site URLs are relative so the GitHub Pages
project path works.

The Pages workflow runs on relevant changes to `main` and on manual dispatch. Candidate builds can
publish only the pending status page. Published builds must pass the release verifier before upload.
It resolves the stable version tag to a commit, then checks that commit against the site metadata and
release manifest. The release must contain the installer, versioned MCP gateway, checksums,
`LICENSE`, `ACKNOWLEDGEMENTS.md`, `THIRD_PARTY_NOTICES.md` and sanitized `VALIDATION.md`. It checks
the installer and gateway bytes against their manifest hashes and `SHA256SUMS.txt` entries. The
validator checks public release assets; it does not fetch or certify live ArcGIS Pro or Rhino hosts.

## Installer

Build on Windows with the .NET 10 SDK and the ArcGIS Pro 3.7 SDK build targets. Close ArcGIS Pro;
the build registers the add-in locally. Start from a clean, committed source revision:

```powershell
./tools/package-release.ps1 -OutputDirectory 'C:\scratch\release-v1.3.0'
```

The output directory must not exist. The script runs Core tests, builds Release, audits the ZIP
contents and produces the versioned `.esriAddinX`, `SHA256SUMS.txt`, `release-manifest.json` and
license notices. ArcGIS and Rhino host DLLs are excluded. The installer is unsigned. The release
manifest also identifies the versioned MCP gateway ZIP and its SHA-256.

Run the licensed-host and visual checks in [production readiness](PRODUCTION_READINESS.md) against
that exact package. After the checks complete, update the manifest status to `published` and create
`VALIDATION.md` from real results. This field layout is a contract, not evidence; do not copy PASS
values into a release without completing each check:

```text
# Validation record
- Version: 1.3.0
- Source commit: <40-character Git commit>
- Installer: RhinoInside.ArcGISPro-v1.3.0.esriAddinX
- Installer SHA-256: <64-character installer hash>
- MCP gateway: RhinoInside-Mcp-v1.3.0.zip
- MCP gateway SHA-256: <64-character gateway hash>
- Compile build: PASS
- Core tests: PASS
- Embedded host run 1: PASS (fresh host and clean project)
- Embedded host run 2: PASS (fresh host and clean project)
- MCP coexistence: PASS
- Visual acceptance: PASS
- Package audit: PASS
- Sanitized record: yes
```

The record contains these fields only, with actual results and version identifiers. Keep raw logs,
crash dumps, tokens, local paths and scratch GIS/model data private. A candidate release has no
completed validation record.

## Public source

The public repository starts from a reviewed source snapshot with synthetic GIS fixtures and a new
Git history. The original private history contains external GIS data and must remain private. The
exporter requires a completely clean checkout, archives only `HEAD`, and refuses tracked or
untracked worktree changes. It creates no history. It adds `PUBLIC-SOURCE-MANIFEST.json` with the
source commit and SHA-256 inventory of each archived file.

```powershell
./tools/export-public-source.ps1 -Destination 'C:\scratch\Rhino.Inside-ArcGIS-public'
```

The destination must be a new directory outside the source repository. Review the inventory,
license notices and source snapshot before initializing the separate public repository.

## Release sequence

The reviewed source candidate and pending-status website can be published before licensed-host
acceptance. Keep `site/release.json` set to `candidate`, publish no installer or gateway artifacts,
and run the portable CI and Pages workflows. The following sequence is for a stable installer.

1. Complete the supported-scope acceptance checks and commit the final source.
2. Export the clean committed source and review its provenance inventory.
3. Build and test the versioned installer and MCP gateway from that revision.
4. Complete compile, portable, two clean embedded-host, MCP coexistence, package and visual checks; record the actual results in `VALIDATION.md`.
5. Publish the reviewed source in its clean public repository, then create the matching stable GitHub release with both versioned artifacts, checksums, manifest, notices and validation record.
6. Set `site/release.json` to `published` with the actual `OWNER/NAME`, source commit and installer SHA-256, then commit that metadata.
7. Enable GitHub Pages with Actions. A relevant `main` change triggers a build; the published release assets must verify before deployment.
8. Check the site and artifact downloads without authentication, then submit the upstream project listing.
