# Install the experimental v1.3.0-preview.1

[Download the complete preview ZIP](https://github.com/nicoazel/Rhino.Inside-ArcGIS-Pro/releases/download/v1.3.0-preview.1/RhinoInside-ArcGIS-v1.3.0-preview.1-win-x64.zip) (about 7 MB), or choose individual assets on the [release page](https://github.com/nicoazel/Rhino.Inside-ArcGIS-Pro/releases/tag/v1.3.0-preview.1).

Complete ZIP SHA-256: `71b86452761a2b9356a033e447387b7acd0eac24b2bf4d18d288d95f59ca3733`.

Experimental installable preview, not a stable or production-certified release.
Windows 11 x64 build 22621+, licensed ArcGIS Pro 3.7, licensed Rhino 8.
Optional MCP gateway requires Node.js 22+. The add-in is unsigned.

## Known issue and acceptance

Use disposable project/data copies. Saving pending Rhino work during ArcGIS Pro shutdown
stalled in native RhinoDoc.WriteFile during the full lifecycle test. That run ended with
312 checks passed and 3 shutdown-related failures. An earlier close/reopen restored its
saved document, links, source bindings, anchor and edited values; this does not certify
all shutdown paths. Visual/keyboard and local MCP approval/denial/save/cancel acceptance
remain pending. No stable installer certification is claimed.

V-Ray for Rhino 6.10 was removed from the validation machine after native unload crashes.
The add-in refuses that exact registered autoloading V-Ray build; this package does not
uninstall V-Ray or alter global plug-in/security settings.

The exact frozen package passed 327 Core tests, 17 offline MCP/release cases, 30 synthetic
fixture hashes, 298 embedded core checks, 21 live MCP read-only/safety cases and all 60
recorded benchmark rows. Pulling 100,000 synthetic streets/parcels took 94.12/99.67 seconds.
Full lifecycle, interactive acceptance and stable publication remain blocked.

## Install for a user

1. Save work and close every ArcGIS Pro process.
2. Extract this whole ZIP to a new folder. Compare asset hashes against SHA256SUMS.txt.
3. Double-click RhinoInside.ArcGISPro-v1.3.0.esriAddinX and choose Install Add-In.
4. Open Pro; check Settings > Add-In Manager, then Rhino.Inside > Launch Rhino.

The internal add-in version is 1.3.0. The GitHub release is explicitly preview.1.
Remove through Pro's Add-In Manager before switching to an older release.

## Install for an agent / PowerShell

Inspect Install-RhinoInsideArcGIS.ps1, then run from the extracted directory:

```powershell
Get-Content .\Install-RhinoInsideArcGIS.ps1
$previewHome = Join-Path $env:LOCALAPPDATA 'RhinoInsideArcGIS\1.3.0-preview.1'
.\Install-RhinoInsideArcGIS.ps1 -PackageDirectory $PWD.Path -DestinationDirectory $previewHome -WithMcp
```

Omit -WithMcp for the add-in alone. -WhatIf previews the action. -DownloadOnly verifies
and copies the local package without registration. The script checks prerequisites,
installer identity/version and package hashes, refuses installation while Pro is running,
and registers per user using Esri's utility. Follow your normal script/add-in policy.
No administrator privileges or developer tools are required for installation.

With MCP installed, launch a disposable project using:

```powershell
& (Join-Path $previewHome 'mcp\Start-RhinoInsideArcGIS.ps1') -Project 'C:\Tests\Disposable.aprx'
```

Use the printed exact-PID mcpServers configuration in your client. Refresh it after every
Pro restart. Configure links locally. GIS/Rhino writes require local review in Pro;
never enable autonomous control as an installation shortcut. ArcGISPro.MCP is optional
and installed separately, targeting the same host PID.

## Provenance

Source: 5dd601f97165ca8a23a3ece609de5fa0091c47dd
Installer SHA-256: f94396760c5081c09c3af36161a279474d60a85bd275f4827fde9850c25e5805
Gateway SHA-256: 5a991c5ce6d4d5abc6d322f987dbf691b8c9d8778f2dede267d979767c71cd0a
The original candidate manifest is included unchanged. No raw logs, crash dumps, machine
settings, project data or licensed host DLLs are included. SHA256SUMS.txt checks files
inside this ZIP; DOWNLOAD-SHA256SUMS.txt on the release also checks the complete ZIP.

https://github.com/nicoazel/Rhino.Inside-ArcGIS-Pro
https://nicoazel.github.io/Rhino.Inside-ArcGIS-Pro/
