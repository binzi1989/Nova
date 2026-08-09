[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "NOVA\Connectors\Maimai"),
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$integrationRoot = $PSScriptRoot
$connectorProject = Join-Path $integrationRoot "connector\Nova.Maimai.Connector.csproj"
$extensionSource = Join-Path $integrationRoot "extension"
$packSource = Join-Path $integrationRoot "agent-pack"
$extensionId = "nplhnfcoijedjoihpghfhnjkkgomhhpg"
$nativeHostName = "ai.nova.maimai"
$packId = "nova.maimai-relationship-research"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$stagingRoot = Join-Path $env:TEMP ("nova-maimai-install-" + [Guid]::NewGuid().ToString("N"))
$publishRoot = Join-Path $stagingRoot "publish"

function Read-JsonUtf8([string]$Path) {
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
}

function Write-JsonUtf8([string]$Path, $Value) {
    $directory = Split-Path -Parent $Path
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json, $utf8)
}

function Set-DynamicProperty($Object, [string]$Name, $Value) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    } else {
        $property.Value = $Value
    }
}

[System.IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
try {
    if (-not $SkipBuild) {
        & dotnet publish $connectorProject --configuration Release --runtime win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $publishRoot
        if ($LASTEXITCODE -ne 0) { throw "连接器发布失败，dotnet exit code: $LASTEXITCODE" }
    } else {
        $publishRoot = Join-Path $integrationRoot "connector\bin\Release\net8.0-windows\win-x64\publish"
    }

    $publishedExe = Join-Path $publishRoot "Nova.Maimai.Connector.exe"
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw "没有找到发布后的连接器：$publishedExe"
    }

    $appRoot = Join-Path $InstallRoot "app"
    $extensionTarget = Join-Path $InstallRoot "extension"
    [System.IO.Directory]::CreateDirectory($appRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($extensionTarget) | Out-Null
    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $appRoot "Nova.Maimai.Connector.exe") -Force
    Get-ChildItem -LiteralPath $extensionSource -File | Where-Object { $_.Name -ne "smoke-extension.mjs" } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $extensionTarget $_.Name) -Force
    }

    $installedExe = Join-Path $appRoot "Nova.Maimai.Connector.exe"
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($null -eq $userPath) { $userPath = "" }
    $pathEntries = @($userPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries))
    $pathExists = $pathEntries | Where-Object {
        $_.Trim().TrimEnd('\') -eq $appRoot.TrimEnd('\')
    }
    if ($null -eq $pathExists) {
        $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $appRoot } else { "$userPath;$appRoot" }
        [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
    }
    if ($null -eq ("NovaEnvironmentBroadcast" -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NovaEnvironmentBroadcast
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint message, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);
}
"@
    }
    $broadcastResult = [UIntPtr]::Zero
    [NovaEnvironmentBroadcast]::SendMessageTimeout(
        [IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, "Environment", 0x0002, 5000,
        [ref]$broadcastResult) | Out-Null
    if (-not (($env:Path -split ';') | Where-Object { $_.Trim().TrimEnd('\') -eq $appRoot.TrimEnd('\') })) {
        $env:Path = "$appRoot;$env:Path"
    }

    $nativeManifestPath = Join-Path $InstallRoot "$nativeHostName.json"
    $nativeManifest = [ordered]@{
        name = $nativeHostName
        description = "NOVA Maimai safe visible-page connector"
        path = $installedExe
        type = "stdio"
        allowed_origins = @("chrome-extension://$extensionId/")
    }
    Write-JsonUtf8 $nativeManifestPath $nativeManifest

    $nativeRegistry = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$nativeHostName"
    New-Item -Path $nativeRegistry -Force | Out-Null
    Set-Item -Path $nativeRegistry -Value $nativeManifestPath

    $novaRoot = Join-Path $env:LOCALAPPDATA "NOVA"
    $mcpConfigPath = Join-Path $novaRoot "mcp-servers.json"
    if (Test-Path -LiteralPath $mcpConfigPath) {
        $mcpConfig = Read-JsonUtf8 $mcpConfigPath
        if ($null -eq $mcpConfig.servers) { $mcpConfig | Add-Member -NotePropertyName servers -NotePropertyValue @() }
    } else {
        $mcpConfig = [pscustomobject]@{ version = 1; servers = @() }
    }
    $server = [pscustomobject]@{
        name = "nova-maimai"
        transport = "stdio"
        command = "Nova.Maimai.Connector.exe"
        arguments = @("mcp")
        workingDirectory = $null
        enabled = $true
        environmentVariables = [pscustomobject]@{}
    }
    $servers = @($mcpConfig.servers)
    $existingIndex = -1
    for ($index = 0; $index -lt $servers.Count; $index++) {
        if ($servers[$index].name -eq "nova-maimai") { $existingIndex = $index; break }
    }
    if ($existingIndex -ge 0) { $servers[$existingIndex] = $server } else { $servers += $server }
    $mcpConfig.servers = $servers
    Write-JsonUtf8 $mcpConfigPath $mcpConfig

    $packsRoot = Join-Path $novaRoot "agent-packs\installed"
    $packTarget = Join-Path $packsRoot $packId
    [System.IO.Directory]::CreateDirectory($packTarget) | Out-Null
    Copy-Item -Path (Join-Path $packSource "*") -Destination $packTarget -Recurse -Force

    $statePath = Join-Path $novaRoot "agent-packs\state.json"
    if (Test-Path -LiteralPath $statePath) {
        $state = Read-JsonUtf8 $statePath
        if ($null -eq $state.Enabled) { $state | Add-Member -NotePropertyName Enabled -NotePropertyValue ([pscustomobject]@{}) }
    } else {
        $state = [pscustomobject]@{ Enabled = [pscustomobject]@{} }
    }
    Set-DynamicProperty $state.Enabled $packId $true
    Write-JsonUtf8 $statePath $state

    & $installedExe smoke
    if ($LASTEXITCODE -ne 0) { throw "安装后的连接器自检失败，exit code: $LASTEXITCODE" }

    [pscustomobject]@{
        ok = $true
        connector = $installedExe
        extension = $extensionTarget
        extensionId = $extensionId
        nativeHostRegistered = $true
        novaMcpConfigured = $true
        connectorAddedToUserPath = $true
        agentPackEnabled = $true
        nextStep = "打开 chrome://extensions，启用开发者模式，加载已解压的扩展：$extensionTarget"
    } | ConvertTo-Json -Depth 5
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
