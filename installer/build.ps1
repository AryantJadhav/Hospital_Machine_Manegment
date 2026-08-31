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

# --- 2. PostgreSQL binaries -----------------------------------------------
# Downloaded rather than committed: 340 MB has no business in a git history,
# and the hash pin makes the result reproducible without it. Cached, so this
# costs nothing after the first run.
#
# Only bin, lib and share are kept. The archive also carries pgAdmin (683 MB),
# StackBuilder, docs and headers - none of which a hospital PC needs, and all
# of which would triple the installer.
$pgVersion = "17.11-1"
$pgSha256 = "6EABDF00D2893713B75DB4336A23C3FDF505F056E217EC6E2E95D901750CFEA3"
$pgUrl = "https://get.enterprisedb.com/postgresql/postgresql-$pgVersion-windows-x64-binaries.zip"

$cacheDir = Join-Path $repoRoot "artifacts\cache"
$pgZip = Join-Path $cacheDir "postgresql-$pgVersion-windows-x64-binaries.zip"
$pgOut = Join-Path $repoRoot "artifacts\pgsql"

New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

if (-not (Test-Path $pgZip)) {
    Write-Host "==> Downloading PostgreSQL $pgVersion (about 340 MB, once)"
    $previous = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"   # the progress bar makes this many times slower
    try {
        Invoke-WebRequest -Uri $pgUrl -OutFile $pgZip -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previous
    }
}

$actual = (Get-FileHash $pgZip -Algorithm SHA256).Hash
if ($actual -ne $pgSha256) {
    # Refuse rather than repair. A mismatch is either a corrupted download or
    # a different archive than the one this was tested against, and shipping
    # a database engine nobody verified is not a risk worth taking.
    throw "PostgreSQL archive hash mismatch.`
  expected $pgSha256`
  actual   $actual`
Delete $pgZip and retry."
}
Write-Host "    PostgreSQL archive verified"

if (Test-Path $pgOut) { Remove-Item -Recurse -Force $pgOut }
New-Item -ItemType Directory -Force -Path $pgOut | Out-Null

Write-Host "==> Extracting PostgreSQL (bin, lib, share)"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($pgZip)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -notmatch '^pgsql/(bin|lib|share)/' -and
            $entry.FullName -notmatch '^pgsql/(server_license|commandlinetools_3rd_party_licenses)\.txt$') {
            continue
        }
        if ($entry.FullName.EndsWith("/")) { continue }

        $relative = $entry.FullName -replace '^pgsql/', ''
        $target = Join-Path $pgOut ($relative -replace '/', '\')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally {
    $zip.Dispose()
}

foreach ($required in @("bin\postgres.exe", "bin\initdb.exe", "bin\pg_ctl.exe",
                        "bin\psql.exe", "bin\pg_dump.exe", "bin\pg_restore.exe")) {
    if (-not (Test-Path (Join-Path $pgOut $required))) {
        throw "PostgreSQL extraction is missing $required"
    }
}

$pgMb = [math]::Round((Get-ChildItem $pgOut -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "    PostgreSQL payload: $pgMb MB"

# --- 3. Publish, into an empty directory ----------------------------------
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

# --- 4. Compile the installer ---------------------------------------------
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
