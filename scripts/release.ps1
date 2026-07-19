<#
.SYNOPSIS
  Builds self-contained single-file noPDF releases for all target platforms.

.DESCRIPTION
  Version is  v0.0.X-beta.YY :
    * X  (VersionPrefix in Directory.Build.props) is the public release line.
    * YY (build-number.txt, gitignored) is auto-incremented on every Debug build.

  With no switches this is a LOCAL build: it publishes all target platforms as
  self-contained single-file executables into  <repo>\Release  named
  noPDF-v0.0.X-beta.YY-<rid>[.exe] , at the CURRENT version, touching neither the
  version files nor git.

  With -Publish it is a PUBLIC release: X += 1, YY resets to 00, it rolls
  RELEASE_NOTES.md, commits the version bump + notes, tags v0.0.X-beta.00, and
  creates a GitHub Release with the binaries attached.

.EXAMPLE
  .\scripts\release.ps1                         # local build at the current version
  .\scripts\release.ps1 -Publish                # public release: X++, YY=00, tag + GitHub Release
  .\scripts\release.ps1 -Version 0.0.3-beta.00  # set an exact version, then build
#>
[CmdletBinding()]
param(
    [string]$Version,     # exact version override "0.0.X-beta.YY" (no leading v)
    [switch]$Publish      # public release: bump X, reset YY=00, commit, tag & GitHub Release
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$buildNoPath = Join-Path $root 'build-number.txt'
$notesPath = Join-Path $root 'RELEASE_NOTES.md'
$proj = Join-Path $root 'src\NoPdf.App\NoPdf.App.csproj'
$outDir = Join-Path $root 'Release'

function Get-Prefix {
    [xml]$props = Get-Content $propsPath
    $p = ($props.Project.PropertyGroup | Where-Object { $_.VersionPrefix }).VersionPrefix
    if (-not $p) { throw "Could not read <VersionPrefix> from $propsPath" }
    $p
}
function Set-Prefix($prefix) {
    (Get-Content $propsPath -Raw) `
        -replace '<VersionPrefix>[^<]*</VersionPrefix>', "<VersionPrefix>$prefix</VersionPrefix>" |
        Set-Content $propsPath -Encoding utf8 -NoNewline
}
function Get-BuildNo {
    if (Test-Path $buildNoPath) { $n = (Get-Content $buildNoPath -Raw).Trim(); if ($n -ne '') { return [int]$n } }
    0
}
function Set-BuildNo($n) { Set-Content $buildNoPath -Value "$n" -Encoding ascii }

# ----- Decide the version -----
$prefix = Get-Prefix
$buildNo = Get-BuildNo

if ($Version) {
    if ($Version -notmatch '^(\d+\.\d+\.\d+)-beta\.(\d+)$') { throw "Version '$Version' is not 0.0.X-beta.YY" }
    $prefix = $Matches[1]; $buildNo = [int]$Matches[2]
    Set-Prefix $prefix; Set-BuildNo $buildNo
}
elseif ($Publish) {
    $parts = $prefix.Split('.')
    $parts[2] = [string]([int]$parts[2] + 1)   # X += 1
    $prefix = ($parts -join '.')
    $buildNo = 0
    Set-Prefix $prefix; Set-BuildNo $buildNo
}
else {
    # Local (non-debug) build for the user: raise YY once.
    $buildNo = $buildNo + 1
    Set-BuildNo $buildNo
}

$ver = "$prefix-beta." + ('{0:00}' -f $buildNo)
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
    # RevSuppress: YY was set above, so all platforms share this one version.
    # Keep the log so a failure is diagnosable instead of vanishing into Out-Null.
    $log = Join-Path $env:TEMP "nopdf-publish-$rid.log"
    dotnet publish $proj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false -p:RevSuppress=true 2>&1 |
        Out-File -FilePath $log -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        Write-Host "--- last lines of $log ---" -ForegroundColor Red
        Get-Content $log -Tail 15 | ForEach-Object { Write-Host "    $_" }
        throw "dotnet publish failed for $rid (see $log)"
    }

    $src = Join-Path $root "src\NoPdf.App\bin\Release\net10.0\$rid\publish\NoPdf.App$($t.ext)"
    $dst = Join-Path $outDir "noPDF-$verTag-$rid$($t.ext)"
    Copy-Item $src $dst -Force
    $mb = [math]::Round((Get-Item $dst).Length / 1MB)
    Write-Host "    -> $dst ($mb MB)" -ForegroundColor Green

    # Keep a stable-named copy of the Windows x64 build for quick launching.
    if ($rid -eq 'win-x64') { Copy-Item $src (Join-Path $outDir 'NoPdf.App.exe') -Force }
}

# Ship the documented default config next to the binaries. It is generated (the command
# list is rendered into its comments), so the app itself has to produce it.
$cfgPath = Join-Path $outDir 'config.yaml'
$cfgExe = Join-Path $root 'src\NoPdf.App\bin\Release\net10.0\win-x64\publish\NoPdf.App.exe'
if (Test-Path $cfgExe) {
    & $cfgExe --write-default-config $cfgPath | Out-Null
    if (Test-Path $cfgPath) {
        $lines = (Get-Content $cfgPath).Count
        Write-Host "  default config -> $cfgPath ($lines lines)" -ForegroundColor Green
    } else {
        Write-Warning "Could not write $cfgPath"
    }
}

# The licence and the third-party notices ship WITH the binaries. This is not a nicety: the
# single-file build compiles in MIT, Apache-2.0, BSD-3-Clause and OFL-1.1 components whose
# licences all require their notices to accompany the software. A release missing these is
# non-compliant, so a missing file fails the build rather than warning.
foreach ($doc in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $srcDoc = Join-Path $root $doc
    if (-not (Test-Path $srcDoc)) {
        throw "$doc is missing from the repo root - released binaries must not ship without it."
    }
    Copy-Item $srcDoc (Join-Path $outDir $doc) -Force
    Write-Host "  $doc -> $(Join-Path $outDir $doc)" -ForegroundColor Green
}

Write-Host "`nArtifacts written to $outDir" -ForegroundColor Cyan

if (-not $Publish) {
    Write-Host "Local build - not tagged or published. Use -Publish for a public release." -ForegroundColor DarkGray
    return
}

# ----- Roll RELEASE_NOTES.md: promote Unreleased to this version, start a fresh one -----
if (Test-Path $notesPath) {
    $notes = Get-Content $notesPath -Raw
    $date = Get-Date -Format 'yyyy-MM-dd'
    $replacement = "## Unreleased ($prefix line)`r`n`r`n_Nothing yet._`r`n`r`n## $verTag - $date"
    $notes = [regex]::Replace($notes, '## Unreleased \([^)]*\)', $replacement, 1)
    Set-Content $notesPath -Value $notes -Encoding utf8
}

# ----- Commit, tag, push, GitHub Release -----
Push-Location $root
try {
    # git/gh write progress + warnings to stderr; relax so they don't abort the run.
    $ErrorActionPreference = 'Continue'

    git commit $propsPath $notesPath -m "Release $verTag" 2>$null
    if ($LASTEXITCODE -eq 0) { Write-Host "Committed version bump + notes" } else { Write-Host "Nothing to commit" }
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
        & $gh release create $verTag @assets --title "noPDF $verTag" --notes-file $notesPath --prerelease
    }
    if ($LASTEXITCODE -ne 0) { throw "gh release failed for $verTag" }
    Write-Host "Published GitHub Release $verTag with $($assets.Count) asset(s)" -ForegroundColor Green
}
finally { Pop-Location }
