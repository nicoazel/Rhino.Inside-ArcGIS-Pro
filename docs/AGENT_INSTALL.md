# Install with an agent

Use Windows 11 x64 (build 22621+), licensed ArcGIS Pro 3.7 and licensed Rhino 8. Open each application
once to finish licensing. Installing the add-in needs neither Visual Studio nor the .NET SDK.
Optional MCP control needs Node.js 22+; ordinary dockpane use does not.

## Install a pinned release

Save work and close ArcGIS Pro. Download the reviewed script from the **same version tag** as the
installer, inspect it, then run it in PowerShell. Do not pipe downloaded code into execution:

```powershell
Invoke-WebRequest 'https://raw.githubusercontent.com/nicoazel/Rhino.Inside-ArcGIS-Pro/v1.3.0/tools/Install-RhinoInsideArcGIS.ps1' -OutFile .\Install-RhinoInsideArcGIS.ps1
Get-Content .\Install-RhinoInsideArcGIS.ps1
.\Install-RhinoInsideArcGIS.ps1 -WithMcp
```

Omit `-WithMcp` for the add-in alone. Use `-WhatIf` to preview the action or `-DownloadOnly` to fetch
and verify without registration. `-Version` selects a specific stable release. PowerShell policy
may require your organization's normal script approval; the installer does not change that policy.
The add-in is unsigned and does not change ArcGIS add-in security restrictions.

The script checks prerequisites and refuses to install while any Pro process is open. It verifies
the manifest, checksum list, actual installer/gateway bytes, and add-in ID/version before using
Esri's per-user registration utility. It prints JSON with the installed version, source commit,
hash, local paths and next step. No admin privileges or GitHub sign-in are required.

Public downloads use the named GitHub release, never a moving `latest` asset. HTTPS and the project
release are the trust source; hashes detect corruption and mismatch, not publisher impersonation.

## Start and connect

After installation with `-WithMcp`:

```powershell
& "$env:LOCALAPPDATA\RhinoInsideArcGIS\1.3.0\mcp\Start-RhinoInsideArcGIS.ps1" -Project 'C:\Projects\Design.aprx'
```

The launcher enables the endpoint only in the child Pro process and prints an exact-PID
`mcpServers` configuration. Copy its `mcpServers` object into your client's configuration. The
`hostPid` and `note` fields are explanatory output, not client configuration keys. Restart the
client's connection after each Pro restart, using the new PID. Open the Rhino.Inside dockpane,
launch Rhino and author the links/profile locally. `rhino_status`, `rhino_links`, `rhino_profile`
and `rhino_preview` inspect that state. Mutations require local confirmation in Pro; an agent
cannot approve its own write. Preview on an uninitialized document asks for a locally approved Pull.

The [ArcGISPro.MCP](https://github.com/nicoazel/ArcGISPro.MCP) companion is optional for normal use.
Install it separately when an agent also needs general map/layer/geoprocessing tools, and route
both gateways to the same PID. Never enable autonomous mode as an installation shortcut.

See [MCP control](MCP_CONTROL.md) for tools, failure handling and supported limits. Agent installation
does not automatically create links, accept licenses, edit client settings or approve GIS writes.

## Manual installation and removal

Download the versioned `.esriAddinX` from Releases, check `Get-FileHash -Algorithm SHA256` against
`SHA256SUMS.txt`, double-click the package, and choose Install Add-In. Start Pro and check
**Settings > Add-In Manager**, then **Rhino.Inside > Launch Rhino**.
For managed deployment, use Esri's documented [add-in management](https://doc.esri.com/en/arcgis-pro/latest/get-started/manage-add-ins.html).
Remove the add-in through Add-In Manager. The optional gateway is stored under the per-user
`RhinoInsideArcGIS\<version>\mcp` folder; remove its MCP client entry before deleting that folder.
