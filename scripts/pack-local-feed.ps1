# Release script — packs to the local folder feed, tags, and pushes.
#
# Why: after pushing a release tag, nuget.org needs minutes to index the new
# packages (plus NuGet's 30-minute HTTP index cache on consumer machines).
# Sibling PicoHex repos consume PicoSerDe packages via PackageReference, so
# local development would stall until indexing completes. This script packs ALL
# packages into the local folder feed (NuGet.config: local -> artifacts/nupkg)
# before tagging, so local restores resolve the new version instantly. CI
# (release.yml) still publishes to nuget.org as usual; once indexed, both
# sources serve the same bits.
#
# Usage (from repo root):
#   ./scripts/release.ps1 -Version 2026.8.9
#   ./scripts/release.ps1 -Version 2026.8.9 -SkipTests
#   ./scripts/release.ps1 -Version 2026.8.9 -NoPush   # pack + tag only
#
# The pack phase mirrors release.yml exactly (Core first, then per-format
# Gen -> Consumer with staged RestoreAdditionalProjectSources), so local
# nupkgs match CI output.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$SkipTests,

    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Fail([string]$message) {
    Write-Error $message
    exit 1
}

# --- Preconditions -----------------------------------------------------------

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
    if (-not ($Version -match '^\d+\.\d+\.\d+$')) {
        Fail "Version must be numeric (e.g. 2026.8.9), got '$Version'"
    }

    $tag = "v$Version"
    if (git tag -l $tag) {
        Fail "Tag '$tag' already exists"
    }

    $dirty = git status --porcelain
    if ($dirty) {
        Fail "Working tree is not clean:`n$dirty`nCommit or stash changes before releasing."
    }

    # --- Tests ------------------------------------------------------------------

    if (-not $SkipTests) {
        Write-Host "=== Running tests ===" -ForegroundColor Cyan
        $testProjects = @(
            "PicoSerDe.Core/tests/PicoSerDe.Core.Tests/PicoSerDe.Core.Tests.csproj",
            "PicoJetson/tests/PicoJetson.Unit.Tests/PicoJetson.Unit.Tests.csproj",
            "PicoJetson/tests/PicoJetson.Integration.Tests/PicoJetson.Integration.Tests.csproj",
            "PicoJetson/tests/PicoJetson.Functional.Tests/PicoJetson.Functional.Tests.csproj",
            "PicoIni/tests/PicoIni.Tests/PicoIni.Tests.csproj",
            "PicoToml/tests/PicoToml.Tests/PicoToml.Tests.csproj",
            "PicoYaml/tests/PicoYaml.Tests/PicoYaml.Tests.csproj",
            "PicoMsgPack/tests/PicoMsgPack.Tests/PicoMsgPack.Tests.csproj",
            "tests/PicoSerDe.Integration.Tests/PicoSerDe.Integration.Tests.csproj"
        )
        foreach ($project in $testProjects) {
            Write-Host "  -> $project" -ForegroundColor DarkGray
            dotnet test --project $project --configuration Release
            if ($LASTEXITCODE -ne 0) { Fail "Tests failed: $project" }
        }
    }

    # --- Pack (mirrors release.yml phase ordering) --------------------------------

    $nupkgDir = Join-Path $repoRoot "artifacts/nupkg"
    Remove-Item -Path $nupkgDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $nupkgDir | Out-Null

    $packCommon = @(
        "--configuration", "Release",
        "--output", $nupkgDir,
        "-p:Version=$Version",
        "-p:UseProjectReferences=false"
    )

    function Pack([string]$project, [bool]$stagedSource = $false) {
        $args = @($packCommon)
        if ($stagedSource) {
            $args += "-p:RestoreAdditionalProjectSources=$nupkgDir"
        }
        Write-Host "  -> pack $project" -ForegroundColor DarkGray
        dotnet pack $project @args
        if ($LASTEXITCODE -ne 0) {
            # Occasional MSBuild node races exit non-zero after a successful
            # compile (no error output) - retry once before failing.
            Write-Host "  !! pack failed (exit $LASTEXITCODE), retrying once..." -ForegroundColor Yellow
            dotnet pack $project @args
            if ($LASTEXITCODE -ne 0) { Fail "Pack failed: $project" }
        }
    }

    Write-Host "=== Phase 1: Core ===" -ForegroundColor Cyan
    Pack "PicoSerDe.Core/src/PicoSerDe.Core.csproj"

    Write-Host "=== Phase 2: per-format Generator -> Consumer ===" -ForegroundColor Cyan
    Pack "PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj" $true
    Pack "PicoJetson/src/PicoJetson/PicoJetson.csproj" $true
    Pack "PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj" $true
    Pack "PicoIni/src/PicoIni/PicoIni.csproj" $true
    Pack "PicoToml/src/PicoToml.Gen/PicoToml.Gen.csproj" $true
    Pack "PicoToml/src/PicoToml/PicoToml.csproj" $true
    Pack "PicoYaml/src/PicoYaml.Gen/PicoYaml.Gen.csproj" $true
    Pack "PicoYaml/src/PicoYaml/PicoYaml.csproj" $true
    Pack "PicoMsgPack/src/PicoMsgPack.Gen/PicoMsgPack.Gen.csproj" $true
    Pack "PicoMsgPack/src/PicoMsgPack/PicoMsgPack.csproj" $true

    $packed = @(Get-ChildItem $nupkgDir -Filter "*.nupkg")
    if ($packed.Count -eq 0) { Fail "No packages were produced" }
    Write-Host "=== Local feed ready: $nupkgDir ($($packed.Count) packages, version $Version) ===" -ForegroundColor Green

    # --- Tag + push ----------------------------------------------------------------

    git tag -a $tag -m "PicoSerDe $Version - packed locally + published via release.yml"
    if ($LASTEXITCODE -ne 0) { Fail "git tag failed" }

    if (-not $NoPush) {
        git push origin main
        if ($LASTEXITCODE -ne 0) { Fail "git push main failed" }
        git push origin $tag
        if ($LASTEXITCODE -ne 0) { Fail "git push tag failed" }
    }
    else {
        Write-Host "Tag '$tag' created locally. Push when ready: git push origin main $tag" -ForegroundColor Yellow
    }

    Write-Host "=== Release $Version complete ===" -ForegroundColor Green
}
finally {
    Pop-Location
}
