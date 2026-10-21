<# Uses a disposable project and restores the installed add-in after the owned host exits. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceProject,
    [Parameter(Mandatory)][string]$Scratch,
    [switch]$Install,
    [ValidateRange(0,60)][int]$VisualSeconds = 0
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Get-Process ArcGISPro -ErrorAction SilentlyContinue) { throw 'Close ArcGIS Pro before starting an isolated acceptance host.' }
$Scratch = [IO.Path]::GetFullPath($Scratch)
if (Test-Path -LiteralPath $Scratch) { throw 'Choose a fresh scratch folder for each independent host.' }
New-Item -ItemType Directory -Path $Scratch | Out-Null
$project = Join-Path $Scratch 'Grasshopper.aprx'
& 'C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe' (Join-Path $PSScriptRoot 'prepare-grasshopper-host.py') $SourceProject $project
if ($LASTEXITCODE -ne 0) { throw 'Disposable project preparation failed.' }
$installed = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ArcGIS\AddIns\ArcGISPro\{f10f6246-8091-4aad-8b5c-2bcb6b523784}\RhinoInside.ArcGISPro.esriAddinX'
$package = Join-Path $repo 'src\RhinoInside.ArcGISPro\bin\Release\net10.0-windows10.0.22621.0\RhinoInside.ArcGISPro.esriAddinX'
$backup = Join-Path $Scratch 'previous.esriAddinX'
if ($Install) {
    if (Test-Path -LiteralPath $installed) { Copy-Item -LiteralPath $installed -Destination $backup }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installed) | Out-Null
    Copy-Item -LiteralPath $package -Destination $installed -Force
}
$bridge = Join-Path $Scratch 'bridge'
$env:RHINOINSIDE_TESTBRIDGE = $bridge
$env:RHINOINSIDE_ARCGIS_WINDOW_STYLE = 'Hidden'
$env:RHINOINSIDE_GRASSHOPPER_ISOLATED = '1'
. (Join-Path $PSScriptRoot 'bridge.ps1')
# Keep this runner independent of desktop focus and modal-dialog automation. A dialog is a failed run.
function Dismiss-Dialogs { param([int]$ProcessId = 0, [string]$Root) @() }
Initialize-Bridge -Root $bridge | Out-Null
$owned = $null
try {
    $owned = Start-Process 'C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe' -ArgumentList ('"' + $project + '"') -WindowStyle Hidden -PassThru
    $script:TestProProcessId = $owned.Id
    Wait-Bridge -Root $bridge -TimeoutSec 90
    $mapDeadline = (Get-Date).AddSeconds(90)
    do {
        $mapState = Send-Bridge ensuremapview -TimeoutSec 30
        if ($mapState.active) { break }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $mapDeadline)
    if (-not $mapState.active) { throw 'The disposable project did not open a map view.' }
    Send-Bridge unsavedpolicy @{ value = 'Discard' } | Out-Null
    Send-Bridge launch -TimeoutSec 120 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Scratch 'launch.json')
    Send-Bridge newdoc | Out-Null
    Send-Bridge setunits @{ units = 'Meters' } | Out-Null
    $acceptance = Send-Bridge grasshopperacceptance @{ examplePath = (Join-Path $Scratch 'MapDesign.ghx') } -TimeoutSec 180
    $acceptance | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Scratch 'acceptance.json')
    if ($VisualSeconds -gt 0) {
        Send-Bridge grasshopperexample @{ path = (Join-Path $Scratch 'MapDesign.ghx') } | Out-Null
        Send-Bridge zoomtolayers @{ layers = @($acceptance.meshLayer.Name) } | Out-Null
        Write-Output "Visual inspection window: $VisualSeconds seconds"
        Start-Sleep -Seconds $VisualSeconds
    }
    Write-Output "Acceptance result: $(Join-Path $Scratch 'acceptance.json')"
} finally {
    if ($owned -and -not $owned.HasExited) {
        try { Send-Bridge closepro @{ saveProject = $true } -TimeoutSec 30 | Out-Null } catch { Write-Warning $_ }
        $cleanExit = $owned.WaitForExit(30000)
        @{ cleanExit = $cleanExit; processId = $owned.Id } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Scratch 'close.json')
        if (-not $cleanExit) {
            $same = Get-Process -Id $owned.Id -ErrorAction SilentlyContinue
            if ($same -and $same.ProcessName -eq 'ArcGISPro' -and $same.StartTime -eq $owned.StartTime) {
                Stop-Process -Id $owned.Id -Force
                $owned.WaitForExit(15000) | Out-Null
            }
            Write-Warning "Disposable host did not close cleanly; clean-close acceptance failed. Functional results remain recorded."
        }
    }
    if ($Install -and (-not $owned -or $owned.HasExited)) {
        if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $installed -Force }
        elseif (Test-Path -LiteralPath $installed) { Remove-Item -LiteralPath $installed }
    }
}
if ($cleanExit -eq $false) { throw 'Functional checks completed, but clean-close acceptance failed; see close.json.' }
