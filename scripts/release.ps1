<#
.SYNOPSIS
  Release CatalogBridge (Γέφυρα Καταλόγου) as a tested, tagged, versioned exe.

.DESCRIPTION
  Refuses to release anything untested or unrecorded:
    1. must be on master with a clean working tree
    2. <Version> in CatalogBridge.csproj must equal -Version
    3. CHANGELOG.md must have a "## [X.Y.Z]" section
    4. dotnet test must pass
  then publishes the single-file exe, copies it to deploy\ (keeping the previous one),
  creates annotated tag vX.Y.Z, pushes master + tag, and creates a GitHub Release with the
  exe attached. Any failure stops before the tag is created.

.EXAMPLE
  .\scripts\release.ps1 -Version 1.1.0
  .\scripts\release.ps1 -Version 1.1.0 -SkipGitHubRelease   # tag + push, no release
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$SkipGitHubRelease,
    [switch]$AllowBranch     # release from a non-master branch (hotfix use only)
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Fail($msg) { Write-Host "RELEASE STOPPED: $msg" -ForegroundColor Red; exit 1 }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Version must be X.Y.Z (got '$Version')" }
$tag = "v$Version"

# 1. branch + clean tree ----------------------------------------------------------------
$branch = (git branch --show-current).Trim()
if ($branch -ne "master" -and -not $AllowBranch) { Fail "on '$branch', not master (use -AllowBranch for a hotfix)" }
if ((git status --porcelain).Length -gt 0) { Fail "working tree is not clean - commit or stash first" }
if (git tag -l $tag) { Fail "tag $tag already exists" }

# 2. version recorded in the project ----------------------------------------------------
$csproj = Get-Content "CatalogBridge.csproj" -Raw
if ($csproj -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    Fail "CatalogBridge.csproj does not contain <Version>$Version</Version> - set it and commit"
}

# 3. changelog entry --------------------------------------------------------------------
$changelog = Get-Content "CHANGELOG.md" -Raw
if ($changelog -notmatch "## \[$([regex]::Escape($Version))\]") {
    Fail "CHANGELOG.md has no '## [$Version]' section - write it and commit"
}
$notes = [regex]::Match($changelog, "## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=\n## \[|\z)", "Singleline").Groups[1].Value.Trim()

# 4. tests ------------------------------------------------------------------------------
Write-Host "== dotnet test" -ForegroundColor Cyan
dotnet test tests\CatalogBridge.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "tests failed" }

# 5. publish ----------------------------------------------------------------------------
Write-Host "== dotnet publish" -ForegroundColor Cyan
# Only the build outputs are cleared. publish\ also holds settings.ini and the app's own data
# files (sync registry, visitor cards) that the owner runs against - those must survive.
New-Item -ItemType Directory -Force publish | Out-Null
Remove-Item publish\CatalogBridge.exe, publish\*.pdb -Force -ErrorAction SilentlyContinue
dotnet publish CatalogBridge.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o publish --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "publish failed" }
Remove-Item publish\*.pdb -ErrorAction SilentlyContinue

$exe = "publish\CatalogBridge.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$fileVer = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "   $exe  $size MB  product version '$fileVer'"
if ($fileVer -notlike "$Version*") { Fail "published exe reports version '$fileVer', expected $Version" }

# 6. deploy folder: keep the previous exe --------------------------------------------------
New-Item -ItemType Directory -Force deploy | Out-Null
if (Test-Path deploy\CatalogBridge.exe) {
    $prev = (Get-Item deploy\CatalogBridge.exe).VersionInfo.ProductVersion
    if (-not $prev) { $prev = "unversioned" }
    Copy-Item deploy\CatalogBridge.exe "deploy\CatalogBridge_PREVIOUS_$($prev -replace '[^0-9.]','').exe" -Force
}
Copy-Item $exe deploy\CatalogBridge.exe -Force

# 7. tag + push -------------------------------------------------------------------------
Write-Host "== git tag $tag" -ForegroundColor Cyan
git tag -a $tag -m "CatalogBridge $Version"
git push origin $branch
git push origin $tag

# 8. GitHub release with the exe --------------------------------------------------------
if (-not $SkipGitHubRelease) {
    Write-Host "== gh release create $tag" -ForegroundColor Cyan
    $notesFile = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $notesFile -Value $notes -Encoding UTF8
    gh release create $tag "$exe#CatalogBridge.exe (CatalogBridge $Version, win-x64, self-contained)" `
        --title "CatalogBridge $Version" --notes-file $notesFile
    Remove-Item $notesFile -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "RELEASED $tag  ->  deploy\CatalogBridge.exe ($size MB)" -ForegroundColor Green
