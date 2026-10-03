<#
.SYNOPSIS
Scale benchmark: how long pull, preview and apply take on large street and parcel layers inside a
real ArcGIS Pro with Rhino hosted in-process, and where the time goes.

.DESCRIPTION
Drives the add-in through the test bridge (tools/bridge.ps1), like tools/e2e.ps1, over synthetic
data from tools/generate-benchmark-data.py. For every dataset and size it runs, on a fresh Rhino
document:

  pull            every feature into Rhino
  preview.clean   the plan straight after the pull (must be 0 changed)
  apply.noop      an apply with nothing to do (re-baselines every object)
  preview.move    after moving EditPercent of the objects in Rhino
  apply.move      pushes those geometry edits
  apply.attr      after setting a user-text field on EditPercent of the objects in Rhino
  apply.gis       after an ArcGIS field edit on EditPercent of the features (pulled into Rhino)
  apply.gismove   after moving EditPercent of the features in ArcGIS (geometry pulled into Rhino)
  preview.final   the plan after all of that (must be 0 changed)

and records the wall-clock time, the phase breakdown the services report (read.rhino,
read.arcgis, plan, push.*, pull.*, baseline.write, ...) and Pro's memory. Results go to
<Scratch>\benchmark.json and benchmark.csv.

Sizes run smallest first. Before each step the time it would take is extrapolated from the
previous size (assuming the growth rate seen between the last two sizes); a step predicted to
exceed -StepBudgetMin is skipped and recorded as such, so a quadratic path cannot park the run
for hours. Steps that follow a skipped pull are skipped too.

.EXAMPLE
  .\tools\benchmark.ps1 -Project C:\scratch\host\Review.aprx -Scratch C:\scratch\bench-1
  .\tools\benchmark.ps1 -SkipStart -Sizes 1000,10000 -Datasets Streets
#>
[CmdletBinding()]
param(
    [string]$Project = $env:RHINOINSIDE_E2E_PROJECT,
    [int[]]$Sizes = @(1000, 10000, 100000),
    [ValidateSet('Streets', 'Parcels')][string[]]$Datasets = @('Streets', 'Parcels'),
    [double]$EditPercent = 1.0,
    [double]$StepBudgetMin = 45,
    [string]$Data,
    [string]$Scratch,
    [switch]$SkipStart,
    [switch]$LeavePro
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'bridge.ps1')

