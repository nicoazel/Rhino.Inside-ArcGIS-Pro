<# Starts an opted-in host and prints an MCP client configuration for that exact process. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Project)
$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $Project).Path
if ([IO.Path]::GetExtension($projectPath) -ine '.aprx') { throw 'Choose an ArcGIS Pro .aprx project.' }
$node = (Get-Command node -ErrorAction Stop).Source
if ([int]((& $node --version).TrimStart('v').Split('.')[0]) -lt 22) { throw 'Node.js 22 or newer is required.' }
$server = Join-Path $PSScriptRoot 'server.mjs'
if (-not (Test-Path -LiteralPath $server)) { throw 'Keep this launcher beside the extracted gateway server.mjs.' }
$priorOptIn = $env:RHINOINSIDE_MCP_ENABLED
try {
    $env:RHINOINSIDE_MCP_ENABLED = '1'
    $hostProcess = Start-Process -FilePath (Join-Path $env:ProgramFiles 'ArcGIS\Pro\bin\ArcGISPro.exe') -ArgumentList ('"' + $projectPath + '"') -WindowStyle Hidden -PassThru
} finally { $env:RHINOINSIDE_MCP_ENABLED = $priorOptIn }
[ordered]@{
    hostPid = $hostProcess.Id
    note = 'Wait for Pro to open. Use the Rhino.Inside ribbon to launch Rhino and configure links. Writes require local review in Pro. Refresh this configuration after restarting Pro.'
    mcpServers = @{ 'rhino-inside-arcgis' = @{ command = $node; args = @($server); env = @{ RHINOINSIDE_MCP_HOST_PID = [string]$hostProcess.Id } } }
} | ConvertTo-Json -Depth 6
