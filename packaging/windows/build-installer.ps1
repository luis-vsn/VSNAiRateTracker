# VSN AI Rate Tracker - Windows Build & Installer Script
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Resolve-Path (Join-Path $scriptDir "..\..")
$projectPath = Join-Path $rootDir "VSNAiRateTracker\VSNAiRateTracker.csproj"
$publishDir = Join-Path $scriptDir "publish"
$distDir = Join-Path $rootDir "packaging\dist"

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
