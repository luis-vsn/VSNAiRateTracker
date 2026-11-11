# VSN AI Rate Tracker - Windows Build & Installer Script
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    # Full version MAJOR.MINOR.PATCH.BUILD. When omitted it is derived from the latest
    # "vX.Y.Z" git tag with BUILD = 0 (local development builds).
    [string]$Version = "",
    [string]$TelemetryEndpoint = "",
    [string]$TelemetryHeaders = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Resolve-Path (Join-Path $scriptDir "..\..")
$projectPath = Join-Path $rootDir "VSNAiRateTracker\VSNAiRateTracker.csproj"
$publishDir = Join-Path $scriptDir "publish"
$distDir = Join-Path $rootDir "packaging\dist"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $baseVersion = "0.0.0"
    $latestTag = (& git -C $rootDir describe --tags --abbrev=0 --match "v[0-9]*" 2>$null)
    if ($LASTEXITCODE -eq 0 -and "$latestTag".Trim() -match '^v(\d+\.\d+\.\d+)$') {
        $baseVersion = $Matches[1]
    }
    $Version = "$baseVersion.0"
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Invalid version '$Version'. Expected MAJOR.MINOR.PATCH.BUILD (e.g. 0.0.2.0)."
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "Building VSN AI Rate Tracker Windows Installer ($Version)" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Clean output directories
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

# 2. Publish self-contained single file executable
Write-Host "`n[1/4] Publishing self-contained executable (win-x64)..." -ForegroundColor Yellow
dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

# 2.5 Inject default telemetry credentials if available (without hardcoding into git)
$endpoint = $TelemetryEndpoint
$headers = $TelemetryHeaders

