<#
.SYNOPSIS
Drives the Rhino.Inside add-in inside a running ArcGIS Pro, without touching the UI.

.DESCRIPTION
The add-in starts a file-based command bridge when the RHINOINSIDE_TESTBRIDGE environment
variable names a directory (see TestBridge.cs). This script writes request files into that
directory and waits for the matching reply, which makes the plugin scriptable end to end:
launch Rhino, pull a layer, preview a sync, draw a line, apply, and read the results back.

Pro must be started from a session where RHINOINSIDE_TESTBRIDGE is set, so that the Pro
process inherits it. Start-Pro below does that.

.EXAMPLE
  . .\tools\bridge.ps1
  Start-Pro -Project 'C:\...\MyProject2.aprx'
  Send-Bridge status
  Send-Bridge launch
  Send-Bridge preview @{ arcgisLayer = 'Polyline_MultipartMix_Streets' }
#>

$script:BridgeRoot = if ($env:RHINOINSIDE_TESTBRIDGE) { $env:RHINOINSIDE_TESTBRIDGE }
                     else { Join-Path $env:TEMP 'rhinoinside-bridge' }

function Initialize-Bridge {
    param([string]$Root = $script:BridgeRoot)
    $script:BridgeRoot = $Root
    $env:RHINOINSIDE_TESTBRIDGE = $Root
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'in'), (Join-Path $Root 'out') | Out-Null
    Remove-Item (Join-Path $Root 'out\*.json') -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $Root 'in\*.json') -ErrorAction SilentlyContinue
    Write-Output "bridge root: $Root"
}

