param(
    [string]$Version = "0.1.0-preview.4",
    [ValidateSet("all", "osx-arm64", "osx-x64")]
    [string]$Runtime = "all"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "NovaDesktop.Mac\NovaDesktop.Mac.csproj"
$packagerProject = Join-Path $projectRoot "tools\NovaMacPackager\NovaMacPackager.csproj"
$plistTemplate = Join-Path $projectRoot "packaging\macos\Info.plist"
$distRoot = Join-Path $projectRoot "dist\macos"
$publishRoot = Join-Path $projectRoot ".mac-release"
$runtimes = if ($Runtime -eq "all") { @("osx-arm64", "osx-x64") } else { @($Runtime) }
$releaseEntries = [System.Collections.Generic.List[object]]::new()

$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

dotnet run --project $projectFile `
    --configuration Release `
    -- "--startup-smoke"
if ($LASTEXITCODE -ne 0) {
    throw "Managed macOS startup smoke failed with exit code $LASTEXITCODE."
}
dotnet run --project $projectFile `
    --configuration Release `
    -- "--agentos-smoke"
if ($LASTEXITCODE -ne 0) {
    throw "Shared AgentOS persistence smoke failed with exit code $LASTEXITCODE."
}

function Assert-MachOArchitecture {
    param(
        [string]$ExecutablePath,
        [string]$RuntimeIdentifier
    )

    $bytes = [IO.File]::ReadAllBytes($ExecutablePath)
    if ($bytes.Length -lt 8 `
        -or $bytes[0] -ne 0xCF `
        -or $bytes[1] -ne 0xFA `
        -or $bytes[2] -ne 0xED `
        -or $bytes[3] -ne 0xFE) {
        throw "The published app host is not a 64-bit Mach-O executable: $ExecutablePath"
    }
    $cpuType = [BitConverter]::ToInt32($bytes, 4)
    $expectedCpuType = if ($RuntimeIdentifier -eq "osx-arm64") {
        0x0100000C
    } else {
        0x01000007
    }
    if ($cpuType -ne $expectedCpuType) {
        throw "Mach-O CPU type $cpuType does not match $RuntimeIdentifier."
    }
}

foreach ($rid in $runtimes) {
    $publishDirectory = Join-Path $publishRoot $rid
    $packageDirectory = Join-Path $distRoot "NOVA-Mac-$Version-$rid"
    $appDirectory = Join-Path $packageDirectory "NOVA.app"
    $contentsDirectory = Join-Path $appDirectory "Contents"
    $macOsDirectory = Join-Path $contentsDirectory "MacOS"
    $resourcesDirectory = Join-Path $contentsDirectory "Resources"
    $executablePath = Join-Path $macOsDirectory "NovaDesktop.Mac"
    $archivePath = Join-Path $distRoot "NOVA-Mac-$Version-$rid.zip"
    $tarGzipPath = Join-Path $distRoot "NOVA-Mac-$Version-$rid.tar.gz"

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $packageDirectory) {
        Remove-Item -LiteralPath $packageDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    if (Test-Path -LiteralPath $tarGzipPath) {
        Remove-Item -LiteralPath $tarGzipPath -Force
    }

    dotnet restore $projectFile `
        --runtime $rid `
        --ignore-failed-sources `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "offline-capable restore failed for $rid with exit code $LASTEXITCODE."
    }

    dotnet publish $projectFile `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        --output $publishDirectory `
        -p:Version=$Version `
        -p:UseAppHost=true `
        --no-restore `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Path $macOsDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $resourcesDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $macOsDirectory -Recurse -Force
    Assert-MachOArchitecture $executablePath $rid

    $buildNumber = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
    $plist = Get-Content -LiteralPath $plistTemplate -Raw -Encoding UTF8
    $plist = $plist.Replace("__VERSION__", $Version)
    $plist = $plist.Replace("__BUILD__", $buildNumber)
    if ($plist.Contains("__VERSION__") -or $plist.Contains("__BUILD__")) {
        throw "Info.plist still contains unresolved release tokens."
    }
    $plist | Set-Content -LiteralPath (Join-Path $contentsDirectory "Info.plist") -Encoding utf8

    @"
NOVA Mac Preview $Version ($rid)

This bundle was cross-built on Windows and is not Developer ID signed or notarized.
Prefer the TAR.GZ download because it preserves Unix executable permissions.

Before first launch, open Terminal in this directory and run:
  zsh ./FIRST-LAUNCH.command

FIRST-LAUNCH.command removes quarantine only from this NOVA.app copy, applies
an ad-hoc local signature, verifies the bundle, and then opens it. This makes
the Preview usable but does not turn it into an Apple-notarized release.

For public distribution, run build-macos.sh on macOS with an Apple Developer
ID and notarytool profile.
"@ | Set-Content -LiteralPath (Join-Path $packageDirectory "MAC-FIRST-RUN.txt") -Encoding utf8

    $launcher = @'
#!/bin/zsh
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP="$SCRIPT_DIR/NOVA.app"
EXECUTABLE="$APP/Contents/MacOS/NovaDesktop.Mac"

chmod +x "$EXECUTABLE"
if xattr -p com.apple.quarantine "$APP" >/dev/null 2>&1; then
  echo "检测到下载隔离属性；仅为当前 NOVA.app 副本解除隔离。"
  xattr -dr com.apple.quarantine "$APP"
fi
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"
echo "NOVA 已完成本机临时签名。正在交给 macOS Gatekeeper 打开…"
open "$APP"
'@
    $launcherPath = Join-Path $packageDirectory "FIRST-LAUNCH.command"
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        $launcherPath,
        $launcher.Replace("`r`n", "`n") + "`n",
        $utf8WithoutBom)

    $packageManifest = [ordered]@{
        schema_version = 1
        product = "NOVA for Mac"
        version = $Version
        runtime = $rid
        bundle_id = "ai.nova.agentos.desktop"
        minimum_macos = "12.0"
        release_gate = [ordered]@{
            status = "CROSS_BUILT_UNSIGNED"
            notarized = $false
            automatic_updates_enabled = $false
            first_launch_required = $true
        }
        synchronized_capabilities = @(
            "shared_agentos_kernel",
            "task_snapshots_and_recovery",
            "monotonic_execution_ledger",
            "task_graph",
            "durable_supervisor",
            "elastic_resource_governor",
            "openai_deepseek_kimi",
            "parallel_readonly_agents"
        )
        platform_gaps = @(
            "workspace_write_and_terminal",
            "keychain",
            "capability_marketplace",
            "developer_id_notarization",
            "automatic_updates"
        )
        executable = "NOVA.app/Contents/MacOS/NovaDesktop.Mac"
    }
    $packageManifest |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $packageDirectory "MAC-RELEASE.json") -Encoding utf8

    dotnet run --project $packagerProject `
        --configuration Release `
        -- $packageDirectory $archivePath $tarGzipPath
    if ($LASTEXITCODE -ne 0) {
        throw "macOS packaging failed for $rid with exit code $LASTEXITCODE."
    }
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $tarHash = (Get-FileHash -LiteralPath $tarGzipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $releaseEntries.Add([ordered]@{
        runtime = $rid
        app = "NOVA-Mac-$Version-$rid/NOVA.app"
        zip = [ordered]@{
            file = Split-Path -Leaf $archivePath
            sha256 = $hash
            size = (Get-Item -LiteralPath $archivePath).Length
        }
        tar_gz = [ordered]@{
            file = Split-Path -Leaf $tarGzipPath
            sha256 = $tarHash
            size = (Get-Item -LiteralPath $tarGzipPath).Length
        }
        release_gate = "CROSS_BUILT_UNSIGNED"
    })
    Write-Host "Published: $appDirectory"
    Write-Host "Archive:   $archivePath"
    Write-Host "SHA256:    $hash"
    Write-Host "TAR.GZ:    $tarGzipPath"
    Write-Host "SHA256:    $tarHash"
}

$releaseManifestPath = Join-Path $distRoot "macos-release-manifest.json"
[ordered]@{
    schema_version = 1
    product = "NOVA for Mac"
    version = $Version
    published_at = [DateTimeOffset]::Now.ToString("O")
    release_gate = "CROSS_BUILT_UNSIGNED"
    packages = $releaseEntries.ToArray()
} |
    ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath $releaseManifestPath -Encoding utf8
Write-Host "Manifest:  $releaseManifestPath"
