<# Creates an unsigned, versioned local release candidate. Does not publish or tag anything. #>
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Get-Process -Name ArcGISPro -ErrorAction SilentlyContinue) {
    throw 'Close ArcGIS Pro before packaging; the Esri build registers the add-in locally. Use -p:SkipAddinPackaging=true for compile-only validation instead.'
}
# SDK compile globs can pick up ignored local integration code even when Git is clean.
$trackedSource = @(& git -C $repo -c core.quotepath=false ls-files src)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read tracked source files.' }
$sourceRoot = Join-Path $repo 'src'
$extraSource = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
    $_.Extension -in @('.cs', '.xaml') -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
} | Where-Object {
    $relative = $_.FullName.Substring($repo.Length + 1).Replace('\', '/')
    $trackedSource -notcontains $relative
})
if ($extraSource.Count) { throw "Untracked or ignored compile inputs: $($extraSource.FullName -join ', ')" }
if (& git -C $repo status --porcelain) { throw 'Commit the release source before packaging.' }
$release = Get-Content (Join-Path $repo 'site/release.json') -Raw | ConvertFrom-Json
$version = [string]$release.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
[xml]$daml = Get-Content (Join-Path $repo 'src/RhinoInside.ArcGISPro/Config.daml')
if ($daml.ArcGIS.AddInInfo.version -ne $version) { throw 'Config.daml and site/release.json versions differ.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts/release-v$version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; existing release artifacts are never overwritten.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$buildRoot = Join-Path $OutputDirectory 'build/'
Push-Location $repo
try {
    & dotnet test tests/RhinoArcGIS.Core.Tests/RhinoArcGIS.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & node tools/verify-fixtures.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture verification failed.' }
    $nodeTests = @((Join-Path $repo 'tests/site-release.test.mjs')) + @(Get-ChildItem -LiteralPath (Join-Path $repo 'tests/mcp') -Filter *.test.mjs -File | ForEach-Object { $_.FullName })
    & node --test @nodeTests
    if ($LASTEXITCODE -ne 0) { throw 'Offline MCP protocol tests failed.' }
    & dotnet build src/RhinoInside.ArcGISPro.AddIn.sln -c Release --no-incremental "-p:BaseOutputPath=$buildRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $packages = @(Get-ChildItem -LiteralPath $buildRoot -Filter *.esriAddinX -Recurse -File)
    if ($packages.Count -ne 1) { throw "Expected one installer, found $($packages.Count)." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        $unexpected = @($entries | Where-Object {
            $_ -match '(?i)(^|/)(ArcGIS[^/]*|ESRI\.[^/]*|RhinoCommon|Grasshopper)\.(dll|exe)$|ArcGISPro[.]MCP|\.pfx$|\.pem$'
        })
        if ($unexpected.Count) { throw "Forbidden package entries: $($unexpected -join ', ')" }
        foreach ($required in @('Config.daml','Install/RhinoInside.ArcGISPro.dll','Install/RhinoArcGIS.Core.dll',
                               'Install/RhinoArcGIS.ArcGIS.dll','Install/RhinoArcGIS.Rhino.dll',
                               'Install/LICENSE','Install/THIRD_PARTY_NOTICES.md')) {
            if ($entries -notcontains $required) { throw "Missing package entry: $required" }
        }
        $reader = [IO.StreamReader]::new($archive.GetEntry('Config.daml').Open())
        try { [xml]$packagedDaml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($packagedDaml.ArcGIS.AddInInfo.version -ne $version) { throw 'Packaged version mismatch.' }
    } finally { $archive.Dispose() }
    $asset = "RhinoInside.ArcGISPro-v$version.esriAddinX"
    Copy-Item -LiteralPath $packages[0].FullName -Destination (Join-Path $OutputDirectory $asset)
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
    $gatewayRoot = Join-Path $OutputDirectory 'mcp-gateway'
    New-Item -ItemType Directory -Path $gatewayRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'tools/mcp/server.mjs'),(Join-Path $repo 'tools/mcp/protocol.mjs'),(Join-Path $repo 'docs/MCP_CONTROL.md'),(Join-Path $repo 'LICENSE') -Destination $gatewayRoot
    $gatewayAsset = "RhinoInside-Mcp-v$version.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($gatewayRoot, (Join-Path $OutputDirectory $gatewayAsset))
    $gatewayHash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $gatewayAsset) -Algorithm SHA256).Hash.ToLowerInvariant()
    @("$hash  $asset", "$gatewayHash  $gatewayAsset") | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'ACKNOWLEDGEMENTS.md'),(Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $OutputDirectory
    $manifest = [ordered]@{
        version = $version
        status = 'candidate'
        sourceCommit = (& git rev-parse HEAD).Trim()
        sourceHasUncommittedChanges = [bool](& git status --porcelain)
        installer = $asset
        sha256 = $hash
        mcpGatewayAsset = $gatewayAsset
        mcpGatewaySha256 = $gatewayHash
        signed = $false
        packagedEntries = $entries
        embeddedHostValidation = 'See the separately recorded E2E evidence; this script does not run ArcGIS Pro.'
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-manifest.json') -Encoding utf8
    Write-Output "Candidate: $(Join-Path $OutputDirectory $asset)"
    Write-Output "SHA-256: $hash"
} finally { Pop-Location }