function Start-Pro {
    param([string]$Project, [string]$Root = $script:BridgeRoot)
    Initialize-Bridge -Root $Root | Out-Null
    Stop-Pro
    # Keep the previous session's log: it is the record of how that session ended.
    $previousLog = Join-Path $Root 'bridge.log'
    if (Test-Path $previousLog) {
        Move-Item $previousLog (Join-Path $Root ("bridge-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))) -Force
    }
    Remove-Item (Join-Path $Root 'process.id') -ErrorAction SilentlyContinue

    $exe = 'C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe'
    $started = if ($Project) { Start-Process $exe -ArgumentList "`"$Project`"" -PassThru }
               else { Start-Process $exe -PassThru }
    $script:TestProProcessId = $started.Id
    Write-Output "started ArcGIS Pro; waiting for the bridge to come up..."
    Wait-Bridge -Root $Root
}

function Get-TestProProcessId {
    param([string]$Root = $script:BridgeRoot)

    $candidates = @()
    if ($script:TestProProcessId) { $candidates += [int]$script:TestProProcessId }

    $pidFile = Join-Path $Root 'process.id'
    if (Test-Path $pidFile) {
        $fromFile = 0
        if ([int]::TryParse(((Get-Content $pidFile -Raw).Trim()), [ref]$fromFile)) {
            $candidates += $fromFile
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $process = Get-Process -Id $candidate -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'ArcGISPro') { return [int]$candidate }
    }
    return 0
}

function Close-Pro {
    <#
    .SYNOPSIS
    Closes ArcGIS Pro the way a person does, through the add-in: the project (and its pending
    edits) is saved or discarded, the Rhino document is saved or discarded per -RhinoWork, and
    Pro runs its full shutdown, which disposes the hosted Rhino. Returns an object saying whether
    Pro exited on its own and how long it took -- a clean close is what keeps the Project
    Recovery and Rhino Autosave Recovery prompts from appearing next time.
    #>
    param(
        [bool]$SaveProject = $true,
        [ValidateSet('Save', 'Discard')][string]$RhinoWork = 'Discard',
        [int]$GraceSec = 120,
        [string]$Root = $script:BridgeRoot
    )
    $target = Get-TestProProcessId -Root $Root
    if (-not $target) { return [pscustomobject]@{ Exited = $true; Seconds = 0; Forced = $false; Note = 'not running' } }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        Send-Bridge unsavedpolicy @{ value = $RhinoWork } -Root $Root -TimeoutSec 30 | Out-Null
        Send-Bridge closepro @{ saveProject = $SaveProject } -Root $Root -TimeoutSec 300 | Out-Null
    }
    catch { return [pscustomobject]@{ Exited = $false; Seconds = 0; Forced = $false; Note = "bridge: $($_.Exception.Message)" } }

    while ((Get-Process -Id $target -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalSeconds -lt $GraceSec) {
        Dismiss-Dialogs -ProcessId $target | Out-Null
        Start-Sleep -Milliseconds 500
    }
    $exited = -not (Get-Process -Id $target -ErrorAction SilentlyContinue)
    $note = ''
    if ($exited) {
        Remove-Item (Join-Path $Root 'process.id') -ErrorAction SilentlyContinue
        $script:TestProProcessId = 0
    }
    else {
        # Say what held it: the windows still open (a prompt nobody answered?) and the last bridge lines.
        try {
            $open = @(Get-ProDialogs -ProcessId $target | Where-Object { $_.Title -notmatch '^(Perspective|Top|Front|Right|ArcGISNotifications|AirspacePopup)$' } |
                ForEach-Object { "'$($_.Title)' [$($_.Buttons)]" })
            $note = 'still open: ' + ($open -join '; ') + ' | last bridge lines: ' + ((Get-BridgeLog -Root $Root -Tail 4) -join ' / ')
        } catch { $note = 'still running; could not list its windows' }
    }
    return [pscustomobject]@{ Exited = $exited; Seconds = [int]$sw.Elapsed.TotalSeconds; Forced = $false; Note = $note }
}

function Stop-Pro {
    <#
    .SYNOPSIS
    Closes ArcGIS Pro, politely first. A force-kill is what makes Pro show the Project Recovery
    prompt on its next start, so the add-in is asked to close Pro (Close-Pro) when the bridge is
    up, then the main window is asked, and only after that is the process killed.
    #>
    param([int]$GraceSec = 40, [int]$ProcessId = 0, [string]$Root = $script:BridgeRoot)
    $target = if ($ProcessId) { $ProcessId } else { Get-TestProProcessId -Root $Root }
    if (-not $target) { return }

    $proc = Get-Process -Id $target -ErrorAction SilentlyContinue
    if (-not $proc -or $proc.ProcessName -ne 'ArcGISPro') { return }

    if (Test-Path (Join-Path $Root 'bridge.log')) {
        $closed = Close-Pro -Root $Root -GraceSec ([Math]::Max($GraceSec, 60))
        if ($closed.Exited) { return }
    }

    try { $proc.CloseMainWindow() | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds($GraceSec)
    while ((Get-Date) -lt $deadline) {
        Dismiss-Dialogs -ProcessId $target | Out-Null
        if (-not (Get-Process -Id $target -ErrorAction SilentlyContinue)) {
            Remove-Item (Join-Path $Root 'process.id') -ErrorAction SilentlyContinue
            $script:TestProProcessId = 0
            return
        }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Id $target -ErrorAction SilentlyContinue) {
        Write-Warning "ArcGIS Pro did not close within $GraceSec s; forcing it. Pro will offer Project Recovery on its next start."
        $script:DismissLog.Add("$(Get-Date -Format HH:mm:ss) FORCED KILL of ArcGIS Pro $target")
    }
    Get-Process -Id $target -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item (Join-Path $Root 'process.id') -ErrorAction SilentlyContinue
    $script:TestProProcessId = 0
    Start-Sleep -Seconds 3
}

function Wait-Bridge {
    param([string]$Root = $script:BridgeRoot, [int]$TimeoutSec = 300)
    $log = Join-Path $Root 'bridge.log'
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Test-Path $log)) {
        if ((Get-Date) -gt $deadline) { throw "bridge did not start within $TimeoutSec s" }
        Dismiss-Dialogs -ProcessId (Get-TestProProcessId -Root $Root) | Out-Null
        Start-Sleep -Seconds 2
    }
    Write-Output "bridge is up"
}

# ---------------------------------------------------------------- dialog dismisser
#
# Pro and Rhino both raise modal prompts at start-up that nothing in a scripted run will ever
# click: Project Recovery after an unclean exit, "save changes?" on close, licence and update
# notices. Any of them parks the whole run. The dismisser is called from every wait loop in this
# file, finds such windows through UI Automation and presses the button that declines -- it never
# closes a window outright, and it never touches the Pro or Rhino main windows.

$script:DismissRules = @(
    # title pattern (regex, case-insensitive)      buttons to try, most cautious first
    # Pro, after an unclean exit: "Project Recovery - The backup is newer than the saved
    # project. Would you like to recover your work from the backup?"  Yes / No / Cancel.
    @{ Title = 'Project Recovery';                  Buttons = @('No', 'Cancel');   Modal = $false },
    # Rhino, after an unclean exit: "Rhino 8  Autosave Recovery ... Rhino will now open the
    # automatically saved file."  OK / Cancel. Cancel discards the autosave; this held the
    # in-process launch for five minutes before it was answered.
    @{ Title = 'Autosave Recovery';                 Buttons = @('Cancel');         Modal = $false },
    @{ Title = '^ArcGIS Pro$';                      Buttons = @("Don't Save", 'No', 'Cancel') },
    # Pro, on close with an unsaved project: " ArcGIS Project - Save changes to Review?". Close-Pro
    # saves first when asked to, so this only answers the deliberate discard path.
    @{ Title = 'Save changes to';                   Buttons = @('No', "Don't Save") },
    # Pro, on close with pending edits.
    @{ Title = 'Save Edits';                        Buttons = @('No', "Don't Save") },
    @{ Title = '^Rhino( 8)?$|^Rhinoceros( 8)?$';    Buttons = @("Don't Save", 'No', 'Later', 'Cancel') },
    @{ Title = 'Rhino.*(Licen|Update|Notice)';      Buttons = @('Later', 'Cancel', 'Close') },
    @{ Title = '^(Warning|Error|Information)$';     Buttons = @('Cancel', 'Close', 'OK') }
)

# Titles that must never be treated as a dialog, however they match above.
$script:DismissNever = ' - ArcGIS Pro$|\.aprx|Rhino(ceros)? 8 (Educational|Commercial|Evaluation)|^Untitled - '

$script:DismissLog = New-Object System.Collections.Generic.List[string]

function Dismiss-Dialogs {
    <#
    .SYNOPSIS
    Presses the declining button on any known start-up or shutdown prompt from ArcGIS Pro or the
    Rhino it hosts. Returns the titles it dismissed. Safe to call as often as you like.
    #>
    param([int]$ProcessId = 0, [string]$Root = $script:BridgeRoot)

    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    } catch { return @() }

    $dismissed = @()
    $target = if ($ProcessId) { $ProcessId } else { Get-TestProProcessId -Root $Root }
    if (-not $target) { return $dismissed }
    $process = Get-Process -Id $target -ErrorAction SilentlyContinue
    if (-not $process -or $process.ProcessName -ne 'ArcGISPro') { return $dismissed }

    # Both hosts show their prompts as windows nested inside the application window rather than
    # as top-level windows, so each Pro top-level window is searched for windows within it too.
    $windows = @()
    try {
        $desktopRoot = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)
        foreach ($top in $desktopRoot.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
            if ($top.Current.ProcessId -ne $target) { continue }
            $windows += $top
            $windows += @($top.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
        }
    } catch { return $dismissed }

    foreach ($w in $windows) {
        try {
            $title = $w.Current.Name
            if (-not $title -or $title -match $script:DismissNever) { continue }

            $rule = $script:DismissRules | Where-Object { $title -match $_.Title } | Select-Object -First 1
            if (-not $rule) { continue }

            # Only a prompt is fair game: a dialog-sized window that is modal (WPF ShowDialog, Win32
            # MessageBox) -- unless the rule names the prompt so exactly that the title suffices, as
            # Rhino's autosave prompt does not always report itself modal. Pro's own main window is
            # titled plain "ArcGIS Pro" while it starts up, and pressing anything on it -- least of
            # all its title-bar Close -- ends the run.
            $needModal = if ($rule.ContainsKey('Modal')) { [bool]$rule.Modal } else { $true }
            $isModal = $false
            try {
                $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
                $isModal = $wp.Current.IsModal
            } catch { }
            if ($needModal -and -not $isModal -and $w.Current.ClassName -ne '#32770') { continue }
            if ($w.Current.BoundingRectangle.Width -gt 1000 -or $w.Current.BoundingRectangle.Height -gt 800) { continue }

            $btnCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            $buttons = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
            $byName = @{}
            foreach ($b in $buttons) {
                # Title-bar buttons are never an answer to a prompt.
                $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($b)
                if ($parent -and $parent.Current.ControlType -eq [System.Windows.Automation.ControlType]::TitleBar) { continue }
                if ($b.Current.AutomationId -in 'Close', 'Minimize', 'Maximize', 'Restore') { continue }
                $n = ($b.Current.Name -replace '_', '').Trim()
                if ($n -and -not $byName.ContainsKey($n)) { $byName[$n] = $b }
            }

            foreach ($want in $rule.Buttons) {
                $key = $byName.Keys | Where-Object { $_ -ieq $want } | Select-Object -First 1
                if (-not $key) { continue }
                $pattern = $byName[$key].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
                $entry = "$(Get-Date -Format HH:mm:ss) dismissed '$title' with '$key'"
                $script:DismissLog.Add($entry)
                Write-Verbose $entry
                $dismissed += $title
                break
            }
        } catch { }
    }
    return $dismissed
}

function Get-ProDialogs {
    <#
    .SYNOPSIS
    Lists the top-level windows of ArcGIS Pro (and the Rhino inside it) with modality, size and
    button names -- what Dismiss-Dialogs sees. For deciding whether a new prompt needs a rule.
    #>
    param([int]$ProcessId = 0, [string]$Root = $script:BridgeRoot)
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $target = if ($ProcessId) { $ProcessId } else { Get-TestProProcessId -Root $Root }
    if (-not $target) { return @() }
    $desktopRoot = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $all = @()
    foreach ($top in $desktopRoot.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        if ($top.Current.ProcessId -ne $target) { continue }
        $all += $top
        $all += @($top.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
    }
    foreach ($w in $all) {
        $modal = $null
        try { $modal = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Current.IsModal } catch { }
        $btnCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $names = @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 12)
        [pscustomobject]@{
            Title = $w.Current.Name; Class = $w.Current.ClassName; Modal = $modal
            Width = [int]$w.Current.BoundingRectangle.Width; Height = [int]$w.Current.BoundingRectangle.Height
            Buttons = ($names -join ' | ')
        }
    }
}

function Get-DismissLog { $script:DismissLog }

function Get-RecoveryPrompts {
    <#
    .SYNOPSIS
    The recovery prompts the dismisser has had to answer: Pro's Project Recovery and Rhino's
    Autosave Recovery, plus any forced kill. Each one means an earlier session did not end cleanly.
    #>
    @($script:DismissLog | Where-Object { $_ -match 'Project Recovery|Autosave Recovery|FORCED KILL' })
}

function Send-Bridge {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [hashtable]$Arguments = @{},
        [int]$TimeoutSec = 300,
        [string]$Root = $script:BridgeRoot
    )

    $id = [guid]::NewGuid().ToString('N').Substring(0, 12)
    $payload = @{ id = $id; command = $Command }
    foreach ($key in $Arguments.Keys) {
        # 'id' and 'command' are the envelope; an argument by either name would silently redirect
        # the reply to a file this function never waits for.
        if ($key -in 'id', 'command') { throw "Argument '$key' is reserved by the bridge envelope; use another name (e.g. objectId)." }
        $payload[$key] = $Arguments[$key]
    }

    $inbox = Join-Path $Root 'in'
    $outbox = Join-Path $Root 'out'
    New-Item -ItemType Directory -Force -Path $inbox, $outbox | Out-Null

    # Write aside then move, so the watcher never sees a partial file.
    $staging = Join-Path $Root "$id.staging"
    $payload | ConvertTo-Json -Depth 8 | Set-Content -Path $staging -Encoding utf8
    Move-Item $staging (Join-Path $inbox "$id.json")

    $reply = Join-Path $outbox "$id.json"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastDismiss = Get-Date
    $processFile = Join-Path $Root 'process.id'
    $expectedProcessId = 0
    if (Test-Path -LiteralPath $processFile) {
        [int]::TryParse(((Get-Content -LiteralPath $processFile -Raw).Trim()), [ref]$expectedProcessId) | Out-Null
    }
    while (-not (Test-Path $reply)) {
        if ($expectedProcessId -gt 0 -and -not (Get-Process -Id $expectedProcessId -ErrorAction SilentlyContinue)) {
            throw "ArcGIS Pro test host $expectedProcessId exited while waiting for '$Command'. Inspect bridge.log and the Windows Application event log."
        }
        if ((Get-Date) -gt $deadline) { throw "timed out after $TimeoutSec s waiting for '$Command'" }
        Start-Sleep -Milliseconds 250
        # A modal prompt in Pro or Rhino would hold this reply forever; clear it if one is up.
        if (((Get-Date) - $lastDismiss).TotalSeconds -ge 3) { Dismiss-Dialogs | Out-Null; $lastDismiss = Get-Date }
    }

    $result = Get-Content $reply -Raw | ConvertFrom-Json
    Remove-Item $reply -Force -ErrorAction SilentlyContinue

    if (-not $result.ok) { throw "$Command failed: $($result.error)" }
    return $result.result
}

function Get-BridgeLog {
    param([string]$Root = $script:BridgeRoot, [int]$Tail = 40)
    $log = Join-Path $Root 'bridge.log'
    if (Test-Path $log) { Get-Content $log -Tail $Tail } else { "no log at $log" }
}
