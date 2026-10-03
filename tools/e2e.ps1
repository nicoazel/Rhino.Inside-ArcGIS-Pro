<#
.SYNOPSIS
End-to-end sync matrix: every fixture layer through pull, preview, push (apply) and the
attribute round trip, inside a real ArcGIS Pro with Rhino hosted in-process.

.DESCRIPTION
Drives the add-in through the test bridge (tools/bridge.ps1) and asserts what each step reports.
Fixtures are copied to a scratch folder first, so the pushes that this run makes never touch the
files under tests/data.

Per fixture layer the sequence is:

  1. add the shapefile to the map, count its features (N)
  1b. on one representative layer, select one GIS feature and pull selected only
  2. pull                       -> N objects created; Rhino layer holds them
  2b. multipatch only: pulled ArcGIS Z geometry becomes a Rhino mesh with shared vertices
  3. preview                    -> N rows, 0 changed (identity and baselines survived the pull)
  4. draw one object of the layer's own type in Rhino
  5. preview                    -> N+1 rows, exactly 1 "New in Rhino"
  5b. link row set to pull-only; apply -> 1 "Held", ArcGIS unchanged; preview still flags it
  6. apply (two-way)            -> ArcGIS now has N+1 features, of the layer's shape type
  6b. the pushed feature's Shape_Leng / Shape_Area were computed from its geometry, and Rhino
      shows the value ArcGIS stored
  6c. multipatch only: pushed Rhino mesh keeps Z extent, faces, and shared indexed topology
  7. preview                    -> N+1 rows, 0 changed (the pushed object was re-baselined)
  7b. move the new Rhino object -> preview sees "Modified in Rhino"; apply updates the same feature
  8. attributes editable on; edit a design field on the new object
  9. preview                    -> exactly 1 "Modified in Rhino"
 10. apply                      -> the pushed feature carries the new value
 11. preview                    -> clean again
  11b. edit the same field in ArcGIS; preview -> 1 "Modified in ArcGIS"; push-only apply holds it
       and the Rhino value is untouched; two-way apply pulls it into Rhino; preview clean
  11c. on one representative layer, edit the same field on both sides; Manual holds both values,
       PreferRhino pushes Rhino's value, and PreferArcGis pulls ArcGIS's value
  11d. preview-all through the pane's link table -> this row reads "in sync"
 12. delete the pushed feature in ArcGIS; preview -> exactly 1 "Deleted in ArcGIS"
 13. apply                      -> nothing recreated, still held
 14. swap the layer for a same-named copy from another folder; preview -> warns, still compares
 15. apply                      -> completed objects adopt the new source; held deletion retains
                                  its original source and warning

After the loop: one deliberately broken link proves that Preview all isolates its failure and keeps
the healthy link results. Disposable file-geodatabase layers then prove GlobalID-first matching,
reverse geometry replacement, creating brand-new PolylineZ and Multipatch feature classes from
Rhino-authored geometry, and saving a per-link field-ownership profile that drives the next sync.
Temporary rows and datasets are removed, then the real link table is saved with the .3dm, gone in a
new document, and back after reopen.

A mixed working session then edits both sides at once (move, retype, delete, copy and draw in Rhino;
attribute and geometry edits in ArcGIS), checks one preview sorts all of it, applies, and repeats
preview/apply rounds to prove nothing drifts; Pull restores a Rhino deletion, one object is edited
back and forth, a preview runs with an attribute table focused, and a renamed layer keeps its link.
A shapefile delete that renumbers every later FID must still match each feature to its own object.

Finally the session is closed and reopened twice through the add-in (-SkipSession to skip): no
recovery prompts, the project's .3dm reopens by itself, every link compares exactly as before, and
an unsaved Rhino edit is kept by "save on close". Pro is closed cleanly at the end (-LeavePro keeps it).

Every assertion is recorded; the run prints a PASS/FAIL table and exits non-zero on any failure.

.EXAMPLE
  .\tools\e2e.ps1
  .\tools\e2e.ps1 -Fixtures Point_Multi_Mixed, Polygons_Single_Buildings
  .\tools\e2e.ps1 -SkipStart      # reuse the Pro that is already running with the bridge

.PARAMETER Project
ArcGIS Pro project to open. Its active map is where the fixture layers are added.

.PARAMETER Fixtures
Base names (without extension) of the shapefiles under tests/data/SHP to run. Default: all.

.PARAMETER SkipStart
Do not (re)start Pro; assume it is up with the bridge listening.

.PARAMETER ResetRhinoDocument
Create a new empty Rhino document before the run. Use only in a disposable test host;
the current Rhino document is discarded. Otherwise the existing document must be empty.
#>
[CmdletBinding()]
param(
    [string]$Project = $env:RHINOINSIDE_E2E_PROJECT,
    [string[]]$Fixtures = @(
        'Point_Multi_Mixed',
        'Polyline_MultipartMix_Streets',
        'Polygons_Single_Buildings',
        'Polygon_MixedMultiPart_Parcels',
        'Boundary_Multipart_Polygon',
        'Multipatch_Single_Building'
    ),
    [switch]$SkipStart,
    [switch]$ResetRhinoDocument,
    [switch]$SkipAttributes,
    [switch]$SkipSession,
    [switch]$LeavePro,
    [string]$Scratch
)

$ErrorActionPreference = 'Stop'
if (-not $SkipStart -and (-not $Project -or -not (Test-Path -LiteralPath $Project -PathType Leaf))) {
    throw 'Pass -Project with a disposable ArcGIS Pro .aprx containing a Map view, or set RHINOINSIDE_E2E_PROJECT.'
}
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'bridge.ps1')

# ---------------------------------------------------------------- assertions

$script:Results = New-Object System.Collections.Generic.List[object]
$script:CurrentFixture = ''

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $entry = [pscustomobject]@{ Fixture = $script:CurrentFixture; Check = $Name; Ok = $Ok; Detail = $Detail }
    $script:Results.Add($entry) | Out-Null
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    $colour = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $tag, $Name) -ForegroundColor $colour -NoNewline
    if ($Detail) { Write-Host ("  -- {0}" -f $Detail) -ForegroundColor DarkGray } else { Write-Host '' }
    return $Ok
}

function Rollup($report) {
    # {Label -> Count} from a Summarise() reply
    $h = @{}
    foreach ($r in @($report.rollup)) { $h[$r.Label] = [int]$r.Count }
    return $h
}

function Describe-Report($report) {
    $parts = foreach ($r in @($report.rollup)) { "{0}={1}" -f $r.Label, $r.Count }
    return ("total={0} changed={1} [{2}]" -f $report.total, $report.changed, ($parts -join ', '))
}

# ---------------------------------------------------------------- fixture setup