if (-not $Scratch) { $Scratch = Join-Path $env:TEMP ("rhinoinside-bench-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
try { Start-Transcript -Path (Join-Path $Scratch 'benchmark.log') -Force | Out-Null } catch { }

# ---------------------------------------------------------------- data

if (-not $Data) { $Data = Join-Path $Scratch 'data' }
$missing = @(foreach ($d in $Datasets) { foreach ($n in $Sizes) {
    if (-not (Test-Path (Join-Path $Data "Bench_${d}_$n.shp"))) { $n } } }) | Select-Object -Unique
if ($missing.Count) {
    $python = Join-Path $repo 'artifacts\fixture-venv\Scripts\python.exe'
    if (-not (Test-Path $python)) { $python = 'python' }
    & $python (Join-Path $PSScriptRoot 'generate-benchmark-data.py') $Data @missing
    if ($LASTEXITCODE) { throw 'generate-benchmark-data.py failed' }
}
# Pushes edit the shapefiles; every run works on its own copy.
$work = Join-Path $Scratch 'work'
New-Item -ItemType Directory -Force -Path $work | Out-Null

# ---------------------------------------------------------------- Pro

if (-not $SkipStart) {
    if (-not $Project -or -not (Test-Path -LiteralPath $Project -PathType Leaf)) {
        throw 'Pass -Project with a disposable ArcGIS Pro .aprx containing a Map view, or set RHINOINSIDE_E2E_PROJECT.'
    }
    $projectCopy = Join-Path $Scratch 'project'
    New-Item -ItemType Directory -Force -Path $projectCopy | Out-Null
    $copied = Join-Path $projectCopy (Split-Path -Leaf $Project)
    Copy-Item -LiteralPath $Project -Destination $copied -Force
    $Project = $copied
    Start-Pro -Project $Project
    Start-Sleep -Seconds 8
}
else { Initialize-Bridge | Out-Null }

$mapView = $null
for ($attempt = 0; $attempt -lt 24; $attempt++) {
    try { $mapView = Send-Bridge ensuremapview @{ map = 'Map' } } catch { $mapView = $null }
    if ($mapView -and $mapView.active) { break }
    Dismiss-Dialogs | Out-Null
    Start-Sleep -Seconds 5
}
if (-not ($mapView -and $mapView.active)) { throw "ArcGIS Pro did not activate the 'Map' view." }
if (-not (Send-Bridge status).rhinoStarted) { Send-Bridge launch -TimeoutSec 600 | Out-Null }

# ---------------------------------------------------------------- measurement

$script:Rows = New-Object System.Collections.Generic.List[object]
# Wall seconds per (dataset, step, size), for extrapolation.
$script:Seen = @{}

function Get-Memory { try { Send-Bridge benchmem @{ collect = $true } -TimeoutSec 120 } catch { $null } }

function Predict([string]$dataset, [string]$step, [int]$size) {
    $known = @($Sizes | Where-Object { $_ -lt $size -and $script:Seen.ContainsKey("$dataset|$step|$_") })
    if ($known.Count -eq 0) { return $null }
    $n1 = $known[-1]; $t1 = $script:Seen["$dataset|$step|$n1"]
    $exp = 1.0
    if ($known.Count -ge 2) {
        $n0 = $known[-2]; $t0 = $script:Seen["$dataset|$step|$n0"]
        # Growth exponent between the last two sizes; floored at linear, and small times are noise.
        if ($t0 -gt 0.5 -and $t1 -gt 0.5) { $exp = [Math]::Max(1.0, [Math]::Log($t1 / $t0) / [Math]::Log($n1 / $n0)) }
    }
    return $t1 * [Math]::Pow($size / $n1, $exp)
}

function Measure-Step {
    param([string]$Dataset, [int]$Size, [string]$Step, [scriptblock]$Action, [int]$Edited = 0)
    $predicted = Predict $Dataset $Step $Size
    if ($predicted -and $predicted -gt $StepBudgetMin * 60) {
        Write-Host ("  {0,-14} SKIPPED: predicted {1:N0} s > budget {2} min" -f $Step, $predicted, $StepBudgetMin) -ForegroundColor Yellow
        $script:Rows.Add([pscustomobject]@{ dataset = $Dataset; size = $Size; step = $Step; seconds = $null
            predictedSeconds = [int]$predicted; skipped = $true; total = $null; changed = $null; edited = $Edited
            phaseMs = $null; memory = $null; error = $null })
        return $null
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $result = $null; $err = $null
    try { $result = & $Action } catch { $err = $_.Exception.Message }
    $seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
    $mem = Get-Memory
    if (-not $err) { $script:Seen["$Dataset|$Step|$Size"] = $seconds }
    $phases = if ($result -and $result.phaseMs) { $result.phaseMs } else { $null }
    $phaseText = if ($phases) { ($phases.PSObject.Properties | Sort-Object { -[long]$_.Value } |
        ForEach-Object { "{0}={1:N1}s" -f $_.Name, ($_.Value / 1000.0) }) -join ' ' } else { '' }
    $colour = if ($err) { 'Red' } else { 'Gray' }
    Write-Host ("  {0,-14} {1,9:N2} s  total={2} changed={3}  mem={4} MB  {5}{6}" -f $Step, $seconds,
        $result.total, $result.changed, $mem.privateMb, $phaseText, $(if ($err) { "  ERROR: $err" } else { '' })) -ForegroundColor $colour
    $script:Rows.Add([pscustomobject]@{ dataset = $Dataset; size = $Size; step = $Step; seconds = $seconds
        predictedSeconds = $(if ($predicted) { [int]$predicted } else { $null }); skipped = $false
        total = $result.total; changed = $result.changed; edited = $Edited; phaseMs = $phases; memory = $mem; error = $err })
    return $result
}

function Save-Results {
    $script:Rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Scratch 'benchmark.json') -Encoding utf8
    $script:Rows | ForEach-Object {
        [pscustomobject]@{
            dataset = $_.dataset; size = $_.size; step = $_.step; seconds = $_.seconds
            predicted_seconds = $_.predictedSeconds; skipped = $_.skipped; total = $_.total; changed = $_.changed
            edited = $_.edited; private_mb = $_.memory.privateMb; managed_mb = $_.memory.managedMb
            phases = $(if ($_.phaseMs) { ($_.phaseMs.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';' } else { '' })
            error = $_.error
        } } | Export-Csv (Join-Path $Scratch 'benchmark.csv') -NoTypeInformation -Encoding utf8
}

# Generous per-call bridge timeout: the budget guard, not this, is what bounds a step.
$callTimeout = [int]([Math]::Max(3600, $StepBudgetMin * 60 * 3))

# ---------------------------------------------------------------- run

foreach ($size in $Sizes) {
    foreach ($dataset in $Datasets) {
        $name = "Bench_${dataset}_$size"
        Write-Host ""
        Write-Host "=== $name" -ForegroundColor Cyan
        Get-ChildItem (Join-Path $Data "$name.*") | Copy-Item -Destination $work -Force

        Send-Bridge newdoc -TimeoutSec 600 | Out-Null
        # Saved with the Rhino document, so a new document starts read-only again.
        Send-Bridge attributeseditable @{ value = $true } | Out-Null
        $layer = (Send-Bridge addlayer @{ path = (Join-Path $work "$name.shp") } -TimeoutSec 600).name
        $baseMem = Get-Memory
        Write-Host ("  baseline memory {0} MB private, {1} MB managed" -f $baseMem.privateMb, $baseMem.managedMb)
        $edits = [int][Math]::Max(1, [Math]::Round($size * $EditPercent / 100.0))

        $pull = Measure-Step $dataset $size 'pull' { Send-Bridge pull @{ arcgisLayer = $layer } -TimeoutSec $callTimeout }
        if ($pull) {
            # The pull maps features on several threads; re-check a sample against a serial run.
            $verify = Send-Bridge benchverify @{ arcgisLayer = $layer; sample = 2000 } -TimeoutSec 1800
            $ok = $verify.mismatches -eq 0 -and $verify.errors -eq 0
            Write-Host ("  verify         {0} features on {1} threads vs serial: {2} mismatches, {3} errors" -f
                $verify.sample, $verify.threads, $verify.mismatches, $verify.errors) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
            $script:Rows.Add([pscustomobject]@{ dataset = $dataset; size = $size; step = 'verify.parallel'; seconds = $null
                predictedSeconds = $null; skipped = $false; total = $verify.sample; changed = $verify.mismatches; edited = 0
                phaseMs = $null; memory = $null; error = $(if ($ok) { $null } else { "parallel preparation differs from serial: $($verify.mismatches) mismatches, $($verify.errors) errors" }) })

            Measure-Step $dataset $size 'preview.clean' { Send-Bridge preview @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } | Out-Null
            Measure-Step $dataset $size 'apply.noop' { Send-Bridge apply @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } | Out-Null

            $moved = Send-Bridge benchrhinoedit @{ layer = $layer; count = $edits; mode = 'move'; dx = 0.5 } -TimeoutSec 1800
            Measure-Step $dataset $size 'preview.move' { Send-Bridge preview @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } $moved.edited | Out-Null
            Measure-Step $dataset $size 'apply.move' { Send-Bridge apply @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } $moved.edited | Out-Null

            $retyped = Send-Bridge benchrhinoedit @{ layer = $layer; count = $edits; mode = 'attr'; key = 'name'; value = 'rhino bench' } -TimeoutSec 1800
            Measure-Step $dataset $size 'apply.attr' { Send-Bridge apply @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } $retyped.edited | Out-Null

            $gis = Send-Bridge benchgisedit @{ arcgisLayer = $layer; count = $edits; field = 'kind'; value = 'gis bench' } -TimeoutSec 1800
            Measure-Step $dataset $size 'apply.gis' { Send-Bridge apply @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } $gis.edited | Out-Null

            $gisMoved = Send-Bridge benchgisedit @{ arcgisLayer = $layer; count = $edits; mode = 'move'; dy = 0.5 } -TimeoutSec 1800
            Measure-Step $dataset $size 'apply.gismove' { Send-Bridge apply @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } $gisMoved.edited | Out-Null
            Measure-Step $dataset $size 'preview.final' { Send-Bridge preview @{ arcgisLayer = $layer } -TimeoutSec $callTimeout } | Out-Null
        }
        Save-Results

        try { Send-Bridge removelayer @{ arcgisLayer = $layer } -TimeoutSec 600 | Out-Null } catch { }
    }
}

Send-Bridge newdoc -TimeoutSec 600 | Out-Null
Save-Results

Write-Host ""
Write-Host "=== summary (seconds)" -ForegroundColor Cyan
$script:Rows | Where-Object step -ne 'verify.parallel' | Group-Object dataset, step | ForEach-Object {
    $cells = foreach ($n in $Sizes) {
        $r = $_.Group | Where-Object size -eq $n | Select-Object -First 1
        if (-not $r) { '-' } elseif ($r.skipped) { "~{0:N0}*" -f $r.predictedSeconds } elseif ($r.error) { 'ERR' } else { '{0:N1}' -f $r.seconds }
    }
    Write-Host ("  {0,-30} {1}" -f $_.Name, ($cells -join "`t"))
}
Write-Host "  (* skipped; value is the extrapolated time)"
Write-Host "results: $Scratch"

if (-not $LeavePro -and -not $SkipStart) {
    $closed = Close-Pro -SaveProject $false -RhinoWork Discard
    if (-not $closed.Exited) { Write-Warning "Pro did not close cleanly: $($closed.Note)" }
}
try { Stop-Transcript | Out-Null } catch { }
