[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "NOVA\Connectors\Maimai")
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $InstallRoot "app\Nova.Maimai.Connector.exe"
$extension = Join-Path $InstallRoot "extension\manifest.json"
$nativeManifest = Join-Path $InstallRoot "ai.nova.maimai.json"
$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\ai.nova.maimai"
$mcpConfig = Join-Path $env:LOCALAPPDATA "NOVA\mcp-servers.json"
$pack = Join-Path $env:LOCALAPPDATA "NOVA\agent-packs\installed\nova.maimai-relationship-research\nova.industry.json"
$mcpServer = $null
if (Test-Path -LiteralPath $mcpConfig -PathType Leaf) {
    $configText = [System.IO.File]::ReadAllText($mcpConfig, [System.Text.Encoding]::UTF8)
    $mcpServer = @(($configText | ConvertFrom-Json).servers) | Where-Object { $_.name -eq "nova-maimai" }
}
$appRoot = Split-Path -Parent $exe
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$pathEntries = @(($userPath -split ';') | ForEach-Object { $_.Trim().TrimEnd('\') })

$checks = [ordered]@{
    connectorExe = Test-Path -LiteralPath $exe -PathType Leaf
    extensionManifest = Test-Path -LiteralPath $extension -PathType Leaf
    nativeManifest = Test-Path -LiteralPath $nativeManifest -PathType Leaf
    nativeRegistry = Test-Path -LiteralPath $registryPath
    mcpConfig = Test-Path -LiteralPath $mcpConfig -PathType Leaf
    mcpCommandName = ($null -ne $mcpServer -and $mcpServer.command -eq "Nova.Maimai.Connector.exe" -and $mcpServer.enabled)
    connectorOnUserPath = ($pathEntries -contains $appRoot.TrimEnd('\'))
    agentPack = Test-Path -LiteralPath $pack -PathType Leaf
}
if ($checks.Values -contains $false) {
    [pscustomobject]@{ ok = $false; checks = $checks } | ConvertTo-Json -Depth 5
    exit 1
}

& $exe smoke
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[pscustomobject]@{ ok = $true; checks = $checks } | ConvertTo-Json -Depth 5
