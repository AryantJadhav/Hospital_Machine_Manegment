<#
.SYNOPSIS
    Builds the Windows installer from a clean tree.

.DESCRIPTION
    Packaging has an order that matters and a trap that does not announce
    itself:

      1. The SPA must be built BEFORE dotnet publish. Vite writes into
         src/HospitalPm.Api/wwwroot and the SDK collects that directory during
         publish. Reverse the order and the installer ships a binary whose UI
         is whatever was there last time.

      2. The publish directory must be emptied first. `dotnet publish -o` does
         not clean its output, so publishing repeatedly into the same folder
         accumulates every stale bundle from every previous build. CI never
         sees this because it starts from a fresh checkout; a release cut on a
         developer's machine does, and ships the lot.

    Run this rather than the individual commands.

.EXAMPLE
    pwsh installer\build.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",

    [string]$Rid = "win-x64",

    # Skips npm ci. Fine for a local rebuild, never for a release.
    [switch]$SkipWebInstall
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\$Rid"
$installerDir = Join-Path $repoRoot "artifacts\installer"

Write-Host "==> Hospital PM installer $Version ($Rid)" -ForegroundColor Cyan

# --- 1. Web ---------------------------------------------------------------
Push-Location (Join-Path $repoRoot "web")
try {
    if (-not $SkipWebInstall) {
        Write-Host "==> npm ci"
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }

    Write-Host "==> Building the SPA into wwwroot"
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "SPA build failed" }
}
finally {
    Pop-Location
}

# --- 2. Publish, into an empty directory ----------------------------------
if (Test-Path $publishDir) {
    Write-Host "==> Clearing $publishDir"
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "==> dotnet publish"
dotnet publish (Join-Path $repoRoot "src\HospitalPm.Api\HospitalPm.Api.csproj") `
    -c Release -r $Rid --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# The UI is the thing most likely to be silently missing, so it is checked
# rather than assumed.
$indexPath = Join-Path $publishDir "wwwroot\index.html"
if (-not (Test-Path $indexPath)) {
    throw "No wwwroot\index.html in the published output  -  the installer would ship no UI."
}

$assetCount = @(Get-ChildItem (Join-Path $publishDir "wwwroot\assets") -File).Count
Write-Host "    wwwroot assets: $assetCount file(s)"

# --- 3. Compile the installer ---------------------------------------------
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup"
}

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

Write-Host "==> Compiling the installer"
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "HospitalPm.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $installerDir "HospitalPM-Setup-$Version.exe"
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host ""
Write-Host "Installer: $setup ($sizeMb MB)" -ForegroundColor Green
Write-Host "NOTE: unsigned. Windows SmartScreen will warn until an OV code-signing certificate is applied." -ForegroundColor Yellow
