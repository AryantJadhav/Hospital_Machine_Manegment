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
    [switch]$SkipWebInstall,

    <#
        The public half of the licence signing key: base64 SubjectPublicKeyInfo,
        exactly as the licence tool's keygen prints it.

        Defaults to the environment so a release pipeline can pass it as a
        secret. It is a PUBLIC key and not sensitive - the private half never
        leaves the machine that generated it and never enters this repository.
    #>
    [string]$LicencePublicKey = $env:HOSPITALPM_LICENCE_PUBLIC_KEY,

    <#
        The public half of the UPDATE signing key, as the update tool
        prints it. A different key from the licence one: this signature
        authorises an executable that a hospital runs as LocalSystem,
        while a licence signature only authorises use of software they
        already have.

        A build without it cannot install updates at all. That is the safe
        failure, but it is permanent for every machine that installs the
        build - so a release refuses to be cut without it, exactly as it
        does for the licence key.
    #>
    [string]$UpdatePublicKey = $env:HOSPITALPM_UPDATE_PUBLIC_KEY,

    <#
        Where an installed copy will look for updates, baked into the build.

        Not secret, and not a hospital's choice: it is the address our releases
        live at. It has to be set here rather than on site because the
        installer rewrites the machine's settings on every upgrade, and because
        a machine that shipped without one cannot be given one remotely.

        Empty is valid and means "this build never looks online" - the USB
        stick path still works. That is the right answer for a development
        build and the wrong one for a release, which is why the release
        workflow supplies it.
    #>
    [string]$UpdateFeedUrl = $env:HOSPITALPM_UPDATE_FEED_URL,

    <#
        Builds an installer that cannot verify any licence.

        Required to be explicit, because the failure it prevents is silent: a
        binary with no public key reports "licensing is not enforced" and
        works perfectly, so nothing about a release cut without a key looks
        wrong until a hospital is sent a licence that their copy cannot check.

        Correct for development and for the CI smoke test. Never for a release.
    #>
    [switch]$Unlicensed,

    <#
        Builds an installer that cannot verify any update.

        Separate from -Unlicensed because the consequences are different:
        an unlicensed build works and nags, while a build with no update
        key can never be updated from a file for as long as it is
        installed. Both are correct for development and the CI smoke test,
        and neither is ever correct for a release.
    #>
    [switch]$NoUpdateKey,

    <#
        SHA-1 thumbprint of the OV code-signing certificate installed in the
        Windows certificate store (CurrentUser\My or LocalMachine\My).

        When provided, the built installer EXE is Authenticode-signed with
        signtool, which removes the SmartScreen warning and the antivirus
        false-positives that come with unsigned installers.

        Defaults to the environment so the release pipeline can pass it as a
        secret. It is not itself sensitive — the thumbprint is embedded in
        the signed binary for anyone to read — but the certificate it points
        to is.
    #>
    [string]$CertificateThumbprint = $env:HOSPITALPM_CODESIGN_THUMBPRINT
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\$Rid"
$installerDir = Join-Path $repoRoot "artifacts\installer"

Write-Host "==> Hospital PM installer $Version ($Rid)" -ForegroundColor Cyan

<#
    Checks the licence public key looks like one, before anything is built.

    A P-256 SubjectPublicKeyInfo is exactly 91 bytes and begins with 0x30, the
    DER SEQUENCE tag. Checking the shape catches the realistic failure - a
    secret that was truncated, wrapped, or pasted with something else around it
    - which would otherwise produce an installer that builds, installs, runs,
    and rejects every licence the vendor ever issues.

    Deliberately structural rather than a real ECDSA import: ImportSubject-
    PublicKeyInfo is .NET Core only, and this script has to keep working under
    the Windows PowerShell 5.1 a release might be cut from.
#>
function Assert-PublicKey([string]$Value, [string]$Which) {
    $bytes = $null
    try {
        $bytes = [Convert]::FromBase64String($Value.Trim())
    }
    catch {
        throw "The $Which public key is not valid base64. Pass the contents of the keygen output file exactly as it was written."
    }

    if ($bytes.Length -ne 91 -or $bytes[0] -ne 0x30) {
        throw "The $Which public key is not a P-256 public key: expected 91 bytes starting 0x30, got $($bytes.Length) bytes starting 0x$('{0:X2}' -f $bytes[0])."
    }
}

