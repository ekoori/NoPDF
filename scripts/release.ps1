<#
.SYNOPSIS
  Builds self-contained single-file noPDF releases for all target platforms.

.DESCRIPTION
  Version format is  v0.0.X-beta.YY :
    * X  is the public release number.
    * YY is the local build number (zero-padded).

  Running the script with no switches is a LOCAL build: it bumps YY, builds all
  target platforms as self-contained single-file executables into  <repo>\Release
  named  noPDF-v0.0.X-beta.YY-<rid>[.exe] , and does not touch git or GitHub.

  Running with -Publish is a PUBLIC release: it bumps X, resets YY to 00, builds,
  commits the version bump, tags  v0.0.X-beta.00 , and creates a GitHub Release
  with the binaries attached. GitHub therefore only ever holds the -beta.00 build
  of each X.

.EXAMPLE
  .\scripts\release.ps1                        # local build: X unchanged, YY++
  .\scripts\release.ps1 -Publish               # public release: X++, YY=00, tag + GitHub Release
  .\scripts\release.ps1 -Version 0.0.3-beta.00 # set an exact version, then build
#>
[CmdletBinding()]
param(
    [string]$Version,     # exact version override "0.0.X-beta.YY" (no leading v)
    [switch]$Publish      # public release: bump X, reset YY=00, commit, tag & GitHub Release
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$proj = Join-Path $root 'src\NoPdf.App\NoPdf.App.csproj'
$outDir = Join-Path $root 'Release'

function Parse-Version([string]$v) {
    if ($v -notmatch '^(\d+)\.(\d+)\.(\d+)-beta\.(\d+)$') {
        throw "Version '$v' is not in the expected 0.0.X-beta.YY form"
    }
    [pscustomobject]@{
        Major = [int]$Matches[1]; Minor = [int]$Matches[2]
        Patch = [int]$Matches[3]; Beta  = [int]$Matches[4]
    }
}
function Format-Version($p) { '{0}.{1}.{2}-beta.{3:00}' -f $p.Major, $p.Minor, $p.Patch, $p.Beta }

# ----- Decide the version for this build -----
[xml]$props = Get-Content $propsPath
$current = ($props.Project.PropertyGroup | Where-Object { $_.InformationalVersion }).InformationalVersion
if (-not $current) { throw "Could not read <InformationalVersion> from $propsPath" }

if ($Version) {
    $ver = $Version
}
else {
    $p = Parse-Version $current
    if ($Publish) { $p.Patch += 1; $p.Beta = 0 }   # public release: X++, YY=00
    else          { $p.Beta  += 1 }                 # local build:   YY++
    $ver = Format-Version $p
}
$prefix = ($ver -split '-')[0]   # numeric 0.0.X for the OS File/Product version

# ----- Persist the version (single source of truth) -----
(Get-Content $propsPath -Raw) `
    -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$ver</InformationalVersion>" `
    -replace '<VersionPrefix>[^<]*</VersionPrefix>', "<VersionPrefix>$prefix</VersionPrefix>" |
    Set-Content $propsPath -Encoding utf8 -NoNewline

$verTag = "v$ver"
Write-Host ("{0} build: noPDF {1}" -f ($(if ($Publish) { 'PUBLIC' } else { 'Local' })), $verTag) -ForegroundColor Cyan

# The target platforms. Edit this list to change what gets built.
$targets = @(
    @{ rid = 'win-x64';   ext = '.exe' },   # Windows (x64)
    @{ rid = 'win-x86';   ext = '.exe' },   # Windows (x86)
    @{ rid = 'linux-x64'; ext = '' },       # Linux (x64)
    @{ rid = 'osx-x64';   ext = '' }        # macOS (Intel)
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

if (-not $Publish) {
    Write-Host "Local build - not tagged or published. Use -Publish for a public release." -ForegroundColor DarkGray
    return
}

# ----- Public release: commit the version bump, tag, push, GitHub Release -----
Push-Location $root
try {
    # Native git/gh write progress and warnings (e.g. "LF will be replaced by CRLF")
    # to stderr. Under $ErrorActionPreference='Stop' PowerShell 5.1 wraps those lines
    # in a NativeCommandError and throws even on exit code 0, aborting the release.
    # Relax to Continue here and gate every step on $LASTEXITCODE instead.
    $ErrorActionPreference = 'Continue'

    git commit $propsPath -m "Release $verTag" 2>$null
    if ($LASTEXITCODE -eq 0) { Write-Host "Committed version bump" } else { Write-Host "Nothing to commit (version already committed)" }
    git push origin HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git push origin HEAD failed" }

    git tag -a $verTag -m "noPDF $verTag" 2>$null
    if ($LASTEXITCODE -eq 0) { Write-Host "Created tag $verTag" } else { Write-Host "Tag $verTag already exists" }
    git push origin $verTag 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git push origin $verTag failed" }

    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) {
        $fallback = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
        if (Test-Path $fallback) { $gh = $fallback }
    }
    if (-not $gh) { throw "gh (GitHub CLI) not found. Install it and run 'gh auth login'." }

    & $gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated. Run 'gh auth login', then re-run with -Publish." }

    $assets = Get-ChildItem $outDir -File | Where-Object { $_.Name -like "noPDF-$verTag-*" } | Select-Object -Expand FullName
    if (-not $assets) { throw "No artifacts found for $verTag in $outDir." }

    & $gh release view $verTag *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Release $verTag exists - uploading assets (clobber)..."
        & $gh release upload $verTag @assets --clobber
    }
    else {
        Write-Host "Creating GitHub Release $verTag..."
        & $gh release create $verTag @assets --title "noPDF $verTag" --notes "noPDF $verTag" --prerelease
    }
    if ($LASTEXITCODE -ne 0) { throw "gh release failed for $verTag" }
    Write-Host "Published GitHub Release $verTag with $($assets.Count) asset(s)" -ForegroundColor Green
}
finally { Pop-Location }
