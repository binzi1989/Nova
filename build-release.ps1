param(
    [string]$Version = "0.9.0-preview.29",
    [string]$Runtime = "win-x64",
    [ValidatePattern('^[A-Za-z0-9._-]*$')]
    [string]$PackageRevision = "",
    [string]$PackageUrl = "",
    [string]$InstallerPath = "",
    [string]$InnoCompilerPath = "",
    [string]$CodeSigningCertificateThumbprint = "",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [string]$GaBenchmarkReportPath = "",
    [switch]$RequireTrustedRelease
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "NovaDesktop\NovaDesktop.csproj"
$distRoot = Join-Path $projectRoot "dist"
$buildOutputRoot = Join-Path $projectRoot ".release-bin\"
$packageRevisionSuffix = if ([string]::IsNullOrWhiteSpace($PackageRevision)) {
    ""
} else {
    "-$PackageRevision"
}
$publishDirectory = Join-Path $distRoot "NOVA-$Version-$Runtime$packageRevisionSuffix"
$archivePath = Join-Path $distRoot "NOVA-$Version-$Runtime$packageRevisionSuffix.zip"
$manifestPath = Join-Path $distRoot "release-manifest.json"
$manifestSignaturePath = Join-Path $distRoot "release-manifest.sig.json"
$isStableVersion = $Version -match '^\d+\.\d+\.\d+$'
$trustedReleaseRequired = $RequireTrustedRelease -or $isStableVersion
$trustedPackageUri = $null

if (-not [string]::IsNullOrWhiteSpace($PackageUrl)) {
    $candidateUri = $null
    if (-not [Uri]::TryCreate($PackageUrl, [UriKind]::Absolute, [ref]$candidateUri) `
        -or $candidateUri.Scheme -ne "https" `
        -or $candidateUri.Host.EndsWith(".invalid", [StringComparison]::OrdinalIgnoreCase)) {
        throw "PackageUrl must be a real absolute HTTPS URL."
    }
    $trustedPackageUri = $candidateUri
}
if ($trustedReleaseRequired -and $null -eq $trustedPackageUri) {
    throw "Trusted 1.0/GA release blocked: provide a real HTTPS PackageUrl."
}
if ($trustedReleaseRequired -and [string]::IsNullOrWhiteSpace($InstallerPath)) {
    if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
        throw "Trusted 1.0/GA release blocked: provide InnoCompilerPath so the installer can be built from the signed payload."
    }
}
if ($trustedReleaseRequired -and [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
    throw "Trusted 1.0/GA release blocked: provide a Windows code-signing certificate thumbprint."
}
if ($trustedReleaseRequired -and [string]::IsNullOrWhiteSpace($GaBenchmarkReportPath)) {
    throw "Trusted 1.0/GA release blocked: provide the 30-task GA benchmark report."
}

$gaBenchmark = $null
if (-not [string]::IsNullOrWhiteSpace($GaBenchmarkReportPath)) {
    $resolvedBenchmarkPath = (Resolve-Path -LiteralPath $GaBenchmarkReportPath).Path
    $gaBenchmark = Get-Content -LiteralPath $resolvedBenchmarkPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ($gaBenchmark.suite -ne "NOVA-1.0-GA" `
        -or -not $gaBenchmark.passed `
        -or [int]$gaBenchmark.total_tasks -ne 30 `
        -or [int]$gaBenchmark.total_runs -ne 90 `
        -or [double]$gaBenchmark.proven_rate -lt 0.80 `
        -or [double]$gaBenchmark.terminal_accuracy -lt 0.90) {
        throw "The supplied GA benchmark report does not satisfy the frozen 30-task release thresholds."
    }
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Resolve-CodeSigningCertificate {
    param([string]$Thumbprint)

    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return $null
    }
    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    $certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object {
            $_.Thumbprint -eq $normalized `
                -and $_.HasPrivateKey `
                -and $_.NotBefore -le (Get-Date) `
                -and $_.NotAfter -gt (Get-Date) `
                -and $_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3"
        } |
        Select-Object -First 1
    if ($null -eq $certificate) {
        throw "No valid current-user code-signing certificate with a private key matched the supplied thumbprint."
    }
    return $certificate
}

function Set-TrustedAuthenticodeSignature {
    param(
        [string]$Path,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $signed = Set-AuthenticodeSignature `
        -LiteralPath $Path `
        -Certificate $Certificate `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampServer
    if ($signed.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signing failed for $Path`: $($signed.Status) $($signed.StatusMessage)"
    }
}

$codeSigningCertificate = Resolve-CodeSigningCertificate $CodeSigningCertificateThumbprint

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $manifestSignaturePath) {
    Remove-Item -LiteralPath $manifestSignaturePath -Force
}

dotnet publish $projectFile `
    --configuration Release `
    --runtime $Runtime `
    --self-contained false `
    --output $publishDirectory `
    -p:Version=$Version `
    -p:BaseOutputPath=$buildOutputRoot `
    --nologo

$executablePath = Join-Path $publishDirectory "NovaDesktop.exe"
if ($trustedReleaseRequired) {
    dotnet run `
        --project (Join-Path $projectRoot "NovaDesktop.SmokeTests\NovaDesktop.SmokeTests.csproj") `
        --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Trusted 1.0/GA release blocked: the complete smoke suite did not pass."
    }
}
if ($null -ne $codeSigningCertificate) {
    Set-TrustedAuthenticodeSignature $executablePath $codeSigningCertificate
}

$resolvedInstallerPath = $null
if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $resolvedInstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
} elseif (-not [string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $resolvedInnoCompiler = (Resolve-Path -LiteralPath $InnoCompilerPath).Path
    $installerDefinition = Join-Path $projectRoot "installer\NOVA.iss"
    $setupOutputName = "NOVA-Setup-$Version-$Runtime$packageRevisionSuffix"
    & $resolvedInnoCompiler `
        "/DMyAppVersion=$Version" `
        "/DPublishDir=$publishDirectory" `
        "/DSetupOutputName=$setupOutputName" `
        $installerDefinition
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed to build the Windows installer."
    }
    $resolvedInstallerPath = Join-Path $distRoot "$setupOutputName.exe"
    if (-not (Test-Path -LiteralPath $resolvedInstallerPath)) {
        throw "Inno Setup reported success but the expected installer was not created."
    }
}
if ($null -ne $codeSigningCertificate -and $null -ne $resolvedInstallerPath) {
    Set-TrustedAuthenticodeSignature $resolvedInstallerPath $codeSigningCertificate
}

$executableSignature = Get-AuthenticodeSignature -LiteralPath $executablePath
$executableSignatureValid = $executableSignature.Status -eq [System.Management.Automation.SignatureStatus]::Valid
$installerSignature = $null
$installerSignatureValid = $false
if ($null -ne $resolvedInstallerPath) {
    $installerSignature = Get-AuthenticodeSignature -LiteralPath $resolvedInstallerPath
    $installerSignatureValid = $installerSignature.Status -eq [System.Management.Automation.SignatureStatus]::Valid
}
if ($trustedReleaseRequired -and -not $executableSignatureValid) {
    throw "Trusted 1.0/GA release blocked: NovaDesktop.exe does not have a valid Authenticode signature."
}
if ($trustedReleaseRequired -and -not $installerSignatureValid) {
    throw "Trusted 1.0/GA release blocked: the Windows installer does not have a valid Authenticode signature."
}

if ($trustedReleaseRequired) {
    $installSmokeRoot = Join-Path $projectRoot ".ga-install-smoke"
    $resolvedSmokeRoot = [IO.Path]::GetFullPath($installSmokeRoot)
    $resolvedProjectRoot = [IO.Path]::GetFullPath($projectRoot)
    if (-not $resolvedSmokeRoot.StartsWith(
        $resolvedProjectRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Fresh-install smoke target escaped the project root."
    }
    if (Test-Path -LiteralPath $resolvedSmokeRoot) {
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
    }
    try {
        & $resolvedInstallerPath `
            "/VERYSILENT" `
            "/SUPPRESSMSGBOXES" `
            "/NORESTART" `
            "/DIR=$resolvedSmokeRoot"
        if ($LASTEXITCODE -ne 0) {
            throw "The signed installer failed the isolated fresh-install smoke."
        }
        $installedExecutable = Join-Path $resolvedSmokeRoot "NovaDesktop.exe"
        & $installedExecutable "--startup-smoke"
        if ($LASTEXITCODE -ne 0) {
            throw "The isolated installed application failed startup smoke."
        }
        & $installedExecutable "--attachment-render-smoke"
        if ($LASTEXITCODE -ne 0) {
            throw "The isolated installed application failed attachment rendering smoke."
        }
    } finally {
        $uninstaller = Join-Path $resolvedSmokeRoot "unins000.exe"
        if (Test-Path -LiteralPath $uninstaller) {
            & $uninstaller "/VERYSILENT" "/SUPPRESSMSGBOXES" "/NORESTART" | Out-Null
        }
        if (Test-Path -LiteralPath $resolvedSmokeRoot) {
            Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
        }
    }
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$releaseBlockers = [System.Collections.Generic.List[string]]::new()
if ($null -eq $trustedPackageUri) {
    $releaseBlockers.Add("No trusted HTTPS update source is configured.")
}
if (-not $executableSignatureValid) {
    $releaseBlockers.Add("NovaDesktop.exe is not Authenticode signed.")
}
if (-not $installerSignatureValid) {
    $releaseBlockers.Add("A signed Windows installer was not supplied.")
}
$releaseTrusted = $releaseBlockers.Count -eq 0
$signature = $null
if ($executableSignatureValid) {
    $signature = [ordered]@{
        type = "Authenticode + detached RSA-SHA256"
        manifest = Split-Path -Leaf $manifestSignaturePath
        executable = [ordered]@{
            status = $executableSignature.Status.ToString()
            signer = $executableSignature.SignerCertificate.Subject
            thumbprint = $executableSignature.SignerCertificate.Thumbprint
        }
        installer = if ($installerSignatureValid) {
            [ordered]@{
                file = Split-Path -Leaf $resolvedInstallerPath
                status = $installerSignature.Status.ToString()
                signer = $installerSignature.SignerCertificate.Subject
                thumbprint = $installerSignature.SignerCertificate.Thumbprint
            }
        } else {
            $null
        }
    }
}
$manifest = [ordered]@{
    schema_version = 3
    product = "NOVA Desktop"
    version = $Version
    runtime = $Runtime
    published_at = [DateTimeOffset]::Now.ToString("O")
    release_gate = [ordered]@{
        status = if ($releaseTrusted) { "TRUSTED" } else { "PREVIEW_UNSIGNED" }
        automatic_updates_enabled = $releaseTrusted
        blockers = $releaseBlockers.ToArray()
        ga_benchmark = if ($null -eq $gaBenchmark) {
            $null
        } else {
            [ordered]@{
                suite = $gaBenchmark.suite
                total_tasks = [int]$gaBenchmark.total_tasks
                total_runs = [int]$gaBenchmark.total_runs
                proven_rate = [double]$gaBenchmark.proven_rate
                terminal_accuracy = [double]$gaBenchmark.terminal_accuracy
            }
        }
    }
    package = [ordered]@{
        file = Split-Path -Leaf $archivePath
        sha256 = $hash
        size = (Get-Item -LiteralPath $archivePath).Length
        url = if ($null -eq $trustedPackageUri) { $null } else { $trustedPackageUri.AbsoluteUri }
    }
    signature = $signature
}
$manifestJson = $manifest | ConvertTo-Json -Depth 8
$manifestJson | Set-Content -LiteralPath $manifestPath -Encoding utf8

if ($releaseTrusted) {
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $codeSigningCertificate)
    if ($null -eq $privateKey) {
        throw "The trusted release certificate does not expose an RSA private key for manifest signing."
    }
    try {
        $signedBytes = $privateKey.SignData(
            $manifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $publicKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey(
            $codeSigningCertificate)
        if ($null -eq $publicKey -or -not $publicKey.VerifyData(
            $manifestBytes,
            $signedBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
            throw "Detached release-manifest signature verification failed."
        }
        $detachedSignature = [ordered]@{
            schema_version = 1
            algorithm = "RSA-SHA256-PKCS1"
            manifest = Split-Path -Leaf $manifestPath
            manifest_sha256 = (
                Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            signer = $codeSigningCertificate.Subject
            thumbprint = $codeSigningCertificate.Thumbprint
            signature = [Convert]::ToBase64String($signedBytes)
        }
        $detachedSignature |
            ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $manifestSignaturePath -Encoding utf8
    } finally {
        $privateKey.Dispose()
        if ($null -ne $publicKey) {
            $publicKey.Dispose()
        }
    }
}

Write-Host "Published: $publishDirectory"
Write-Host "Archive:   $archivePath"
Write-Host "SHA256:    $hash"
Write-Host "Manifest:  $manifestPath"
if (Test-Path -LiteralPath $manifestSignaturePath) {
    Write-Host "Signature: $manifestSignaturePath"
}