if ([string]::IsNullOrWhiteSpace($endpoint) -or [string]::IsNullOrWhiteSpace($headers)) {
    $cfgPath = "$env:APPDATA\VSNAiRateTracker\config.enc"
    $keyPath = "$env:APPDATA\VSNAiRateTracker\master.key"
    if ((Test-Path $cfgPath) -and (Test-Path $keyPath)) {
        try {
            if ([Type]::GetType("System.Security.Cryptography.AesGcm") -ne $null) {
                $keyBytes = [System.IO.File]::ReadAllBytes($keyPath)
                $unprotected = [System.Security.Cryptography.ProtectedData]::Unprotect($keyBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                $enc = [System.IO.File]::ReadAllBytes($cfgPath)
                $nonce = [byte[]]::new(12)
                [Array]::Copy($enc, 1, $nonce, 0, 12)
                $tag = [byte[]]::new(16)
                [Array]::Copy($enc, 13, $tag, 0, 16)
                $cipherLen = $enc.Length - 29
                $cipher = [byte[]]::new($cipherLen)
                [Array]::Copy($enc, 29, $cipher, 0, $cipherLen)
                $aes = [System.Security.Cryptography.AesGcm]::new($unprotected, 16)
                $plain = [byte[]]::new($cipherLen)
                $aes.Decrypt($nonce, $cipher, $tag, $plain)
                $localConfig = [System.Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
                if ($localConfig.Telemetry -and $localConfig.Telemetry.Endpoint) {
                    if ([string]::IsNullOrWhiteSpace($endpoint)) { $endpoint = $localConfig.Telemetry.Endpoint }
                    if ([string]::IsNullOrWhiteSpace($headers)) { $headers = $localConfig.Telemetry.Headers }
                }
            } else {
                # Fallback for Windows PowerShell 5.1: invoke pwsh if available
                $pwshExe = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source
                if ($pwshExe) {
                    $jsonOut = & $pwshExe -NoProfile -Command "
                        `$cfgPath = `"$cfgPath`"
                        `$keyPath = `"$keyPath`"
                        `$keyBytes = [System.IO.File]::ReadAllBytes(`$keyPath)
                        `$unprotected = [System.Security.Cryptography.ProtectedData]::Unprotect(`$keyBytes, `$null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                        `$enc = [System.IO.File]::ReadAllBytes(`$cfgPath)
                        `$nonce = [byte[]]::new(12)
                        [Array]::Copy(`$enc, 1, `$nonce, 0, 12)
                        `$tag = [byte[]]::new(16)
                        [Array]::Copy(`$enc, 13, `$tag, 0, 16)
                        `$cipherLen = `$enc.Length - 29
                        `$cipher = [byte[]]::new(`$cipherLen)
                        [Array]::Copy(`$enc, 29, `$cipher, 0, `$cipherLen)
                        `$aes = [System.Security.Cryptography.AesGcm]::new(`$unprotected, 16)
                        `$plain = [byte[]]::new(`$cipherLen)
                        `$aes.Decrypt(`$nonce, `$cipher, `$tag, `$plain)
                        [System.Text.Encoding]::UTF8.GetString(`$plain)
                    "
                    if ($jsonOut) {
                        $localConfig = $jsonOut | ConvertFrom-Json
                        if ($localConfig.Telemetry -and $localConfig.Telemetry.Endpoint) {
                            if ([string]::IsNullOrWhiteSpace($endpoint)) { $endpoint = $localConfig.Telemetry.Endpoint }
                            if ([string]::IsNullOrWhiteSpace($headers)) { $headers = $localConfig.Telemetry.Headers }
                        }
                    }
                }
            }
        } catch {
            Write-Verbose "Could not read local config: $_"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($endpoint)) {
    $endpoint = [System.Environment]::GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
}
if ([string]::IsNullOrWhiteSpace($endpoint)) {
    $endpoint = [System.Environment]::GetEnvironmentVariable("TELEMETRY_ENDPOINT")
}
if ([string]::IsNullOrWhiteSpace($headers)) {
    $headers = [System.Environment]::GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS")
}
if ([string]::IsNullOrWhiteSpace($headers)) {
    $headers = [System.Environment]::GetEnvironmentVariable("TELEMETRY_HEADERS")
}

if (-not [string]::IsNullOrWhiteSpace($endpoint) -and -not [string]::IsNullOrWhiteSpace($headers)) {
    $defaultsObj = @{
        Telemetry = @{
            Enabled = $true
            Endpoint = $endpoint
            Headers = $headers
            ServiceName = "vsn-ai-rate-tracker"
            Protocol = "HttpProtobuf"
        }
    }
    $defaultsJson = $defaultsObj | ConvertTo-Json -Depth 5
    $defaultsEncFile = Join-Path $publishDir "defaults.enc"

    $hasAesGcm = $false
    try { [void][System.Security.Cryptography.AesGcm]; $hasAesGcm = $true } catch { }

    if ($hasAesGcm) {
        $derivedKey = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes("VSNAiRateTracker-Bundled-Defaults-AES256-GCM-v1"))
        $nonce = [byte[]]::new(12)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($nonce)
        $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($defaultsJson)
        $cipher = [byte[]]::new($plainBytes.Length)
        $tag = [byte[]]::new(16)
        $aes = [System.Security.Cryptography.AesGcm]::new($derivedKey, 16)
        $aes.Encrypt($nonce, $plainBytes, $cipher, $tag)

        $payload = [byte[]]::new(1 + 12 + 16 + $cipher.Length)
        $payload[0] = 0x01
        [Array]::Copy($nonce, 0, $payload, 1, 12)
        [Array]::Copy($tag, 0, $payload, 13, 16)
        [Array]::Copy($cipher, 0, $payload, 29, $cipher.Length)
        [System.IO.File]::WriteAllBytes($defaultsEncFile, $payload)
    } else {
        # Fallback for Windows PowerShell 5.1: invoke pwsh if available
        $pwshExe = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source
        if ($pwshExe) {
            $tempJson = Join-Path $scriptDir "temp_defaults.json"
            [System.IO.File]::WriteAllText($tempJson, $defaultsJson, [System.Text.Encoding]::UTF8)
            & $pwshExe -NoProfile -Command "
                `$derivedKey = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes('VSNAiRateTracker-Bundled-Defaults-AES256-GCM-v1'))
                `$nonce = [byte[]]::new(12)
                [System.Security.Cryptography.RandomNumberGenerator]::Fill(`$nonce)
                `$plainBytes = [System.IO.File]::ReadAllBytes('$tempJson')
                `$cipher = [byte[]]::new(`$plainBytes.Length)
                `$tag = [byte[]]::new(16)
                `$aes = [System.Security.Cryptography.AesGcm]::new(`$derivedKey, 16)
                `$aes.Encrypt(`$nonce, `$plainBytes, `$cipher, `$tag)
                `$payload = [byte[]]::new(1 + 12 + 16 + `$cipher.Length)
                `$payload[0] = 0x01
                [Array]::Copy(`$nonce, 0, `$payload, 1, 12)
                [Array]::Copy(`$tag, 0, `$payload, 13, 16)
                [Array]::Copy(`$cipher, 0, `$payload, 29, `$cipher.Length)
                [System.IO.File]::WriteAllBytes('$defaultsEncFile', `$payload)
            "
            if (Test-Path $tempJson) { Remove-Item $tempJson -Force }
        }
    }
    Write-Host "Injected encrypted bundled telemetry defaults ($defaultsEncFile) into installer package." -ForegroundColor Green
}

# 3. Create Portable ZIP
Write-Host "`n[2/4] Packaging portable ZIP package..." -ForegroundColor Yellow
$zipPath = Join-Path $distDir "VSNAiRateTracker-$Version-win-x64-portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
Write-Host "Portable package created at: $zipPath" -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host "`n[INFO] Skipping Inno Setup installer (--SkipInstaller passed)." -ForegroundColor Gray
    exit 0
}

# 4. Locate Inno Setup Compiler (ISCC)
Write-Host "`n[3/4] Searching for Inno Setup Compiler (ISCC.exe)..." -ForegroundColor Yellow
$isccPaths = @(
    "ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "C:\Program Files\Inno Setup 7\ISCC.exe"
)

$iscc = $null
foreach ($path in $isccPaths) {
    if (Get-Command $path -ErrorAction SilentlyContinue) {
        $iscc = $path
        break
    }
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

if (-not $iscc) {
    Write-Warning "Inno Setup Compiler (ISCC.exe) not found. Only portable ZIP was generated."
    Write-Warning "Install Inno Setup using: winget install JRSoftware.InnoSetup"
    exit 0
}

Write-Host "Found ISCC at: $iscc" -ForegroundColor Green

# 5. Compile Setup Installer
Write-Host "`n[4/4] Compiling Windows Setup Installer with Inno Setup..." -ForegroundColor Yellow
$issScript = Join-Path $scriptDir "setup.iss"

& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishDir" $issScript
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
}

$installerExe = Join-Path $distDir "VSNAiRateTracker-Setup-$Version-win-x64.exe"

# Explorer caches icons per file path: after rebuilding the installer with a new icon it keeps showing the
# old one. Ask the shell to refresh its icon cache so the installer shows the current application icon.
if ($IsWindows -or $env:OS -eq "Windows_NT") {
    try {
        & "$env:SystemRoot\System32\ie4uinit.exe" -show 2>$null
    } catch {
        Write-Verbose "Could not refresh the shell icon cache: $_"
    }
}
Write-Host "`n====================================================" -ForegroundColor Green
Write-Host "Build complete! Artifacts generated:" -ForegroundColor Green
if (Test-Path $installerExe) {
    $installerHash = (Get-FileHash $installerExe -Algorithm SHA256).Hash
    Write-Host "Installer: $installerExe" -ForegroundColor Cyan
    Write-Host "SHA-256:   $installerHash" -ForegroundColor Gray
}
if (Test-Path $zipPath) {
    $zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    Write-Host "Portable:  $zipPath" -ForegroundColor Cyan
    Write-Host "SHA-256:   $zipHash" -ForegroundColor Gray
}
Write-Host "====================================================" -ForegroundColor Green