if (-not $Scratch) {
    $Scratch = Join-Path $env:TEMP ("rhinoinside-e2e-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
$src = Join-Path $repo 'tests\data\SHP'
$fixtureNamesToStage = @(
    $Fixtures
    # Cross-cutting GlobalID scenarios run after every selected fixture set.
    'Point_Multi_Mixed'
    'Boundary_Multipart_Polygon'
) | Select-Object -Unique
foreach ($f in $fixtureNamesToStage) {
    Get-ChildItem (Join-Path $src "$f.*") | Copy-Item -Destination $Scratch -Force
}
try { Start-Transcript -Path (Join-Path $Scratch 'e2e.log') -Force | Out-Null } catch { }
Write-Host "fixtures copied to $Scratch"

# The run saves the project (layers, pending edits, the Rhino document path) when it closes Pro,
# and restarts it; that must never touch the project it was given. A copy in the scratch folder
# also keeps Pro's recovery backups for this run out of the original project's folder.
if (-not $SkipStart) {
    $projectCopy = Join-Path $Scratch 'project'
    New-Item -ItemType Directory -Force -Path $projectCopy | Out-Null
    $copied = Join-Path $projectCopy (Split-Path -Leaf $Project)
    Copy-Item -LiteralPath $Project -Destination $copied -Force
    $Project = $copied
    Write-Host "project copied to $Project"
}

# Kind of object to draw on the Rhino side, and the model-space vertices for it. The earth
# anchor is placed at the map centre with model (0,0) on it, so anything near the origin lands
# in the middle of the data.
$Shapes = @{
    Point      = @{ kind = 'point';   points = @(,@(15.0, 25.0)) }
    Polyline   = @{ kind = 'line';    points = @(@(0.0, 0.0), @(40.0, 10.0), @(80.0, 0.0)) }
    Polygon    = @{ kind = 'polygon'; points = @(@(0.0, 0.0), @(30.0, 0.0), @(30.0, 20.0), @(0.0, 20.0)) }
    Multipatch = @{ kind = 'mesh';    points = @(@(0.0, 0.0), @(20.0, 0.0), @(20.0, 20.0), @(0.0, 20.0)) }
}

# ---------------------------------------------------------------- bring Pro up

if (-not $SkipStart) {
    Start-Pro -Project $Project
    # The map view takes a moment after the bridge log appears.
    Start-Sleep -Seconds 8
    $startupRecovery = @(Get-RecoveryPrompts)
    if ($startupRecovery.Count) {
        # Left over from a session this run did not own; worth knowing, not this run's failure.
        Write-Host ("recovery prompts from an earlier session were dismissed: " + ($startupRecovery -join '; ')) -ForegroundColor DarkYellow
    }
}
else {
    Initialize-Bridge | Out-Null
}

$status = Send-Bridge status
$mapView = $null
for ($attempt = 0; $attempt -lt 24; $attempt++) {
    try { $mapView = Send-Bridge ensuremapview @{ map = 'Map' } }
    catch { $mapView = $null }
    if ($mapView -and $mapView.active) { break }
    Dismiss-Dialogs | Out-Null
    Start-Sleep -Seconds 5
}
if (-not ($mapView -and $mapView.active)) {
    throw "ArcGIS Pro did not activate the project's 'Map' view before the E2E timeout."
}
Write-Host ("active map ready: {0}{1}" -f $mapView.name, $(if ($mapView.opened) { ' (opened by test bridge)' } else { '' }))

if (-not $status.rhinoStarted) {
    $launch = Send-Bridge launch -TimeoutSec 600
    Write-Host ("Rhino {0} started" -f $launch.version)
}
if ($ResetRhinoDocument) {
    # Explicitly opt in only for a disposable host: Rhino may restore the last test .3dm.
    Send-Bridge newdoc -TimeoutSec 600 | Out-Null
}
$initialDocument = (Send-Bridge status).document
if ($initialDocument -and [int]$initialDocument.ObjectCount -gt 0) {
    throw 'The E2E matrix requires an empty Rhino document. Use -ResetRhinoDocument only in a disposable test host.'
}
Send-Bridge attributeseditable @{ value = $false } | Out-Null
# A scripted close must never wait on the "save the Rhino document?" prompt.
Send-Bridge unsavedpolicy @{ value = 'Discard' } | Out-Null
Send-Bridge showpane | Out-Null
# Render the real view, including its Profile bindings, before testing the coordinator.
# Constructing only the view model cannot catch WPF binding errors that terminate the host.
$profileTab = Send-Bridge subtab @{ value = 'Profile' }
$renderedView = Send-Bridge uistate
Check 'dockpane view loads with Profile bindings' `
    ([bool]$renderedView -and [bool]$profileTab.IsProfileSubTab) `
    'real DockpaneView loaded; Profile selected' | Out-Null
Send-Bridge subtab @{ value = 'Link' } | Out-Null

# ---------------------------------------------------------------- the matrix

foreach ($fixture in $Fixtures) {
    $script:CurrentFixture = $fixture
    Write-Host ""
    Write-Host ("=== {0} ===" -f $fixture) -ForegroundColor Cyan

    try {
        $shp = Join-Path $Scratch "$fixture.shp"
        # The map view can lag the bridge by a few seconds on a cold start.
        $added = $null
        for ($attempt = 0; $attempt -lt 12; $attempt++) {
            $added = Send-Bridge addlayer @{ path = $shp }
            if ($added.added) { break }
            Start-Sleep -Seconds 5
        }
        if (-not (Check 'layer added to map' ([bool]$added.added) $added.name)) { continue }
        $L = $added.name

        # ---- what ArcGIS holds before anything happens
        $before = Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600
        $N = [int]$before.total
        $shapeKind = ($before.kinds.PSObject.Properties | Sort-Object { -[int]$_.Value } | Select-Object -First 1).Name
        Check 'features readable' ($N -gt 0) ("{0} features, kinds: {1}" -f $N, (($before.kinds.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' ')) | Out-Null
        if (-not $Shapes.ContainsKey($shapeKind)) {
            Check 'shape kind supported' $false "no test shape for '$shapeKind'" | Out-Null
            continue
        }

        # ---- 1b. prove the production ArcGIS selection path on one representative layer
        if ($fixture -eq $Fixtures[0]) {
            $selectedOid = [long]$before.last[-1].objectId
            $selected = Send-Bridge selectfeatures @{ arcgisLayer = $L; objectIds = @($selectedOid) }
            Check 'one ArcGIS feature selected for selected-only pull' ($selected.selected -eq 1) "selected oid $selectedOid" | Out-Null

            $selectionLayer = "$L selection"
            $selectionPull = Send-Bridge pull @{ arcgisLayer = $L; rhinoLayer = $selectionLayer; selectedOnly = $true } -TimeoutSec 900
            $selectionCounts = (Send-Bridge objectcounts @{ layer = $selectionLayer }).counts
            Check 'selected-only pull creates exactly the selected feature' `
                ($selectionPull.total -eq 1 -and [int]$selectionCounts.'(total)' -eq 1) `
                (Describe-Report $selectionPull) | Out-Null
            Send-Bridge clearselection @{ arcgisLayer = $L } | Out-Null
        }

        # ---- 2. pull
        $pull = Send-Bridge pull @{ arcgisLayer = $L } -TimeoutSec 900
        Check 'pull reports one row per feature' ($pull.total -eq $N) (Describe-Report $pull) | Out-Null
        $rhinoLayers = (Send-Bridge rhinolayers).layers
        Check 'pull created the Rhino layer' ($rhinoLayers -contains $L) ("rhino layers: " + ($rhinoLayers -join ', ')) | Out-Null
        $counts = (Send-Bridge objectcounts @{ layer = $L }).counts
        $objTotal = [int]$counts.'(total)'
        Check 'Rhino layer holds the pulled objects' ($objTotal -ge $N) (($counts.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' ') | Out-Null
        # One tracked object per feature: multipart pieces carry only a part-of marker.
        $tracked = @((Send-Bridge trackedlayers).layers | Where-Object { $_.Layer -eq $L })
        Check 'tracked layers list the pulled layer' ($tracked.Count -eq 1 -and [int]$tracked[0].Tracked -eq $N) `
            $(if ($tracked.Count) { "tracked={0} parts={1} total={2} from '{3}'" -f $tracked[0].Tracked, $tracked[0].Parts, $tracked[0].Total, $tracked[0].ArcGisLayer } else { 'not listed' }) | Out-Null

        # ---- 2b. ArcGIS -> Rhino 3D: the fixture must keep Z and become an indexed mesh, not
        # one disconnected set of three vertices per triangle.
        if ($shapeKind -eq 'Multipatch') {
            $pulledObjects = (Send-Bridge userstrings @{ layer = $L }).objects
            $pulledId = @($pulledObjects.PSObject.Properties | Select-Object -First 1).Name
            $pulledGeometry = Send-Bridge objectgeometry @{ objectId = $pulledId }
            Check '3D pull keeps a non-zero Z extent in Rhino' `
                ($pulledGeometry.Kind -eq 'Mesh' -and ([double]$pulledGeometry.ZMax - [double]$pulledGeometry.ZMin) -gt 0) `
                ("kind={0}, Z={1}..{2}" -f $pulledGeometry.Kind, $pulledGeometry.ZMin, $pulledGeometry.ZMax) | Out-Null
            Check '3D pull produces a shared-vertex Rhino mesh' `
                ($pulledGeometry.SharedVertices -and $pulledGeometry.VertexCount -eq $pulledGeometry.TopologyVertexCount) `
                ("vertices={0}, topology={1}, faces={2}, corners={3}" -f $pulledGeometry.VertexCount, $pulledGeometry.TopologyVertexCount, $pulledGeometry.FaceCount, $pulledGeometry.FaceCornerCount) | Out-Null
        }

        # ---- 3. preview straight after pull is clean
        $p1 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
        Check 'preview after pull sees every feature' ($p1.total -eq $N) (Describe-Report $p1) | Out-Null
        Check 'preview after pull is clean' ($p1.changed -eq 0) (Describe-Report $p1) | Out-Null

        # ---- 4. draw one object of the layer's type
        $shape = $Shapes[$shapeKind]
        $newId = (Send-Bridge addobject @{ layer = $L; kind = $shape.kind; points = $shape.points }).id
        Check 'test object drawn' ([bool]$newId) ("{0} {1}" -f $shape.kind, $newId) | Out-Null

        # ---- 5. preview sees exactly that one
        $p2 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
        $r2 = Rollup $p2
        Check 'preview counts the new object' ($p2.total -eq ($N + 1)) (Describe-Report $p2) | Out-Null
        Check 'preview flags exactly one New in Rhino' ($p2.changed -eq 1 -and $r2['New in Rhino'] -eq 1) (Describe-Report $p2) | Out-Null

        # ---- 5b. a pull-only row: the new Rhino object is held, not pushed, and not swallowed
        $links = Send-Bridge setlink @{ arcgisLayer = $L; direction = 'PullOnly' }
        $stored = @($links.stored | Where-Object { $_.arcgisLayer -eq $L })
        Check 'link row is saved in the Rhino document' ($stored.Count -eq 1 -and $stored[0].direction -eq 'PullOnly' -and $links.active -eq $L) `
            ("stored rows: " + ((@($links.stored) | ForEach-Object { "$($_.arcgisLayer)/$($_.direction)" }) -join ', ')) | Out-Null
        $h1 = Send-Bridge apply @{ arcgisLayer = $L; direction = 'PullOnly' } -TimeoutSec 900
        $rh1 = Rollup $h1
        $held = Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600
        Check 'pull-only apply holds the new Rhino object' ($rh1['Held'] -eq 1 -and $held.total -eq $N) ("apply: {0}; features still {1}" -f (Describe-Report $h1), $held.total) | Out-Null
        $p2b = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
        $r2b = Rollup $p2b
        Check 'held object is still New in Rhino afterwards' ($p2b.changed -eq 1 -and $r2b['New in Rhino'] -eq 1) (Describe-Report $p2b) | Out-Null
        Send-Bridge setlink @{ arcgisLayer = $L; direction = 'TwoWay' } | Out-Null

        # ---- 6. apply pushes it
        $a1 = Send-Bridge apply @{ arcgisLayer = $L } -TimeoutSec 900
        $after = Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600
        Check 'apply created one ArcGIS feature' ($after.total -eq ($N + 1)) ("apply: {0}; features now {1}" -f (Describe-Report $a1), $after.total) | Out-Null
        $lastKind = $after.last[-1].kind
        Check 'pushed feature has the layer shape type' ($lastKind -eq $shapeKind) ("last feature kind: {0}, points: {1}" -f $lastKind, $after.last[-1].pointCount) | Out-Null
        $pushedOid = [long]$after.last[-1].objectId

        # ---- 6c. Rhino -> ArcGIS 3D: the adapter's readback exposes the indexed neutral mesh.
        # Multipatches store coordinates per patch, but the sync boundary welds them immediately.
        if ($shapeKind -eq 'Multipatch') {
            $gis3d = $after.last[-1]
            Check '3D push keeps a non-zero Z extent in ArcGIS' `
                (([double]$gis3d.zMax - [double]$gis3d.zMin) -gt 0 -and $gis3d.faceCount -gt 0) `
                ("Z={0}..{1}, faces={2}" -f $gis3d.zMin, $gis3d.zMax, $gis3d.faceCount) | Out-Null
            Check '3D ArcGIS readback has shared indexed vertices' `
                ($gis3d.sharedVertices -and $gis3d.vertexCount -lt $gis3d.faceCornerCount) `
                ("vertices={0}, faces={1}, corners={2}" -f $gis3d.vertexCount, $gis3d.faceCount, $gis3d.faceCornerCount) | Out-Null
        }

        # ---- 6b. a shapefile's Shape_Leng / Shape_Area are recomputed from the pushed geometry
        $metricSchema = Send-Bridge schema @{ arcgisLayer = $L }
        $lengthField = @($metricSchema.fields | Where-Object { $_.Name -match '^Shape_?Le' } | Select-Object -First 1).Name
        $areaField = @($metricSchema.fields | Where-Object { $_.Name -match '^Shape_?Ar' } | Select-Object -First 1).Name
        if ($shapeKind -ne 'Point' -and ($lengthField -or $areaField)) {
            $attrs = $after.last[-1].attributes
            $len = if ($lengthField) { [double]$attrs.$lengthField } else { 0 }
            $area = if ($areaField) { [double]$attrs.$areaField } else { 0 }
            if ($shapeKind -eq 'Polygon' -and $lengthField -and $areaField) {
                # The 30 x 20 test rectangle: area / perimeter^2 = 600 / 100^2, whatever the CRS unit.
                $ratio = if ($len -gt 0) { $area / ($len * $len) } else { 0 }
                Check 'pushed polygon carries its length and area' ([math]::Abs($ratio - 0.06) -lt 0.001) ("{0}={1} {2}={3} ratio={4}" -f $lengthField, $len, $areaField, $area, $ratio) | Out-Null
            }
            elseif ($lengthField) {
                Check 'pushed feature carries its length' ($len -gt 0) ("{0}={1}" -f $lengthField, $len) | Out-Null
            }
            $metricKey = if ($lengthField) { $lengthField } else { $areaField }
            $rhinoMetric = (Send-Bridge userstrings @{ objectId = $newId }).objects.$newId.$metricKey
            Check 'Rhino shows the metric ArcGIS stored' ($rhinoMetric -eq [string]$attrs.$metricKey) ("Rhino {0}='{1}', ArcGIS '{2}'" -f $metricKey, $rhinoMetric, $attrs.$metricKey) | Out-Null
        }

        # ---- 7. preview after apply is clean again
        $p3 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
        Check 'preview after apply is clean' ($p3.total -eq ($N + 1) -and $p3.changed -eq 0) (Describe-Report $p3) | Out-Null

        # ---- 7b. update, rather than create: every supported geometry family takes this path
        $moveZ = if ($shapeKind -eq 'Multipatch') { 2.0 } else { 0.0 }
        Send-Bridge moveobject @{ objectId = $newId; x = 5.0; y = 3.0; z = $moveZ } | Out-Null
        $pg1 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
        $rg1 = Rollup $pg1
        Check 'Rhino geometry edit shows as Modified in Rhino' `
            ($pg1.changed -eq 1 -and $rg1['Modified in Rhino'] -eq 1) (Describe-Report $pg1) | Out-Null

        $ag1 = Send-Bridge apply @{ arcgisLayer = $L } -TimeoutSec 900
        $afterGeometryUpdate = Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600
        Check 'geometry apply updates the existing ArcGIS feature' `
            ($afterGeometryUpdate.total -eq ($N + 1) -and (Rollup $ag1)['Updated'] -eq 1) `
            ("apply: {0}; features still {1}" -f (Describe-Report $ag1), $afterGeometryUpdate.total) | Out-Null
        $pg2 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
        Check 'preview after geometry update is clean' ($pg2.changed -eq 0) (Describe-Report $pg2) | Out-Null

        # ---- 8-11. attribute round trip on the object we pushed
        if (-not $SkipAttributes) {
            # A text field wide enough for the marker: a shapefile truncates to the field width, and
            # identity / geometry-metric fields are ArcGIS-owned whatever the toggle says.
            $value = "e2e-" + (Get-Date -Format 'HHmmss')
            $schema = Send-Bridge schema @{ arcgisLayer = $L }
            $value2 = $value + '-gis'
            $field = @($schema.fields | Where-Object {
                    $_.type -eq 'Text' -and [int]$_.Length -ge $value2.Length -and
                    $_.Name -notmatch '^(FID|OBJECTID|OID|GLOBALID|Shape)' } | Select-Object -First 1).Name
            if (-not $field) {
                Check 'a text field exists for the attribute round trip' $false ("fields: " + (($schema.fields | ForEach-Object { "$($_.Name):$($_.type)($($_.Length))" }) -join ', ')) | Out-Null
            }
            else {
                Send-Bridge attributeseditable @{ value = $true } | Out-Null

                # The pushed object was re-baselined by apply, so a preview with attributes editable
                # but nothing edited must still be clean.
                $p4 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                Check 'editable attributes alone do not create changes' ($p4.changed -eq 0) (Describe-Report $p4) | Out-Null

                Send-Bridge setuserstring @{ objectId = $newId; key = $field; value = $value } | Out-Null
                $p5 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                $r5 = Rollup $p5
                Check "attribute edit ($field) shows as Modified in Rhino" ($p5.changed -eq 1 -and $r5['Modified in Rhino'] -eq 1) (Describe-Report $p5) | Out-Null

                $a2 = Send-Bridge apply @{ arcgisLayer = $L } -TimeoutSec 900
                $after2 = Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600
                $pushedValue = $after2.last[-1].attributes.$field
                Check 'attribute value reached ArcGIS' ($pushedValue -eq $value) ("apply: {0}; {1}='{2}'" -f (Describe-Report $a2), $field, $pushedValue) | Out-Null
                Check 'attribute apply did not create features' ($after2.total -eq ($N + 1)) ("features now {0}" -f $after2.total) | Out-Null

                $p6 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                Check 'preview after attribute apply is clean' ($p6.changed -eq 0) (Describe-Report $p6) | Out-Null

                # ---- 11b. the same field edited on the ArcGIS side: push-only holds it, two-way pulls it
                Send-Bridge setattribute @{ arcgisLayer = $L; objectId = $pushedOid; field = $field; value = $value2 } -TimeoutSec 600 | Out-Null
                $p6b = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                $r6b = Rollup $p6b
                Check 'ArcGIS-side attribute edit shows as Modified in ArcGIS' ($p6b.changed -eq 1 -and $r6b['Modified in ArcGIS'] -eq 1) (Describe-Report $p6b) | Out-Null

                $h2 = Send-Bridge apply @{ arcgisLayer = $L; direction = 'PushOnly' } -TimeoutSec 900
                $rh2 = Rollup $h2
                $us1 = (Send-Bridge userstrings @{ objectId = $newId }).objects.$newId.$field
                Check 'push-only apply holds the ArcGIS edit' ($rh2['Held'] -eq 1 -and $us1 -eq $value) ("apply: {0}; Rhino {1}='{2}'" -f (Describe-Report $h2), $field, $us1) | Out-Null

                $a4 = Send-Bridge apply @{ arcgisLayer = $L } -TimeoutSec 900
                $us2 = (Send-Bridge userstrings @{ objectId = $newId }).objects.$newId.$field
                Check 'two-way apply pulls the ArcGIS value into Rhino' ($us2 -eq $value2) ("apply: {0}; Rhino {1}='{2}'" -f (Describe-Report $a4), $field, $us2) | Out-Null
                $p6c = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                Check 'preview after the ArcGIS attribute pull is clean' ($p6c.changed -eq 0) (Describe-Report $p6c) | Out-Null

                # ---- 11c. one real both-sides conflict through all three resolution policies
                if ($fixture -eq 'Polygons_Single_Buildings') {
                    $rhinoConflict = $value + '-rhino-conflict'
                    $arcConflict = $value + '-gis-conflict'
                    Send-Bridge setuserstring @{ objectId = $newId; key = $field; value = $rhinoConflict } | Out-Null
                    Send-Bridge setattribute @{ arcgisLayer = $L; objectId = $pushedOid; field = $field; value = $arcConflict } -TimeoutSec 600 | Out-Null

                    $pc1 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                    $rc1 = Rollup $pc1
                    Check 'both-side attribute edits preview as one conflict' `
                        ($pc1.changed -eq 1 -and $rc1['Conflict'] -eq 1) (Describe-Report $pc1) | Out-Null

                    $manual = Send-Bridge apply @{ arcgisLayer = $L; conflicts = 'Manual' } -TimeoutSec 900
                    $manualArc = (Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600).last[-1].attributes.$field
                    $manualRhino = (Send-Bridge userstrings @{ objectId = $newId }).objects.$newId.$field
                    Check 'manual conflict resolution holds both values' `
                        ((Rollup $manual)['Conflict'] -eq 1 -and $manualArc -eq $arcConflict -and $manualRhino -eq $rhinoConflict) `
                        ("ArcGIS='{0}', Rhino='{1}'" -f $manualArc, $manualRhino) | Out-Null

                    $preferRhino = Send-Bridge apply @{ arcgisLayer = $L; conflicts = 'PreferRhino' } -TimeoutSec 900
                    $preferRhinoArc = (Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600).last[-1].attributes.$field
                    Check 'prefer-Rhino resolves the conflict into ArcGIS' `
                        ((Rollup $preferRhino)['Updated'] -eq 1 -and $preferRhinoArc -eq $rhinoConflict) `
                        ("ArcGIS='{0}'" -f $preferRhinoArc) | Out-Null
                    $pc2 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                    Check 'preview after prefer-Rhino is clean' ($pc2.changed -eq 0) (Describe-Report $pc2) | Out-Null

                    $rhinoConflict2 = $value + '-rhino-second'
                    $arcConflict2 = $value + '-gis-second'
                    Send-Bridge setuserstring @{ objectId = $newId; key = $field; value = $rhinoConflict2 } | Out-Null
                    Send-Bridge setattribute @{ arcgisLayer = $L; objectId = $pushedOid; field = $field; value = $arcConflict2 } -TimeoutSec 600 | Out-Null
                    $preferArc = Send-Bridge apply @{ arcgisLayer = $L; conflicts = 'PreferArcGis' } -TimeoutSec 900
                    $preferArcRhino = (Send-Bridge userstrings @{ objectId = $newId }).objects.$newId.$field
                    Check 'prefer-ArcGIS resolves the conflict into Rhino' `
                        ((Rollup $preferArc)['Updated'] -eq 1 -and $preferArcRhino -eq $arcConflict2) `
                        ("Rhino='{0}'" -f $preferArcRhino) | Out-Null
                    $pc3 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
                    Check 'preview after prefer-ArcGIS is clean' ($pc3.changed -eq 0) (Describe-Report $pc3) | Out-Null
                }

                Send-Bridge attributeseditable @{ value = $false } | Out-Null
            }
        }

        # ---- 11d. the link table drives a whole-table run through the pane's own view model
        $all = Send-Bridge runall @{ apply = $false } -TimeoutSec 1800
        $row = @($all.table | Where-Object { $_.arcgisLayer -eq $L })
        Check 'preview-all reports this row in sync' ($row.Count -eq 1 -and $row[0].lastResult -match '^in sync') `
            ("rows: " + ((@($all.table) | ForEach-Object { "$($_.arcgisLayer): $($_.lastResult)" }) -join '; ')) | Out-Null

        # ---- 12-13. the pushed feature is deleted on the ArcGIS side
        Send-Bridge deletefeatures @{ arcgisLayer = $L; objectIds = @($pushedOid) } -TimeoutSec 600 | Out-Null
        $p7 = Send-Bridge preview @{ arcgisLayer = $L } -TimeoutSec 900
        $r7 = Rollup $p7
        Check 'deleted feature shows as Deleted in ArcGIS' ($p7.changed -eq 1 -and $r7['Deleted in ArcGIS'] -eq 1) (Describe-Report $p7) | Out-Null
        $a3 = Send-Bridge apply @{ arcgisLayer = $L } -TimeoutSec 900
        $after3 = Send-Bridge features @{ arcgisLayer = $L; take = 1 } -TimeoutSec 600
        Check 'apply holds the deletion and recreates nothing' ($after3.total -eq $N) ("apply: {0}; features now {1}" -f (Describe-Report $a3), $after3.total) | Out-Null

        # ---- 14. same layer name, different data underneath
        $alt = Join-Path $Scratch 'alt'
        New-Item -ItemType Directory -Force -Path $alt | Out-Null
        Get-ChildItem (Join-Path $Scratch "$fixture.*") | Copy-Item -Destination $alt -Force
        Send-Bridge removelayer @{ arcgisLayer = $L } | Out-Null
        $swapped = Send-Bridge addlayer @{ path = (Join-Path $alt "$fixture.shp") }
        $p8 = Send-Bridge preview @{ arcgisLayer = $swapped.name } -TimeoutSec 900
        $r8 = Rollup $p8
        $warn = @($p8.changedRows | Where-Object { $_.State -eq 'Warning' } | Select-Object -First 1)
        Check 'preview warns about a same-named layer over different data' ($r8['Warning'] -eq 1 -and $warn.Count -eq 1 -and $warn[0].Detail -match 'now reads from') (Describe-Report $p8) | Out-Null
        # The comparison still ran against the copy, which has the same features and ids.
        Check 'comparison against the copy still runs' ($p8.total -ge $N) (Describe-Report $p8) | Out-Null
        $newSource = (Send-Bridge features @{ arcgisLayer = $swapped.name; take = 1 } -TimeoutSec 600).source
        Send-Bridge apply @{ arcgisLayer = $swapped.name } -TimeoutSec 900 | Out-Null
        $p9 = Send-Bridge preview @{ arcgisLayer = $swapped.name } -TimeoutSec 900
        $r9 = Rollup $p9
        $sourceObjects = (Send-Bridge userstrings @{ layer = $L }).objects
        $completed = @($sourceObjects.PSObject.Properties | Where-Object {
            $_.Name -ne $newId -and $_.Value.'gis.arcgis_objectid'
        })
        $wrongSources = @($completed | Where-Object { $_.Value.'gis.arcgis_source' -ne $newSource })
        Check 'completed objects adopt the copied source' `
            ($newSource -and $newSource -ne $before.source -and $completed.Count -eq $N -and $wrongSources.Count -eq 0) `
            ("completed={0} wrong sources={1}; source={2}" -f $completed.Count, $wrongSources.Count, $newSource) | Out-Null
        $heldSource = $sourceObjects.$newId.'gis.arcgis_source'
        Check 'held deletion keeps its original source' ($heldSource -eq $before.source) $heldSource | Out-Null
        $remainingWarning = @($p9.changedRows | Where-Object { $_.State -eq 'Warning' })
        Check 'source warning remains only for the held deletion' `
            ($r9['Warning'] -eq 1 -and $r9['Deleted in ArcGIS'] -eq 1 -and $remainingWarning.Count -eq 1 -and $remainingWarning[0].Detail -match 'Source changed: 1 object') `
            (Describe-Report $p9) | Out-Null
    }
    catch {
        Check 'fixture ran without a bridge error' $false $_.Exception.Message | Out-Null
        Write-Host (Get-BridgeLog -Tail 15) -ForegroundColor DarkGray
        try { Send-Bridge attributeseditable @{ value = $false } | Out-Null } catch { }
    }
}

# ---------------------------------------------------------------- GlobalID beats a stale ObjectID

$script:CurrentFixture = '(GlobalID)'
Write-Host ""
Write-Host "=== GlobalID-first identity ===" -ForegroundColor Cyan
try {
    $globalIdLayerName = 'GlobalId_Point_Fixture'
    $globalIdRhinoLayer = "$globalIdLayerName Rhino"
    $globalIdGdb = Join-Path $Scratch 'globalid.gdb'
    $globalIdFixture = Send-Bridge prepareglobalidfixture @{
        sourcePath = (Join-Path $Scratch 'Point_Multi_Mixed.shp')
        gdbPath = $globalIdGdb
        featureClass = $globalIdLayerName
    } -TimeoutSec 900
    $globalIdLayer = $globalIdFixture.layer.name
    Check 'temporary geodatabase layer added' `
        ($globalIdFixture.layer.added -and $globalIdLayer -eq $globalIdLayerName) `
        $globalIdFixture.path | Out-Null

    $globalIdSchema = Send-Bridge schema @{ arcgisLayer = $globalIdLayer }
    Check 'geodatabase fixture has GlobalIDs' `
        ([bool]$globalIdSchema.HasGlobalIds) `
        (($globalIdSchema.fields | ForEach-Object { $_.Name }) -join ', ') | Out-Null

    $globalIdBefore = Send-Bridge features @{ arcgisLayer = $globalIdLayer; take = 20 } -TimeoutSec 600
    $globalIdPull = Send-Bridge pull @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    $globalIdObjects = (Send-Bridge userstrings @{ layer = $globalIdRhinoLayer }).objects
    $globalIdObjectProperties = @($globalIdObjects.PSObject.Properties | Where-Object {
            $_.Value.'gis.arcgis_globalid' -and $_.Value.'gis.arcgis_objectid'
        })
    Check 'pull records GlobalID and ObjectID on Rhino objects' `
        ($globalIdPull.total -eq $globalIdBefore.total -and $globalIdObjectProperties.Count -eq $globalIdBefore.total) `
        ("features={0}, identified Rhino objects={1}" -f $globalIdBefore.total, $globalIdObjectProperties.Count) | Out-Null

    if ($globalIdObjectProperties.Count -lt 2) {
        throw 'The GlobalID fixture needs at least two identified objects to stage an ObjectID collision.'
    }

    $target = $globalIdObjectProperties[0]
    $targetId = $target.Name
    $originalOid = [long]$target.Value.'gis.arcgis_objectid'
    $wrongOid = [long]$globalIdObjectProperties[1].Value.'gis.arcgis_objectid'
    $targetGlobalId = [string]$target.Value.'gis.arcgis_globalid'
    Send-Bridge setuserstring @{
        objectId = $targetId
        key = 'gis.arcgis_objectid'
        value = [string]$wrongOid
    } | Out-Null

    $globalIdClean = Send-Bridge preview @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    Check 'GlobalID match ignores the deliberately wrong ObjectID' `
        ($globalIdClean.changed -eq 0 -and $globalIdClean.total -eq $globalIdBefore.total) `
        ("GlobalID={0}; original OID={1}; staged OID={2}; {3}" -f $targetGlobalId, $originalOid, $wrongOid, (Describe-Report $globalIdClean)) | Out-Null

    Send-Bridge moveobject @{ objectId = $targetId; x = 1.0; y = 2.0; z = 0.0 } | Out-Null
    $globalIdEdited = Send-Bridge preview @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    Check 'edit with stale ObjectID still targets one feature by GlobalID' `
        ($globalIdEdited.changed -eq 1 -and (Rollup $globalIdEdited)['Modified in Rhino'] -eq 1) `
        (Describe-Report $globalIdEdited) | Out-Null

    $globalIdApplied = Send-Bridge apply @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    $repairedIdentity = (Send-Bridge userstrings @{ objectId = $targetId }).objects.$targetId
    $globalIdAfter = Send-Bridge features @{ arcgisLayer = $globalIdLayer; take = 20 } -TimeoutSec 600
    Check 'apply updates the matched feature and repairs the Rhino ObjectID' `
        ((Rollup $globalIdApplied)['Updated'] -eq 1 -and
         $globalIdAfter.total -eq $globalIdBefore.total -and
         [long]$repairedIdentity.'gis.arcgis_objectid' -eq $originalOid) `
        ("apply: {0}; repaired OID={1}; features={2}" -f (Describe-Report $globalIdApplied), $repairedIdentity.'gis.arcgis_objectid', $globalIdAfter.total) | Out-Null

    $globalIdFinal = Send-Bridge preview @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    Check 'GlobalID scenario finishes clean' ($globalIdFinal.changed -eq 0) (Describe-Report $globalIdFinal) | Out-Null

    # The reverse geometry path must replace the same Rhino object; attribute-only pull coverage
    # is not evidence that production geometry replacement works.
    $gisEditBefore = Send-Bridge objectgeometry @{ objectId = $targetId }
    Send-Bridge editfeaturegeometry @{
        arcgisLayer = $globalIdLayer
        objectId = $originalOid
        dx = 3.0
        dy = -2.0
    } | Out-Null
    $gisEditedPreview = Send-Bridge preview @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    Check 'GIS geometry edit is detected as Modified in ArcGIS' `
        ($gisEditedPreview.changed -eq 1 -and (Rollup $gisEditedPreview)['Modified in ArcGIS'] -eq 1) `
        (Describe-Report $gisEditedPreview) | Out-Null

    $gisEditedApply = Send-Bridge apply @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    $gisEditAfter = Send-Bridge objectgeometry @{ objectId = $targetId }
    $sameIdentity = (Send-Bridge userstrings @{ objectId = $targetId }).objects.PSObject.Properties.Name -contains $targetId
    Check 'GIS geometry replaces the same Rhino object identity' `
        ((Rollup $gisEditedApply)['Updated'] -eq 1 -and $sameIdentity -and
         [Math]::Abs([double]$gisEditAfter.XMin - [double]$gisEditBefore.XMin) -gt 1e-6) `
        ("Rhino object={0}; XMin {1} -> {2}; {3}" -f $targetId, $gisEditBefore.XMin, $gisEditAfter.XMin, (Describe-Report $gisEditedApply)) | Out-Null
    $gisEditClean = Send-Bridge preview @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    Check 'GIS geometry replacement finishes clean' ($gisEditClean.changed -eq 0) (Describe-Report $gisEditClean) | Out-Null

    # A protected Rhino object must fail before mutation and keep its geometry and baseline. After
    # unlocking, the same queued GIS change must be recoverable through an ordinary apply.
    $lockedGeometryBefore = Send-Bridge objectgeometry @{ objectId = $targetId }
    $lockedStringsBefore = (Send-Bridge userstrings @{ objectId = $targetId }).objects.$targetId
    Send-Bridge setobjectlocked @{ objectId = $targetId; locked = $true } | Out-Null
    try {
        Send-Bridge editfeaturegeometry @{
            arcgisLayer = $globalIdLayer
            objectId = $originalOid
            dx = 4.0
            dy = 1.0
        } | Out-Null
        $lockedPreview = Send-Bridge preview @{
            arcgisLayer = $globalIdLayer
            rhinoLayer = $globalIdRhinoLayer
        } -TimeoutSec 900
        $lockedApply = Send-Bridge apply @{
            arcgisLayer = $globalIdLayer
            rhinoLayer = $globalIdRhinoLayer
        } -TimeoutSec 900
        $lockedGeometryAfter = Send-Bridge objectgeometry @{ objectId = $targetId }
        $lockedStringsAfter = (Send-Bridge userstrings @{ objectId = $targetId }).objects.$targetId
        Check 'locked Rhino replacement reports one failure' `
            ((Rollup $lockedApply)['Failed'] -eq 1 -and $lockedPreview.changed -eq 1) `
            (Describe-Report $lockedApply) | Out-Null
        Check 'locked Rhino failure preserves geometry and baseline' `
            ([Math]::Abs([double]$lockedGeometryAfter.XMin - [double]$lockedGeometryBefore.XMin) -lt 1e-9 -and
             [string]$lockedStringsAfter.'gis.geometry_hash' -eq [string]$lockedStringsBefore.'gis.geometry_hash') `
            ("XMin={0}; baseline={1}" -f $lockedGeometryAfter.XMin, $lockedStringsAfter.'gis.geometry_hash') | Out-Null
    }
    finally {
        Send-Bridge setobjectlocked @{ objectId = $targetId; locked = $false } | Out-Null
    }
    $unlockedApply = Send-Bridge apply @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    $unlockedFinal = Send-Bridge preview @{
        arcgisLayer = $globalIdLayer
        rhinoLayer = $globalIdRhinoLayer
    } -TimeoutSec 900
    Check 'unlock recovers the held GIS geometry change' `
        ((Rollup $unlockedApply)['Updated'] -eq 1 -and $unlockedFinal.changed -eq 0) `
        (Describe-Report $unlockedFinal) | Out-Null

    Send-Bridge removelink @{ arcgisLayer = $globalIdLayer } | Out-Null
    Send-Bridge removelayer @{ arcgisLayer = $globalIdLayer } | Out-Null
}
catch {
    Check 'GlobalID scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 20) -ForegroundColor DarkGray
    try { Send-Bridge removelink @{ arcgisLayer = $globalIdLayerName } | Out-Null } catch { }
    try { Send-Bridge removelayer @{ arcgisLayer = $globalIdLayerName } | Out-Null } catch { }
}

# ---------------------------------------------------------------- multipart GIS geometry collapse

$script:CurrentFixture = '(GlobalID multipart reverse geometry)'
Write-Host ""
Write-Host "=== GlobalID multipart GIS-to-Rhino replacement ===" -ForegroundColor Cyan
try {
    $multipartLayerName = 'GlobalId_Multipart_Polygon'
    $multipartRhinoLayer = "$multipartLayerName Rhino"
    $multipartFixture = Send-Bridge prepareglobalidfixture @{
        sourcePath = (Join-Path $Scratch 'Boundary_Multipart_Polygon.shp')
        gdbPath = (Join-Path $Scratch 'globalid-multipart.gdb')
        featureClass = $multipartLayerName
    } -TimeoutSec 900
    $multipartLayer = $multipartFixture.layer.name
    $multipartPull = Send-Bridge pull @{
        arcgisLayer = $multipartLayer
        rhinoLayer = $multipartRhinoLayer
    } -TimeoutSec 900
    $multipartObjects = (Send-Bridge userstrings @{ layer = $multipartRhinoLayer }).objects
    $multipartTarget = @($multipartObjects.PSObject.Properties | Where-Object {
            $_.Value.'gis.arcgis_globalid' -and $_.Value.'gis.arcgis_objectid'
        }) | Select-Object -First 1
    if (-not $multipartTarget) { throw 'No GlobalID-tracked multipart representative was pulled.' }
    $multipartId = $multipartTarget.Name
    $multipartOid = [long]$multipartTarget.Value.'gis.arcgis_objectid'
    $multipartEdit = Send-Bridge editfeaturegeometry @{
        arcgisLayer = $multipartLayer
        objectId = $multipartOid
        singlePart = $true
        partIndex = 0
        dx = 2.0
        dy = 2.0
    }
    Check 'GIS fixture collapsed from multipart to one part' `
        ($multipartEdit.beforePartCount -gt 1 -and $multipartEdit.afterPartCount -eq 1) `
        ("parts {0} -> {1}" -f $multipartEdit.beforePartCount, $multipartEdit.afterPartCount) | Out-Null
    $multipartPreview = Send-Bridge preview @{
        arcgisLayer = $multipartLayer
        rhinoLayer = $multipartRhinoLayer
    } -TimeoutSec 900
    $multipartApply = Send-Bridge apply @{
        arcgisLayer = $multipartLayer
        rhinoLayer = $multipartRhinoLayer
    } -TimeoutSec 900
    $multipartFinal = Send-Bridge preview @{
        arcgisLayer = $multipartLayer
        rhinoLayer = $multipartRhinoLayer
    } -TimeoutSec 900
    $multipartIdentityStillExists = (Send-Bridge userstrings @{ objectId = $multipartId }).objects.PSObject.Properties.Name -contains $multipartId
    Check 'multipart-to-single replacement retains Rhino identity' `
        ((Rollup $multipartPreview)['Modified in ArcGIS'] -eq 1 -and
         (Rollup $multipartApply)['Updated'] -eq 1 -and
         $multipartIdentityStillExists -and $multipartFinal.changed -eq 0) `
        ("Rhino object={0}; {1}" -f $multipartId, (Describe-Report $multipartFinal)) | Out-Null
    Send-Bridge removelink @{ arcgisLayer = $multipartLayer } | Out-Null
    Send-Bridge removelayer @{ arcgisLayer = $multipartLayer } | Out-Null
}
catch {
    Check 'multipart reverse-geometry scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 20) -ForegroundColor DarkGray
    try { Send-Bridge removelink @{ arcgisLayer = $multipartLayerName } | Out-Null } catch { }
    try { Send-Bridge removelayer @{ arcgisLayer = $multipartLayerName } | Out-Null } catch { }
}

# ---------------------------------------------------------------- new feature class from Rhino

$script:CurrentFixture = '(new ArcGIS layer from Rhino)'
Write-Host ""
Write-Host "=== new ArcGIS layer from Rhino ===" -ForegroundColor Cyan
$createdArcGisLayer = $null
$originalDefaultGdb = $null
try {
    $originalDefaultGdb = (Send-Bridge defaultgdb).path
    $newLayerGdb = Join-Path $scratch 'new-layer-e2e.gdb'
    Send-Bridge setdefaultgdb @{ path = $newLayerGdb } | Out-Null

    $authoredRhinoLayer = '__E2E_Rhino_Authored_Lines'
    $line1 = (Send-Bridge addobject @{
        layer = $authoredRhinoLayer
        kind = 'line'
        points = @(@(5.0, 5.0), @(25.0, 10.0), @(45.0, 5.0))
    }).id
    $line2 = (Send-Bridge addobject @{
        layer = $authoredRhinoLayer
        kind = 'line'
        points = @(@(5.0, 20.0), @(25.0, 25.0), @(45.0, 20.0))
    }).id
    Send-Bridge setuserstring @{ objectId = $line1; key = 'note'; value = 'first line' } | Out-Null
    Send-Bridge setuserstring @{ objectId = $line1; key = 'rank'; value = '1' } | Out-Null
    Send-Bridge setuserstring @{ objectId = $line2; key = 'note'; value = 'second line' } | Out-Null
    Send-Bridge setuserstring @{ objectId = $line2; key = 'rank'; value = '2' } | Out-Null

    $newLayer = Send-Bridge newlayer @{
        rhinoLayer = $authoredRhinoLayer
        name = 'E2E_Rhino_Authored_Lines'
    } -TimeoutSec 900
    $createdArcGisLayer = $newLayer.layer
    Check 'new feature class is inferred and added to the active map' `
        ([bool]$createdArcGisLayer -and $newLayer.target -eq 'PolylineZ' -and
         $newLayer.ObjectCount -eq 2 -and $newLayer.MatchingCount -eq 2) `
        ("layer={0}, target={1}, matching={2}/{3}" -f $createdArcGisLayer, $newLayer.target,
            $newLayer.MatchingCount, $newLayer.ObjectCount) | Out-Null

    $newSchema = Send-Bridge schema @{ arcgisLayer = $createdArcGisLayer }
    $noteField = @($newSchema.fields | Where-Object { $_.Name -eq 'note' } | Select-Object -First 1)
    $rankField = @($newSchema.fields | Where-Object { $_.Name -eq 'rank' } | Select-Object -First 1)
    Check 'inferred text and integer fields exist in the new schema' `
        ($noteField.Count -eq 1 -and $noteField[0].type -eq 'Text' -and
         $rankField.Count -eq 1 -and $rankField[0].type -eq 'Integer') `
        (($newSchema.fields | ForEach-Object { "$($_.Name):$($_.type)" }) -join ', ') | Out-Null
    Check 'new feature class has durable GlobalIDs' ([bool]$newSchema.HasGlobalIds) `
        (($newSchema.fields | ForEach-Object { $_.Name }) -join ', ') | Out-Null

    $newFeatures = Send-Bridge features @{ arcgisLayer = $createdArcGisLayer; take = 10 }
    $notes = @($newFeatures.last | ForEach-Object { $_.attributes.note } | Sort-Object)
    $ranks = @($newFeatures.last | ForEach-Object { [string]$_.attributes.rank } | Sort-Object)
    Check 'initial push creates both polylines with their user text' `
        ($newFeatures.total -eq 2 -and $newFeatures.kinds.Polyline -eq 2 -and
         ($notes -join ',') -eq 'first line,second line' -and ($ranks -join ',') -eq '1,2') `
        ("total={0}; kinds={1}; notes={2}; ranks={3}" -f $newFeatures.total,
            ($newFeatures.kinds | ConvertTo-Json -Compress), ($notes -join ','), ($ranks -join ',')) | Out-Null

    $newObjectStrings = (Send-Bridge userstrings @{ layer = $authoredRhinoLayer }).objects
    $identified = @($newObjectStrings.PSObject.Properties | Where-Object {
        $_.Value.'gis.sync_guid' -and $_.Value.'gis.arcgis_objectid' -and $_.Value.'gis.arcgis_globalid'
    }).Count
    Check 'initial push writes ObjectID and GlobalID identity back to Rhino' ($identified -eq 2) `
        "identified Rhino objects=$identified" | Out-Null

    $newPreview = Send-Bridge preview @{
        arcgisLayer = $createdArcGisLayer
        rhinoLayer = $authoredRhinoLayer
    } -TimeoutSec 900
    Check 'newly created link previews clean' ($newPreview.total -eq 2 -and $newPreview.changed -eq 0) `
        (Describe-Report $newPreview) | Out-Null

    # Author the field rule through the pane's editor/save method while the global compatibility
    # toggle remains off. The saved per-link profile, not test-only global state, must make the
    # Rhino-owned field writable on the next coordinator run.
    $profileResult = Send-Bridge configureprofile @{
        field = 'note'
        rhinoKey = 'note'
        owner = 'RhinoOwned'
        included = $true
    } -TimeoutSec 900
    $profileLinks = Send-Bridge links
    $profileStored = @($profileLinks.stored | Where-Object { $_.arcgisLayer -eq $createdArcGisLayer })
    Check 'profile editor saves field ownership on the active link' `
        ($profileResult.custom -and $profileResult.owner -eq 'RhinoOwned' -and
         $profileResult.field -eq 'note' -and $profileResult.rhinoKey -eq 'note' -and
         $profileStored.Count -eq 1 -and $profileStored[0].profile -eq 'Custom') `
        ("owner={0}, key={1}, stored={2}" -f $profileResult.owner, $profileResult.rhinoKey,
            $(if ($profileStored.Count) { $profileStored[0].profile } else { 'missing' })) | Out-Null

    $profileBaseline = Send-Bridge preview @{
        arcgisLayer = $createdArcGisLayer
        rhinoLayer = $authoredRhinoLayer
    } -TimeoutSec 900
    Check 'ownership-only profile change keeps the existing link clean' `
        ($profileBaseline.total -eq 2 -and $profileBaseline.changed -eq 0) `
        (Describe-Report $profileBaseline) | Out-Null

    $line1Entry = @($newObjectStrings.PSObject.Properties | Where-Object { $_.Name -eq $line1 } | Select-Object -First 1)
    $line1Oid = if ($line1Entry.Count) { [long]$line1Entry[0].Value.'gis.arcgis_objectid' } else { 0 }
    Send-Bridge setuserstring @{ objectId = $line1; key = 'note'; value = 'profile-authored note' } | Out-Null
    $profilePreview = Send-Bridge preview @{
        arcgisLayer = $createdArcGisLayer
        rhinoLayer = $authoredRhinoLayer
    } -TimeoutSec 900
    $profilePreviewRollup = Rollup $profilePreview
    Check 'saved Rhino-owned field rule detects the Rhino edit' `
        ($profilePreview.changed -eq 1 -and $profilePreviewRollup['Modified in Rhino'] -eq 1) `
        (Describe-Report $profilePreview) | Out-Null

    $profileApply = Send-Bridge apply @{
        arcgisLayer = $createdArcGisLayer
        rhinoLayer = $authoredRhinoLayer
    } -TimeoutSec 900
    $profileFeatures = Send-Bridge features @{ arcgisLayer = $createdArcGisLayer; take = 10 }
    $profileFeature = @($profileFeatures.last | Where-Object { [long]$_.objectId -eq $line1Oid } | Select-Object -First 1)
    $profileFinal = Send-Bridge preview @{
        arcgisLayer = $createdArcGisLayer
        rhinoLayer = $authoredRhinoLayer
    } -TimeoutSec 900
    Check 'saved Rhino-owned field rule writes the existing ArcGIS row and re-baselines' `
        ((Rollup $profileApply)['Updated'] -eq 1 -and $profileFeatures.total -eq 2 -and
         $profileFeature.Count -eq 1 -and $profileFeature[0].attributes.note -eq 'profile-authored note' -and
         $profileFinal.changed -eq 0) `
        ("oid={0}, note={1}; {2}" -f $line1Oid,
            $(if ($profileFeature.Count) { $profileFeature[0].attributes.note } else { '(missing)' }),
            (Describe-Report $profileFinal)) | Out-Null
}
catch {
    Check 'new-layer scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 20) -ForegroundColor DarkGray
}
finally {
    if ($createdArcGisLayer) {
        try {
            # CreateFeatures uses ArcGIS EditOperation. Schema deletion is correctly rejected while
            # edits are pending, so commit this disposable GDB before dropping its feature class.
            Send-Bridge saveedits | Out-Null
            $drop = Send-Bridge droplayer @{ arcgisLayer = $createdArcGisLayer } -TimeoutSec 900
            $remainingLayers = (Send-Bridge arcgislayers).layers
            Check 'new feature class and link are removed after the test' `
                ($drop.dropped -and $createdArcGisLayer -notin $remainingLayers) `
                ("dropped={0}; still in map={1}" -f $drop.dropped, ($createdArcGisLayer -in $remainingLayers)) | Out-Null
        }
        catch {
            Check 'new feature class cleanup succeeds' $false $_.Exception.Message | Out-Null
        }
    }
    if ($originalDefaultGdb) {
        try { Send-Bridge setdefaultgdb @{ path = $originalDefaultGdb } | Out-Null }
        catch { Check 'default geodatabase is restored after new-layer test' $false $_.Exception.Message | Out-Null }
    }
}

# ---------------------------------------------------------------- new Multipatch feature class from Rhino

$script:CurrentFixture = '(new Multipatch layer from Rhino)'
Write-Host ""
Write-Host "=== new Multipatch layer from Rhino ===" -ForegroundColor Cyan
$createdMultipatchLayer = $null
$multipatchDefaultGdb = $null
try {
    $multipatchDefaultGdb = (Send-Bridge defaultgdb).path
    $newMultipatchGdb = Join-Path $scratch 'new-multipatch-e2e.gdb'
    Send-Bridge setdefaultgdb @{ path = $newMultipatchGdb } | Out-Null

    $authoredMultipatchRhinoLayer = '__E2E_Rhino_Authored_Multipatch'
    $authoredMesh = (Send-Bridge addobject @{
        layer = $authoredMultipatchRhinoLayer
        kind = 'mesh'
        points = @(@(0.0, 0.0, 24.0), @(24.0, 0.0, 0.0), @(24.0, 16.0, 0.0), @(0.0, 16.0, 0.0))
    }).id
    Send-Bridge setuserstring @{ objectId = $authoredMesh; key = 'model_name'; value = 'Rhino box' } | Out-Null

    $newMultipatch = Send-Bridge newlayer @{
        rhinoLayer = $authoredMultipatchRhinoLayer
        name = 'E2E_Rhino_Authored_Multipatch'
    } -TimeoutSec 900
    $createdMultipatchLayer = $newMultipatch.layer
    Check 'Rhino mesh creates and links a Multipatch feature class' `
        ([bool]$createdMultipatchLayer -and $newMultipatch.target -eq 'Multipatch' -and
         $newMultipatch.ObjectCount -eq 1 -and $newMultipatch.MatchingCount -eq 1) `
        ("layer={0}, target={1}, matching={2}/{3}" -f $createdMultipatchLayer,
            $newMultipatch.target, $newMultipatch.MatchingCount, $newMultipatch.ObjectCount) | Out-Null

    $multipatchSchema = Send-Bridge schema @{ arcgisLayer = $createdMultipatchLayer }
    $modelNameField = @($multipatchSchema.fields | Where-Object { $_.Name -eq 'model_name' } | Select-Object -First 1)
    Check 'new Multipatch schema is Z-enabled with inferred fields and GlobalIDs' `
        ($multipatchSchema.geometry -eq 'Multipatch' -and $multipatchSchema.ZEnabled -and
         $multipatchSchema.HasGlobalIds -and $modelNameField.Count -eq 1 -and
         $modelNameField[0].type -eq 'Text') `
        ("geometry={0}, Z={1}, GlobalIDs={2}, fields={3}" -f $multipatchSchema.geometry,
            $multipatchSchema.ZEnabled, $multipatchSchema.HasGlobalIds,
            (($multipatchSchema.fields | ForEach-Object { "$($_.Name):$($_.type)" }) -join ', ')) | Out-Null

    $multipatchFeatures = Send-Bridge features @{ arcgisLayer = $createdMultipatchLayer; take = 5 }
    $multipatchFeature = @($multipatchFeatures.last | Select-Object -First 1)
    Check 'initial Multipatch push preserves 3D mesh topology and user text' `
        ($multipatchFeatures.total -eq 1 -and $multipatchFeatures.kinds.Multipatch -eq 1 -and
         $multipatchFeature.Count -eq 1 -and
         ([double]$multipatchFeature[0].zMax - [double]$multipatchFeature[0].zMin) -gt 0 -and
         $multipatchFeature[0].faceCount -gt 0 -and $multipatchFeature[0].sharedVertices -and
         $multipatchFeature[0].attributes.model_name -eq 'Rhino box') `
        ("total={0}, Z={1}..{2}, vertices={3}, faces={4}, shared={5}, model={6}" -f
            $multipatchFeatures.total, $multipatchFeature[0].zMin, $multipatchFeature[0].zMax,
            $multipatchFeature[0].vertexCount, $multipatchFeature[0].faceCount,
            $multipatchFeature[0].sharedVertices, $multipatchFeature[0].attributes.model_name) | Out-Null

    $multipatchStrings = (Send-Bridge userstrings @{ objectId = $authoredMesh }).objects
    $multipatchIdentity = @($multipatchStrings.PSObject.Properties | Where-Object { $_.Name -eq $authoredMesh } | Select-Object -First 1)
    $multipatchPreview = Send-Bridge preview @{
        arcgisLayer = $createdMultipatchLayer
        rhinoLayer = $authoredMultipatchRhinoLayer
    } -TimeoutSec 900
    Check 'new Multipatch carries durable identity and previews clean' `
        ($multipatchIdentity.Count -eq 1 -and $multipatchIdentity[0].Value.'gis.sync_guid' -and
         $multipatchIdentity[0].Value.'gis.arcgis_objectid' -and
         $multipatchIdentity[0].Value.'gis.arcgis_globalid' -and
         $multipatchPreview.total -eq 1 -and $multipatchPreview.changed -eq 0) `
        (Describe-Report $multipatchPreview) | Out-Null
}
catch {
    Check 'new-Multipatch scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 20) -ForegroundColor DarkGray
}
finally {
    if ($createdMultipatchLayer) {
        try {
            Send-Bridge saveedits | Out-Null
            $dropMultipatch = Send-Bridge droplayer @{ arcgisLayer = $createdMultipatchLayer } -TimeoutSec 900
            $remainingMultipatchLayers = (Send-Bridge arcgislayers).layers
            Check 'new Multipatch feature class and link are removed after the test' `
                ($dropMultipatch.dropped -and $createdMultipatchLayer -notin $remainingMultipatchLayers) `
                ("dropped={0}; still in map={1}" -f $dropMultipatch.dropped,
                    ($createdMultipatchLayer -in $remainingMultipatchLayers)) | Out-Null
        }
        catch {
            Check 'new Multipatch feature class cleanup succeeds' $false $_.Exception.Message | Out-Null
        }
    }
    if ($multipatchDefaultGdb) {
        try { Send-Bridge setdefaultgdb @{ path = $multipatchDefaultGdb } | Out-Null }
        catch { Check 'default geodatabase is restored after new-Multipatch test' $false $_.Exception.Message | Out-Null }
    }
}

# ---------------------------------------------------------------- a failed bulk row is isolated

$script:CurrentFixture = '(bulk)'
Write-Host ""
Write-Host "=== bulk failure isolation ===" -ForegroundColor Cyan
try {
    $missingLayer = '__E2E_Missing_Layer__'
    Send-Bridge setlink @{ arcgisLayer = $missingLayer; rhinoLayer = $missingLayer; direction = 'TwoWay' } | Out-Null
    $isolated = Send-Bridge runall @{ apply = $false } -TimeoutSec 1800
    $failedRows = @($isolated.table | Where-Object { $_.lastResult -match '^failed:' })
    $healthyRows = @($isolated.table | Where-Object {
            $_.arcgisLayer -ne $missingLayer -and $_.lastResult -and $_.lastResult -notmatch '^failed:' })

    Check 'preview-all marks only the broken link as failed' `
        ($failedRows.Count -eq 1 -and $failedRows[0].arcgisLayer -eq $missingLayer) `
        ("failed rows: " + (($failedRows | ForEach-Object { "$($_.arcgisLayer): $($_.lastResult)" }) -join '; ')) | Out-Null
    Check 'preview-all preserves results for every healthy link' `
        ($healthyRows.Count -eq $Fixtures.Count) `
        ("healthy rows with results: {0} of {1}" -f $healthyRows.Count, $Fixtures.Count) | Out-Null

    $removedMissing = Send-Bridge removelink @{ arcgisLayer = $missingLayer }
    Check 'the temporary broken link is removed without disturbing the real links' `
        ($removedMissing.removed -and @($removedMissing.links.table).Count -eq $Fixtures.Count) `
        ("removed={0}, links={1}" -f $removedMissing.removed, @($removedMissing.links.table).Count) | Out-Null
}
catch {
    Check 'bulk failure-isolation scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 15) -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- a mixed working session

# What two people (or one person between two apps) actually do between syncs: move, retype,
# delete, copy and draw in Rhino while ArcGIS gets attribute and geometry edits -- all before one
# preview. Then the same object is edited back and forth, a Rhino delete is restored by Pull, and
# a preview runs with an attribute table (not a map) as ArcGIS's active pane.

function Find-TextField($schema, [int]$MinLength = 12) {
    @($schema.fields | Where-Object {
            $_.type -eq 'Text' -and [int]$_.Length -ge $MinLength -and
            $_.Name -notmatch '^(FID|OBJECTID|OID|GLOBALID|Shape)' } | Select-Object -First 1).Name
}

function Copy-Fixture([string]$Fixture, [string]$NewName, [string]$Folder) {
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    Get-ChildItem (Join-Path $src "$Fixture.*") | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $Folder ($NewName + $_.Extension)) -Force
    }
    return (Join-Path $Folder "$NewName.shp")
}

$script:CurrentFixture = '(range)'
Write-Host ""
Write-Host "=== mixed edits on both sides ===" -ForegroundColor Cyan
$rangeLayer = $null
try {
    $rangeShp = Copy-Fixture 'Point_Multi_Mixed' 'Range_Points' (Join-Path $Scratch 'range')
    $rangeLayer = (Send-Bridge addlayer @{ path = $rangeShp }).name
    $R = $rangeLayer
    $rangeBefore = Send-Bridge features @{ arcgisLayer = $R; take = 50 } -TimeoutSec 600
    $RN = [int]$rangeBefore.total
    Send-Bridge pull @{ arcgisLayer = $R } -TimeoutSec 900 | Out-Null
    Send-Bridge setlink @{ arcgisLayer = $R; direction = 'TwoWay' } | Out-Null
    Send-Bridge apply @{ arcgisLayer = $R } -TimeoutSec 900 | Out-Null
    $rangeClean = Send-Bridge preview @{ arcgisLayer = $R } -TimeoutSec 900
    Check 'range layer starts clean' ($RN -ge 6 -and $rangeClean.changed -eq 0 -and $rangeClean.total -eq $RN) (Describe-Report $rangeClean) | Out-Null

    $again = Send-Bridge pull @{ arcgisLayer = $R } -TimeoutSec 900
    $againCounts = [int](Send-Bridge objectcounts @{ layer = $R }).counts.'(total)'
    Check 'pulling a pulled layer again creates nothing' `
        ((Rollup $again)['Skipped'] -eq $RN -and -not (Rollup $again).ContainsKey('Created') -and $againCounts -eq $RN) `
        ("pull: {0}; Rhino objects {1}" -f (Describe-Report $again), $againCounts) | Out-Null

    $field = Find-TextField (Send-Bridge schema @{ arcgisLayer = $R })
    $objs = @((Send-Bridge userstrings @{ layer = $R }).objects.PSObject.Properties |
        Where-Object { $_.Value.'gis.arcgis_objectid' } |
        Sort-Object { [long]$_.Value.'gis.arcgis_objectid' })
    $ids = @($objs | ForEach-Object { $_.Name })
    $oids = @($objs | ForEach-Object { [long]$_.Value.'gis.arcgis_objectid' })
    Send-Bridge attributeseditable @{ value = $true } | Out-Null

    # Rhino side
    Send-Bridge moveobject @{ objectId = $ids[0]; x = 3.0; y = 0.0; z = 0.0 } | Out-Null                    # A: moved
    Send-Bridge setuserstring @{ objectId = $ids[1]; key = $field; value = 'range-rhino' } | Out-Null      # B: retyped
    Send-Bridge deleteobject @{ objectId = $ids[2] } | Out-Null                                             # C: deleted
    $copyId = (Send-Bridge duplicateobject @{ objectId = $ids[5]; dx = 40.0 }).id                          # D: copied
    $newId = (Send-Bridge addobject @{ layer = $R; kind = 'point'; points = @(,@(-25.0, -15.0)) }).id       # E: drawn
    # ArcGIS side
    Send-Bridge setattribute @{ arcgisLayer = $R; objectId = $oids[3]; field = $field; value = 'range-gis' } -TimeoutSec 600 | Out-Null  # F
    Send-Bridge editfeaturegeometry @{ arcgisLayer = $R; objectId = $oids[4]; dx = 2.0; dy = -1.0 } | Out-Null                         # G

    $mixed = Send-Bridge preview @{ arcgisLayer = $R } -TimeoutSec 900
    $rm = Rollup $mixed
    Check 'one preview sorts every kind of edit correctly' `
        ($rm['Modified in Rhino'] -eq 2 -and $rm['Modified in ArcGIS'] -eq 2 -and $rm['Deleted in Rhino'] -eq 1 -and
         $rm['New in Rhino'] -eq 2 -and $rm['Clean'] -eq ($RN - 5) -and -not $rm.ContainsKey('Deleted in ArcGIS') -and
         -not $rm.ContainsKey('New in ArcGIS') -and -not $rm.ContainsKey('Conflict')) `
        (Describe-Report $mixed) | Out-Null
    $copyRow = @($mixed.changedRows | Where-Object { $_.Summary -match "Copy of a tracked object" })
    Check 'the copied object is labelled as a copy, not a deletion' ($copyRow.Count -eq 1) `
        ("rows: " + ((@($mixed.changedRows) | ForEach-Object { "$($_.State): $($_.Summary)" }) -join ' | ')) | Out-Null

    $mixedApply = Send-Bridge apply @{ arcgisLayer = $R } -TimeoutSec 900
    $afterMixed = Send-Bridge features @{ arcgisLayer = $R; take = 50 } -TimeoutSec 600
    $rhinoAfterMixed = [int](Send-Bridge objectcounts @{ layer = $R }).counts.'(total)'
    $bValue = @($afterMixed.last | Where-Object { [long]$_.objectId -eq $oids[1] })[0].attributes.$field
    $fValue = (Send-Bridge userstrings @{ objectId = $ids[3] }).objects.($ids[3]).$field
    Check 'one apply settles both sides without losing or duplicating anything' `
        ($afterMixed.total -eq ($RN + 2) -and $rhinoAfterMixed -eq ($RN + 1) -and $bValue -eq 'range-rhino' -and $fValue -eq 'range-gis') `
        ("apply: {0}; ArcGIS {1} (expect {2}); Rhino {3} (expect {4}); B='{5}' F='{6}'" -f (Describe-Report $mixedApply),
            $afterMixed.total, ($RN + 2), $rhinoAfterMixed, ($RN + 1), $bValue, $fValue) | Out-Null

    $stable = $true; $stableDetail = @()
    for ($round = 1; $round -le 3; $round++) {
        $pr = Send-Bridge preview @{ arcgisLayer = $R } -TimeoutSec 900
        $rr = Rollup $pr
        Send-Bridge apply @{ arcgisLayer = $R } -TimeoutSec 900 | Out-Null
        $count = [int](Send-Bridge features @{ arcgisLayer = $R; take = 1 } -TimeoutSec 600).total
        $ok = ($rr['Clean'] -eq ($RN + 1) -and $rr['Deleted in Rhino'] -eq 1 -and $pr.changed -eq 1 -and $count -eq ($RN + 2))
        $stable = $stable -and $ok
        $stableDetail += "round ${round}: $(Describe-Report $pr); features=$count"
    }
    Check 'repeated preview/apply rounds change nothing further' $stable ($stableDetail -join ' | ') | Out-Null

    $restore = Send-Bridge pull @{ arcgisLayer = $R } -TimeoutSec 900
    $restored = Send-Bridge preview @{ arcgisLayer = $R } -TimeoutSec 900
    Check 'Pull restores just the object deleted in Rhino' `
        ((Rollup $restore)['Created'] -eq 1 -and $restored.changed -eq 0 -and $restored.total -eq ($RN + 2)) `
        ("pull: {0}; then {1}" -f (Describe-Report $restore), (Describe-Report $restored)) | Out-Null

    # Back and forth on one object, the way a designer and a GIS editor trade a value.
    $pingOk = $true; $pingDetail = @()
    for ($round = 1; $round -le 3; $round++) {
        Send-Bridge setuserstring @{ objectId = $ids[1]; key = $field; value = "rhino-$round" } | Out-Null
        $up = Send-Bridge apply @{ arcgisLayer = $R } -TimeoutSec 900
        $gisNow = @((Send-Bridge features @{ arcgisLayer = $R; take = 50 } -TimeoutSec 600).last |
            Where-Object { [long]$_.objectId -eq $oids[1] })[0].attributes.$field
        Send-Bridge setattribute @{ arcgisLayer = $R; objectId = $oids[1]; field = $field; value = "gis-$round" } -TimeoutSec 600 | Out-Null
        $down = Send-Bridge apply @{ arcgisLayer = $R } -TimeoutSec 900
        $rhinoNow = (Send-Bridge userstrings @{ objectId = $ids[1] }).objects.($ids[1]).$field
        $settled = Send-Bridge preview @{ arcgisLayer = $R } -TimeoutSec 900
        $ok = ($gisNow -eq "rhino-$round" -and $rhinoNow -eq "gis-$round" -and $settled.changed -eq 0 -and
               (Rollup $up)['Updated'] -eq 1 -and (Rollup $down)['Updated'] -eq 1)
        $pingOk = $pingOk -and $ok
        $pingDetail += "round ${round}: ArcGIS='$gisNow' Rhino='$rhinoNow' after=$(Describe-Report $settled)"
    }
    Check 'back-and-forth edits on one object land every time and settle clean' $pingOk ($pingDetail -join ' | ') | Out-Null

    # ArcGIS with an attribute table focused: no active map view at all.
    $table = Send-Bridge opentable @{ arcgisLayer = $R }
    $tablePreview = Send-Bridge preview @{ arcgisLayer = $R } -TimeoutSec 900
    $tableLayers = @((Send-Bridge arcgislayers).layers)
    Check 'preview with an attribute table focused still sees the map and stays clean' `
        (-not $table.mapViewActive -and $tablePreview.changed -eq 0 -and $tablePreview.total -eq ($RN + 2) -and $tableLayers -contains $R) `
        ("map view active={0}; {1}; layers={2}" -f $table.mapViewActive, (Describe-Report $tablePreview), $tableLayers.Count) | Out-Null
    Send-Bridge ensuremapview @{ map = 'Map' } | Out-Null

    # A layer renamed in the Contents pane: the link follows its data to the new name.
    $renamedName = 'Range_Points_Renamed'
    Send-Bridge renamelayer @{ arcgisLayer = $R; newName = $renamedName } | Out-Null
    $followed = $null
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 500
        $followed = @((Send-Bridge links).table | Where-Object { $_.arcgisLayer -eq $renamedName })
        if ($followed.Count -eq 1) { break }
    }
    $renamedPreview = if ($followed.Count -eq 1) {
        Send-Bridge preview @{ arcgisLayer = $renamedName; rhinoLayer = $followed[0].effectiveRhinoLayer } -TimeoutSec 900
    }
    Check 'a renamed ArcGIS layer keeps its link and its Rhino layer' `
        ($followed.Count -eq 1 -and -not $followed[0].missing -and $followed[0].effectiveRhinoLayer -eq $R -and
         $renamedPreview.changed -eq 0) `
        $(if ($followed.Count) { "row: $($followed[0].arcgisLayer) -> $($followed[0].effectiveRhinoLayer); $(Describe-Report $renamedPreview)" } else { 'link did not follow the rename' }) | Out-Null
    if ($followed.Count -eq 1) { $rangeLayer = $renamedName }

    Send-Bridge removelayer @{ arcgisLayer = $rangeLayer } | Out-Null
    Start-Sleep -Seconds 1
    $orphan = @((Send-Bridge links).table | Where-Object { $_.arcgisLayer -eq $rangeLayer })
    Check 'a link whose layer left the map is flagged, not failed' `
        ($orphan.Count -eq 1 -and $orphan[0].missing -and $orphan[0].status -eq 'Not in map') `
        $(if ($orphan.Count) { "missing=$($orphan[0].missing) status=$($orphan[0].status)" } else { 'row gone' }) | Out-Null
}
catch {
    Check 'mixed-session scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 15) -ForegroundColor DarkGray
}
finally {
    try { Send-Bridge attributeseditable @{ value = $false } | Out-Null } catch { }
    try { Send-Bridge ensuremapview @{ map = 'Map' } | Out-Null } catch { }
    foreach ($name in @($rangeLayer, 'Range_Points', 'Range_Points_Renamed') | Select-Object -Unique) {
        if (-not $name) { continue }
        try { Send-Bridge removelink @{ arcgisLayer = $name } | Out-Null } catch { }
        try { Send-Bridge removelayer @{ arcgisLayer = $name } | Out-Null } catch { }
    }
}

# ---------------------------------------------------------------- shapefile FIDs renumbered

# Deleting a row from a shapefile and saving renumbers every FID after it, at once. Matching by
# FID alone then pairs Rhino objects with their neighbours' features.
$script:CurrentFixture = '(renumbered FIDs)'
Write-Host ""
Write-Host "=== shapefile FID renumbering ===" -ForegroundColor Cyan
$renumLayer = $null
try {
    $renumShp = Copy-Fixture 'Point_Multi_Mixed' 'Renum_Points' (Join-Path $Scratch 'renum')
    $renumLayer = (Send-Bridge addlayer @{ path = $renumShp }).name
    $renumBefore = Send-Bridge features @{ arcgisLayer = $renumLayer; take = 50 } -TimeoutSec 600
    $UN = [int]$renumBefore.total
    Send-Bridge pull @{ arcgisLayer = $renumLayer } -TimeoutSec 900 | Out-Null
    $middle = [long]$renumBefore.last[[int][Math]::Floor($UN / 2)].objectId
    Send-Bridge deletefeatures @{ arcgisLayer = $renumLayer; objectIds = @($middle) } -TimeoutSec 600 | Out-Null
    Send-Bridge saveedits | Out-Null
    Send-Bridge removelayer @{ arcgisLayer = $renumLayer } | Out-Null
    $renumLayer = (Send-Bridge addlayer @{ path = $renumShp }).name
    $renumAfter = Send-Bridge features @{ arcgisLayer = $renumLayer; take = 50 } -TimeoutSec 600
    $maxOid = ($renumAfter.last | ForEach-Object { [long]$_.objectId } | Measure-Object -Maximum).Maximum
    Check 'saving the delete renumbered the FIDs' ($renumAfter.total -eq ($UN - 1) -and $maxOid -eq ($UN - 2)) `
        ("FIDs now: " + (($renumAfter.last | ForEach-Object { $_.objectId }) -join ',')) | Out-Null

    # Every surviving feature pairs with its own object; the only ArcGIS-side "edit" allowed is the
    # FID column itself, which the default profile carries and which really did change for the rows
    # after the deleted one. Their geometry must not be touched.
    $geometryBefore = @{}
    foreach ($o in (Send-Bridge userstrings @{ layer = $renumLayer }).objects.PSObject.Properties) {
        $geometryBefore[$o.Name] = (Send-Bridge objectgeometry @{ objectId = $o.Name }).XMin
    }
    $rp = Send-Bridge preview @{ arcgisLayer = $renumLayer } -TimeoutSec 900
    $rr = Rollup $rp
    $renumbered = [int]($UN - 1 - [int][Math]::Floor($UN / 2))
    $modified = @($rp.changedRows | Where-Object { $_.State -eq 'Modified in ArcGIS' })
    $fidOnly = @($modified | Where-Object { $_.Summary -match 'matched by geometry; (FID|OBJECTID|OID)$' })
    Check 'renumbered rows still match their own Rhino objects' `
        ($rr['Deleted in ArcGIS'] -eq 1 -and ([int]$rr['Clean'] + $modified.Count) -eq ($UN - 1) -and
         $modified.Count -eq $renumbered -and $fidOnly.Count -eq $modified.Count -and
         -not $rr.ContainsKey('New in ArcGIS') -and -not $rr.ContainsKey('New in Rhino')) `
        ("{0}; changed rows: {1}" -f (Describe-Report $rp), ((@($rp.changedRows) | ForEach-Object { "$($_.State): $($_.Summary)" }) -join ' | ')) | Out-Null
    $ra = Send-Bridge apply @{ arcgisLayer = $renumLayer } -TimeoutSec 900
    $rp2 = Send-Bridge preview @{ arcgisLayer = $renumLayer } -TimeoutSec 900
    $renumFinal = [int](Send-Bridge features @{ arcgisLayer = $renumLayer; take = 1 } -TimeoutSec 600).total
    $moved = @($geometryBefore.Keys | Where-Object {
            [Math]::Abs([double](Send-Bridge objectgeometry @{ objectId = $_ }).XMin - [double]$geometryBefore[$_]) -gt 1e-9 })
    Check 'apply after renumbering moves no geometry, creates nothing, and stays settled' `
        (-not (Rollup $ra).ContainsKey('Created') -and $moved.Count -eq 0 -and
         (Rollup $rp2)['Clean'] -eq ($UN - 1) -and $rp2.changed -eq 1 -and $renumFinal -eq ($UN - 1)) `
        ("apply: {0}; then {1}; features {2}; moved objects {3}" -f (Describe-Report $ra), (Describe-Report $rp2), $renumFinal, $moved.Count) | Out-Null
}
catch {
    Check 'renumbering scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 15) -ForegroundColor DarkGray
}
finally {
    if ($renumLayer) {
        try { Send-Bridge removelink @{ arcgisLayer = $renumLayer } | Out-Null } catch { }
        try { Send-Bridge removelayer @{ arcgisLayer = $renumLayer } | Out-Null } catch { }
    }
}

# ---------------------------------------------------------------- the link table survives the .3dm

$script:CurrentFixture = '(links)'
Write-Host ""
Write-Host "=== link table persistence ===" -ForegroundColor Cyan
try {
    $before = Send-Bridge links
    $expected = @($before.stored).Count
    Check 'one stored row per fixture, matching the table' ($expected -eq @($before.table).Count -and $expected -gt 0) `
        ("stored={0} table={1}" -f $expected, @($before.table).Count) | Out-Null

    $doc = Join-Path $Scratch 'links.3dm'
    Send-Bridge savedoc @{ path = $doc } -TimeoutSec 600 | Out-Null
    Check 'document saved' (Test-Path $doc) $doc | Out-Null

    Send-Bridge newdoc -TimeoutSec 600 | Out-Null
    $empty = Send-Bridge links
    Check 'a new document starts with no links' (@($empty.stored).Count -eq 0 -and @($empty.table).Count -eq 0) `
        ("stored={0} table={1}" -f @($empty.stored).Count, @($empty.table).Count) | Out-Null

    Send-Bridge opendoc @{ path = $doc } -TimeoutSec 600 | Out-Null
    $back = Send-Bridge links
    Check 'reopening the document brings the links back' (@($back.stored).Count -eq $expected -and @($back.table).Count -eq $expected) `
        ("stored={0} table={1}: {2}" -f @($back.stored).Count, @($back.table).Count, ((@($back.table) | ForEach-Object { "$($_.arcgisLayer)/$($_.direction)" }) -join ', ')) | Out-Null
    $trackedBack = @((Send-Bridge trackedlayers).layers)
    Check 'reopened document still tracks its layers' ($trackedBack.Count -ge 1) ("tracked layers: " + (($trackedBack | ForEach-Object { $_.Layer }) -join ', ')) | Out-Null
}
catch {
    Check 'persistence ran without a bridge error' $false $_.Exception.Message | Out-Null
    Write-Host (Get-BridgeLog -Tail 15) -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- close, reopen, carry on

# Two real sittings: everything is saved and ArcGIS Pro is closed the way a person closes it.
# The next start must show no recovery prompt, reopen the project's Rhino document on its own,
# and bring back the same links and the same comparison. An unsaved Rhino edit made just before
# closing must be kept when the close is told to save it.
$script:CurrentFixture = '(session)'
Write-Host ""
Write-Host "=== close and reopen ===" -ForegroundColor Cyan
if ($SkipStart -or $SkipSession) {
    Write-Host "  skipped (-SkipStart or -SkipSession)" -ForegroundColor DarkGray
}
else {
    try {
        # ---- lifecycle, part A: features born on each side, then edited back and forth.
        # r1 is drawn in Rhino, g1 is created in ArcGIS; one pre-existing object is deleted on each
        # side. Every round edits user text / table data and geometry on the side that did not
        # create the feature, and the values are read back on both sides.
        $lifeShp = Copy-Fixture 'Polygons_Single_Buildings' 'Life_Buildings' (Join-Path $Scratch 'life')
        $LL = (Send-Bridge addlayer @{ path = $lifeShp }).name
        Send-Bridge pull @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        Send-Bridge setlink @{ arcgisLayer = $LL; direction = 'TwoWay' } | Out-Null
        Send-Bridge attributeseditable @{ value = $true } | Out-Null
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $lf = Find-TextField (Send-Bridge schema @{ arcgisLayer = $LL })
        $lifeBase = @((Send-Bridge features @{ arcgisLayer = $LL; take = 50 } -TimeoutSec 600).last)
        $lifeObjects = @((Send-Bridge userstrings @{ layer = $LL }).objects.PSObject.Properties |
            Sort-Object { [long]$_.Value.'gis.arcgis_objectid' })

        function Get-LifeState([string]$RhinoId, [long]$Oid = -1) {
            # The object's own record of its feature, as a sync repairs it -- not a number this
            # script remembered, which a saved shapefile delete may have handed to another row.
            $Oid = [long](Send-Bridge userstrings @{ objectId = $RhinoId }).objects.$RhinoId.'gis.arcgis_objectid'
            $feature = @((Send-Bridge features @{ arcgisLayer = $LL; take = 100 } -TimeoutSec 600).last |
                Where-Object { [long]$_.objectId -eq $Oid })
            [pscustomobject]@{
                Rhino = (Send-Bridge userstrings @{ objectId = $RhinoId }).objects.$RhinoId.$lf
                ArcGis = if ($feature.Count) { $feature[0].attributes.$lf } else { $null }
                RhinoX = [double](Send-Bridge objectgeometry @{ objectId = $RhinoId }).XMin
                Exists = $feature.Count -eq 1
            }
        }
        function Find-RhinoIdForOid([long]$Oid) {
            @((Send-Bridge userstrings @{ layer = $LL }).objects.PSObject.Properties |
                Where-Object { [long]$_.Value.'gis.arcgis_objectid' -eq $Oid -and -not $_.Value.'gis.part_of' }) |
                Select-Object -First 1 -ExpandProperty Name
        }

        $r1 = (Send-Bridge addobject @{ layer = $LL; kind = 'polygon'; points = @(@(200.0, 0.0), @(230.0, 0.0), @(230.0, 20.0), @(200.0, 20.0)) }).id
        Send-Bridge setuserstring @{ objectId = $r1; key = $lf; value = 'r1-v1' } | Out-Null
        $g1Oid = [long](Send-Bridge addfeature @{ arcgisLayer = $LL; copyOf = [long]$lifeBase[0].objectId; dx = 0.0; dy = -0.0006; attributes = @{ $lf = 'g1-v1' } }).objectId
        Send-Bridge deleteobject @{ objectId = $lifeObjects[1].Name } | Out-Null
        Send-Bridge deletefeatures @{ arcgisLayer = $LL; objectIds = @([long]$lifeBase[2].objectId) } -TimeoutSec 600 | Out-Null
        $la = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        $lr = Rollup $la
        Check 'life: new and deleted on both sides preview as four distinct changes' `
            ($lr['New in Rhino'] -eq 1 -and $lr['New in ArcGIS'] -eq 1 -and $lr['Deleted in Rhino'] -eq 1 -and $lr['Deleted in ArcGIS'] -eq 1 -and $la.changed -eq 4) `
            (Describe-Report $la) | Out-Null
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $r1Oid = [long](Send-Bridge userstrings @{ objectId = $r1 }).objects.$r1.'gis.arcgis_objectid'
        $g1 = Find-RhinoIdForOid $g1Oid
        $s1 = Get-LifeState $r1; $t1 = Get-LifeState $g1
        Check 'life: each new feature now exists on the other side with its value' `
            ($s1.Exists -and $s1.ArcGis -eq 'r1-v1' -and $g1 -and $t1.Rhino -eq 'g1-v1') `
            ("r1 -> OID {0} '{1}'; g1 OID {2} -> Rhino {3} '{4}'" -f $r1Oid, $s1.ArcGis, $g1Oid, $g1, $t1.Rhino) | Out-Null

        # Round 2: each feature edited (value + shape) on the side that did not create it.
        Send-Bridge setattribute @{ arcgisLayer = $LL; objectId = $r1Oid; field = $lf; value = 'r1-v2-gis' } -TimeoutSec 600 | Out-Null
        Send-Bridge editfeaturegeometry @{ arcgisLayer = $LL; objectId = $r1Oid; dx = 0.0004; dy = 0.0 } | Out-Null
        Send-Bridge setuserstring @{ objectId = $g1; key = $lf; value = 'g1-v2-rhino' } | Out-Null
        Send-Bridge moveobject @{ objectId = $g1; x = 6.0; y = 0.0; z = 0.0 } | Out-Null
        $l2 = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $s2 = Get-LifeState $r1; $t2 = Get-LifeState $g1
        Check 'life round 2: edits made on the other side flow back to the creator' `
            ((Rollup $l2)['Modified in ArcGIS'] -eq 1 -and (Rollup $l2)['Modified in Rhino'] -eq 1 -and
             $s2.Rhino -eq 'r1-v2-gis' -and [Math]::Abs($s2.RhinoX - $s1.RhinoX) -gt 1e-6 -and $t2.ArcGis -eq 'g1-v2-rhino') `
            ("{0}; r1 Rhino='{1}' dx={2:N3}; g1 ArcGIS='{3}'" -f (Describe-Report $l2), $s2.Rhino, ($s2.RhinoX - $s1.RhinoX), $t2.ArcGis) | Out-Null

        $settle2 = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        Check 'life round 2: the very next preview is settled' `
            ($settle2.changed -eq 2 -and (Rollup $settle2)['Deleted in Rhino'] -eq 1 -and (Rollup $settle2)['Deleted in ArcGIS'] -eq 1) `
            ("{0}; rows: {1}" -f (Describe-Report $settle2), ((@($settle2.changedRows) | ForEach-Object { "$($_.State): $($_.Summary)" }) -join ' | ')) | Out-Null

        # Round 3: and back again, on the creating side.
        Send-Bridge setuserstring @{ objectId = $r1; key = $lf; value = 'r1-v3-rhino' } | Out-Null
        Send-Bridge setattribute @{ arcgisLayer = $LL; objectId = $g1Oid; field = $lf; value = 'g1-v3-gis' } -TimeoutSec 600 | Out-Null
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $s3 = Get-LifeState $r1; $t3 = Get-LifeState $g1
        $l3 = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        Check 'life round 3: edited back on the creating side and settled' `
            ($s3.ArcGis -eq 'r1-v3-rhino' -and $t3.Rhino -eq 'g1-v3-gis' -and $l3.changed -eq 2 -and
             (Rollup $l3)['Deleted in Rhino'] -eq 1 -and (Rollup $l3)['Deleted in ArcGIS'] -eq 1) `
            ("r1 ArcGIS='{0}'; g1 Rhino='{1}'; {2}" -f $s3.ArcGis, $t3.Rhino, (Describe-Report $l3)) | Out-Null

        Send-Bridge saveedits | Out-Null
        $saved = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $settled = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        $s3b = Get-LifeState $r1; $t3b = Get-LifeState $g1
        Check 'life: saving the ArcGIS delete renumbers FIDs; one apply records them and nothing else moves' `
            ((Rollup $saved)['New in ArcGIS'] -eq $null -and (Rollup $saved)['New in Rhino'] -eq $null -and
             $settled.changed -eq 2 -and $s3b.ArcGis -eq 'r1-v3-rhino' -and $t3b.ArcGis -eq 'g1-v3-gis' -and
             [Math]::Abs($s3b.RhinoX - $s3.RhinoX) -lt 1e-9) `
            ("after save: {0}; after apply: {1}" -f (Describe-Report $saved), (Describe-Report $settled)) | Out-Null
    }
    catch {
        Check 'lifecycle part A ran without a bridge error' $false $_.Exception.Message | Out-Null
        Write-Host (Get-BridgeLog -Tail 15) -ForegroundColor DarkGray
    }

    try {
        $sessionDoc = Join-Path $Scratch 'session.3dm'
        Send-Bridge savedoc @{ path = $sessionDoc } -TimeoutSec 600 | Out-Null
        $beforeClose = Send-Bridge runall @{ apply = $false } -TimeoutSec 1800
        $expectedRows = @($beforeClose.table | ForEach-Object { "$($_.arcgisLayer)|$($_.effectiveRhinoLayer)|$($_.direction)|$($_.lastResult)" } | Sort-Object)
        $anchorBefore = (Send-Bridge status).anchor

        $script:DismissLog.Clear()
        $closed = Close-Pro -SaveProject $true -RhinoWork Discard
        Check 'ArcGIS Pro closes cleanly through the add-in' ($closed.Exited -and -not $closed.Forced) `
            ("exited={0} in {1}s {2}" -f $closed.Exited, $closed.Seconds, $closed.Note) | Out-Null

        function Restart-Session {
            $script:DismissLog.Clear()
            Start-Pro -Project $Project | Out-Null
            Start-Sleep -Seconds 8
            $mv = $null
            for ($attempt = 0; $attempt -lt 24; $attempt++) {
                try { $mv = Send-Bridge ensuremapview @{ map = 'Map' } } catch { $mv = $null }
                if ($mv -and $mv.active) { break }
                Dismiss-Dialogs | Out-Null
                Start-Sleep -Seconds 5
            }
            Send-Bridge launch -TimeoutSec 600 | Out-Null
            Send-Bridge unsavedpolicy @{ value = 'Discard' } | Out-Null
            Send-Bridge showpane | Out-Null
            # The pane reads the map asynchronously; give it a beat to reconcile the links.
            Start-Sleep -Seconds 3
        }

        Restart-Session
        $recovery = @(Get-RecoveryPrompts)
        Check 'restart shows no Project Recovery or Autosave Recovery prompt' ($recovery.Count -eq 0) `
            $(if ($recovery.Count) { $recovery -join '; ' } else { 'dismissed: ' + ((Get-DismissLog) -join '; ') }) | Out-Null

        $reopened = (Send-Bridge status).document
        Check "the project's Rhino document reopens on its own" `
            ($reopened -and [string]$reopened.Path -ieq $sessionDoc) `
            ("document: {0}" -f $(if ($reopened) { $reopened.Path } else { '(none)' })) | Out-Null

        $afterLinks = Send-Bridge links
        $missing = @($afterLinks.table | Where-Object { $_.missing })
        $noSource = @($afterLinks.table | Where-Object { -not $_.source })
        Check 'every link is back, bound to its layer, with its data source' `
            (@($afterLinks.table).Count -eq $expectedRows.Count -and $missing.Count -eq 0 -and $noSource.Count -eq 0) `
            ("links={0} (expect {1}); missing={2}; without source={3}" -f @($afterLinks.table).Count, $expectedRows.Count,
                (($missing | ForEach-Object { $_.arcgisLayer }) -join ','), (($noSource | ForEach-Object { $_.arcgisLayer }) -join ',')) | Out-Null

        $afterOpen = Send-Bridge runall @{ apply = $false } -TimeoutSec 1800
        $actualRows = @($afterOpen.table | ForEach-Object { "$($_.arcgisLayer)|$($_.effectiveRhinoLayer)|$($_.direction)|$($_.lastResult)" } | Sort-Object)
        $diff = @(Compare-Object $expectedRows $actualRows)
        Check 'every link compares exactly as it did before closing' ($diff.Count -eq 0) `
            $(if ($diff.Count) { ($diff | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ' | ' } else { "$($actualRows.Count) rows identical" }) | Out-Null
        $anchorAfter = (Send-Bridge status).anchor
        Check 'the earth anchor survives the restart' `
            ($anchorAfter.IsSet -and [Math]::Abs([double]$anchorAfter.Latitude - [double]$anchorBefore.Latitude) -lt 1e-9 -and
             [Math]::Abs([double]$anchorAfter.Longitude - [double]$anchorBefore.Longitude) -lt 1e-9) `
            ("{0},{1}" -f $anchorAfter.Latitude, $anchorAfter.Longitude) | Out-Null

        # Second sitting: a Rhino edit left unsaved, then Pro closed with "save my Rhino work".
        # ---- lifecycle, part B: same values after the restart, then another mixed round.
        $editable = (Send-Bridge links).attributesEditable
        $s4 = Get-LifeState $r1 $r1Oid; $t4 = Get-LifeState $g1 $g1Oid
        Check 'life after restart: both features and their values are intact on both sides' `
            ($editable -and $s4.Rhino -eq 'r1-v3-rhino' -and $s4.ArcGis -eq 'r1-v3-rhino' -and
             $t4.Rhino -eq 'g1-v3-gis' -and $t4.ArcGis -eq 'g1-v3-gis') `
            ("attributes editable={0}; r1 '{1}'/'{2}'; g1 '{3}'/'{4}'" -f $editable, $s4.Rhino, $s4.ArcGis, $t4.Rhino, $t4.ArcGis) | Out-Null

        $r2 = (Send-Bridge addobject @{ layer = $LL; kind = 'polygon'; points = @(@(260.0, 0.0), @(280.0, 0.0), @(280.0, 15.0), @(260.0, 15.0)) }).id
        Send-Bridge setuserstring @{ objectId = $r2; key = $lf; value = 'r2-v1' } | Out-Null
        $g2Oid = [long](Send-Bridge addfeature @{ arcgisLayer = $LL; copyOf = [long]$lifeBase[0].objectId; dx = 0.0; dy = -0.0012; attributes = @{ $lf = 'g2-v1' } }).objectId
        $r1Oid = [long](Send-Bridge userstrings @{ objectId = $r1 }).objects.$r1.'gis.arcgis_objectid'
        Send-Bridge setattribute @{ arcgisLayer = $LL; objectId = $r1Oid; field = $lf; value = 'r1-v4-gis' } -TimeoutSec 600 | Out-Null
        Send-Bridge setuserstring @{ objectId = $g1; key = $lf; value = 'g1-v4-rhino' } | Out-Null
        Send-Bridge moveobject @{ objectId = $g1; x = 0.0; y = 4.0; z = 0.0 } | Out-Null
        $l4 = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        $r4 = Rollup $l4
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $r2Oid = [long](Send-Bridge userstrings @{ objectId = $r2 }).objects.$r2.'gis.arcgis_objectid'
        $g2 = Find-RhinoIdForOid $g2Oid
        if (-not $g2) { throw "The ArcGIS-created feature $g2Oid was not pulled into Rhino." }
        $s5 = Get-LifeState $r1; $t5 = Get-LifeState $g1
        $u5 = Get-LifeState $r2; $v5 = Get-LifeState $g2
        Check 'life after restart: a new mixed round lands on both sides' `
            ($r4['New in Rhino'] -eq 1 -and $r4['New in ArcGIS'] -eq 1 -and $r4['Modified in ArcGIS'] -eq 1 -and $r4['Modified in Rhino'] -eq 1 -and
             $s5.Rhino -eq 'r1-v4-gis' -and $t5.ArcGis -eq 'g1-v4-rhino' -and $u5.ArcGis -eq 'r2-v1' -and $v5.Rhino -eq 'g2-v1') `
            ("{0}; r1 Rhino='{1}' g1 ArcGIS='{2}' r2 ArcGIS='{3}' g2 Rhino='{4}'" -f (Describe-Report $l4), $s5.Rhino, $t5.ArcGis, $u5.ArcGis, $v5.Rhino) | Out-Null

        $settle5 = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        Check 'life after-restart round: the very next preview is settled' `
            ($settle5.changed -eq 2 -and (Rollup $settle5)['Deleted in Rhino'] -eq 1 -and (Rollup $settle5)['Deleted in ArcGIS'] -eq 1) `
            ("{0}; rows: {1}" -f (Describe-Report $settle5), ((@($settle5.changedRows) | ForEach-Object { "$($_.State): $($_.Summary)" }) -join ' | ')) | Out-Null
        # Left applied but unsaved in Rhino: the "save my Rhino work" close below must keep it.

        # Held rows (a feature deleted in ArcGIS earlier in the run) are fine; what must hold is
        # that exactly one more change is pending, and that it is still pending after the restart.
        $carry = @($afterOpen.table | Where-Object { $_.lastResult -and $_.lastResult -notmatch '^failed' } | Select-Object -First 1)
        if ($carry.Count -eq 0) { throw 'No healthy link to carry an edit across the restart.' }
        $carryLayer = $carry[0].arcgisLayer
        $carryRhino = $carry[0].effectiveRhinoLayer
        $liveOids = @((Send-Bridge features @{ arcgisLayer = $carryLayer; take = 500 } -TimeoutSec 600).last | ForEach-Object { [string]$_.objectId })
        $carryObjects = @((Send-Bridge userstrings @{ layer = $carryRhino }).objects.PSObject.Properties |
            Where-Object { $_.Value.'gis.arcgis_objectid' -and -not $_.Value.'gis.part_of' -and $liveOids -contains [string]$_.Value.'gis.arcgis_objectid' })
        if ($carryObjects.Count -eq 0) { throw "No linked object on '$carryRhino' to edit." }
        $carryId = $carryObjects[0].Name
        $base = Send-Bridge preview @{ arcgisLayer = $carryLayer; rhinoLayer = $carryRhino } -TimeoutSec 900
        Send-Bridge moveobject @{ objectId = $carryId; x = 1.5; y = 0.5; z = 0.0 } | Out-Null
        $pending = Send-Bridge preview @{ arcgisLayer = $carryLayer; rhinoLayer = $carryRhino } -TimeoutSec 900
        $closed2 = Close-Pro -SaveProject $true -RhinoWork Save
        Check 'closing with unsaved Rhino work saves it and still exits cleanly' `
            ($closed2.Exited -and -not $closed2.Forced -and $pending.changed -eq ($base.changed + 1)) `
            ("exited={0} in {1}s; pending: {2}" -f $closed2.Exited, $closed2.Seconds, (Describe-Report $pending)) | Out-Null

        Restart-Session
        $recovery2 = @(Get-RecoveryPrompts)
        $carried = Send-Bridge preview @{ arcgisLayer = $carryLayer; rhinoLayer = $carryRhino } -TimeoutSec 900
        Check 'the unsaved edit survives the restart as the same pending change' `
            ($recovery2.Count -eq 0 -and $carried.changed -eq $pending.changed -and
             (Rollup $carried)['Modified in Rhino'] -eq (Rollup $pending)['Modified in Rhino']) `
            ("recovery prompts: {0}; {1}" -f $recovery2.Count, (Describe-Report $carried)) | Out-Null
        $carriedApply = Send-Bridge apply @{ arcgisLayer = $carryLayer; rhinoLayer = $carryRhino } -TimeoutSec 900
        $carriedClean = Send-Bridge preview @{ arcgisLayer = $carryLayer; rhinoLayer = $carryRhino } -TimeoutSec 900
        Check 'the carried edit applies and settles in the new session' `
            ((Rollup $carriedApply)['Updated'] -ge 1 -and $carriedClean.changed -eq $base.changed) `
            ("apply: {0}; then {1}" -f (Describe-Report $carriedApply), (Describe-Report $carriedClean)) | Out-Null

        # ---- lifecycle, part C: everything from the unsaved round survived, and one more round.
        # Closing saved the shapefile, which gives features created in the last session their final
        # FIDs. The first preview may therefore show FID-only changes; nothing else may differ, and
        # one apply records the final FIDs on the Rhino objects.
        $l6a = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        $l6aOther = @($l6a.changedRows | Where-Object {
                $_.State -notin 'Deleted in Rhino', 'Deleted in ArcGIS' -and
                -not ($_.State -eq 'Modified in ArcGIS' -and $_.Summary -match 'matched by geometry; (FID|OBJECTID|OID)$') })
        Check 'life after second restart: saving only renumbered FIDs, nothing else changed' `
            ($l6aOther.Count -eq 0 -and $l6a.total -eq 7) `
            ("{0}; rows: {1}" -f (Describe-Report $l6a), ((@($l6a.changedRows) | ForEach-Object { "$($_.State): $($_.Summary)" }) -join ' | ')) | Out-Null
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $s6 = Get-LifeState $r1; $t6 = Get-LifeState $g1
        $u6 = Get-LifeState $r2; $v6 = Get-LifeState $g2
        $l6 = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        Check 'life after second restart: the last round survived on both sides and previews settled' `
            ($s6.Rhino -eq 'r1-v4-gis' -and $s6.ArcGis -eq 'r1-v4-gis' -and $t6.Rhino -eq 'g1-v4-rhino' -and $t6.ArcGis -eq 'g1-v4-rhino' -and
             $u6.Rhino -eq 'r2-v1' -and $u6.ArcGis -eq 'r2-v1' -and $v6.Rhino -eq 'g2-v1' -and $v6.ArcGis -eq 'g2-v1' -and
             $l6.changed -eq 2 -and (Rollup $l6)['Deleted in Rhino'] -eq 1 -and (Rollup $l6)['Deleted in ArcGIS'] -eq 1) `
            ("{0}; r1 '{1}'/'{2}' g1 '{3}'/'{4}' r2 '{5}'/'{6}' g2 '{7}'/'{8}'" -f (Describe-Report $l6),
                $s6.Rhino, $s6.ArcGis, $t6.Rhino, $t6.ArcGis, $u6.Rhino, $u6.ArcGis, $v6.Rhino, $v6.ArcGis) | Out-Null

        $r2Oid = [long](Send-Bridge userstrings @{ objectId = $r2 }).objects.$r2.'gis.arcgis_objectid'
        Send-Bridge setattribute @{ arcgisLayer = $LL; objectId = $r2Oid; field = $lf; value = 'r2-v2-gis' } -TimeoutSec 600 | Out-Null
        Send-Bridge editfeaturegeometry @{ arcgisLayer = $LL; objectId = $r2Oid; dx = -0.0003; dy = 0.0002 } | Out-Null
        Send-Bridge setuserstring @{ objectId = $g2; key = $lf; value = 'g2-v2-rhino' } | Out-Null
        Send-Bridge moveobject @{ objectId = $g2; x = -3.0; y = 2.0; z = 0.0 } | Out-Null
        Send-Bridge apply @{ arcgisLayer = $LL } -TimeoutSec 900 | Out-Null
        $u7 = Get-LifeState $r2; $v7 = Get-LifeState $g2
        $l7 = Send-Bridge preview @{ arcgisLayer = $LL } -TimeoutSec 900
        $lifeCount = [int](Send-Bridge features @{ arcgisLayer = $LL; take = 1 } -TimeoutSec 600).total
        $lifeRhino = [int](Send-Bridge objectcounts @{ layer = $LL }).counts.'(total)'
        Check 'life final round: settled with no duplicates on either side' `
            ($u7.Rhino -eq 'r2-v2-gis' -and [Math]::Abs($u7.RhinoX - $u6.RhinoX) -gt 1e-6 -and $v7.ArcGis -eq 'g2-v2-rhino' -and
             $l7.changed -eq 2 -and $lifeCount -eq ($lifeBase.Count - 1 + 4) -and $lifeRhino -eq ($lifeObjects.Count - 1 + 4)) `
            ("r2 Rhino='{0}' g2 ArcGIS='{1}'; {2}; features={3} (expect {4}); Rhino={5} (expect {6}); rows: {7}" -f $u7.Rhino, $v7.ArcGis,
                (Describe-Report $l7), $lifeCount, ($lifeBase.Count + 3), $lifeRhino, ($lifeObjects.Count + 3),
                ((@($l7.changedRows) | ForEach-Object { "$($_.State): $($_.Summary)" }) -join ' | ')) | Out-Null
    }
    catch {
        Check 'session scenario ran without a bridge error' $false $_.Exception.Message | Out-Null
        Write-Host (Get-BridgeLog -Tail 15) -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------- finish cleanly

# Leave nothing behind that the next start would have to recover from.
if (-not $SkipStart -and -not $LeavePro) {
    $final = Close-Pro -SaveProject $true -RhinoWork Discard
    $script:CurrentFixture = '(shutdown)'
    Check 'the run ends with a clean ArcGIS Pro shutdown' ($final.Exited -and -not $final.Forced) `
        ("exited={0} in {1}s {2}" -f $final.Exited, $final.Seconds, $final.Note) | Out-Null
}

# ---------------------------------------------------------------- summary

Write-Host ""
Write-Host "=== summary ===" -ForegroundColor Cyan
$script:Results | Format-Table -AutoSize Fixture, @{ n = 'Result'; e = { if ($_.Ok) { 'PASS' } else { 'FAIL' } } }, Check, Detail | Out-String -Width 220 | Write-Host

$dismissals = @(Get-DismissLog)
if ($dismissals.Count) { Write-Host ("dialogs dismissed during the run: " + ($dismissals -join '; ')) -ForegroundColor DarkYellow }

$failed = @($script:Results | Where-Object { -not $_.Ok }).Count
$passed = @($script:Results | Where-Object { $_.Ok }).Count
Write-Host ("{0} passed, {1} failed. Scratch data: {2}" -f $passed, $failed, $Scratch) -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })

try { Stop-Transcript | Out-Null } catch { }
if ($failed) { exit 1 } else { exit 0 }
