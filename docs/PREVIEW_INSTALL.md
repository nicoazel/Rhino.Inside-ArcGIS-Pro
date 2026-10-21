# Grasshopper 2.0 alpha installation

The latest experimental release is [v2.0.0-alpha.1](https://github.com/nicoazel/Rhino.Inside-ArcGIS-Pro/releases/tag/v2.0.0-alpha.1).
It supersedes the 1.3 preview for users evaluating Grasshopper integration. It is unsigned and
not stable or production-certified. Windows 11 x64, licensed ArcGIS Pro 3.7, and licensed
Rhino 8.36.26282.15001 or newer are required. Optional MCP requires Node.js 22+.

Download the release ZIP and extract it, or download the versioned `.esriAddinX`,
`MapDesign.ghx`, `GRASSHOPPER-EXAMPLE.md`, and checksums individually. Close Pro and
install the `.esriAddinX` using Esri's installer. Restart Pro, launch Rhino, then click
**Grasshopper Core** as the first Grasshopper launch in the session. Open `MapDesign.ghx`.
See [the example walkthrough](../examples/Grasshopper/README.md).

The `.esriAddinX` contains `RhinoArcGIS.Grasshopper.gha` and the example. Our library is
loaded automatically in the embedded Grasshopper session. Do not install another copy into
Grasshopper Libraries. A separate GHA installer or Yak package is not needed. Rhino/Grasshopper
itself comes from the user's licensed Rhino installation.

For PowerShell, inspect the release's included script, then run from the extracted folder:

```powershell
.\Install-RhinoInsideArcGIS.ps1 -Version 2.0.0-alpha.1 -AllowPrerelease -PackageDirectory $PWD.Path -DestinationDirectory "$env:LOCALAPPDATA\RhinoInsideArcGIS\2.0.0-alpha.1"
```

Add `-WithMcp` only for optional MCP control; GIS writes still require local review in Pro.
Use `-DownloadOnly` to verify and copy the files without registration. The installer checks
package identity, numeric ArcGIS version 2.0.0, hashes, bundled GHA/example, prerequisites,
and that Pro is closed. A newer Rhino installation is required for McNeel's .NET 10 fix.

Known limits: native project-save prompts can block unattended shutdown. Direct slider and
3D scene appearance checks and the complete repeatable lifecycle matrix remain pending.
A copied-host loading check is not install/upgrade/uninstall certification. Use disposable
project and data copies; save explicitly and respond to any close prompts.

The earlier [1.3.0-preview.1](https://github.com/nicoazel/Rhino.Inside-ArcGIS-Pro/releases/tag/v1.3.0-preview.1)
remains available for comparison. It does not include this Grasshopper component library.
