$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path (Split-Path -Parent $projectRoot) "Nova.AgentOS.Bridge\Nova.AgentOS.Bridge.csproj"
$output = Join-Path $projectRoot "resources\bridge"

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

New-Item -ItemType Directory -Path $output -Force | Out-Null

dotnet publish $bridgeProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    --output $output

if (-not (Test-Path -LiteralPath (Join-Path $output "Nova.AgentOS.Bridge.exe"))) {
    throw "AgentOS bridge publish did not produce Nova.AgentOS.Bridge.exe."
}

Write-Host "AgentOS bridge ready: $output"