# Checked here rather than after the build, so a release cut without a key
# fails in a second instead of five minutes.
if (-not $LicencePublicKey -and -not $Unlicensed) {
    throw @"
No licence public key.

A release build must carry the public half of the licence signing key, or the
installer it produces cannot verify any licence ever issued - and says so only
on the Licence page, long after it has shipped.

Pass it one of these ways:

  -LicencePublicKey <base64>              the contents of public-key.txt
  `$env:HOSPITALPM_LICENCE_PUBLIC_KEY      preferred in a pipeline

If no signing key exists yet, create one ONCE, keep signing-key.pem off this
machine, and never lose it - every licence issued under it becomes
unverifiable if you do:

  dotnet run --project tools\HospitalPm.LicenceTool -- keygen --out <dir>

To build deliberately without licensing - development, or the CI smoke test -
pass -Unlicensed.
"@
}

# The same shape of failure, one step worse: a build with no update key can
# never install an update from a file, and there is no way to fix that
# remotely on a machine with no internet.
if (-not $UpdatePublicKey -and -not $NoUpdateKey) {
    throw @"
No update public key.

A release build must carry the public half of the update signing key, or no
machine that installs it can ever be updated from a file - and on an
air-gapped hospital PC there is no second route.

Pass it one of these ways:

  -UpdatePublicKey <base64>              the contents of update-public-key.txt
  `$env:HOSPITALPM_UPDATE_PUBLIC_KEY      preferred in a pipeline

If no update signing key exists yet, create one ONCE. It is a different key
from the licence one on purpose - it authorises code to run as LocalSystem -
and losing it means no installed copy can verify an update again:

  dotnet run --project tools\HospitalPm.UpdateTool -- keygen --out <dir>

To build deliberately without it - development, or the CI smoke test - pass
-NoUpdateKey.
"@
}

if ($LicencePublicKey) {
    Assert-PublicKey $LicencePublicKey "licence"
    Write-Host "    Licence public key: present and well-formed"
}
else {
    Write-Host "    Licence public key: NONE (-Unlicensed) - this build cannot verify a licence" -ForegroundColor Yellow
}

if ($UpdatePublicKey) {
    Assert-PublicKey $UpdatePublicKey "update"
    Write-Host "    Update public key: present and well-formed"
}
else {
    Write-Host "    Update public key: NONE (-NoUpdateKey) - this build cannot install an update" -ForegroundColor Yellow
}

