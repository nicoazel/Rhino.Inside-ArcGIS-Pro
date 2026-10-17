<#
.SYNOPSIS
Downloads, verifies and installs the pinned public release for the current Windows user.
.EXAMPLE
.\Install-RhinoInsideArcGIS.ps1 -WithMcp
.EXAMPLE
.\Install-RhinoInsideArcGIS.ps1 -DownloadOnly -DestinationDirectory C:\Downloads\RhinoInside
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.3.0',
    [switch]$WithMcp,
    [switch]$DownloadOnly,
    [string]$DestinationDirectory = (Join-Path $env:LOCALAPPDATA "RhinoInsideArcGIS\$Version"),
    [string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$repository = 'nicoazel/Rhino.Inside-ArcGIS-Pro'
$installerName = "RhinoInside.ArcGISPro-v$Version.esriAddinX"
$gatewayName = "RhinoInside-Mcp-v$Version.zip"
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (-not $PSCmdlet.ShouldProcess($destination, "Download and verify Rhino.Inside-ArcGIS $Version$(if (-not $DownloadOnly) { ' and install for the current user' })")) { return }
if (-not $DownloadOnly) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Installation requires Windows 11 x64.' }
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 22621) { throw 'Windows 11 x64 build 22621 or newer is required.' }
    if (Get-Process ArcGISPro -ErrorAction SilentlyContinue) { throw 'Save your work and close every ArcGIS Pro process before installation. No processes were closed.' }
    $proRoot = Join-Path $env:ProgramFiles 'ArcGIS\Pro\bin'
    $proExe = Join-Path $proRoot 'ArcGISPro.exe'
    if (-not (Test-Path -LiteralPath $proExe)) { throw 'Install and license ArcGIS Pro 3.7 first.' }
    $proVersion = (Get-Item -LiteralPath $proExe).VersionInfo.FileVersion
    if ($proVersion -notmatch '^3\.7\.') { throw "Supported ArcGIS Pro version is 3.7; found $proVersion." }
    if (-not (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'Rhino 8\System\Rhino.exe'))) { throw 'Install Rhino 8 and activate its license first.' }
    if ($WithMcp) {
        $node = Get-Command node -ErrorAction SilentlyContinue
        if (-not $node -or [int]((& node --version).TrimStart('v').Split('.')[0]) -lt 22) { throw 'Optional MCP control requires Node.js 22 or newer on PATH.' }
    }
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
function Get-ReleaseFile([string]$Name) {
    $target = Join-Path $destination $Name
    if ($PackageDirectory) {
        $source = Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) $Name
        if ($source -ne $target) { Copy-Item -LiteralPath $source -Destination $target }
    } else {
        Invoke-WebRequest -Uri "https://github.com/$repository/releases/download/v$Version/$Name" -OutFile $target -UseBasicParsing
    }
    return $target
}
$manifestPath = Get-ReleaseFile 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.installer -ne $installerName -or $manifest.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
    $manifest.sourceHasUncommittedChanges -ne $false -or $manifest.sourceCommit -notmatch '^[a-fA-F0-9]{40}$' -or
    ($manifest.status -ne 'published' -and -not $PackageDirectory)) { throw 'Release manifest does not identify a clean, published, matching installer.' }
$sumsPath = Get-ReleaseFile 'SHA256SUMS.txt'
function Get-VerifiedFile([string]$Name, [string]$Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid manifest checksum for $Name." }
    $lines = @(Get-Content -LiteralPath $sumsPath | Where-Object { $_ -match ('^[a-fA-F0-9]{64}\s+\*?' + [regex]::Escape($Name) + '$') })
    if ($lines.Count -ne 1 -or $lines[0].Substring(0,64) -ine $Expected) { throw "Checksum list does not match manifest for $Name." }
    $file = Get-ReleaseFile $Name
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $Expected) { throw "Downloaded bytes failed SHA-256 verification: $Name. Installation stopped." }
    return $file
}
$installer = Get-VerifiedFile $installerName $manifest.sha256
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($installer)
try {
    $entry = $archive.GetEntry('Config.daml')
    if (-not $entry) { throw 'Installer is missing Config.daml.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$config = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($config.ArcGIS.AddInInfo.id -ne '{f10f6246-8091-4aad-8b5c-2bcb6b523784}' -or $config.ArcGIS.AddInInfo.version -ne $Version) { throw 'Installer identity/version mismatch.' }
} finally { $archive.Dispose() }
$gatewayDirectory = $null
if ($WithMcp) {
    if ($manifest.mcpGatewayAsset -ne $gatewayName) { throw 'Gateway version mismatch.' }
    $gateway = Get-VerifiedFile $gatewayName $manifest.mcpGatewaySha256
    $gatewayDirectory = Join-Path $destination 'mcp'
    if (-not $DownloadOnly) {
        $zip = [IO.Compression.ZipFile]::OpenRead($gateway)
        try {
            $allowed = @('server.mjs','protocol.mjs','MCP_CONTROL.md','LICENSE','Start-RhinoInsideArcGIS.ps1')
            foreach ($item in $zip.Entries) {
                if ($item.FullName -notin $allowed) { throw "Unexpected gateway entry: $($item.FullName)" }
            }
            New-Item -ItemType Directory -Force -Path $gatewayDirectory | Out-Null
            foreach ($item in $zip.Entries) {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($item, (Join-Path $gatewayDirectory $item.FullName), $true)
            }
        } finally { $zip.Dispose() }
    }
}
$installed = $false
if (-not $DownloadOnly) {
    if (Get-Process ArcGISPro -ErrorAction SilentlyContinue) { throw 'ArcGIS Pro opened during download. Close it and rerun; nothing was registered.' }
    $utility = Join-Path $proRoot 'RegisterAddIn.exe'
    $registration = Start-Process -FilePath $utility -ArgumentList ('"' + $installer + '" /s') -WindowStyle Hidden -PassThru
    if (-not $registration.WaitForExit(30000)) { throw 'Registration timed out. Check Add-In Manager before retrying.' }
    if ($registration.ExitCode -ne 0) { throw "Esri registration failed: exit $($registration.ExitCode)." }
    $installed = $true
}
[ordered]@{
    version = $Version; installed = $installed; installer = $installer; sha256 = $manifest.sha256
    sourceCommit = $manifest.sourceCommit; signed = $false; mcpDirectory = $gatewayDirectory
    nextStep = $(if ($DownloadOnly) { 'Verified download complete.' } elseif ($WithMcp) { 'Run mcp\Start-RhinoInsideArcGIS.ps1 -Project <project.aprx>; use its printed PID and MCP configuration.' } else { 'Open ArcGIS Pro, check Settings > Add-In Manager, then Rhino.Inside > Launch Rhino.' })
} | ConvertTo-Json
