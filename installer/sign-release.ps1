<#
    .SYNOPSIS
    Signs a drafted release so installed copies can update to it.

    .DESCRIPTION
    The release workflow builds and drafts, but cannot sign. Signing needs the
    update private key, and that key authorises an executable to run as
    LocalSystem on every hospital that installs this software - so it stays on
    the machine that generated it rather than living in a CI secret where a
    workflow edit, a compromised token, or anyone with repository admin could
    reach it.

    The cost of that decision is this script: one command, run locally, between
    drafting a release and publishing it.

    It downloads the installer that CI actually built, signs a manifest naming
    it, proves the manifest verifies against the public key that same build
    shipped with, and uploads it. A release without HospitalPM.update is
    invisible to the update page; a release with a manifest signed by the wrong
    key would be worse, which is why that is checked before anything is
    uploaded.

    .EXAMPLE
    ./installer/sign-release.ps1 -Version 1.2.0 -Key C:\keys\update-signing-key.pem

    .EXAMPLE
    ./installer/sign-release.ps1 -Version 1.2.0 -Key C:\keys\update-signing-key.pem -Publish
#>
[CmdletBinding()]
param(
    # The release to sign, as x.y.z. The tag is v<Version>.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # The update signing key. Never the licence one.
    [Parameter(Mandatory = $true)]
    [string]$Key,

    # What changed, shown on the hospital's update page. One or two sentences.
    [string]$Notes,

    # Publish the release afterwards. Without it the release stays a draft and
    # releases/latest keeps pointing at the previous one, so nothing reaches a
    # hospital until someone looks.
    [switch]$Publish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
$installerName = "HospitalPM-Setup-$Version.exe"

# One fixed name, because the feed URL baked into every shipped build points at
# it. Renaming this asset silently breaks updating for every installed copy.
$manifestName = "HospitalPM.update"

if (-not (Test-Path $Key)) {
    throw "No update signing key at $Key."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI (gh) is required to read and update the release."
}

Write-Host "Signing release $tag" -ForegroundColor Cyan

# --- 1. The release has to exist, and not already be signed ----------------

$releaseJson = gh release view $tag --json isDraft,assets 2>$null
if ($LASTEXITCODE -ne 0) {
    throw @"
No release tagged $tag.

The release workflow drafts one when a v$Version tag is pushed. Push the tag,
wait for the workflow, then run this.
"@
}

$release = $releaseJson | ConvertFrom-Json
$assetNames = @($release.assets | ForEach-Object { $_.name })

if ($assetNames -notcontains $installerName) {
    throw "Release $tag has no $installerName asset. The build may still be running, or it failed."
}

if ($assetNames -contains $manifestName) {
    # Replacing it is legitimate - a re-signed manifest for the same installer
    # verifies identically - but it is worth being deliberate about, because
    # the usual reason to be here twice is that something went wrong.
    Write-Warning "$tag already has a $manifestName. It will be replaced."
}

# --- 2. Work from what CI actually built ------------------------------------

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "hospitalpm-sign-$Version-$(Get-Random)"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    Write-Host "  Downloading $installerName from the release..."

    # The asset, not a local build. A manifest signed against a locally rebuilt
    # installer would name a hash that no hospital's download can match: the
    # build is deterministic, but "should be identical" and "is the file people
    # will run" are different claims.
    gh release download $tag --pattern $installerName --dir $staging
    if ($LASTEXITCODE -ne 0) { throw "Could not download $installerName from $tag." }

    if ($assetNames -contains "update-public-key.txt") {
        gh release download $tag --pattern "update-public-key.txt" --dir $staging
        if ($LASTEXITCODE -ne 0) { throw "Could not download update-public-key.txt from $tag." }
    }
    else {
        Write-Warning @"
This release has no update-public-key.txt asset, so the signature cannot be
checked against the key the build shipped with. That check is the only thing
that catches signing with the wrong key before a hospital does. Releases cut
after this change include it.
"@
    }

    $installerPath = Join-Path $staging $installerName
    $manifestPath = Join-Path $staging $manifestName

    # --- 3. Sign ------------------------------------------------------------

    if (-not $Notes) { $Notes = "Hospital PM $Version." }

    Write-Host "  Signing..."
    dotnet run --project (Join-Path $repoRoot "tools\HospitalPm.UpdateTool") -- `
        sign --key $Key --installer $installerPath --out $manifestPath --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "Signing failed." }

    # --- 4. Prove it will verify on a hospital's machine --------------------

    $publicKeyPath = Join-Path $staging "update-public-key.txt"
    if (Test-Path $publicKeyPath) {
        Write-Host "  Verifying against the key this build shipped with..."

        # Runs the product's own verifier over the product's own files. If the
        # private key does not match what CI baked in, this is where it stops -
        # before an unusable manifest is attached to a release.
        dotnet run --project (Join-Path $repoRoot "tools\HospitalPm.UpdateTool") -- `
            verify --public-key $publicKeyPath --file $manifestPath
        if ($LASTEXITCODE -ne 0) {
            throw @"
The manifest does not verify against the public key in release $tag.

This almost always means the key passed to -Key is not the private half of the
UPDATE_PUBLIC_KEY secret this build was made with. Nothing has been uploaded.
"@
        }
    }

    # --- 5. Upload ----------------------------------------------------------

    Write-Host "  Uploading $manifestName..."
    gh release upload $tag $manifestPath --clobber
    if ($LASTEXITCODE -ne 0) { throw "Could not upload $manifestName to $tag." }

    Write-Host ""
    Write-Host "Signed and attached." -ForegroundColor Green

    if ($Publish) {
        gh release edit $tag --draft=false | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not publish $tag." }
        Write-Host "Published. releases/latest now points at $tag." -ForegroundColor Green
        Write-Host "Every installed copy with a feed URL will offer $Version the next time someone checks."
    }
    else {
        Write-Host ""
        Write-Host "The release is still a draft, so nothing has changed for any hospital."
        Write-Host "Install it on a real machine first. When it is good:"
        Write-Host ""
        Write-Host "  gh release edit $tag --draft=false"
    }
}
finally {
    # The staged installer is a couple of hundred megabytes.
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