if ($UpdateFeedUrl) {
    # https only, and checked here rather than at run time: a typo baked into a
    # release is permanent for every machine that installs it, and the product
    # would refuse the address silently on a page nobody opens.
    $parsed = $null
    if (-not [Uri]::TryCreate($UpdateFeedUrl.Trim(), [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -ne 'https') {
        throw "The update feed URL must be an absolute https address. Got '$UpdateFeedUrl'."
    }
    if (-not $parsed.AbsolutePath.EndsWith('.update')) {
        throw "The update feed URL must point at a .update file, not at a folder or an index. Got '$UpdateFeedUrl'."
    }
    Write-Host "    Update feed URL: $UpdateFeedUrl"
}
else {
    Write-Host "    Update feed URL: NONE - this build will only update from a file" -ForegroundColor Yellow
}

if ($LicencePublicKey -and $UpdatePublicKey -and
    $LicencePublicKey.Trim() -eq $UpdatePublicKey.Trim()) {
    # Not a style preference. One key means anyone trusted to issue a
    # licence is thereby trusted to push code that runs as LocalSystem on
    # every install, and rotating either one breaks the other.
    throw "The licence and update public keys are the same. They must be different keys."
}

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
$pgVersion = "18.6-1"
$pgSha256 = "FBE23DA234EE31547BF8A36D29DFD81E82B849DF2D2B78D2EECB43D360252F8C"
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

# -p:Version stamps the assembly, and the assembly is what the running service
# reports as its own version - on /health, on the update page, and in the
# comparison that decides whether an update is newer than what is installed.
#
# Without it every build ships 1.0.0.0 forever. The installer's own version
# would still be right, so an upgrade would install correctly and then report
# the old number, offer the same update again, and keep offering it. The
# "already installed" guard is inert in exactly the case it exists for.
dotnet publish (Join-Path $repoRoot "src\HospitalPm.Api\HospitalPm.Api.csproj") `
    -c Release -r $Rid --self-contained true -o $publishDir `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Read back rather than trust the switch. A version that silently failed to
# apply is invisible until a hospital cannot get past an update it already has.
#
# Read off the executable's version resource, not with GetAssemblyName: this
# is a self-contained single-file publish, so hospitalpm.exe is a native host
# with the managed assembly bundled inside it and there is no .dll to inspect.
# Both numbers come from the same -p:Version, so the resource is a faithful
# proxy for what Assembly.GetEntryAssembly() will report at run time.
$exePath = Join-Path $publishDir "hospitalpm.exe"
if (-not (Test-Path $exePath)) {
    throw "No hospitalpm.exe in the published output."
}

$stamped = (Get-Item $exePath).VersionInfo.FileVersion
if ($stamped -ne "$Version.0") {
    throw "The published binary reports version $stamped, expected $Version.0. An install would report the wrong version and mis-handle updates."
}
Write-Host "    stamped version: $stamped"

# The UI is the thing most likely to be silently missing, so it is checked
# rather than assumed.
$indexPath = Join-Path $publishDir "wwwroot\index.html"
if (-not (Test-Path $indexPath)) {
    throw "No wwwroot\index.html in the published output  -  the installer would ship no UI."
}

$assetCount = @(Get-ChildItem (Join-Path $publishDir "wwwroot\assets") -File).Count
Write-Host "    wwwroot assets: $assetCount file(s)"

# --- 3a. The licence public key -------------------------------------------
# Written into the PUBLISHED appsettings.json rather than the one in source,
# so the key never lands in the repository and a working tree is never left
# dirty by a release.
#
# This file ships into Program Files, which standard users cannot write. That
# matters: the public key is what decides whether a licence is genuine, and a
# hospital that could edit it could substitute their own and sign whatever
# they liked. An administrator still can, but an administrator can replace the
# binary too.
#
# The machine-specific settings the installer writes live in ProgramData and
# layer OVER this file; they never mention Licence, so nothing downstream
# overwrites what is set here.
function Write-PublicKey([string]$SettingsPath, [string]$Section, [string]$Value) {
    $settingsText = Get-Content $SettingsPath -Raw

    # A targeted replacement rather than parse-and-reserialise: ConvertTo-Json
    # reorders and reformats the whole file, and this is the file a reviewer
    # reads to see what a build shipped.
    #
    # Scoped to its own section. There are two PublicKey settings now, and a
    # pattern that matched either would happily write the update key into the
    # licence slot - a build that then rejects every licence ever issued and
    # says so only on a page nobody opens until months later.
    $pattern = '("' + $Section + '"\s*:\s*\{\s*"PublicKey"\s*:\s*)""'
    $matchCount = ([regex]::Matches($settingsText, $pattern)).Count
    if ($matchCount -ne 1) {
        throw "Expected exactly one empty $Section PublicKey setting in $SettingsPath, found $matchCount."
    }

    $settingsText = [regex]::Replace($settingsText, $pattern, "`${1}""$($Value.Trim())""")

    # No BOM. PowerShell 5.1 writes one for -Encoding utf8, and a leading
    # EF BB BF has already broken one machine-written JSON file in this
    # project.
    [System.IO.File]::WriteAllText(
        $SettingsPath, $settingsText, (New-Object System.Text.UTF8Encoding($false)))

    # Read back rather than trust the write. This is the last moment a key can
    # be confirmed present; after this it is inside a compressed installer.
    $written = (Get-Content $SettingsPath -Raw | ConvertFrom-Json).$Section.PublicKey
    if ($written -ne $Value.Trim()) {
        throw "The $Section public key did not survive being written to $SettingsPath."
    }
    Write-Host "    $Section public key written into appsettings.json and read back"
}

<#
    .SYNOPSIS
    Writes Update:FeedUrl into the published settings.

    .DESCRIPTION
    A plain anchored pattern rather than the section-scoped one above, because
    "FeedUrl" appears once in the whole file while "PublicKey" appears twice.

    This has to be baked in at build time rather than set on site. The
    installer deletes and rewrites the machine's appsettings.json on every
    upgrade, so a URL added by hand in ProgramData survives exactly until the
    next update - and a machine that shipped without one can never be given
    one remotely, which on an air-gapped PC means never.
#>
function Write-FeedUrl([string]$SettingsPath, [string]$Value) {
    $settingsText = Get-Content $SettingsPath -Raw

    $pattern = '("FeedUrl"\s*:\s*)""'
    $matchCount = ([regex]::Matches($settingsText, $pattern)).Count
    if ($matchCount -ne 1) {
        throw "Expected exactly one empty FeedUrl setting in $SettingsPath, found $matchCount."
    }

    $settingsText = [regex]::Replace($settingsText, $pattern, "`${1}""$($Value.Trim())""")

    [System.IO.File]::WriteAllText(
        $SettingsPath, $settingsText, (New-Object System.Text.UTF8Encoding($false)))

    $written = (Get-Content $SettingsPath -Raw | ConvertFrom-Json).Update.FeedUrl
    if ($written -ne $Value.Trim()) {
        throw "The update feed URL did not survive being written to $SettingsPath."
    }
    Write-Host "    Update feed URL written into appsettings.json and read back"
}

if ($LicencePublicKey -or $UpdatePublicKey -or $UpdateFeedUrl) {
    $settingsPath = Join-Path $publishDir "appsettings.json"
    if (-not (Test-Path $settingsPath)) {
        throw "No appsettings.json in the published output, so the public keys have nowhere to go."
    }

    if ($LicencePublicKey) { Write-PublicKey $settingsPath "Licence" $LicencePublicKey }
    if ($UpdatePublicKey)  { Write-PublicKey $settingsPath "Update"  $UpdatePublicKey }
    if ($UpdateFeedUrl)    { Write-FeedUrl   $settingsPath $UpdateFeedUrl }
}

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

# --- 5. Code-sign the installer (optional) ---------------------------------
#
# Without an Authenticode signature, Windows SmartScreen warns on download,
# warns again on first run, and some hospital antivirus products delete the
# file outright. An OV certificate removes all of those.
#
# signtool is part of the Windows SDK. It ships on GitHub Actions runners and
# on any machine with Visual Studio. The certificate must be installed in the
# Windows certificate store and identified by its SHA-1 thumbprint.

if ($CertificateThumbprint) {
    Write-Host "==> Code-signing the installer"

    $signtool = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22000.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    # Fall back to whatever is on the PATH — CI runners install the SDK in
    # unpredictable directories, but signtool is always reachable.
    if (-not $signtool) {
        $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    }
    if (-not $signtool) {
        throw "signtool.exe not found. Install the Windows SDK or add it to the PATH."
    }

    # SHA-256 signature with RFC 3161 timestamp. The timestamp is critical:
    # without it, the signature expires when the certificate does, and every
    # installer ever shipped stops being trusted on that date.
    & $signtool sign `
        /sha1 $CertificateThumbprint `
        /fd sha256 `
        /tr http://timestamp.digicert.com `
        /td sha256 `
        /d "Hospital PM" `
        $setup

    if ($LASTEXITCODE -ne 0) {
        throw "Code signing failed (signtool exit $LASTEXITCODE). Check the certificate thumbprint and that the certificate is installed."
    }

    # Verify the signature was actually applied. signtool exits 0 even when
    # it warns about an untrusted root, so an explicit verify catches that.
    & $signtool verify /pa $setup | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: the signature was applied but does not verify. The certificate chain may be incomplete." -ForegroundColor Yellow
    }

    Write-Host "    Signed with certificate $CertificateThumbprint" -ForegroundColor Green
}

Write-Host ""
Write-Host "Installer: $setup ($sizeMb MB)" -ForegroundColor Green
if (-not $CertificateThumbprint) {
    Write-Host "NOTE: unsigned. Windows SmartScreen will warn until an OV code-signing certificate is applied." -ForegroundColor Yellow
}

# Said at the end as well as the beginning. The beginning scrolls away, and
# this is the line that distinguishes a releasable installer from one that
# will reject every licence it is ever sent.
if (-not $LicencePublicKey) {
    Write-Host "NOTE: built WITHOUT a licence public key. This installer cannot verify any licence. Do not release it." -ForegroundColor Yellow
}
