<# Export only committed source from a clean Git checkout. No Git history or publish. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or -not $repo) { throw 'Run this exporter from inside a Git checkout.' }
$repo = [IO.Path]::GetFullPath($repo)
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($destinationPath.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $destinationPath.Equals($repo, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be outside the source repository.' }
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination must not already exist.' }
Push-Location $repo
$archivePath = $null
try {
    $status = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Git worktree state.' }
    if ($status.Count -ne 0) { throw "Export requires a clean tree with no tracked or untracked changes: $($status -join '; ')" }
    $commit = (& git rev-parse --verify HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40,64}$') { throw 'Cannot identify committed source revision.' }
    $tree = @(& git -c core.quotepath=false ls-tree -r --full-tree HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory committed source files.' }
    $paths = @()
    foreach ($entry in $tree) {
        if ($entry -notmatch '^(100644|100755)\s+blob\s+[0-9a-f]+\t(.+)$') { throw "Unsupported symlink or submodule entry in source tree: $entry" }
        $relative = $Matches[2]
        if ($relative.StartsWith('/') -or $relative.Contains('\') -or @($relative -split '/' | Where-Object { $_ -in @('', '.', '..') }).Count) { throw "Unsafe source path: $relative" }
        $paths += $relative
    }
    if ($paths -contains 'PUBLIC-SOURCE-MANIFEST.json') { throw 'The generated provenance filename is reserved.' }
    $excluded = @($paths | Where-Object { $_ -match '(^|/)(ArcGISPro[.]MCP|node_modules|bin|obj|artifacts|\.git)(/|$)|(?i)\.(dll|exe|esriAddinX|pfx|pem)$' })
    if ($excluded.Count) { throw "Committed distribution files are forbidden: $($excluded -join ', ')" }

    New-Item -ItemType Directory -Path $destinationPath | Out-Null
    $archivePath = [IO.Path]::GetTempFileName()
    & git archive --format=tar "--output=$archivePath" HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not create an archive of the committed source.' }
    & tar -xf $archivePath -C $destinationPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the committed source archive.' }

    $inventory = @($paths | Sort-Object | ForEach-Object {
        $file = Join-Path $destinationPath $_
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Archived file missing: $_" }
        [ordered]@{ path = $_.Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $provenance = [ordered]@{
        schemaVersion = 1
        sourceCommit = $commit
        sourceRef = 'HEAD'
        exportedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        fileCount = $inventory.Count
        files = $inventory
    }
    $provenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destinationPath 'PUBLIC-SOURCE-MANIFEST.json') -Encoding utf8
    Write-Output "Exported $($inventory.Count) committed files from $commit to $destinationPath"
} finally {
    if ($archivePath -and (Test-Path -LiteralPath $archivePath -PathType Leaf)) { Remove-Item -LiteralPath $archivePath -Force }
    Pop-Location
}
