<#
.SYNOPSIS
  Builds self-contained single-file noPDF releases for all target platforms.

.DESCRIPTION
  Publishes the app for each runtime identifier as a self-contained, single-file
  executable into  <repo>\Release  named  noPDF-v<version>-<rid>[.exe].
  The version comes from Directory.Build.props (the single source of truth).

.EXAMPLE
  .\scripts\release.ps1                       # build artifacts for the current version
  .\scripts\release.ps1 -Version 0.0.2-beta.01  # bump version, then build
  .\scripts\release.ps1 -Tag -Push            # also create & push the git tag
  .\scripts\release.ps1 -Publish              # tag, push, and create a GitHub Release
                                              #   with the four binaries attached (needs gh)
#>
[CmdletBinding()]
param(
    [string]$Version,     # e.g. 0.0.2-beta.01 (omit "v"); if set, updates Directory.Build.props
    [switch]$Tag,         # create an annotated git tag v<version>
    [switch]$Push,        # push the tag to origin (implies -Tag)
    [switch]$Publish      # create a GitHub Release for the tag & upload the binaries (implies -Tag -Push; needs authenticated gh)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$proj = Join-Path $root 'src\NoPdf.App\NoPdf.App.csproj'
$outDir = Join-Path $root 'Release'

# Optionally bump the version in Directory.Build.props.
# InformationalVersion holds the full label (0.0.2-beta.01); VersionPrefix the
# numeric X.Y.Z the OS reads (must be valid semver, so strip the prerelease part).
if ($Version) {
    $prefix = ($Version -split '-')[0]
    (Get-Content $propsPath -Raw) `
        -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>" `
        -replace '<VersionPrefix>[^<]*</VersionPrefix>', "<VersionPrefix>$prefix</VersionPrefix>" |
        Set-Content $propsPath -Encoding utf8 -NoNewline
    Write-Host "Set version to $Version"
}

# Read the effective version (the full display label).
[xml]$props = Get-Content $propsPath
$ver = ($props.Project.PropertyGroup | Where-Object { $_.InformationalVersion }).InformationalVersion
if (-not $ver) { throw "Could not read <InformationalVersion> from $propsPath" }
$verTag = "v$ver"
Write-Host "Releasing noPDF $verTag" -ForegroundColor Cyan

# The four target platforms. Edit this list to change what gets built.
$targets = @(
    @{ rid = 'win-x64';   ext = '.exe' },   # Windows (x64)
    @{ rid = 'linux-x64'; ext = '' },       # Linux (x64)
    @{ rid = 'osx-x64';   ext = '' },       # macOS (Intel)
    @{ rid = 'osx-arm64'; ext = '' }        # macOS (Apple Silicon)
)

Get-Process NoPdf.App -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

foreach ($t in $targets) {
    $rid = $t.rid
    Write-Host "  publishing $rid ..." -ForegroundColor Yellow
    dotnet publish $proj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    $src = Join-Path $root "src\NoPdf.App\bin\Release\net10.0\$rid\publish\NoPdf.App$($t.ext)"
    $dst = Join-Path $outDir "noPDF-$verTag-$rid$($t.ext)"
    Copy-Item $src $dst -Force
    $mb = [math]::Round((Get-Item $dst).Length / 1MB)
    Write-Host "    -> $dst ($mb MB)" -ForegroundColor Green
}

Write-Host "`nArtifacts written to $outDir" -ForegroundColor Cyan

# -Publish implies tag + push (a GitHub Release needs the tag on the remote).
if ($Publish) { $Tag = $true; $Push = $true }

if ($Tag -or $Push) {
    Push-Location $root
    try {
        git tag -a $verTag -m "noPDF $verTag" 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Host "Created tag $verTag" } else { Write-Host "Tag $verTag already exists" }
        if ($Push) { git push origin $verTag; Write-Host "Pushed tag $verTag" }
    }
    finally { Pop-Location }
}

if ($Publish) {
    # Locate gh (may not be on PATH right after install).
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) {
        $fallback = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
        if (Test-Path $fallback) { $gh = $fallback }
    }
    if (-not $gh) { throw "gh (GitHub CLI) not found. Install it and run 'gh auth login'." }

    & $gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated. Run 'gh auth login' once, then re-run with -Publish." }

    $assets = Get-ChildItem $outDir -File | Where-Object { $_.Name -like "noPDF-$verTag-*" } | Select-Object -Expand FullName
    if (-not $assets) { throw "No artifacts found for $verTag in $outDir." }

    # Mark as prerelease when the version has a prerelease suffix (e.g. -beta.01).
    $preArgs = @(); if ($ver -match '-') { $preArgs = @('--prerelease') }

    Push-Location $root
    try {
        & $gh release view $verTag *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Release $verTag exists - uploading assets (clobber)..."
            & $gh release upload $verTag @assets --clobber
        }
        else {
            Write-Host "Creating GitHub Release $verTag..."
            & $gh release create $verTag @assets --title "noPDF $verTag" --notes "noPDF $verTag" @preArgs
        }
        if ($LASTEXITCODE -ne 0) { throw "gh release failed for $verTag" }
        Write-Host "Published GitHub Release $verTag with $($assets.Count) asset(s)" -ForegroundColor Green
    }
    finally { Pop-Location }
}
